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
    readonly ToolStripMenuItem miOpen = new(), miBrowser = new(), miExit = new();
    bool langSynced;                                  // limba plăcii a fost pusă ca a aplicației (o dată pe conexiune)

    void MenuTexts()
    {
        miOpen.Text = L.T("Deschide DeskBuddy", "Open DeskBuddy");
        miBrowser.Text = L.T("Pagina plăcii în browser", "Board page in the browser");
        miExit.Text = L.T("Ieșire", "Exit");
    }

    /// limba aplicației și a plăcii: salvează, refă fereastra (pe pagina Aplicație) și trimite setarea plăcii
    public void SetLanguage(string code)
    {
        Settings.Language = code;
        Settings.Save();
        L.En = code == "en";
        MenuTexts();
        if (form != null)
        {
            bool visible = form.Visible;
            var old = form;
            form = null;
            old.Replacing = true;
            old.Close();
            if (visible) { ShowWindow(); form?.ShowAppPage(); }
        }
        langSynced = false;
        _ = SyncBoardLanguageAsync();
    }

    /// placa afișează textele în limba aplicației: dacă diferă, i-o trimitem (o dată pe conexiune)
    async Task SyncBoardLanguageAsync()
    {
        if (langSynced || !DeviceFound) return;
        langSynced = true;
        try
        {
            var c = await ApiAsync("/api/config");
            string want = Settings.LanguageCode;
            if (c?["lang"] is { } l && (string?)l != want) await ApiAsync("/api/set", new Dictionary<string, string> { ["lang"] = want });
        }
        catch (Exception) { langSynced = false; }             // placa fără răspuns / cu parolă: reîncercăm la următoarea conexiune
    }
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

        L.En = Settings.LanguageCode == "en";
        miOpen.Font = new Font(SystemFonts.MenuFont ?? Theme.Body, FontStyle.Bold);
        MenuTexts();
        menu.Items.AddRange([miOpen, miBrowser, new ToolStripSeparator(), miStats, new ToolStripSeparator(), miExit]);
        ui = new WindowsFormsSynchronizationContext();
        usb.Enabled = Settings.Link != LinkMode.Wifi;
        usb.Changed += () =>
        {
            timeAt = -60_000;                         // ora pleacă imediat pe USB
            if (usb.Connected != null) usb.Send(System.Text.Encoding.UTF8.GetBytes(
                new System.Text.Json.Nodes.JsonObject { ["t"] = "pcip", ["a"] = string.Join(',', LocalIps().Take(3)) }.ToJsonString()));
            ui.Post(_ => { if (!Quitting) { Updated?.Invoke(); if (usb.Connected == null) langSynced = false; else _ = SyncBoardLanguageAsync(); } }, null);
        };
        wifi.Changed += () => ui.Post(_ => { if (!Quitting) { Updated?.Invoke(); if (DeviceFound) _ = SyncBoardLanguageAsync(); } }, null);
        relay = Relay.Listen(Route);
        needs = new BoardNeeds(usb.Send);             // vremea / imprimanta / login-ul Bambu pentru placa fără WiFi
        usb.Need += needs.Handle;

        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "DeskBuddy", Visible = true };
        SetHealth(Health.Starting, force: true);
        menu.Opening += (_, _) => miStats.Text = Latest?.Summary() ?? L.T("se citește...", "reading...");
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
            form?.ShowAppPage();
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
        tray.ShowBalloonTip(3000, "DeskBuddy", L.T("Rulează în continuare în tray. Click pe iconiță deschide fereastra.", "Still running in the tray. Click the icon to open the window."), ToolTipIcon.Info);
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
        else throw new IOException(L.T("Placa nu e conectată (nici pe USB, nici pe WiFi).", "The board isn't connected (neither USB nor WiFi)."));
        if (res == null) throw new TimeoutException(L.T("Placa n-a răspuns.", "The board didn't respond."));
        using var d = JsonDocument.Parse(res);
        int code = d.RootElement.GetProperty("c").GetInt32();
        string body = d.RootElement.GetProperty("b").GetString() ?? "";
        if (code == 401) throw new UnauthorizedAccessException(L.T("Placa cere parola paginii.", "The board asks for the page password."));
        if (code != 200) throw new IOException(L.T($"Placa a răspuns cu {code}.", $"The board replied with {code}."));
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
            return L.T("Flash eșuat: ", "Flash failed: ") + e.Message;
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
                if (!merged && !DeviceFirmware.LooksLikeApp(image)) return L.T("Fișierul nu e un firmware ESP32 (nici DeskBuddy-flash.bin, nici DeskBuddy.ino.bin).", "The file isn't ESP32 firmware (neither DeskBuddy-flash.bin nor DeskBuddy.ino.bin).");
            }
            else
            {
                var rel = FirmwareRelease;
                if (rel?.Flash is not Asset asset)
                    return PrivateFirmwareMissing ? L.T("Firmware-ul plăcii vine din repo-ul privat: completează-l mai jos și apasă „Verifică acum”.", "The board's firmware comes from the private repo: fill it in below and press “Check now”.")
                                                  : L.T("Nu găsesc DeskBuddy-flash.bin în ultimul release. Apasă „Verifică acum”.", "Can't find DeskBuddy-flash.bin in the latest release. Press “Check now”.");
                bool priv = UsePrivateFirmware;
                status.Report(L.T("Descarc firmware-ul din GitHub...", "Downloading the firmware from GitHub..."));
                image = await Updater.DownloadAsync(asset, priv ? Settings.FirmwareToken : null);
                merged = EspFlasher.LooksLikeMerged(image);
                if (!merged) return L.T("DeskBuddy-flash.bin din release nu arată ca o imagine completă.", "DeskBuddy-flash.bin from the release doesn't look like a full image.");
            }
        }
        catch (Exception e) { return L.T("Nu pot citi firmware-ul: ", "Can't read the firmware: ") + e.Message; }

        var regions = merged
            ? kind == UsbFlashKind.Full ? EspFlasher.PlanFull(image) : EspFlasher.PlanKeepSettings(image)
            : EspFlasher.PlanApp(image);
        if (await WriteFlashAsync(portName, regions, status) is string err) return err;

        status.Report(L.T("Placa repornește...", "The board is restarting..."));
        for (int i = 0; i < 20; i++)                  // așteaptă să răspundă pe USB (setup-ul poate aștepta WiFi-ul ~20 s)
        {
            await Task.Delay(1500);
            if (usb.Connected is { } b) return L.T($"Gata: placa rulează firmware-ul {b.Version} ({b.Variant}).", $"Done: the board runs firmware {b.Version} ({b.Variant}).");
        }
        return kind == UsbFlashKind.Full
            ? L.T("Gata. Placa pornește în modul SETUP: conectează-te la rețeaua DeskBuddy-Setup ca să-i dai WiFi-ul (sau las-o doar pe USB).", "Done. The board starts in SETUP mode: connect to the DeskBuddy-Setup network to give it WiFi (or keep it on USB only).")
            : L.T("Gata, firmware scris. Placa n-a răspuns încă pe USB (dacă „Conexiune” e pe „Doar WiFi”, e normal).", "Done, firmware written. The board hasn't answered on USB yet (normal if “Connection” is set to “WiFi only”).");
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
                if (Release == null) UpdateError = L.T("niciun release încă", "no release yet");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                UpdateError = L.T("nu pot ajunge la GitHub", "can't reach GitHub");
            }
        }
        else UpdateError = L.T("build local: fără update-uri pentru aplicație", "local build: no app updates");

        PrivateError = null;
        if (Settings.HasPrivateFirmware)
        {
            try
            {
                PrivateRelease = await Updater.GetLatestAsync(Settings.FirmwareRepo.Trim(), Settings.FirmwareToken);
                if (PrivateRelease == null) PrivateError = L.T("niciun release în repo-ul privat (sau repo greșit)", "no release in the private repo (or wrong repo)");
            }
            catch (UnauthorizedAccessException e) { PrivateError = e.Message; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                PrivateError = L.T("nu pot ajunge la repo-ul privat", "can't reach the private repo");
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
            string what = app && fw ? L.T("aplicația și firmware-ul plăcii", "the app and the board firmware") : app ? L.T("aplicația", "the app") : L.T("firmware-ul plăcii", "the board firmware");
            var v = app ? Release!.Version : FirmwareRelease!.Version;
            tray.ShowBalloonTip(8000, $"DeskBuddy {v.ToString(3)}", L.T($"E disponibilă o versiune nouă pentru {what}. Click aici ca s-o instalezi.", $"A new version is available for {what}. Click here to install it."), ToolTipIcon.Info);
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
        if (Release?.App is not Asset asset) return L.T("nu e niciun update", "no update available");
        try
        {
            byte[] zip = await Updater.DownloadAsync(asset);
            Updater.InstallApp(zip);
        }
        catch (Exception e) { return L.T("Update eșuat: ", "Update failed: ") + e.Message; }
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, Arguments = "--restarted --show" };
        try { Process.Start(psi); }
        catch (Win32Exception e) { return L.T("Instalat, dar nu pot reporni: ", "Installed, but can't restart: ") + e.Message + L.T(". Pornește DeskBuddy din nou.", ". Start DeskBuddy again."); }
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
        if (rel?.Firmware is not Asset asset || DeviceUrl is not string url) return (DeviceFirmware.Result.Failed, L.T("Placa sau release-ul lipsesc.", "The board or the release is missing."));
        bool priv = UsePrivateFirmware;
        try
        {
            if (firmwareCache == null || firmwareCacheVersion != rel.Version || firmwareCachePrivate != priv)
            {
                status.Report(L.T("Descarc firmware-ul din GitHub...", "Downloading the firmware from GitHub..."));
                firmwareCache = await Updater.DownloadAsync(asset, priv ? Settings.FirmwareToken : null);
                firmwareCacheVersion = rel.Version;
                firmwareCachePrivate = priv;
            }
        }
        catch (Exception e) { return (DeviceFirmware.Result.Failed, L.T("Nu pot descărca firmware-ul: ", "Can't download the firmware: ") + e.Message); }

        status.Report(L.T($"Urc firmware-ul pe placă ({firmwareCache.Length / 1024} KB, durează ~1 min pe WiFi slab)...", $"Uploading the firmware ({firmwareCache.Length / 1024} KB, ~1 min on weak WiFi)..."));
        var r = await DeviceFirmware.UploadAsync(url, firmwareCache, password);
        if (r.Result != DeviceFirmware.Result.Ok) return r;

        status.Report(L.T("Placa repornește...", "The board is restarting..."));
        string want = rel.Version.ToString(3);
        for (int i = 0; i < 30; i++)                  // până la ~60 s
        {
            await Task.Delay(2000);
            if (Board?.Version == want) return (DeviceFirmware.Result.Ok, L.T($"Gata: placa rulează firmware-ul {want}.", $"Done: the board runs firmware {want}."));
        }
        return (DeviceFirmware.Result.Ok, L.T("Firmware urcat, dar placa n-a răspuns încă după repornire. Verifică peste un minut.", "Firmware uploaded, but the board hasn't answered after the restart yet. Check again in a minute."));
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
