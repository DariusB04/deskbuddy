using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace DeskBuddy;

/// Pagină cu titlu + carduri una sub alta, cu scroll. Cardurile au lățime maximă, ca textul să nu se întindă pe tot ecranul.
abstract class Page : Panel
{
    readonly FlowLayoutPanel stack = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Theme.Bg, Location = new Point(32, 24), Margin = new Padding(0),
    };

    protected Page(string title, string subtitle)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        AutoScroll = true;
        stack.Controls.Add(Theme.Label(title, Theme.Title));
        var sub = Theme.Label(subtitle, Theme.Body, Theme.Muted);
        sub.Margin = new Padding(0, 0, 0, 18);
        stack.Controls.Add(sub);
        Controls.Add(stack);
        Resize += (_, _) => Fit();
    }

    protected Card AddCard(string title)
    {
        var c = new Card(title);
        stack.Controls.Add(c);
        return c;
    }

    protected static Label Note(string text) => Theme.Label(text, Theme.Small, Theme.Muted);

    void Fit()
    {
        int w = Math.Clamp(ClientSize.Width - 64, 320, 780);
        foreach (Control c in stack.Controls)
        {
            if (c is Card card) card.FitWidth(w);
            else if (c is Label l) l.MaximumSize = new Size(w, 0);
        }
    }

    public virtual void OnShown() { Fit(); RefreshData(); }
    public virtual void RefreshData() { }

    protected static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
}

// ================================================================ PC
sealed class PcPage : Page
{
    readonly TrayApp app;
    readonly MetricBar cpu = new("CPU"), gpu = new("GPU"), ram = new("RAM");
    readonly Label game = Theme.Label("");
    readonly Label device = Theme.Label("");
    readonly Label targets = Note("");
    readonly TextBox host = Theme.TextBox();
    readonly Label hostError = Theme.Label("", Theme.Small, Theme.Bad);
    readonly Label tempState = Theme.Label("");
    readonly Button tempFix = Theme.Button("", primary: true);
    readonly ComboBox link = Theme.Combo(330);
    static readonly (LinkMode Mode, string Text)[] LinkModes =
    [
        (LinkMode.Auto, "Auto: USB dacă e conectată prin cablu, altfel WiFi"),
        (LinkMode.Usb, "Doar USB"),
        (LinkMode.Wifi, "Doar WiFi"),
    ];

    public PcPage(TrayApp app) : base("PC", "Ce trimite PC-ul la DeskBuddy, o dată pe secundă (ecranul PC de pe placă).")
    {
        this.app = app;
        var now = AddCard("Acum");
        now.Controls.AddRange([cpu, gpu, ram, game]);

        var conn = AddCard("Conexiune");
        foreach (var (_, text) in LinkModes) link.Items.Add(text);
        link.SelectedIndex = Math.Max(0, Array.FindIndex(LinkModes, m => m.Mode == app.Settings.Link));
        link.SelectedIndexChanged += (_, _) =>
        {
            app.SetLinkMode(LinkModes[link.SelectedIndex].Mode);
            RefreshData();
        };
        conn.Controls.Add(link);
        conn.Controls.Add(device);
        conn.Controls.Add(targets);
        conn.Controls.Add(Note("Pe USB merg ecranele PC, CLAUDE și CEAS (ora vine de la PC) și fără WiFi; VREME și IMPRIMANTA cer WiFi. " +
                               "Cât timp aplicația folosește portul USB, Serial Monitor din Arduino nu se poate conecta: alege „Doar WiFi”."));
        conn.Controls.Add(Note("IP-ul plăcii: gol = o găsim singuri (broadcast și semnalele trimise de placă). " +
                               "Scrie-l doar dacă n-o găsim. Îl vezi pe ecranul PC al plăcii."));
        host.Width = 220;
        host.PlaceholderText = "ex. 192.168.1.77";
        host.Text = app.Settings.Host;
        var save = Theme.Button("Salvează");
        save.Click += (_, _) =>
        {
            string h = host.Text.Trim();
            if (h.Length > 0 && !IPAddress.TryParse(h, out _)) { hostError.Text = "Nu e un IP valid (ex. 192.168.1.77)."; hostError.Visible = true; return; }
            hostError.Visible = false;
            app.SetHost(h);
            RefreshData();
        };
        int pad = Math.Max(0, (save.GetPreferredSize(Size.Empty).Height - host.PreferredHeight) / 2);
        host.Margin = new Padding(0, save.Margin.Top + pad, 8, 0);     // câmpul centrat pe înălțimea butonului
        conn.Controls.Add(Theme.Row(host, save));
        hostError.Visible = false;
        conn.Controls.Add(hostError);

                var temp = AddCard("Temperatura CPU");
        temp.Controls.Add(tempState);
        temp.Controls.Add(tempFix);
        tempFix.Click += (_, _) =>
        {
            if (!Autostart.IsAdmin) app.RestartAsAdmin();
            else Open("https://pawnio.eu/");
        };
    }


