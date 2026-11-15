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

    // cât se construiește pagina, aranjarea e suspendată: altfel fiecare control adăugat rearanja tot (O(n²))
    bool building = true;
    readonly List<Card> buildCards = [];

    protected Page(string title, string subtitle)
    {
        SuspendLayout();
        stack.SuspendLayout();
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        AutoScroll = true;
        DoubleBuffered = true;
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
        if (building) { c.SuspendLayout(); buildCards.Add(c); }
        stack.Controls.Add(c);
        fitWidth = -1;                                    // card nou (ex. temperatura CPU, apărut mai târziu): aranjare din nou
        return c;
    }

    protected static Label Note(string text) => Theme.Label(text, Theme.Small, Theme.Muted);

    /// un rând oarecare între carduri (ex. bara de secțiuni a paginii Placă)
    protected void AddRow(Control c) => stack.Controls.Add(c);

    int fitWidth = -1;

    /// lățimea cardurilor după fereastră. Fiecare MaximumSize schimbat reface aranjarea paginii, deci totul se face
    /// cu aranjarea suspendată (o singură trecere la final) și doar când lățimea s-a schimbat
    protected void Fit(bool force = false)
    {
        int w = Math.Clamp(ClientSize.Width - 64, 320, 780);
        if (w == fitWidth && !force) return;
        fitWidth = w;
        SuspendLayout();
        stack.SuspendLayout();
        var cards = new List<Card>();
        foreach (Control c in stack.Controls)
        {
            if (c is Card card) { card.SuspendLayout(); card.FitWidth(w); cards.Add(card); }
            else if (c is Label l) l.MaximumSize = new Size(w, 0);
        }
        foreach (var card in cards) card.ResumeLayout(false);
        stack.ResumeLayout(true);
        ResumeLayout(true);
    }

    public virtual void OnShown() { EndBuild(); Fit(); RefreshData(); }

    /// prima afișare: aranjarea suspendată la construcție se face acum, o singură dată
    void EndBuild()
    {
        if (!building) return;
        building = false;
        foreach (var c in buildCards) c.ResumeLayout(false);
        buildCards.Clear();
        stack.ResumeLayout(false);
        ResumeLayout(false);
        fitWidth = -1;
    }
    public void FitIfNeeded() => Fit();
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
    static (LinkMode Mode, string Text)[] LinkModes =>
    [
        (LinkMode.Auto, L.T("Auto: USB dacă e conectată prin cablu, altfel WiFi", "Auto: USB when connected by cable, otherwise WiFi")),
        (LinkMode.Usb, L.T("Doar USB", "USB only")),
        (LinkMode.Wifi, L.T("Doar WiFi", "WiFi only")),
    ];

    public PcPage(TrayApp app) : base("PC", L.T("Ce trimite PC-ul la DeskBuddy, o dată pe secundă (ecranul PC de pe placă).", "What the PC sends to DeskBuddy, once a second (the PC screen on the board)."))
    {
        this.app = app;
        var now = AddCard(L.T("Acum", "Now"));
        now.Controls.AddRange([cpu, gpu, ram, game]);

        var conn = AddCard(L.T("Conexiune", "Connection"));
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
        conn.Controls.Add(Note(L.T("Pe USB merg ecranele PC, CLAUDE și CEAS (ora vine de la PC) și fără WiFi; VREME și IMPRIMANTA cer WiFi. ", "Over USB the PC, CLAUDE and CLOCK screens work without WiFi (the time comes from the PC); WEATHER and PRINTER need WiFi. ") +
                               L.T("Cât timp aplicația folosește portul USB, Serial Monitor din Arduino nu se poate conecta: alege „Doar WiFi”.", "While the app uses the USB port, the Arduino Serial Monitor cannot connect: choose “WiFi only”.")));
        conn.Controls.Add(Note(L.T("IP-ul plăcii: gol = o găsim singuri (broadcast și semnalele trimise de placă). ", "Board IP: empty = we find it ourselves (broadcast and the signals the board sends). ") +
                               L.T("Scrie-l doar dacă n-o găsim. Îl vezi pe ecranul PC al plăcii.", "Only fill it in if we can't find it. It's shown on the board's PC screen.")));
        host.Width = 220;
        host.PlaceholderText = "ex. 192.168.1.77";
        host.Text = app.Settings.Host;
        var save = Theme.Button(L.T("Salvează", "Save"));
        save.Click += (_, _) =>
        {
            string h = host.Text.Trim();
            if (h.Length > 0 && !IPAddress.TryParse(h, out _)) { hostError.Text = L.T("Nu e un IP valid (ex. 192.168.1.77).", "Not a valid IP (e.g. 192.168.1.77)."); hostError.Visible = true; return; }
            hostError.Visible = false;
            app.SetHost(h);
            RefreshData();
        };
        int pad = Math.Max(0, (save.GetPreferredSize(Size.Empty).Height - host.PreferredHeight) / 2);
        host.Margin = new Padding(0, save.Margin.Top + pad, 8, 0);     // câmpul centrat pe înălțimea butonului
        conn.Controls.Add(Theme.Row(host, save));
        hostError.Visible = false;
        conn.Controls.Add(hostError);

                var temp = AddCard(L.T("Temperatura CPU", "CPU temperature"));
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
            cpu.Set(L.T("se citește...", "reading..."), null, Theme.Muted);
            gpu.Set("", null, Theme.Muted);
            ram.Set("", null, Theme.Muted);
        }
        else
        {
            cpu.Set($"{T(s.CpuT)} · {P(s.CpuL)}", s.CpuL, Theme.TempColor(s.CpuT), L.T("temperatură · load", "temperature · load"));
            gpu.Set($"{T(s.GpuT)} · {P(s.GpuL)}", s.GpuL, Theme.TempColor(s.GpuT), L.T("temperatură · load", "temperature · load"));
            string used = s.RamU is float u && s.RamT is float t ? string.Create(CultureInfo.InvariantCulture, $"{u:0.0} / {t:0} GB · ") : "";
            ram.Set(used + P(s.RamL), s.RamL, Theme.Accent);
            game.Text = app.Game is { } g ? L.T($"Joc: {g} · ", $"Game: {g} · ") + (app.Fps is float fp ? $"{fp:0} FPS" : app.FpsError ?? L.T("FPS: -- (jocul nu folosește DirectX?)", "FPS: -- (the game doesn't use DirectX?)")) : L.T("Niciun joc pornit.", "No game running.");
            game.ForeColor = app.Game != null ? Theme.Fg : Theme.Muted;
        }

        var mode = app.Settings.Link;
        if (app.SendingOverUsb && app.UsbBoard is { } ub)
        {
            device.Text = L.T($"DeskBuddy pe USB: {ub.Port} · firmware {ub.Version}", $"DeskBuddy on USB: {ub.Port} · firmware {ub.Version}") + (ub.Ip != null ? $" · WiFi {ub.Ip}" : L.T(" · fără WiFi", " · no WiFi"));
            device.ForeColor = Theme.Accent;
            targets.Text = L.T("Trimit prin cablul USB.", "Sending over the USB cable.");
        }
        else if (mode == LinkMode.Usb)
        {
            device.Text = L.T("DeskBuddy negăsit pe USB", "DeskBuddy not found on USB");
            device.ForeColor = Theme.Warn;
            targets.Text = L.T("Conectează placa prin cablu. Pe WiFi nu trimit nimic în modul ăsta.", "Connect the board with a cable. Nothing is sent over WiFi in this mode.");
        }
        else
        {
            if (app.WifiBoard is { } wb && app.WifiEndpoint is { } ep)
            {
                device.Text = L.T($"DeskBuddy pe WiFi: {wb.Ip} · firmware {wb.Version}", $"DeskBuddy on WiFi: {wb.Ip} · firmware {wb.Version}") + (app.WifiDirect ? "" : L.T(" · prin router", " · through a router"));
                device.ForeColor = Theme.Accent;
                targets.Text = L.T($"Trimit direct la placă: {ep}", $"Sending straight to the board: {ep}");
            }
            else
            {
                device.Text = L.T("DeskBuddy negăsit pe WiFi (trimit oricum broadcast)", "DeskBuddy not found on WiFi (broadcasting anyway)");
                device.ForeColor = Theme.Warn;
                targets.Text = L.T("Broadcast la: ", "Broadcast to: ") + (app.Targets.Count > 0 ? string.Join(", ", app.Targets) : "...") + $"  (UDP {UdpSender.Port})";
            }
        }

        if (s != null && s.CpuT != null)
        {
            tempState.Text = L.T("Merge (driverul PawnIO, cu drepturi de administrator).", "Working (PawnIO driver, with administrator rights).");
            tempState.ForeColor = Theme.Accent;
            tempFix.Visible = false;
        }
        else if (!Autostart.IsAdmin)
        {
            tempState.Text = L.T("Temperatura CPU cere drepturi de administrator. Ca să pornească singură ca admin la fiecare logare, ", "The CPU temperature needs administrator rights. To start as admin automatically at every sign-in, ") +
                             L.T("bifează „Pornește cu Windows” în pagina Aplicație.", "tick “Start with Windows” on the App page.");
            tempState.ForeColor = Theme.Warn;
            tempFix.Text = L.T("Repornește ca administrator", "Restart as administrator");
            tempFix.Visible = true;
        }
        else if (!Autostart.PawnIoInstalled())
        {
            tempState.Text = L.T("Lipsește driverul PawnIO (îl folosește și LibreHardwareMonitor). Sau din terminal: winget install namazso.PawnIO", "The PawnIO driver is missing (LibreHardwareMonitor uses it too). Or from a terminal: winget install namazso.PawnIO");
            tempState.ForeColor = Theme.Warn;
            tempFix.Text = L.T("Descarcă PawnIO", "Download PawnIO");
            tempFix.Visible = true;
        }
        else
        {
            tempState.Text = s == null ? L.T("se citește...", "reading...") : L.T("Procesorul nu raportează temperatura prin LibreHardwareMonitor.", "The processor doesn't report its temperature through LibreHardwareMonitor.");
            tempState.ForeColor = Theme.Muted;
            tempFix.Visible = false;
        }
    }
}

