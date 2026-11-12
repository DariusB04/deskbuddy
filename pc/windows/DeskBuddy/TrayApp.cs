using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DeskBuddy;

/// Nucleul aplicației: o dată pe secundă citește senzorii și îi trimite la DeskBuddy, la 30 s retrimite usage-ul Claude
/// și caută placa în rețea. Ține iconița din tray și fereastra (MainForm), pe care o creează la prima deschidere.
sealed class TrayApp : ApplicationContext
{
    enum Health { Starting, Ok, NoDevice, Error }

    public AppSettings Settings { get; } = AppSettings.Load();
    public Stats? Latest { get; private set; }                     // ultima citire (pe thread-ul UI)
    public bool Quitting { get; private set; }
    public IReadOnlyList<IPAddress> Targets => udp.Targets;
    public Icon WindowIcon => (Icon)(currentIcon ?? MakeIcon(Color.Gray)).Clone();
    /// pe thread-ul UI, după fiecare citire (o dată pe secundă)
    public event Action? Updated;

    /// IP-ul plăcii: cel setat de mână sau cel găsit prin deskbuddy.local
    public IPAddress? Device => Settings.HostIp ?? resolved;
    public string? DeviceUrl => Device is { } ip ? $"http://{ip}/" : null;

    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new();
    readonly ToolStripMenuItem miStats = new() { Enabled = false };
    readonly WindowsFormsSynchronizationContext ui;
    readonly UdpSender udp = new();
    readonly System.Threading.Timer timer;
    readonly EventWaitHandle? showSignal;
    MainForm? form;
    Sensors? sensors;
    int busy;
    long usageAt = -60_000, deviceAt = -60_000;     // „demult”: prima bifă le face imediat
    volatile IPAddress? resolved;
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
    public DeviceFirmware.Board? Board { get; private set; }
    public string? BoardVersion => Board?.Version;
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
        udp.ResetTargets();
        deviceAt = -60_000;                           // caută din nou acum
    }

    public Task SendClaudeTestAsync() => Task.Run(() => ClaudeBridge.Test(udp));

    // ------------------------------------------------------------ update-uri
    async Task PeriodicChecksAsync()
    {
        if (Quitting) return;
        if (DateTime.Now >= nextFirmwareCheck)
        {
            nextFirmwareCheck = DateTime.Now.AddMinutes(10);
            await RefreshDeviceFirmwareAsync();
        }
        if (Settings.AutoUpdateCheck && DateTime.Now >= nextUpdateCheck)
        {
            nextUpdateCheck = DateTime.Now.AddHours(24);
            await CheckUpdatesAsync(manual: false);
        }
    }

    public async Task RefreshDeviceFirmwareAsync()
    {
        Board = DeviceUrl is { } url ? await DeviceFirmware.GetVersionAsync(url) : null;
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
        if (DeviceUrl != null) await RefreshDeviceFirmwareAsync();
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
            Board = await DeviceFirmware.GetVersionAsync(url);
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
        udp.Dispose();
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
            udp.Send(s.ToPacket());
            long now = Environment.TickCount64;
            if (now - usageAt > 30_000)
            {
                usageAt = now;
                if (ClaudeBridge.CachedUsagePacket() is byte[] cu) udp.Send(cu);
            }
            if (now - deviceAt > 30_000)
            {
                deviceAt = now;
                _ = ResolveDeviceAsync();
            }
            ui.Post(_ =>
            {
                if (Quitting) return;
                Latest = s;
                SetHealth(Device != null ? Health.Ok : Health.NoDevice);
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

    async Task ResolveDeviceAsync()
    {
        if (Settings.HostIp != null) return;          // IP setat de mână
        try
        {
            var lookup = Dns.GetHostAddressesAsync("deskbuddy.local");
            if (await Task.WhenAny(lookup, Task.Delay(4000)) == lookup)
                resolved = lookup.Result.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            else resolved = null;
        }
        catch (Exception) { resolved = null; }
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
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
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
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