    public override void RefreshData()
    {
        var s = app.Latest;
        string T(float? v) => v is float f ? $"{f:0}°C" : "--";
        string P(float? v) => v is float f ? $"{f:0}%" : "--";
        if (s == null)
        {
            cpu.Set("se citește...", null, Theme.Muted);
            gpu.Set("", null, Theme.Muted);
            ram.Set("", null, Theme.Muted);
        }
        else
        {
            cpu.Set($"{T(s.CpuT)} · {P(s.CpuL)}", s.CpuL, Theme.TempColor(s.CpuT), "temperatură · load");
            gpu.Set($"{T(s.GpuT)} · {P(s.GpuL)}", s.GpuL, Theme.TempColor(s.GpuT), "temperatură · load");
            string used = s.RamU is float u && s.RamT is float t ? string.Create(CultureInfo.InvariantCulture, $"{u:0.0} / {t:0} GB · ") : "";
            ram.Set(used + P(s.RamL), s.RamL, Theme.Accent);
            game.Text = app.Game is { } g ? $"Joc: {g} · " + (app.Fps is float fp ? $"{fp:0} FPS" : app.FpsError ?? "FPS: -- (jocul nu folosește DirectX?)") : "Niciun joc pornit.";
            game.ForeColor = app.Game != null ? Theme.Fg : Theme.Muted;
        }

        var mode = app.Settings.Link;
        if (app.SendingOverUsb && app.UsbBoard is { } ub)
        {
            device.Text = $"DeskBuddy pe USB: {ub.Port} · firmware {ub.Version}" + (ub.Ip != null ? $" · WiFi {ub.Ip}" : " · fără WiFi");
            device.ForeColor = Theme.Accent;
            targets.Text = "Trimit prin cablul USB.";
        }
        else if (mode == LinkMode.Usb)
        {
            device.Text = "DeskBuddy negăsit pe USB";
            device.ForeColor = Theme.Warn;
            targets.Text = "Conectează placa prin cablu. Pe WiFi nu trimit nimic în modul ăsta.";
        }
        else
        {
            if (app.WifiBoard is { } wb && app.WifiEndpoint is { } ep)
            {
                device.Text = $"DeskBuddy pe WiFi: {wb.Ip} · firmware {wb.Version}" + (app.WifiDirect ? "" : " · prin router");
                device.ForeColor = Theme.Accent;
                targets.Text = $"Trimit direct la placă: {ep}";
            }
            else
            {
                device.Text = "DeskBuddy negăsit pe WiFi (trimit oricum broadcast)";
                device.ForeColor = Theme.Warn;
                targets.Text = "Broadcast la: " + (app.Targets.Count > 0 ? string.Join(", ", app.Targets) : "...") + $"  (UDP {UdpSender.Port})";
            }
        }

        if (s != null && s.CpuT != null)
        {
            tempState.Text = "Merge (driverul PawnIO, cu drepturi de administrator).";
            tempState.ForeColor = Theme.Accent;
            tempFix.Visible = false;
        }
        else if (!Autostart.IsAdmin)
        {
            tempState.Text = "Temperatura CPU cere drepturi de administrator. Ca să pornească singură ca admin la fiecare logare, " +
                             "bifează „Pornește cu Windows” în pagina Aplicație.";
            tempState.ForeColor = Theme.Warn;
            tempFix.Text = "Repornește ca administrator";
            tempFix.Visible = true;
        }
        else if (!Autostart.PawnIoInstalled())
        {
            tempState.Text = "Lipsește driverul PawnIO (îl folosește și LibreHardwareMonitor). Sau din terminal: winget install namazso.PawnIO";
            tempState.ForeColor = Theme.Warn;
            tempFix.Text = "Descarcă PawnIO";
            tempFix.Visible = true;
        }
        else
        {
            tempState.Text = s == null ? "se citește..." : "Procesorul nu raportează temperatura prin LibreHardwareMonitor.";
            tempState.ForeColor = Theme.Muted;
            tempFix.Visible = false;
        }
    }
}