// ================================================================ Claude Code
sealed class ClaudePage : Page
{
    readonly TrayApp app;
    readonly CheckBox integration = Theme.Check(L.T("Trimite la DeskBuddy ce face Claude Code (hook-uri și status line în settings.json)", "Send what Claude Code is doing to DeskBuddy (hooks and status line in settings.json)"));
    readonly Label integrationInfo = Note("");
    readonly MetricBar h5 = new(L.T("5 ore", "5 hours")), d7 = new(L.T("7 zile", "7 days"));
    readonly Label usageInfo = Note("");
    readonly Button test = Theme.Button(L.T("Trimite testul", "Send the test"), primary: true);
    bool updating;

    public ClaudePage(TrayApp app) : base("Claude Code", L.T("Ecranul CLAUDE de pe placă: ce face Claude Code acum și cât ai folosit din limite.", "The CLAUDE screen on the board: what Claude Code is doing now and how much of your limits you've used."))
    {
        this.app = app;
        var integ = AddCard(L.T("Integrare", "Integration"));
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
                integrationInfo.Text = L.T("Nu pot modifica settings.json: ", "Can't modify settings.json: ") + e.Message;
                integrationInfo.ForeColor = Theme.Bad;
                return;
            }
            RefreshIntegration();
        };

        var usage = AddCard("Usage");
        usage.Controls.AddRange([h5, d7, usageInfo]);
        usage.Controls.Add(Note(L.T("Procentele vin doar din status line, iar status line-ul îl rulează doar `claude` pornit în terminal. ", "The percentages only come from the status line, and only `claude` started in a terminal runs the status line. ") +
                                L.T("Aplicația desktop (tab-ul Code) trimite doar starea: lucrează / te așteaptă / gata.", "The desktop app (Code tab) only sends the status: working / waiting for you / done.")));

        var t = AddCard("Test");
        t.Controls.Add(Note(L.T("Trimite pe ecranul CLAUDE o secvență de ~16 s: lucrează → Bash → Edit → te așteaptă → gata.", "Sends a ~16 s sequence to the CLAUDE screen: working → Bash → Edit → waiting for you → done.")));
        t.Controls.Add(test);
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            test.Text = L.T("Se trimite...", "Sending...");
            await app.SendClaudeTestAsync();
            test.Text = L.T("Trimite testul", "Send the test");
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
        integrationInfo.Text = (integration.Checked ? L.T("Instalat în ", "Installed in ") : L.T("Neinstalat. Fișierul: ", "Not installed. File: ")) + ClaudeBridge.SettingsFile +
                               (integration.Checked ? L.T(". Repornește sesiunile Claude Code deschise ca să-l preia.", ". Restart open Claude Code sessions so they pick it up.") : "");
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
            usageInfo.Text = L.T("Încă nimic: pornește `claude` în terminal și trimite un mesaj.", "Nothing yet: start `claude` in a terminal and send a message.");
            return;
        }
        double? h = AfterReset(u.H, u.Hr, now), w = AfterReset(u.W, u.Wr, now);
        h5.Set(h is double a ? $"{a:0}%" : "--", (float?)h, UsageColor(h), ResetText(u.Hr, now));
        d7.Set(w is double b ? $"{b:0}%" : "--", (float?)w, UsageColor(w), ResetText(u.Wr, now));
        string ago = u.At > 0 ? Ago(now - u.At) : "?";
        usageInfo.Text = $"{(u.Model.Length > 0 ? u.Model + " · " : "")}{L.T("citit ", "read ")}{ago}";
    }

    static Color UsageColor(double? p) => p switch { null => Theme.Muted, < 70 => Theme.Accent, < 90 => Theme.Warn, _ => Theme.Bad };

    static string ResetText(long resetAt, long now)
    {
        if (resetAt <= 0 || now >= resetAt) return "";
        var t = DateTimeOffset.FromUnixTimeSeconds(resetAt).ToLocalTime();
        return resetAt - now < 86400 ? L.T($"reset la {t:HH:mm}", $"resets at {t:HH:mm}") : L.T($"reset {t:dd.MM HH:mm}", $"resets {t:dd.MM HH:mm}");
    }

    static string Ago(long s) => s switch
    {
        < 60 => L.T("acum", "just now"),
        < 3600 => L.T($"acum {s / 60} min", $"{s / 60} min ago"),
        < 86400 => L.T($"acum {s / 3600} h", $"{s / 3600} h ago"),
        _ => L.T($"acum {s / 86400} zile", $"{s / 86400} days ago"),
    };
}

