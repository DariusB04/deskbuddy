using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskBuddy;

// ================================================================ PC (ca PcPage din Pages.cs de pe Windows)
sealed class PcPage : PageBase
{
    readonly TrayApp app;
    readonly MetricBar cpu = new("CPU"), gpu = new("GPU"), ram = new("RAM");
    readonly TextBlock game = Ui.Label(""), device = Ui.Label(""), targets = Ui.Note(""), hostError = Ui.Label("", 12.5, Ui.Bad), sensors = Ui.Note("");
    readonly TextBox host = Ui.TextBox(220);
    readonly ComboBox link = Ui.Combo(380);
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
        now.Children.AddRange([cpu, gpu, ram, game, sensors]);

        var conn = AddCard(L.T("Conexiune", "Connection"));
        foreach (var (_, text) in LinkModes) link.Items.Add(text);
        link.SelectedIndex = Math.Max(0, Array.FindIndex(LinkModes, m => m.Mode == app.Settings.Link));
        link.SelectionChanged += (_, _) => { if (link.SelectedIndex >= 0) { app.SetLinkMode(LinkModes[link.SelectedIndex].Mode); RefreshData(); } };
        conn.Children.AddRange([link, device, targets]);
        conn.Children.Add(Ui.Note(L.T("Pe USB merg ecranele PC, CLAUDE și CEAS (ora vine de la PC) și fără WiFi; VREME și IMPRIMANTA cer WiFi.", "Over USB the PC, CLAUDE and CLOCK screens work without WiFi (the time comes from the PC); WEATHER and PRINTER need WiFi.")));
        conn.Children.Add(Ui.Note(L.T("IP-ul plăcii: gol = o găsim singuri (broadcast și semnalele trimise de placă). Scrie-l doar dacă n-o găsim.", "Board IP: empty = we find it ourselves (broadcast and the signals the board sends). Only fill it in if we can't find it.")));
        host.Watermark = "ex. 192.168.1.77";
        host.Text = app.Settings.Host;
        var save = Ui.Button(L.T("Salvează", "Save"));
        save.Click += (_, _) =>
        {
            string h = (host.Text ?? "").Trim();
            if (h.Length > 0 && !IPAddress.TryParse(h, out _)) { hostError.Text = L.T("Nu e un IP valid (ex. 192.168.1.77).", "Not a valid IP (e.g. 192.168.1.77)."); hostError.IsVisible = true; return; }
            hostError.IsVisible = false;
            app.SetHost(h);
            RefreshData();
        };
        conn.Children.Add(Ui.Row(host, save));
        hostError.IsVisible = false;
        conn.Children.Add(hostError);
    }

    public override void RefreshData()
    {
        var s = app.Latest;
        string T(float? v) => v is float f ? $"{f:0}°C" : "--";
        string P(float? v) => v is float f ? $"{f:0}%" : "--";
        if (s == null)
        {
            cpu.Set(L.T("se citește...", "reading..."), null, Ui.Muted);
            gpu.Set("", null, Ui.Muted);
            ram.Set("", null, Ui.Muted);
        }
        else
        {
            cpu.Set($"{T(s.CpuT)} · {P(s.CpuL)}", s.CpuL, Ui.TempColor(s.CpuT), L.T("temperatură · load", "temperature · load"));
            gpu.Set($"{T(s.GpuT)} · {P(s.GpuL)}", s.GpuL, Ui.TempColor(s.GpuT), L.T("temperatură · load", "temperature · load"));
            string used = s.RamU is float u && s.RamT is float t ? string.Create(CultureInfo.InvariantCulture, $"{u:0.0} / {t:0} GB · ") : "";
            ram.Set(used + P(s.RamL), s.RamL, Ui.Accent);
            game.Text = app.Game is { } g ? L.T($"Joc: {g} · ", $"Game: {g} · ") + (app.Fps is float fp ? $"{fp:0} FPS" : app.FpsError ?? "FPS: --") : L.T("Niciun joc pornit.", "No game running.");
            game.Foreground = app.Game != null ? Ui.Fg : Ui.Muted;
        }
        sensors.Text = s != null && s.CpuT == null
            ? L.T("Temperatura CPU lipsește: kernelul trebuie să aibă modulul k10temp (AMD) sau coretemp (Intel) încărcat.", "No CPU temperature: the kernel needs the k10temp (AMD) or coretemp (Intel) module loaded.")
            : L.T("Senzorii vin din /sys/class/hwmon; GPU NVIDIA prin nvidia-smi.", "Sensors come from /sys/class/hwmon; NVIDIA GPUs through nvidia-smi.");

        var mode = app.Settings.Link;
        if (app.SendingOverUsb && app.UsbBoard is { } ub)
        {
            device.Text = L.T($"DeskBuddy pe USB: {ub.Port} · firmware {ub.Version}", $"DeskBuddy on USB: {ub.Port} · firmware {ub.Version}") + (ub.Ip != null ? $" · WiFi {ub.Ip}" : L.T(" · fără WiFi", " · no WiFi"));
            device.Foreground = Ui.Accent;
            targets.Text = L.T("Trimit prin cablul USB.", "Sending over the USB cable.");
        }
        else if (mode == LinkMode.Usb)
        {
            device.Text = L.T("DeskBuddy negăsit pe USB", "DeskBuddy not found on USB");
            device.Foreground = Ui.Warn;
            targets.Text = L.T("Conectează placa prin cablu. Pe Linux, utilizatorul trebuie să fie în grupul dialout (sudo usermod -aG dialout $USER, apoi relogare).", "Connect the board with a cable. On Linux your user must be in the dialout group (sudo usermod -aG dialout $USER, then log in again).");
        }
        else if (app.WifiBoard is { } wb && app.WifiEndpoint is { } ep)
        {
            device.Text = L.T($"DeskBuddy pe WiFi: {wb.Ip} · firmware {wb.Version}", $"DeskBuddy on WiFi: {wb.Ip} · firmware {wb.Version}") + (app.WifiDirect ? "" : L.T(" · prin router", " · through a router"));
            device.Foreground = Ui.Accent;
            targets.Text = L.T($"Trimit direct la placă: {ep}", $"Sending straight to the board: {ep}");
        }
        else
        {
            device.Text = L.T("DeskBuddy negăsit pe WiFi (trimit oricum broadcast)", "DeskBuddy not found on WiFi (broadcasting anyway)");
            device.Foreground = Ui.Warn;
            targets.Text = L.T("Broadcast la: ", "Broadcast to: ") + (app.Targets.Count > 0 ? string.Join(", ", app.Targets) : "...") + $"  (UDP {UdpSender.Port})";
        }
    }
}