// ================================================================ Claude Code
sealed class ClaudePage : Page
{
    readonly TrayApp app;
    readonly CheckBox integration = Theme.Check("Trimite la DeskBuddy ce face Claude Code (hook-uri și status line în settings.json)");
    readonly Label integrationInfo = Note("");
    readonly MetricBar h5 = new("5 ore"), d7 = new("7 zile");
    readonly Label usageInfo = Note("");
    readonly Button test = Theme.Button("Trimite testul", primary: true);
    bool updating;

    public ClaudePage(TrayApp app) : base("Claude Code", "Ecranul CLAUDE de pe placă: ce face Claude Code acum și cât ai folosit din limite.")
    {
        this.app = app;
        var integ = AddCard("Integrare");
        integ.Controls.Add(integration);
        integ.Controls.Add(integrationInfo);
        integration.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            try
            {
                if (integration.Checked) ClaudeBridge.Install();
                else ClaudeBridge.Uninstall();
                integrationInfo.ForeColor = Theme.Muted;
            }
            catch (Exception e)
            {
                integrationInfo.Text = "Nu pot modifica settings.json: " + e.Message;
                integrationInfo.ForeColor = Theme.Bad;
                return;
            }
            RefreshIntegration();
        };

        var usage = AddCard("Usage");
        usage.Controls.AddRange([h5, d7, usageInfo]);
        usage.Controls.Add(Note("Procentele vin doar din status line, iar status line-ul îl rulează doar `claude` pornit în terminal. " +
                                "Aplicația desktop (tab-ul Code) trimite doar starea: lucrează / te așteaptă / gata."));