// ================================================================ Aplicație
sealed class AppPage : Page
{
    readonly TrayApp app;
    readonly CheckBox autostart = Theme.Check(L.T("Pornește cu Windows, ca administrator (pentru temperatura CPU; cere o dată acordul UAC)", "Start with Windows, as administrator (for the CPU temperature; asks for UAC consent once)"));
    readonly CheckBox minimized = Theme.Check(L.T("La pornirea manuală, rămâi doar în tray (fără fereastră)", "When started manually, stay in the tray (no window)"));
    readonly CheckBox closeToTray = Theme.Check(L.T("Butonul X ascunde fereastra în tray (DeskBuddy continuă să trimită datele)", "The X button hides the window to the tray (DeskBuddy keeps sending data)"));
    readonly Label autostartInfo = Note("");
    readonly Label about = Theme.Label("");
    readonly Label appVersion = Theme.Label("");
    readonly Label boardVersion = Theme.Label("");
    readonly Label updateStatus = Note("");
    readonly Button checkNow = Theme.Button(L.T("Verifică acum", "Check now"));
    readonly Button updateApp = Theme.Button(L.T("Actualizează aplicația", "Update the app"), primary: true);
    readonly Button updateBoard = Theme.Button(L.T("Urcă firmware-ul pe placă", "Upload firmware to the board"), primary: true);
    readonly Button notes = Theme.Button(L.T("Ce e nou", "What's new"));
    readonly CheckBox autoCheck = Theme.Check(L.T("Verifică automat (la pornire și o dată pe zi)", "Check automatically (at startup and once a day)"));
    readonly TextBox privRepo = Theme.TextBox();
    readonly TextBox privToken = Theme.TextBox();
    readonly Label privInfo = Note("");
    readonly ComboBox usbPort = Theme.Combo(200);
    readonly ComboBox language = Theme.Combo(200);
    readonly CheckBox usbErase = Theme.Check(L.T("Instalare completă: șterge și setările plăcii (WiFi, imprimantă, parolă)", "Full install: also erase the board's settings (WiFi, printer, password)"));
    readonly Button usbFlash = Theme.Button(L.T("Scrie firmware-ul din release", "Write the firmware from the release"), primary: true);
    readonly Button usbFile = Theme.Button(L.T("Din fișier...", "From file..."));
    readonly Button usbScan = Theme.Button(L.T("Caută din nou", "Search again"));
    readonly Label usbInfo = Note("");
    readonly Label usbStatus = Note("");
    readonly Button usbDriver = Theme.Button("", primary: true);
    (string? Chip, string? DriverUrl) missingDriver;
    List<UsbLink.PortInfo> ports = [];
    bool updating, busy;