// ================================================================ Claude Code (ca ClaudePage din Pages.cs de pe Windows)
sealed class ClaudePage : PageBase
{
    readonly TrayApp app;
    readonly CheckBox integration = Ui.Check(L.T("Trimite la DeskBuddy ce face Claude Code (hook-uri și status line în settings.json)", "Send what Claude Code is doing to DeskBuddy (hooks and status line in settings.json)"));
    readonly TextBlock integrationInfo = Ui.Note(""), usageInfo = Ui.Note("");
    readonly MetricBar h5 = new(L.T("5 ore", "5 hours")), d7 = new(L.T("7 zile", "7 days"));
    readonly Button test = Ui.Button(L.T("Trimite testul", "Send the test"), primary: true);
    bool updating;

    public ClaudePage(TrayApp app) : base("Claude Code", L.T("Ecranul CLAUDE de pe placă: ce face Claude Code acum și cât ai folosit din limite.", "The CLAUDE screen on the board: what Claude Code is doing now and how much of your limits you've used."))
    {
        this.app = app;
        var integ = AddCard(L.T("Integrare", "Integration"));
        integ.Children.AddRange([integration, integrationInfo]);
        integration.IsCheckedChanged += (_, _) =>
        {
            if (updating) return;
            try
            {
                if (integration.IsChecked == true) ClaudeBridge.Install();
                else ClaudeBridge.Uninstall();
                integrationInfo.Foreground = Ui.Muted;
            }
            catch (Exception e) { Ui.Fail(integrationInfo, L.T("Nu pot modifica settings.json: ", "Can't modify settings.json: ") + e.Message); return; }
            RefreshIntegration();
        };

        var usage = AddCard("Usage");
        usage.Children.AddRange([h5, d7, usageInfo]);
        usage.Children.Add(Ui.Note(L.T("Procentele vin doar din status line, iar status line-ul îl rulează doar `claude` pornit în terminal.", "The percentages only come from the status line, and only `claude` started in a terminal runs the status line.")));

        var t = AddCard("Test");
        t.Children.Add(Ui.Note(L.T("Trimite pe ecranul CLAUDE o secvență de ~16 s: lucrează → Bash → Edit → te așteaptă → gata.", "Sends a ~16 s sequence to the CLAUDE screen: working → Bash → Edit → waiting for you → done.")));
        t.Children.Add(test);
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            test.Content = L.T("Se trimite...", "Sending...");
            await app.SendClaudeTestAsync();
            test.Content = L.T("Trimite testul", "Send the test");
            test.IsEnabled = true;
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
        integration.IsChecked = ClaudeBridge.IsInstalled();
        updating = false;
        bool on = integration.IsChecked == true;
        integrationInfo.Text = (on ? L.T("Instalat în ", "Installed in ") : L.T("Neinstalat. Fișierul: ", "Not installed. File: ")) + ClaudeBridge.SettingsFile +
                               (on ? L.T(". Repornește sesiunile Claude Code deschise ca să-l preia.", ". Restart open Claude Code sessions so they pick it up.") : "");
    }

    public override void RefreshData()
    {
        var u = ClaudeBridge.ReadUsage();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static double? AfterReset(double? pct, long resetAt, long now) => pct != null && resetAt > 0 && now >= resetAt ? 0 : pct;
        if (u == null)
        {
            h5.Set("--", null, Ui.Muted);
            d7.Set("--", null, Ui.Muted);
            usageInfo.Text = L.T("Încă nimic: pornește `claude` în terminal și trimite un mesaj.", "Nothing yet: start `claude` in a terminal and send a message.");
            return;
        }
        double? h = AfterReset(u.H, u.Hr, now), w = AfterReset(u.W, u.Wr, now);
        h5.Set(h is double a ? $"{a:0}%" : "--", (float?)h, UsageColor(h), ResetText(u.Hr, now));
        d7.Set(w is double b ? $"{b:0}%" : "--", (float?)w, UsageColor(w), ResetText(u.Wr, now));
        string ago = u.At > 0 ? Ago(now - u.At) : "?";
        usageInfo.Text = $"{(u.Model.Length > 0 ? u.Model + " · " : "")}{L.T("citit ", "read ")}{ago}";
    }

    static IBrush UsageColor(double? p) => p switch { null => Ui.Muted, < 70 => Ui.Accent, < 90 => Ui.Warn, _ => Ui.Bad };

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