        var t = AddCard("Test");
        t.Controls.Add(Note("Trimite pe ecranul CLAUDE o secvență de ~16 s: lucrează → Bash → Edit → te așteaptă → gata."));
        t.Controls.Add(test);
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            test.Text = "Se trimite...";
            await app.SendClaudeTestAsync();
            test.Text = "Trimite testul";
            test.Enabled = true;
        };
    }

    public override void OnShown()
    {
        RefreshIntegration();
        base.OnShown();
    }

    void RefreshIntegration()
    {
        updating = true;
        integration.Checked = ClaudeBridge.IsInstalled();
        updating = false;
        integrationInfo.Text = (integration.Checked ? "Instalat în " : "Neinstalat. Fișierul: ") + ClaudeBridge.SettingsFile +
                               (integration.Checked ? ". Repornește sesiunile Claude Code deschise ca să-l preia." : "");
    }

    public override void RefreshData()
    {
        var u = ClaudeBridge.ReadUsage();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static double? AfterReset(double? pct, long resetAt, long now) => pct != null && resetAt > 0 && now >= resetAt ? 0 : pct;
        if (u == null)
        {
            h5.Set("--", null, Theme.Muted);
            d7.Set("--", null, Theme.Muted);
            usageInfo.Text = "Încă nimic: pornește `claude` în terminal și trimite un mesaj.";
            return;
        }
        double? h = AfterReset(u.H, u.Hr, now), w = AfterReset(u.W, u.Wr, now);
        h5.Set(h is double a ? $"{a:0}%" : "--", (float?)h, UsageColor(h), ResetText(u.Hr, now));
        d7.Set(w is double b ? $"{b:0}%" : "--", (float?)w, UsageColor(w), ResetText(u.Wr, now));
        string ago = u.At > 0 ? Ago(now - u.At) : "?";
        usageInfo.Text = $"{(u.Model.Length > 0 ? u.Model + " · " : "")}citit {ago}";
    }

    static Color UsageColor(double? p) => p switch { null => Theme.Muted, < 70 => Theme.Accent, < 90 => Theme.Warn, _ => Theme.Bad };

    static string ResetText(long resetAt, long now)
    {
        if (resetAt <= 0 || now >= resetAt) return "";
        var t = DateTimeOffset.FromUnixTimeSeconds(resetAt).ToLocalTime();
        return resetAt - now < 86400 ? $"reset la {t:HH:mm}" : $"reset {t:dd.MM HH:mm}";
    }

    static string Ago(long s) => s switch
    {
        < 60 => "acum",
        < 3600 => $"acum {s / 60} min",
        < 86400 => $"acum {s / 3600} h",
        _ => $"acum {s / 86400} zile",
    };
}

// ================================================================ Aplicație
sealed class AppPage : Page
{
    readonly TrayApp app;
    readonly CheckBox autostart = Theme.Check("Pornește cu Windows, ca administrator (pentru temperatura CPU; cere o dată acordul UAC)");
    readonly CheckBox minimized = Theme.Check("La pornirea manuală, rămâi doar în tray (fără fereastră)");
    readonly CheckBox closeToTray = Theme.Check("Butonul X ascunde fereastra în tray (DeskBuddy continuă să trimită datele)");
    readonly Label autostartInfo = Note("");
    readonly Label about = Theme.Label("");
    readonly Label appVersion = Theme.Label("");
    readonly Label boardVersion = Theme.Label("");
    readonly Label updateStatus = Note("");
    readonly Button checkNow = Theme.Button("Verifică acum");
    readonly Button updateApp = Theme.Button("Actualizează aplicația", primary: true);
    readonly Button updateBoard = Theme.Button("Urcă firmware-ul pe placă", primary: true);
    readonly Button notes = Theme.Button("Ce e nou");
    readonly CheckBox autoCheck = Theme.Check("Verifică automat (la pornire și o dată pe zi)");
    readonly TextBox privRepo = Theme.TextBox();
    readonly TextBox privToken = Theme.TextBox();
    readonly Label privInfo = Note("");
    readonly ComboBox usbPort = Theme.Combo(200);
    readonly CheckBox usbErase = Theme.Check("Instalare completă: șterge și setările plăcii (WiFi, imprimantă, parolă)");
    readonly Button usbFlash = Theme.Button("Scrie firmware-ul din release", primary: true);
    readonly Button usbFile = Theme.Button("Din fișier...");
    readonly Button usbScan = Theme.Button("Caută din nou");
    readonly Label usbInfo = Note("");
    readonly Label usbStatus = Note("");
    readonly Button usbDriver = Theme.Button("", primary: true);
    (string? Chip, string? DriverUrl) missingDriver;
    List<UsbLink.PortInfo> ports = [];
    bool updating, busy;