    public AppPage(TrayApp app) : base(L.T("Aplicație", "App"), L.T("Cum pornește, unde stă și cum se actualizează DeskBuddy.", "How DeskBuddy starts, where it lives and how it updates."))
    {
        this.app = app;
        var lang = AddCard(L.T("Limba / Language", "Language / Limba"));
        language.Items.AddRange(["Română", "English"]);
        language.SelectedIndex = L.En ? 1 : 0;
        language.SelectedIndexChanged += (_, _) =>
        {
            string code = language.SelectedIndex == 1 ? "en" : "ro";
            if (code != app.Settings.LanguageCode) BeginInvoke(() => app.SetLanguage(code));
        };
        lang.Controls.Add(language);
        lang.Controls.Add(Note(L.T("Se aplică aplicației și plăcii (textele de pe ecran și pagina ei web).",
                                   "Applies to the app and the board (its screen texts and web page).")));

        var upd = AddCard(L.T("Actualizări", "Updates"));
        upd.Controls.Add(appVersion);
        upd.Controls.Add(boardVersion);
        upd.Controls.Add(Theme.Row(updateApp, updateBoard, checkNow, notes));
        upd.Controls.Add(updateStatus);
        upd.Controls.Add(autoCheck);
        checkNow.Click += async (_, _) => await Busy(L.T("Verific GitHub...", "Checking GitHub..."), async () => { await app.CheckUpdatesAsync(manual: true); return null; });
        notes.Click += (_, _) => { if (app.Release?.PageUrl is { Length: > 0 } u) Open(u); };
        updateApp.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), L.T($"Instalez DeskBuddy {app.Release?.Version.ToString(3)}? Aplicația se închide și pornește din nou.", $"Install DeskBuddy {app.Release?.Version.ToString(3)}? The app will close and start again."),
                    "DeskBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await Busy(L.T("Descarc aplicația din GitHub...", "Downloading the app from GitHub..."), app.UpdateAppAsync);
        };
        updateBoard.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), L.T($"Urc firmware-ul {app.Release?.Version.ToString(3)} pe placă prin WiFi? Placa repornește, setările rămân.", $"Upload firmware {app.Release?.Version.ToString(3)} to the board over WiFi? The board restarts, settings are kept."),
                    "DeskBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            string? password = null;
            await Busy(L.T("Pregătesc...", "Preparing..."), async () =>
            {
                var progress = new Progress<string>(s => updateStatus.Text = s);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var (result, message) = await app.UpdateFirmwareAsync(password, progress);
                    if (result != DeviceFirmware.Result.NeedPassword) return message;
                    password = PasswordDialog.Ask(FindForm(), message + L.T(" Scrie parola paginii (utilizator admin):", " Enter the page password (user admin):"));
                    if (password == null) return L.Cancelled;
                }
                return L.T("Parolă greșită.", "Wrong password.");
            });
        };
        autoCheck.CheckedChanged += (_, _) => { if (!updating) { app.Settings.AutoUpdateCheck = autoCheck.Checked; app.Settings.Save(); } };

        var usbCard = AddCard(L.T("Firmware prin USB", "Firmware over USB"));
        usbCard.Controls.Add(Note(L.T("Pentru o placă nouă (prima instalare) sau una fără WiFi. Leagă placa de PC cu un cablu USB de date. ", "For a new board (first install) or one without WiFi. Connect the board to the PC with a USB data cable. ") +
                                  L.T("Aplicația o pune singură în modul de programare; dacă nu reușește, ține apăsat butonul BOOT de pe placă.", "The app puts it into programming mode by itself; if that fails, hold the BOOT button on the board.")));
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
                Title = L.T("Alege firmware-ul", "Choose the firmware"),
                Filter = L.T("Firmware ESP32 (*.bin)|*.bin|Toate fișierele|*.*", "ESP32 firmware (*.bin)|*.bin|All files|*.*"),
            };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) await FlashUsb(dlg.FileName);
        };

        var priv = AddCard(L.T("Firmware din repo privat (opțional)", "Firmware from a private repo (optional)"));
        priv.Controls.Add(Note(L.T("Dacă placa ta rulează un firmware compilat dintr-un repo GitHub privat, aplicația îl poate actualiza de acolo. ", "If your board runs firmware built from a private GitHub repo, the app can update it from there. ") +
                               L.T("Trebuie un token GitHub (fine-grained) cu acces doar de citire (Contents: Read) la repo-ul ăla. ", "You need a (fine-grained) GitHub token with read-only access (Contents: Read) to that repo. ") +
                               L.T("Token-ul se salvează criptat (DPAPI), doar pentru userul tău, pe PC-ul ăsta.", "The token is saved encrypted (DPAPI), only for your user, on this PC.")));
        privRepo.Width = 260;
        privRepo.PlaceholderText = "owner/repo";
        privToken.Width = 260;
        privToken.UseSystemPasswordChar = true;
        var savePriv = Theme.Button(L.T("Salvează", "Save"));
        var clearPriv = Theme.Button(L.T("Șterge", "Remove"));
        int pad = Math.Max(0, (savePriv.GetPreferredSize(Size.Empty).Height - privRepo.PreferredHeight) / 2);
        privRepo.Margin = privToken.Margin = new Padding(0, savePriv.Margin.Top + pad, 8, 0);
        priv.Controls.Add(Theme.Row(privRepo, privToken, savePriv, clearPriv));
        priv.Controls.Add(privInfo);
        savePriv.Click += async (_, _) =>
        {
            string repo = privRepo.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) { privInfo.Text = L.T("Repo-ul se scrie owner/nume.", "Write the repo as owner/name."); privInfo.ForeColor = Theme.Bad; return; }
            string? token = privToken.Text.Trim().Length > 0 ? privToken.Text.Trim() : null;
            if (token == null && app.Settings.FirmwareTokenProtected.Length == 0) { privInfo.Text = L.T("Lipsește token-ul.", "The token is missing."); privInfo.ForeColor = Theme.Bad; return; }
            app.SetPrivateFirmware(repo, token);
            privToken.Text = "";
            await Busy(L.T("Verific repo-ul privat...", "Checking the private repo..."), async () => { await app.CheckUpdatesAsync(manual: true); return app.PrivateError; });
            RefreshPrivate();
        };
        clearPriv.Click += (_, _) =>
        {
            app.Settings.FirmwareToken = null;
            app.SetPrivateFirmware("", null);
            privRepo.Text = privToken.Text = "";
            RefreshPrivate();
        };

        var start = AddCard(L.T("Pornire", "Startup"));
        start.Controls.AddRange([autostart, autostartInfo, minimized, closeToTray]);
        autostart.CheckedChanged += async (_, _) =>
        {
            if (updating) return;
            bool want = autostart.Checked;
            autostart.Enabled = false;
            autostartInfo.Text = L.T("Aștept acordul UAC...", "Waiting for UAC consent...");
            bool ok = await Task.Run(() => Autostart.Set(want));
            autostart.Enabled = true;
            RefreshAutostart(ok ? "" : L.T("Nu s-a putut schimba (UAC refuzat?).", "Couldn't change it (UAC declined?)."));
        };
        minimized.CheckedChanged += (_, _) => { if (!updating) { app.Settings.StartMinimized = minimized.Checked; app.Settings.Save(); } };
        closeToTray.CheckedChanged += (_, _) => { if (!updating) { app.Settings.CloseToTray = closeToTray.Checked; app.Settings.Save(); } };

        var info = AddCard(L.T("Despre", "About"));
        info.Controls.Add(about);
        var folder = Theme.Button(L.T("Deschide folderul", "Open the folder"));
        folder.Click += (_, _) => Open(Path.GetDirectoryName(Environment.ProcessPath) ?? ".");
        var exit = Theme.Button(L.T("Ieșire din DeskBuddy", "Exit DeskBuddy"));
        exit.Click += (_, _) => app.Quit();
        info.Controls.Add(Theme.Row(folder, exit));
    }

    /// porturile (registry) și driverele lipsă se citesc pe fundal: pe un PC slab țineau pagina blocată
    async Task RefreshPortsAsync()
    {
        var (found, missing) = await Task.Run(() =>
        {
            var l = TrayApp.UsbPorts();
            return (l, l.Count == 0 ? UsbLink.MissingDrivers().FirstOrDefault() : default);
        });
        if (IsDisposed) return;
        ApplyPorts(found, missing);
    }

    void RefreshPorts() => _ = RefreshPortsAsync();

    void ApplyPorts(List<UsbLink.PortInfo> found, (string? Chip, string? DriverUrl) missing)
    {
        string? keep = usbPort.SelectedIndex >= 0 && usbPort.SelectedIndex < ports.Count ? ports[usbPort.SelectedIndex].Name : null;
        ports = found;
        usbPort.Items.Clear();
        foreach (var p in ports)
            usbPort.Items.Add(app.UsbBoard?.Port == p.Name ? $"{p} · DeskBuddy {app.UsbBoard.Version}" : p.ToString());
        if (ports.Count == 0)
        {
            usbPort.Items.Add(L.T("nicio placă pe USB", "no board on USB"));
            usbPort.SelectedIndex = 0;
            missingDriver = missing;
        }
        else missingDriver = default;
        usbDriver.Visible = missingDriver.Chip != null;
        if (missingDriver.Chip != null) usbDriver.Text = L.T($"Descarcă driverul {missingDriver.Chip}", $"Download the {missingDriver.Chip} driver");
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
        string source = app.UsePrivateFirmware ? L.T("repo-ul privat", "the private repo") : L.T("release-ul public", "the public release");
        if (missingDriver.Chip != null)
        {
            usbInfo.Text = L.T($"Placa e conectată, dar Windows n-are driverul pentru cipul ei USB ({missingDriver.Chip}), deci nu apare niciun port COM. ", $"The board is connected, but Windows has no driver for its USB chip ({missingDriver.Chip}), so no COM port shows up. ") +
                           L.T("Instalează driverul, apoi apasă „Caută din nou”.", "Install the driver, then press “Search again”.");
            usbInfo.ForeColor = Theme.Warn;
        }
        else if (app.PrivateFirmwareMissing) { usbInfo.Text = L.T("Firmware-ul plăcii vine din repo-ul privat: completează-l în cardul de mai jos. Sau alege un .bin cu „Din fișier...”.", "The board's firmware comes from the private repo: fill it in below. Or pick a .bin with “From file...”."); usbInfo.ForeColor = Theme.Warn; }
        else if (rel?.Flash != null) { usbInfo.Text = L.T($"Din release: firmware {rel.Version.ToString(3)} din {source}.", $"From the release: firmware {rel.Version.ToString(3)} from {source}."); usbInfo.ForeColor = Theme.Muted; }
        else { usbInfo.Text = L.T("Încă nu știu ultimul release: apasă „Verifică acum” sau alege un .bin cu „Din fișier...”.", "Don't know the latest release yet: press “Check now” or pick a .bin with “From file...”."); usbInfo.ForeColor = Theme.Muted; }
        usbFlash.Enabled &= rel?.Flash != null;
    }

    async Task FlashUsb(string? file)
    {
        if (busy || ports.Count == 0) return;
        var port = ports[Math.Clamp(usbPort.SelectedIndex, 0, ports.Count - 1)];
        bool full = usbErase.Checked;
        string what = file != null ? Path.GetFileName(file) : L.T($"firmware-ul {app.FirmwareRelease?.Version.ToString(3)}", $"firmware {app.FirmwareRelease?.Version.ToString(3)}");
        string keep = full ? L.T("Se șterg și setările plăcii (WiFi, imprimantă, parolă): după aceea pornește în SETUP.", "The board's settings (WiFi, printer, password) are erased too: it will start in SETUP afterwards.")
                           : L.T("Setările plăcii rămân.", "The board's settings are kept.");
        if (MessageBox.Show(FindForm(), L.T($"Scriu {what} pe placa de pe {port.Name}? {keep}\nNu scoate cablul cât durează (~30 s).", $"Write {what} to the board on {port.Name}? {keep}\nDon't unplug the cable while it runs (~30 s)."),
                "DeskBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        busy = true;
        RefreshUsb();
        usbStatus.ForeColor = Theme.Muted;
        var progress = new Progress<string>(s => usbStatus.Text = s);
        string result;
        try { result = await app.FlashUsbAsync(port.Name, full ? TrayApp.UsbFlashKind.Full : TrayApp.UsbFlashKind.KeepSettings, file, progress); }
        catch (Exception e) { result = L.T("Eroare: ", "Error: ") + e.Message; }
        busy = false;
        if (IsDisposed) return;
        usbStatus.Text = result;
        usbStatus.ForeColor = L.IsOk(result) ? Theme.Accent : Theme.Bad;
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
        catch (Exception e) { result = L.T("Eroare: ", "Error: ") + e.Message; }
        busy = false;
        if (IsDisposed) return;
        updateStatus.Text = result ?? "";
        updateStatus.ForeColor = L.IsOk(result) ? Theme.Accent
                               : result != null && result != L.Cancelled ? Theme.Bad : Theme.Muted;
        RefreshData();
    }

    public override void RefreshData()
    {
        var rel = app.Release;
        string latest = rel?.Version.ToString(3) ?? "?";
        if (Updater.Repo == null)
            appVersion.Text = L.T($"Aplicația: {Updater.CurrentText} (build local, fără verificare de update-uri)", $"App: {Updater.CurrentText} (local build, no update checks)");
        else if (app.AppUpdateAvailable)
            appVersion.Text = L.T($"Aplicația: {Updater.CurrentText}  →  {latest} disponibilă", $"App: {Updater.CurrentText}  →  {latest} available");
        else if (rel != null)
            appVersion.Text = L.T($"Aplicația: {Updater.CurrentText} (la zi)", $"App: {Updater.CurrentText} (up to date)");
        else
            appVersion.Text = L.T($"Aplicația: {Updater.CurrentText}", $"App: {Updater.CurrentText}") + (app.UpdateError != null ? $" ({app.UpdateError})" : "");
        appVersion.ForeColor = app.AppUpdateAvailable ? Theme.Accent : Theme.Fg;

        var fwRel = app.FirmwareRelease;
        string board = app.BoardVersion switch
        {
            null => app.Device == null ? L.T("negăsită în rețea sau pe USB", "not found on the network or USB") : L.T("nu răspunde", "not responding"),
            "" => L.T("firmware vechi (fără număr de versiune)", "old firmware (no version number)"),
            var v => "firmware " + v + (app.Board?.Variant == "full" ? L.T(" (din repo-ul privat)", " (from the private repo)") : "") + (app.BoardViaUsb ? L.T(", pe USB", ", on USB") : ""),
        };
        string fwState = app.FirmwareUpdateAvailable ? L.T($"  →  {fwRel!.Version.ToString(3)} disponibil", $"  →  {fwRel!.Version.ToString(3)} available")
            : app.PrivateFirmwareMissing && app.Board?.Variant == "full" ? L.T("  (update-urile vin din repo-ul privat: completează-l mai jos)", "  (updates come from the private repo: fill it in below)")
            : fwRel != null && app.BoardVersion is { Length: > 0 } ? L.T(" (la zi)", " (up to date)") : "";
        boardVersion.Text = L.T($"Placa: {board}{fwState}", $"Board: {board}{fwState}");
        boardVersion.ForeColor = app.FirmwareUpdateAvailable ? Theme.Accent : Theme.Fg;

        updateApp.Visible = app.AppUpdateAvailable;
        updateBoard.Visible = app.FirmwareUpdateAvailable && app.DeviceUrl != null;     // prin WiFi; fără WiFi: cardul USB
        if (!busy) RefreshUsb();
        notes.Visible = rel != null;
        checkNow.Enabled = updateApp.Enabled = updateBoard.Enabled = !busy && (Updater.Repo != null || app.Settings.HasPrivateFirmware);
        if (!busy && updateStatus.Text.Length == 0 && app.LastUpdateCheck is DateTime t)
            updateStatus.Text = L.T($"Ultima verificare: {t:HH:mm}", $"Last check: {t:HH:mm}") + (Updater.Repo != null ? $" · github.com/{Updater.Repo}/releases" : "");
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
        _ = RefreshPortsAsync();
        _ = RefreshAutostartAsync();
        string ver = typeof(AppPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        about.Text = L.T($"DeskBuddy pentru Windows {ver}\n{Environment.ProcessPath}\n", $"DeskBuddy for Windows {ver}\n{Environment.ProcessPath}\n") +
                     (Autostart.IsAdmin ? L.T("Rulează ca administrator.", "Running as administrator.") : L.T("Rulează fără drepturi de administrator.", "Running without administrator rights."));
        base.OnShown();
    }

    void RefreshPrivate()
    {
        bool hasToken = app.Settings.FirmwareTokenProtected.Length > 0;
        privToken.PlaceholderText = hasToken ? L.T("token salvat · lasă gol ca să-l păstrezi", "token saved · leave empty to keep it") : L.T("token GitHub (github_pat_...)", "GitHub token (github_pat_...)");
        if (app.PrivateError != null) { privInfo.Text = app.PrivateError; privInfo.ForeColor = Theme.Bad; }
        else if (app.PrivateRelease is { } r) { privInfo.Text = L.T($"Merge: ultimul release privat e {r.Version.ToString(3)}.", $"Works: the latest private release is {r.Version.ToString(3)}."); privInfo.ForeColor = Theme.Accent; }
        else { privInfo.Text = app.Settings.HasPrivateFirmware ? L.T("Salvat. Apasă „Verifică acum”.", "Saved. Press “Check now”.") : L.T("Neconfigurat.", "Not set up."); privInfo.ForeColor = Theme.Muted; }
    }

    /// „Pornește cu Windows” = există task-ul programat: schtasks e un proces separat (sute de ms), deci pe fundal
    async Task RefreshAutostartAsync(string error = "")
    {
        bool on = await Task.Run(Autostart.IsEnabled);
        if (!IsDisposed) RefreshAutostart(error, on);
    }

    void RefreshAutostart(string error, bool? known = null)
    {
        updating = true;
        autostart.Checked = known ?? Autostart.IsEnabled();
        updating = false;
        autostartInfo.Text = error.Length > 0 ? error
            : autostart.Checked ? L.T("Task programat „DeskBuddy”: pornește la logare, în tray, fără prompt UAC.", "Scheduled task “DeskBuddy”: starts at sign-in, in the tray, without a UAC prompt.")
            : L.T("Dacă muți DeskBuddy.exe, bifează din nou.", "If you move DeskBuddy.exe, tick it again.");
        autostartInfo.ForeColor = error.Length > 0 ? Theme.Bad : Theme.Muted;
    }
}
