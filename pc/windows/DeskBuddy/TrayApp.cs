using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Nucleul aplicației: o dată pe secundă citește senzorii și îi trimite la DeskBuddy, la 30 s retrimite usage-ul Claude
/// și caută placa în rețea. Ține iconița din tray și fereastra (MainForm), pe care o creează la prima deschidere.
sealed class TrayApp : ApplicationContext
{
    enum Health { Starting, Ok, NoDevice, Error }

    public AppSettings Settings { get; } = AppSettings.Load();
    public Stats? Latest { get; private set; }                     // ultima citire (pe thread-ul UI)
    public bool Quitting { get; private set; }
    public IReadOnlyList<IPAddress> Targets => wifi.BroadcastTargets;
    public Icon WindowIcon => (Icon)(currentIcon ?? MakeIcon(Color.Gray)).Clone();
    /// pe thread-ul UI, după fiecare citire (o dată pe secundă)
    public event Action? Updated;

    /// IP-ul plăcii: cel setat de mână, cel găsit prin deskbuddy.local sau cel spus de placă pe USB
    public IPAddress? Device => Settings.HostIp ?? wifi.BoardIp ?? (usb.Connected?.Ip is { } uip && IPAddress.TryParse(uip, out var a) ? a : null);

    /// placa pe USB (null = nu e conectată prin cablu sau are firmware fără USB)
    public UsbLink.Board? UsbBoard => usb.Connected;
    /// pe unde pleacă acum datele
    public bool SendingOverUsb => Settings.Link != LinkMode.Wifi && usb.Connected != null;
    /// placa e conectată la PC (USB sau găsită în rețea)
    public bool DeviceFound => usb.Connected != null || wifi.Connected != null;
    /// pagina plăcii prin HTTP: doar dacă placa e în aceeași rețea cu PC-ul (sau IP-ul e setat de mână)
    public string? DeviceUrl => Settings.HostIp is { } h ? $"http://{h}/" : wifi.Direct && wifi.BoardIp is { } ip ? $"http://{ip}/" : null;
    /// placa pe WiFi (din heartbeat sau din căutare)
    public UsbLink.Board? WifiBoard => wifi.Connected;
    public IPEndPoint? WifiEndpoint => wifi.Endpoint;
    public bool WifiDirect => wifi.Direct;
    public string? WifiError => wifi.Error;
    public string? FpsError => fps.Error;
    public float? Fps { get; private set; }

    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new();
    readonly ToolStripMenuItem miStats = new() { Enabled = false };
    readonly WindowsFormsSynchronizationContext ui;
    readonly UsbLink usb = new();
    readonly WifiLink wifi = new();
    readonly FpsMonitor fps = new();
    readonly MediaWatcher media = new();
    string? mediaSent;
    long mediaAt = -60_000;
    /// ce se redă acum pe PC (null = nimic)
    public MediaWatcher.Now? Media => media.Current;
    readonly GameDetector games = new();
    /// jocul pornit acum (null = niciunul)
    public string? Game { get; private set; }
    readonly BoardNeeds needs;
    readonly IDisposable? relay;
    readonly System.Threading.Timer timer;
    readonly EventWaitHandle? showSignal;
    MainForm? form;
    Sensors? sensors;
    int busy;
    bool fansDumped;                            // fans.txt: ce senzori de ventilatoare găsește aplicația (depanare)
    long usageAt = -60_000, timeAt = -60_000;   // „demult”: prima bifă le face imediat
    Health health = Health.Starting;
    Icon? currentIcon;
    bool trayHintShown;