    public AppPage(TrayApp app) : base("Aplicație", "Cum pornește, unde stă și cum se actualizează DeskBuddy.")
    {
        this.app = app;
        var upd = AddCard("Actualizări");
        upd.Controls.Add(appVersion);
        upd.Controls.Add(boardVersion);
        upd.Controls.Add(Theme.Row(updateApp, updateBoard, checkNow, notes));
        upd.Controls.Add(updateStatus);
        upd.Controls.Add(autoCheck);
        checkNow.Click += async (_, _) => await Busy("Verific GitHub...", async () => { await app.CheckUpdatesAsync(manual: true); return null; });
        notes.Click += (_, _) => { if (app.Release?.PageUrl is { Length: > 0 } u) Open(u); };
        updateApp.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), $"Instalez DeskBuddy {app.Release?.Version.ToString(3)}? Aplicația se închide și pornește din nou.",
                    "DeskBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await Busy("Descarc aplicația din GitHub...", app.UpdateAppAsync);
        };
        updateBoard.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), $"Urc firmware-ul {app.Release?.Version.ToString(3)} pe placă prin WiFi? Placa repornește, setările rămân.",
                    "DeskBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            string? password = null;
            await Busy("Pregătesc...", async () =>
            {
                var progress = new Progress<string>(s => updateStatus.Text = s);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var (result, message) = await app.UpdateFirmwareAsync(password, progress);
                    if (result != DeviceFirmware.Result.NeedPassword) return message;
                    password = PasswordDialog.Ask(FindForm(), message + " Scrie parola paginii (utilizator admin):");
                    if (password == null) return "Anulat.";
                }
                return "Parolă greșită.";
            });
        };
        autoCheck.CheckedChanged += (_, _) => { if (!updating) { app.Settings.AutoUpdateCheck = autoCheck.Checked; app.Settings.Save(); } };

        var usbCard = AddCard("Firmware prin USB");
        usbCard.Controls.Add(Note("Pentru o placă nouă (prima instalare) sau una fără WiFi. Leagă placa de PC cu un cablu USB de date. " +
                                  "Aplicația o pune singură în modul de programare; dacă nu reușește, ține apăsat butonul BOOT de pe placă."));
        usbCard.Controls.Add(Theme.Row(usbPort, usbScan, usbDriver));
        usbDriver.Visible = false;
        usbDriver.Click += (_, _) => { if (missingDriver.DriverUrl is { } u) Open(u); };
        usbCard.Controls.Add(usbErase);
        usbCard.Controls.Add(Theme.Row(usbFlash, usbFile));
        usbCard.Controls.Add(usbInfo);
        usbCard.Controls.Add(usbStatus);
        usbScan.Click += (_, _) => RefreshPorts();
        usbFlash.Click += async (_, _) => await FlashUsb(null);
        usbFile.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Alege firmware-ul",
                Filter = "Firmware ESP32 (*.bin)|*.bin|Toate fișierele|*.*",
            };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) await FlashUsb(dlg.FileName);
        };

        var priv = AddCard("Firmware din repo privat (opțional)");
        priv.Controls.Add(Note("Dacă placa ta rulează un firmware compilat dintr-un repo GitHub privat, aplicația îl poate actualiza de acolo. " +
                               "Trebuie un token GitHub (fine-grained) cu acces doar de citire (Contents: Read) la repo-ul ăla. " +
                               "Token-ul se salvează criptat (DPAPI), doar pentru userul tău, pe PC-ul ăsta."));
        privRepo.Width = 260;
        privRepo.PlaceholderText = "owner/repo";
        privToken.Width = 260;
        privToken.UseSystemPasswordChar = true;
        var savePriv = Theme.Button("Salvează");
        var clearPriv = Theme.Button("Șterge");
        int pad = Math.Max(0, (savePriv.GetPreferredSize(Size.Empty).Height - privRepo.PreferredHeight) / 2);
        privRepo.Margin = privToken.Margin = new Padding(0, savePriv.Margin.Top + pad, 8, 0);
        priv.Controls.Add(Theme.Row(privRepo, privToken, savePriv, clearPriv));
        priv.Controls.Add(privInfo);
        savePriv.Click += async (_, _) =>
        {
            string repo = privRepo.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) { privInfo.Text = "Repo-ul se scrie owner/nume."; privInfo.ForeColor = Theme.Bad; return; }
            string? token = privToken.Text.Trim().Length > 0 ? privToken.Text.Trim() : null;
            if (token == null && app.Settings.FirmwareTokenProtected.Length == 0) { privInfo.Text = "Lipsește token-ul."; privInfo.ForeColor = Theme.Bad; return; }
            app.SetPrivateFirmware(repo, token);
            privToken.Text = "";
            await Busy("Verific repo-ul privat...", async () => { await app.CheckUpdatesAsync(manual: true); return app.PrivateError; });
            RefreshPrivate();
        };
        clearPriv.Click += (_, _) =>
        {
            app.Settings.FirmwareToken = null;
            app.SetPrivateFirmware("", null);
            privRepo.Text = privToken.Text = "";
            RefreshPrivate();
        };

        var start = AddCard("Pornire");
        start.Controls.AddRange([autostart, autostartInfo, minimized, closeToTray]);
        autostart.CheckedChanged += async (_, _) =>
        {
            if (updating) return;
            bool want = autostart.Checked;
            autostart.Enabled = false;
            autostartInfo.Text = "Aștept acordul UAC...";
            bool ok = await Task.Run(() => Autostart.Set(want));
            autostart.Enabled = true;
            RefreshAutostart(ok ? "" : "Nu s-a putut schimba (UAC refuzat?).");
        };
        minimized.CheckedChanged += (_, _) => { if (!updating) { app.Settings.StartMinimized = minimized.Checked; app.Settings.Save(); } };
        closeToTray.CheckedChanged += (_, _) => { if (!updating) { app.Settings.CloseToTray = closeToTray.Checked; app.Settings.Save(); } };

        var info = AddCard("Despre");
        info.Controls.Add(about);
        var folder = Theme.Button("Deschide folderul");
        folder.Click += (_, _) => Open(Path.GetDirectoryName(Environment.ProcessPath) ?? ".");
        var exit = Theme.Button("Ieșire din DeskBuddy");
        exit.Click += (_, _) => app.Quit();
        info.Controls.Add(Theme.Row(folder, exit));
    }

    void RefreshPorts()
    {
        string? keep = usbPort.SelectedIndex >= 0 && usbPort.SelectedIndex < ports.Count ? ports[usbPort.SelectedIndex].Name : null;
        ports = TrayApp.UsbPorts();
        usbPort.Items.Clear();
        foreach (var p in ports)
            usbPort.Items.Add(app.UsbBoard?.Port == p.Name ? $"{p} · DeskBuddy {app.UsbBoard.Version}" : p.ToString());
        if (ports.Count == 0)
        {
            usbPort.Items.Add("nicio placă pe USB");
            usbPort.SelectedIndex = 0;
            missingDriver = UsbLink.MissingDrivers().FirstOrDefault();
        }
        else missingDriver = default;
        usbDriver.Visible = missingDriver.Chip != null;
        if (missingDriver.Chip != null) usbDriver.Text = $"Descarcă driverul {missingDriver.Chip}";
        else
        {
            int i = ports.FindIndex(p => p.Name == (keep ?? app.UsbBoard?.Port));
            usbPort.SelectedIndex = Math.Max(0, i);
        }
        RefreshUsb();
    }

    void RefreshUsb()
    {
        bool hasPort = ports.Count > 0;
        usbFlash.Enabled = usbFile.Enabled = usbPort.Enabled = usbErase.Enabled = !busy && hasPort;
        usbScan.Enabled = !busy;
        var rel = app.FirmwareRelease;
        string source = app.UsePrivateFirmware ? "repo-ul privat" : "release-ul public";
        if (missingDriver.Chip != null)
        {
            usbInfo.Text = $"Placa e conectată, dar Windows n-are driverul pentru cipul ei USB ({missingDriver.Chip}), deci nu apare niciun port COM. " +
                           "Instalează driverul, apoi apasă „Caută din nou”.";
            usbInfo.ForeColor = Theme.Warn;
        }
        else if (app.PrivateFirmwareMissing) { usbInfo.Text = "Firmware-ul plăcii vine din repo-ul privat: completează-l în cardul de mai jos. Sau alege un .bin cu „Din fișier...”."; usbInfo.ForeColor = Theme.Warn; }
        else if (rel?.Flash != null) { usbInfo.Text = $"Din release: firmware {rel.Version.ToString(3)} din {source}."; usbInfo.ForeColor = Theme.Muted; }
        else { usbInfo.Text = "Încă nu știu ultimul release: apasă „Verifică acum” sau alege un .bin cu „Din fișier...”."; usbInfo.ForeColor = Theme.Muted; }
        usbFlash.Enabled &= rel?.Flash != null;
    }

    async Task FlashUsb(string? file)
    {
        if (busy || ports.Count == 0) return;
        var port = ports[Math.Clamp(usbPort.SelectedIndex, 0, ports.Count - 1)];
        bool full = usbErase.Checked;
        string what = file != null ? Path.GetFileName(file) : $"firmware-ul {app.FirmwareRelease?.Version.ToString(3)}";
        string keep = full ? "Se șterg și setările plăcii (WiFi, imprimantă, parolă): după aceea pornește în SETUP."
                           : "Setările plăcii rămân.";
        if (MessageBox.Show(FindForm(), $"Scriu {what} pe placa de pe {port.Name}? {keep}\nNu scoate cablul cât durează (~30 s).",
                "DeskBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        busy = true;
        RefreshUsb();
        usbStatus.ForeColor = Theme.Muted;
        var progress = new Progress<string>(s => usbStatus.Text = s);
        string result;
        try { result = await app.FlashUsbAsync(port.Name, full ? TrayApp.UsbFlashKind.Full : TrayApp.UsbFlashKind.KeepSettings, file, progress); }
        catch (Exception e) { result = "Eroare: " + e.Message; }
        busy = false;
        if (IsDisposed) return;
        usbStatus.Text = result;
        usbStatus.ForeColor = result.StartsWith("Gata") ? Theme.Accent : Theme.Bad;
        usbErase.Checked = false;
        RefreshPorts();
        RefreshData();
    }

    async Task Busy(string text, Func<Task<string?>> work)
    {
        if (busy) return;
        busy = true;
        updateStatus.Text = text;
        updateStatus.ForeColor = Theme.Muted;
        RefreshData();
        string? result;
        try { result = await work(); }
        catch (Exception e) { result = "Eroare: " + e.Message; }
        busy = false;
        if (IsDisposed) return;
        updateStatus.Text = result ?? "";
        updateStatus.ForeColor = result != null && (result.StartsWith("Gata") || result.StartsWith("Firmware urcat")) ? Theme.Accent
                               : result != null && result != "Anulat." ? Theme.Bad : Theme.Muted;
        RefreshData();
    }

    public override void RefreshData()
    {
        var rel = app.Release;
        string latest = rel?.Version.ToString(3) ?? "?";
        if (Updater.Repo == null)
            appVersion.Text = $"Aplicația: {Updater.CurrentText} (build local, fără verificare de update-uri)";
        else if (app.AppUpdateAvailable)
            appVersion.Text = $"Aplicația: {Updater.CurrentText}  →  {latest} disponibilă";
        else if (rel != null)
            appVersion.Text = $"Aplicația: {Updater.CurrentText} (la zi)";
        else
            appVersion.Text = $"Aplicația: {Updater.CurrentText}" + (app.UpdateError != null ? $" ({app.UpdateError})" : "");
        appVersion.ForeColor = app.AppUpdateAvailable ? Theme.Accent : Theme.Fg;

        var fwRel = app.FirmwareRelease;
        string board = app.BoardVersion switch
        {
            null => app.Device == null ? "negăsită în rețea sau pe USB" : "nu răspunde",
            "" => "firmware vechi (fără număr de versiune)",
            var v => "firmware " + v + (app.Board?.Variant == "full" ? " (din repo-ul privat)" : "") + (app.BoardViaUsb ? ", pe USB" : ""),
        };
        string fwState = app.FirmwareUpdateAvailable ? $"  →  {fwRel!.Version.ToString(3)} disponibil"
            : app.PrivateFirmwareMissing && app.Board?.Variant == "full" ? "  (update-urile vin din repo-ul privat: completează-l mai jos)"
            : fwRel != null && app.BoardVersion is { Length: > 0 } ? " (la zi)" : "";
        boardVersion.Text = $"Placa: {board}{fwState}";
        boardVersion.ForeColor = app.FirmwareUpdateAvailable ? Theme.Accent : Theme.Fg;

        updateApp.Visible = app.AppUpdateAvailable;
        updateBoard.Visible = app.FirmwareUpdateAvailable && app.DeviceUrl != null;     // prin WiFi; fără WiFi: cardul USB
        if (!busy) RefreshUsb();
        notes.Visible = rel != null;
        checkNow.Enabled = updateApp.Enabled = updateBoard.Enabled = !busy && (Updater.Repo != null || app.Settings.HasPrivateFirmware);
        if (!busy && updateStatus.Text.Length == 0 && app.LastUpdateCheck is DateTime t)
            updateStatus.Text = $"Ultima verificare: {t:HH:mm}" + (Updater.Repo != null ? $" · github.com/{Updater.Repo}/releases" : "");
    }

    public override void OnShown()
    {
        updating = true;
        minimized.Checked = app.Settings.StartMinimized;
        closeToTray.Checked = app.Settings.CloseToTray;
        autoCheck.Checked = app.Settings.AutoUpdateCheck;
        updating = false;
        privRepo.Text = app.Settings.FirmwareRepo;
        RefreshPrivate();
        RefreshPorts();
        RefreshAutostart("");
        string ver = typeof(AppPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        about.Text = $"DeskBuddy pentru Windows {ver}\n{Environment.ProcessPath}\n" +
                     (Autostart.IsAdmin ? "Rulează ca administrator." : "Rulează fără drepturi de administrator.");
        base.OnShown();
    }

    void RefreshPrivate()
    {
        bool hasToken = app.Settings.FirmwareTokenProtected.Length > 0;
        privToken.PlaceholderText = hasToken ? "token salvat · lasă gol ca să-l păstrezi" : "token GitHub (github_pat_...)";
        if (app.PrivateError != null) { privInfo.Text = app.PrivateError; privInfo.ForeColor = Theme.Bad; }
        else if (app.PrivateRelease is { } r) { privInfo.Text = $"Merge: ultimul release privat e {r.Version.ToString(3)}."; privInfo.ForeColor = Theme.Accent; }
        else { privInfo.Text = app.Settings.HasPrivateFirmware ? "Salvat. Apasă „Verifică acum”." : "Neconfigurat."; privInfo.ForeColor = Theme.Muted; }
    }

    void RefreshAutostart(string error)
    {
        updating = true;
        autostart.Checked = Autostart.IsEnabled();
        updating = false;
        autostartInfo.Text = error.Length > 0 ? error
            : autostart.Checked ? "Task programat „DeskBuddy”: pornește la logare, în tray, fără prompt UAC."
            : "Dacă muți DeskBuddy.exe, bifează din nou.";
        autostartInfo.ForeColor = error.Length > 0 ? Theme.Bad : Theme.Muted;
    }
}