    // ------------------------------------------------------------ update-uri (GitHub Releases)
    /// release-ul public: aplicația + firmware-ul public
    public ReleaseInfo? Release { get; private set; }
    /// release-ul din repo-ul privat (opțional, cu token): firmware-ul complet
    public ReleaseInfo? PrivateRelease { get; private set; }
    public DateTime? LastUpdateCheck { get; private set; }
    public string? UpdateError { get; private set; }
    public string? PrivateError { get; private set; }
    /// placa: null = nu răspunde; Version "" = firmware vechi (fără /api/version)
    /// versiunea plăcii: de pe USB sau de pe WiFi (răspunsul ei la „hi” / heartbeat)
    public DeviceFirmware.Board? Board => (usb.Connected ?? wifi.Connected) is { } b ? new DeviceFirmware.Board(b.Version, b.Variant) : null;
    public string? BoardVersion => Board?.Version;
    public bool BoardViaUsb => usb.Connected != null;
    public bool AppUpdateAvailable => Release?.App != null && Release.Version > Updater.Current;

    /// firmware-ul plăcii se ia din același loc din care a venit: "full" din repo-ul privat, "public" din cel public;
    /// necunoscut (firmware vechi) -> din privat dacă e configurat
    public bool UsePrivateFirmware => Board?.Variant switch
    {
        "full" => true,
        "public" => false,
        _ => Settings.HasPrivateFirmware,
    };
    public ReleaseInfo? FirmwareRelease => UsePrivateFirmware ? PrivateRelease : Release;
    public bool PrivateFirmwareMissing => UsePrivateFirmware && !Settings.HasPrivateFirmware;
    public bool FirmwareUpdateAvailable => FirmwareRelease?.Firmware != null && Board != null &&
        (Board.Version.Length == 0 || Updater.ParseVersion(Board.Version) is not Version v || v < FirmwareRelease.Version);

    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60_000 };
    DateTime nextUpdateCheck = DateTime.Now.AddSeconds(15), nextFirmwareCheck = DateTime.Now.AddSeconds(5);
    string? notifiedKey;
    bool balloonOpensUpdates;

    public TrayApp(bool showWindow, EventWaitHandle? showSignal)
    {
        this.showSignal = showSignal;
        UdpSender.HostOverride = Settings.HostIp;

        var miOpen = new ToolStripMenuItem("Deschide DeskBuddy") { Font = new Font(SystemFonts.MenuFont ?? Theme.Body, FontStyle.Bold) };
        var miBrowser = new ToolStripMenuItem("Pagina plăcii în browser");
        var miExit = new ToolStripMenuItem("Ieșire");
        menu.Items.AddRange([miOpen, miBrowser, new ToolStripSeparator(), miStats, new ToolStripSeparator(), miExit]);
        ui = new WindowsFormsSynchronizationContext();
        usb.Enabled = Settings.Link != LinkMode.Wifi;
        usb.Changed += () =>
        {
            timeAt = -60_000;                         // ora pleacă imediat pe USB
            if (usb.Connected != null) usb.Send(System.Text.Encoding.UTF8.GetBytes(
                new System.Text.Json.Nodes.JsonObject { ["t"] = "pcip", ["a"] = string.Join(',', LocalIps().Take(3)) }.ToJsonString()));
            ui.Post(_ => { if (!Quitting) Updated?.Invoke(); }, null);
        };
        wifi.Changed += () => ui.Post(_ => { if (!Quitting) Updated?.Invoke(); }, null);
        relay = Relay.Listen(Route);
        needs = new BoardNeeds(usb.Send);             // vremea / imprimanta / login-ul Bambu pentru placa fără WiFi
        usb.Need += needs.Handle;

        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "DeskBuddy", Visible = true };
        SetHealth(Health.Starting, force: true);
        menu.Opening += (_, _) => miStats.Text = Latest?.Summary() ?? "se citește...";
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        miOpen.Click += (_, _) => ShowWindow();
        miBrowser.Click += (_, _) => Shell(DeviceUrl ?? "http://deskbuddy.local/");
        miExit.Click += (_, _) => Quit();

        ClaudeBridge.FixPathIfMoved();
        Updater.CleanupOld();                         // DeskBuddy.exe.old rămas de la ultimul update
        tray.BalloonTipClicked += (_, _) =>
        {
            if (!balloonOpensUpdates) return;
            ShowWindow();
            form?.ShowPage("Aplicație");
        };
        tray.BalloonTipClosed += (_, _) => balloonOpensUpdates = false;
        updateTimer.Tick += async (_, _) => await PeriodicChecksAsync();
        updateTimer.Start();
        if (showSignal != null)                       // a doua pornire a exe-ului aduce fereastra în față
            new Thread(WaitForShowSignal) { IsBackground = true, Name = "show-signal" }.Start();

        timer = new System.Threading.Timer(Tick, null, 200, 1000);
        if (showWindow) ShowWindow();
    }

    // ------------------------------------------------------------ fereastra
    public void ShowWindow()
    {
        if (Quitting) return;
        form ??= new MainForm(this);
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.BringToFront();
    }

    public void NotifyHiddenToTray()
    {
        if (trayHintShown) return;
        trayHintShown = true;
        tray.ShowBalloonTip(3000, "DeskBuddy", "Rulează în continuare în tray. Click pe iconiță deschide fereastra.", ToolTipIcon.Info);
    }

    void WaitForShowSignal()
    {
        while (!Quitting)
        {
            try { if (showSignal!.WaitOne(1000)) ui.Post(_ => ShowWindow(), null); }
            catch (ObjectDisposedException) { return; }
        }
    }

    // ------------------------------------------------------------ acțiuni din fereastră
    public void SetHost(string host)
    {
        Settings.Host = host;
        Settings.Save();
        UdpSender.HostOverride = Settings.HostIp;
        wifi.ResetTargets();                          // caută din nou acum
    }

    public Task SendClaudeTestAsync() => Task.Run(() => ClaudeBridge.Test(Route));

    /// rețelele WiFi văzute de placă (prin USB); null = placa nu e pe USB / n-a răspuns
    public async Task<List<(string Ssid, int Rssi, bool Locked)>?> UsbScanWifiAsync()
    {
        string? reply = await usb.RequestAsync("{\"t\":\"scan\"}", "{\"scan\"", 12_000);
        if (reply == null) return null;
        var list = new List<(string, int, bool)>();
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(reply);
            foreach (var n in d.RootElement.GetProperty("scan").EnumerateArray())
                list.Add((n.GetProperty("s").GetString() ?? "", n.GetProperty("r").GetInt32(), n.GetProperty("l").GetBoolean()));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    /// salvează WiFi-ul pe placă prin USB (placa repornește și se conectează); parolă goală = păstrează parola veche a aceleiași rețele
    public async Task<bool> UsbSaveWifiAsync(string ssid, string password)
    {
        var json = new System.Text.Json.Nodes.JsonObject { ["t"] = "wifi", ["ssid"] = ssid, ["pass"] = password }.ToJsonString();
        string? reply = await usb.RequestAsync(json, "{\"wifi_saved\"", 5000);
        if (reply?.Contains("true") != true) return false;
        return true;
    }

    // ------------------------------------------------------------ setările plăcii (fără browser): aceleași /api/... ca pagina ei
    /// cerere la placă, pe USB dacă e pe cablu, altfel pe WiFi (cu parola paginii, dacă are). Întoarce JSON-ul răspunsului.
    public async Task<JsonNode?> ApiAsync(string path, IDictionary<string, string>? args = null, int timeoutMs = 9000)
    {
        int id = usb.NextId();
        var a = new JsonObject();
        if (args != null) foreach (var (k, v) in args) a[k] = v;
        var req = new JsonObject { ["t"] = "req", ["id"] = id, ["p"] = path, ["a"] = a };
        string prefix = $"{{\"res\":{id},";
        string? res;
        if (usb.Connected != null) res = await usb.RequestAsync(req.ToJsonString(), prefix, timeoutMs);
        else if (wifi.Connected != null)
        {
            if (Settings.BoardPassword is { Length: > 0 } pw) req["pw"] = pw;
            res = await wifi.RequestAsync(req.ToJsonString(), prefix, timeoutMs);
        }
        else throw new IOException("Placa nu e conectată (nici pe USB, nici pe WiFi).");
        if (res == null) throw new TimeoutException("Placa n-a răspuns.");
        using var d = JsonDocument.Parse(res);
        int code = d.RootElement.GetProperty("c").GetInt32();
        string body = d.RootElement.GetProperty("b").GetString() ?? "";
        if (code == 401) throw new UnauthorizedAccessException("Placa cere parola paginii.");
        if (code != 200) throw new IOException($"Placa a răspuns cu {code}.");
        return JsonNode.Parse(body);
    }

    /// IP-urile PC-ului în rețelele locale (pentru heartbeat-ul plăcii): întâi cele cu gateway
    public static List<string> LocalIps()
    {
        var list = new List<(string Ip, bool Gw)>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    if (ua.PrefixLength >= 31) continue;
                    list.Add((ua.Address.ToString(), gw));
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return list.OrderByDescending(x => x.Gw).Select(x => x.Ip).Distinct().ToList();
    }

    public void SetLinkMode(LinkMode mode)
    {
        Settings.Link = mode;
        Settings.Save();
        usb.Enabled = mode != LinkMode.Wifi;
    }

    /// trimite un pachet la placă: Auto = USB dacă e conectată prin cablu, altfel WiFi
    public void Route(byte[] data)
    {
        var mode = Settings.Link;
        if (mode != LinkMode.Wifi && usb.Send(data)) return;
        if (mode != LinkMode.Usb) wifi.Send(data);
    }

    // ------------------------------------------------------------ flash prin USB
    public enum UsbFlashKind { KeepSettings, Full }

    public static List<UsbLink.PortInfo> UsbPorts() => UsbLink.CandidatePorts();

    /// scrie firmware-ul prin USB: din release (varianta potrivită) sau dintr-un fișier .bin ales de mână
    /// null = scris și verificat; altfel mesajul de eroare
    /// ecranul MUZICA: la schimbare imediat, altfel la 2 s (placa ține poziția la zi între pachete)
    void SendMedia(long now)
    {
        var m = media.Current;
        var o = new JsonObject { ["t"] = "md", ["st"] = m?.St ?? 0 };
        if (m != null)
        {
            o["ti"] = MediaWatcher.Ascii(m.Title, 63);
            o["ar"] = MediaWatcher.Ascii(m.Artist, 39);
            o["ap"] = MediaWatcher.Ascii(m.App, 15);
            if (m.Dur is double du) o["du"] = Math.Round(du, 1);
            if (m.Pos is double po) o["po"] = Math.Round(po + (m.St == 1 ? (DateTime.Now - m.At).TotalSeconds : 0), 1);
        }
        string key = $"{m?.St}|{m?.Title}|{m?.Artist}|{m?.App}|{m?.Dur}";
        if (key == mediaSent && now - mediaAt < 2000) return;
        mediaSent = key;
        mediaAt = now;
        Route(System.Text.Encoding.UTF8.GetBytes(o.ToJsonString()));
    }

    async Task<string?> WriteFlashAsync(string portName, List<EspFlasher.Region> regions, IProgress<string> status)
    {
        usb.Pause();
        try
        {
            await Task.Run(() =>
            {
                using var f = new EspFlasher(portName, status);
                f.Flash(regions);
            });
            return null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "Flash eșuat: " + e.Message;
        }
        finally { usb.Resume(); }
    }

    public async Task<string> FlashUsbAsync(string portName, UsbFlashKind kind, string? localFile, IProgress<string> status)
    {
        byte[] image;
        bool merged;
        try
        {
            if (localFile != null)
            {
                image = await File.ReadAllBytesAsync(localFile);
                merged = EspFlasher.LooksLikeMerged(image);
                if (!merged && !DeviceFirmware.LooksLikeApp(image)) return "Fișierul nu e un firmware ESP32 (nici DeskBuddy-flash.bin, nici DeskBuddy.ino.bin).";
            }
            else
            {
                var rel = FirmwareRelease;
                if (rel?.Flash is not Asset asset)
                    return PrivateFirmwareMissing ? "Firmware-ul plăcii vine din repo-ul privat: completează-l mai jos și apasă „Verifică acum”."
                                                  : "Nu găsesc DeskBuddy-flash.bin în ultimul release. Apasă „Verifică acum”.";
                bool priv = UsePrivateFirmware;
                status.Report("Descarc firmware-ul din GitHub...");
                image = await Updater.DownloadAsync(asset, priv ? Settings.FirmwareToken : null);
                merged = EspFlasher.LooksLikeMerged(image);
                if (!merged) return "DeskBuddy-flash.bin din release nu arată ca o imagine completă.";
            }
        }
        catch (Exception e) { return "Nu pot citi firmware-ul: " + e.Message; }

        var regions = merged
            ? kind == UsbFlashKind.Full ? EspFlasher.PlanFull(image) : EspFlasher.PlanKeepSettings(image)
            : EspFlasher.PlanApp(image);
        if (await WriteFlashAsync(portName, regions, status) is string err) return err;

        status.Report("Placa repornește...");
        for (int i = 0; i < 20; i++)                  // așteaptă să răspundă pe USB (setup-ul poate aștepta WiFi-ul ~20 s)
        {
            await Task.Delay(1500);
            if (usb.Connected is { } b) return $"Gata: placa rulează firmware-ul {b.Version} ({b.Variant}).";
        }
        return kind == UsbFlashKind.Full
            ? "Gata. Placa pornește în modul SETUP: conectează-te la rețeaua DeskBuddy-Setup ca să-i dai WiFi-ul (sau las-o doar pe USB)."
            : "Gata, firmware scris. Placa n-a răspuns încă pe USB (dacă „Conexiune” e pe „Doar WiFi”, e normal).";
    }

    // ------------------------------------------------------------ update-uri
    async Task PeriodicChecksAsync()
    {
        if (Quitting) return;
        if (DateTime.Now >= nextFirmwareCheck)
        {
            nextFirmwareCheck = DateTime.Now.AddMinutes(10);
        }
        if (Settings.AutoUpdateCheck && DateTime.Now >= nextUpdateCheck)
        {
            nextUpdateCheck = DateTime.Now.AddHours(24);
            await CheckUpdatesAsync(manual: false);
        }
    }

    public async Task CheckUpdatesAsync(bool manual)
    {
        UpdateError = null;
        if (Updater.Repo != null)
        {
            try
            {
                Release = await Updater.GetLatestAsync();
                if (Release == null) UpdateError = "niciun release încă";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                UpdateError = "nu pot ajunge la GitHub";
            }
        }
        else UpdateError = "build local: fără update-uri pentru aplicație";

        PrivateError = null;
        if (Settings.HasPrivateFirmware)
        {
            try
            {
                PrivateRelease = await Updater.GetLatestAsync(Settings.FirmwareRepo.Trim(), Settings.FirmwareToken);
                if (PrivateRelease == null) PrivateError = "niciun release în repo-ul privat (sau repo greșit)";
            }
            catch (UnauthorizedAccessException e) { PrivateError = e.Message; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                PrivateError = "nu pot ajunge la repo-ul privat";
            }
        }
        else PrivateRelease = null;

        LastUpdateCheck = DateTime.Now;
        if (manual || Quitting) return;
        bool app = AppUpdateAvailable, fw = FirmwareUpdateAvailable;
        string key = $"{Release?.Version}|{FirmwareRelease?.Version}";
        if ((app || fw) && notifiedKey != key)
        {
            notifiedKey = key;
            balloonOpensUpdates = true;
            string what = app && fw ? "aplicația și firmware-ul plăcii" : app ? "aplicația" : "firmware-ul plăcii";
            var v = app ? Release!.Version : FirmwareRelease!.Version;
            tray.ShowBalloonTip(8000, $"DeskBuddy {v.ToString(3)}", $"E disponibilă o versiune nouă pentru {what}. Click aici ca s-o instalezi.", ToolTipIcon.Info);
        }
    }

    public void SetPrivateFirmware(string repo, string? token)
    {
        Settings.FirmwareRepo = repo.Trim();
        if (token != null) Settings.FirmwareToken = token.Trim();   // null = păstrează token-ul salvat
        Settings.Save();
        PrivateRelease = null;
    }

    /// descarcă noul DeskBuddy.exe, îl pune în locul celui vechi și repornește (cu aceleași drepturi)
    public async Task<string?> UpdateAppAsync()
    {
        if (Release?.App is not Asset asset) return "nu e niciun update";
        try
        {
            byte[] zip = await Updater.DownloadAsync(asset);
            Updater.InstallApp(zip);
        }
        catch (Exception e) { return "Update eșuat: " + e.Message; }
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, Arguments = "--restarted --show" };
        try { Process.Start(psi); }
        catch (Win32Exception e) { return "Instalat, dar nu pot reporni: " + e.Message + ". Pornește DeskBuddy din nou."; }
        Quit();
        return null;
    }

    byte[]? firmwareCache;
    Version? firmwareCacheVersion;
    bool firmwareCachePrivate;

    /// urcă firmware-ul din release pe placă și așteaptă să repornească cu noua versiune
    public async Task<(DeviceFirmware.Result Result, string Message)> UpdateFirmwareAsync(string? password, IProgress<string> status)
    {
        var rel = FirmwareRelease;
        if (rel?.Firmware is not Asset asset || DeviceUrl is not string url) return (DeviceFirmware.Result.Failed, "Placa sau release-ul lipsesc.");
        bool priv = UsePrivateFirmware;
        try
        {
            if (firmwareCache == null || firmwareCacheVersion != rel.Version || firmwareCachePrivate != priv)
            {
                status.Report("Descarc firmware-ul din GitHub...");
                firmwareCache = await Updater.DownloadAsync(asset, priv ? Settings.FirmwareToken : null);
                firmwareCacheVersion = rel.Version;
                firmwareCachePrivate = priv;
            }
        }
        catch (Exception e) { return (DeviceFirmware.Result.Failed, "Nu pot descărca firmware-ul: " + e.Message); }

        status.Report($"Urc firmware-ul pe placă ({firmwareCache.Length / 1024} KB, durează ~1 min pe WiFi slab)...");
        var r = await DeviceFirmware.UploadAsync(url, firmwareCache, password);
        if (r.Result != DeviceFirmware.Result.Ok) return r;

        status.Report("Placa repornește...");
        string want = rel.Version.ToString(3);
        for (int i = 0; i < 30; i++)                  // până la ~60 s
        {
            await Task.Delay(2000);
            if (Board?.Version == want) return (DeviceFirmware.Result.Ok, $"Gata: placa rulează firmware-ul {want}.");
        }
        return (DeviceFirmware.Result.Ok, "Firmware urcat, dar placa n-a răspuns încă după repornire. Verifică peste un minut.");
    }

    public void RestartAsAdmin()
    {
        bool visible = form?.Visible == true;
        // cu „Pornește cu Windows” bifat, repornim prin task (admin fără prompt UAC); altfel cerem UAC
        var psi = Autostart.IsEnabled()
            ? new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, Arguments = visible ? "--via-task --show" : "--via-task" }
            : new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = visible ? "--restarted --show" : "--restarted" };
        try { Process.Start(psi); }
        catch (Win32Exception) { return; }            // UAC refuzat: rămânem cum suntem
        Quit();
    }

    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        timer.Dispose();
        updateTimer.Dispose();
        form?.Close();
        tray.Visible = false;
        tray.Dispose();
        sensors?.Dispose();
        relay?.Dispose();
        needs.Dispose();
        usb.Dispose();
        wifi.Dispose();
        fps.Dispose();
        media.Dispose();
        ExitThread();
    }

    // ------------------------------------------------------------ bucla de citire (thread separat)
    void Tick(object? _)
    {
        if (Quitting || Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            sensors ??= new Sensors();                 // prima deschidere poate dura câteva secunde
            var s = sensors.Read();
            if (!fansDumped)
            {
                fansDumped = true;
                try { File.WriteAllText(Path.Combine(AppSettings.Dir, "fans.txt"), sensors.DumpFans()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Game = games.Current();
            var f = games.CurrentPid is int gpid ? fps.For(gpid) : null;
            Fps = f?.Fps;
            Route(s.ToPacket(Game, f?.Fps, f?.FrameMs));
            long now = Environment.TickCount64;
            SendMedia(now);
            if (now - usageAt > 30_000)
            {
                usageAt = now;
                if (ClaudeBridge.CachedUsagePacket() is byte[] cu) Route(cu);
            }
            if (usb.Connected != null && now - timeAt > 60_000)   // fără WiFi placa n-are NTP: îi dăm ora PC-ului
            {
                timeAt = now;
                usb.Send(System.Text.Encoding.UTF8.GetBytes($"{{\"t\":\"tm\",\"s\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}"));
            }
            ui.Post(_ =>
            {
                if (Quitting) return;
                Latest = s;
                SetHealth(DeviceFound ? Health.Ok : Health.NoDevice);
                string tip = $"DeskBuddy\n{s.Summary()}";
                tray.Text = tip.Length > 127 ? tip[..127] : tip;
                Updated?.Invoke();
            }, null);
        }
        catch (Exception)
        {
            ui.Post(_ => { if (!Quitting) SetHealth(Health.Error); }, null);
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }

    // ------------------------------------------------------------ iconița: un DeskBuddy mic cu ecran, plus un punct de stare
    void SetHealth(Health h, bool force = false)
    {
        if (h == health && !force) return;
        health = h;
        var dot = h switch
        {
            Health.Ok => Theme.Accent,
            Health.NoDevice => Theme.Warn,
            Health.Error => Theme.Bad,
            _ => Color.FromArgb(150, 150, 150),
        };
        var old = currentIcon;
        currentIcon = MakeIcon(dot);
        tray.Icon = currentIcon;
        old?.Dispose();
    }

    static Icon MakeIcon(Color dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) DrawLogo(g, 32, dot);
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    /// iconița exe-ului (DeskBuddy.ico) = cea din tray cu punctul verde, în toate mărimile Windows, ca PNG-uri într-un .ico
    ///   DeskBuddy.exe make-icon pc\windows\DeskBuddy\DeskBuddy.ico
    public static int WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var pngs = sizes.Select(s =>
        {
            using var bmp = new Bitmap(s, s);
            using (var g = Graphics.FromImage(bmp)) DrawLogo(g, s, Theme.Accent);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var f = new BinaryWriter(File.Create(path));
        f.Write((ushort)0); f.Write((ushort)1); f.Write((ushort)sizes.Length);          // antet ICO
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            f.Write((byte)0); f.Write((byte)0); f.Write((ushort)1); f.Write((ushort)32);
            f.Write(pngs[i].Length); f.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) f.Write(p);
        return 0;
    }

    /// desenat pe o grilă de 32, scalat la mărimea cerută
    static void DrawLogo(Graphics g, float size, Color dot)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 32f, size / 32f);
        {
            using var body = Theme.Rounded(new RectangleF(2, 3, 28, 25), 6);
            using var bodyBrush = new SolidBrush(Color.FromArgb(235, 238, 240));
            g.FillPath(bodyBrush, body);
            using var screen = Theme.Rounded(new RectangleF(5.5f, 7, 21, 14), 3);
            using var screenBrush = new SolidBrush(Color.FromArgb(25, 30, 36));
            g.FillPath(screenBrush, screen);
            using var eye = new SolidBrush(Color.FromArgb(110, 200, 255));
            g.FillRectangle(eye, 10, 11, 3, 6);
            g.FillRectangle(eye, 19, 11, 3, 6);
            using var dotBrush = new SolidBrush(dot);
            using var ring = new Pen(Color.FromArgb(30, 30, 30), 1.5f);
            g.FillEllipse(dotBrush, 21, 20, 10, 10);
            g.DrawEllipse(ring, 21, 20, 10, 10);
        }
        g.ResetTransform();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
