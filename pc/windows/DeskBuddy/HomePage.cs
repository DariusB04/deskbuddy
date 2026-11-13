using System.Globalization;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Acasă: cum e legată placa și tot ce arată ea pe ecrane (PC, Claude, imprimantă, vreme, sistemul plăcii).
/// Datele plăcii vin din /api/status și /api/sys (pe USB sau WiFi), la câteva secunde cât pagina e deschisă.
sealed class HomePage : Page
{
    readonly TrayApp app;
    readonly Label link = Theme.Label("", Theme.Value);
    readonly Label linkInfo = Note("");
    readonly Label linkHint = Theme.Label("", Theme.Small, Theme.Warn);
    readonly MetricBar cpu = new("CPU"), gpu = new("GPU"), ram = new("RAM");
    readonly Label game = Theme.Label("");
    readonly Label claude = Theme.Label("");
    readonly MetricBar h5 = new("5 ore"), d7 = new("7 zile");
    readonly Label printer = Theme.Label("");
    readonly MetricBar printBar = new("Progres");
    readonly Label printInfo = Note("");
    readonly Label weather = Theme.Label("");
    readonly Label mediaTitle = Theme.Label("", Theme.Value);
    readonly Label mediaInfo = Note("");
    readonly MetricBar mediaBar = new("Progres");
    readonly Label boardInfo = Note("");
    readonly MetricBar heap = new("RAM placă"), cores = new("Procesor placă");
    readonly Label boardError = Theme.Label("", Theme.Small, Theme.Muted);
    long statusAt = -60_000, sysAt = -60_000;
    bool busy;
    JsonNode? status, sys;

    public HomePage(TrayApp app) : base("Acasă", "Placa și tot ce arată ea pe ecrane, într-un singur loc.")
    {
        this.app = app;
        var c = AddCard("Conexiune");
        c.Controls.AddRange([link, linkInfo, linkHint]);
        var pc = AddCard("PC");
        pc.Controls.AddRange([cpu, gpu, ram, game]);
        var cl = AddCard("Claude Code");
        cl.Controls.AddRange([claude, h5, d7]);
        var pr = AddCard("Imprimantă");
        pr.Controls.AddRange([printer, printBar, printInfo]);
        var md = AddCard("Se ascultă");
        md.Controls.AddRange([mediaTitle, mediaInfo, mediaBar]);
        var wx = AddCard("Vreme");
        wx.Controls.Add(weather);
        var b = AddCard("Placa");
        b.Controls.AddRange([boardInfo, heap, cores, boardError]);
    }

    public override void RefreshData()
    {
        RefreshLink();
        RefreshPc();
        RefreshMedia();
        long now = Environment.TickCount64;
        if (!busy && app.DeviceFound && (now - statusAt > 3000 || now - sysAt > 15_000)) _ = PollAsync(now - sysAt > 15_000);
        Show();
    }

    void RefreshLink()
    {
        var usb = app.UsbBoard;
        var wifi = app.WifiBoard;
        linkHint.Text = "";
        if (usb != null)
        {
            link.Text = $"Pe USB ({usb.Port})";
            link.ForeColor = Theme.Accent;
            linkInfo.Text = $"Firmware {usb.Version}" + (usb.Wifi ? $" · WiFi {usb.Ssid} ({usb.Ip})" : usb.Ssid.Length > 0 ? $" · WiFi {usb.Ssid}: neconectat" : " · fără WiFi");
            if (usb.Wifi && wifi == null)
                linkHint.Text = "Placa e pe WiFi, dar semnalele ei nu ajung la PC. Dacă Windows a întrebat de DeskBuddy în Firewall, permite-l " +
                                "(rețele private). Pe cablu merge oricum tot.";
        }
        else if (wifi != null)
        {
            link.Text = app.WifiDirect ? $"Pe WiFi ({wifi.Ip})" : $"Pe WiFi, prin router ({wifi.Ip})";
            link.ForeColor = Theme.Accent;
            linkInfo.Text = $"Firmware {wifi.Version} · rețeaua {wifi.Ssid}" +
                            (app.WifiDirect ? "" : $" · placa e în altă rețea decât PC-ul; ajungem la ea prin {app.WifiEndpoint}");
        }
        else
        {
            link.Text = "Placa nu e găsită";
            link.ForeColor = Theme.Warn;
            linkInfo.Text = "Leag-o prin USB, sau pune-o pe WiFi: o găsim singuri (și când e în altă rețea, după ce a fost o dată pe USB).";
        }
        if (app.WifiError != null) linkHint.Text = "WiFi: " + app.WifiError;
        if (app.Settings.Link == LinkMode.Usb) linkInfo.Text += " · trimit doar pe USB";
        else if (app.Settings.Link == LinkMode.Wifi) linkInfo.Text += " · trimit doar pe WiFi";
    }

    void RefreshPc()
    {
        var s = app.Latest;
        string T(float? v) => v is float f ? $"{f:0}°C" : "--";
        string P(float? v) => v is float f ? $"{f:0}%" : "--";
        if (s == null) { cpu.Set("se citește...", null, Theme.Muted); return; }
        cpu.Set($"{T(s.CpuT)} · {P(s.CpuL)}", s.CpuL, Theme.TempColor(s.CpuT));
        gpu.Set($"{T(s.GpuT)} · {P(s.GpuL)}", s.GpuL, Theme.TempColor(s.GpuT));
        string used = s.RamU is float u && s.RamT is float t ? string.Create(CultureInfo.InvariantCulture, $"{u:0.0} / {t:0} GB · ") : "";
        ram.Set(used + P(s.RamL), s.RamL, Theme.Accent);
        game.Text = app.Game is { } g ? $"Joc: {g} · " + (app.Fps is float fp ? $"{fp:0} FPS" : app.FpsError ?? "FPS: --") : "Niciun joc pornit.";
        game.ForeColor = app.Game != null ? Theme.Fg : Theme.Muted;
    }

    async Task PollAsync(bool withSys)
    {
        busy = true;
        try
        {
            status = await app.ApiAsync("/api/status");
            statusAt = Environment.TickCount64;
            if (withSys) { sys = await app.ApiAsync("/api/sys", timeoutMs: 6000); sysAt = Environment.TickCount64; }
            boardError.Text = "";
        }
        catch (UnauthorizedAccessException) { boardError.Text = "Placa are parolă: pune-o în pagina Placă → Securitate."; statusAt = Environment.TickCount64 + 30_000; }
        catch (Exception e) { boardError.Text = "Nu pot citi starea plăcii: " + e.Message; statusAt = Environment.TickCount64; }
        finally { busy = false; }
        if (!IsDisposed) Show();
    }

    void Show()
    {
        var st = status;
        // Claude: starea de pe placă + usage-ul (din cache-ul de pe PC, cel mai proaspăt)
        string cs = (string?)st?["cl"] ?? "neconectat";
        claude.Text = "Acum: " + cs;
        claude.ForeColor = cs is "lucreaza" or "te asteapta" ? Theme.Accent : Theme.Fg;
        var u = ClaudeBridge.ReadUsage();
        long nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double? h = u?.H, w = u?.W;
        if (u != null && u.Hr > 0 && nowS >= u.Hr) h = 0;
        if (u != null && u.Wr > 0 && nowS >= u.Wr) w = 0;
        h5.Set(h is double a ? $"{a:0}%" : "--", (float?)h, Usage(h));
        d7.Set(w is double b ? $"{b:0}%" : "--", (float?)w, Usage(w));

        // imprimanta
        if (st == null) { printer.Text = "--"; printBar.Set("--", null, Theme.Muted); }
        else if (st["pr_on"]?.GetValue<bool>() != true) { printer.Text = "Oprită din setări."; printBar.Set("--", null, Theme.Muted); printInfo.Text = ""; }
        else if (st["pr_ok"]?.GetValue<bool>() != true)
        {
            int rc = (int?)st["pr_rc"] ?? 0;
            printer.Text = "Neconectată" + rc switch { -2 => " (nu ajunge la server)", -4 => " (timeout)", 4 or 5 => " (token / cod greșit)", _ => "" };
            printBar.Set("--", null, Theme.Muted);
            printInfo.Text = "";
        }
        else
        {
            string state = (string?)st["pr_state"] ?? "";
            printer.Text = state switch { "RUNNING" => "Printează", "PAUSE" => "Pauză", "PREPARE" => "Pregătire", "FINISH" => "Gata!", "FAILED" => "Eșuat", "IDLE" => "Liberă", "" => "--", _ => state };
            int pct = (int?)st["pr_pct"] ?? -1;
            printBar.Set(pct >= 0 ? $"{pct}%" : "--", pct >= 0 ? pct : null, Theme.Accent, (string?)st["pr_file"] ?? "");
            var parts = new List<string>();
            if ((int?)st["pr_rem"] is int rem && state is "RUNNING" or "PAUSE") parts.Add(rem >= 60 ? $"încă {rem / 60} h {rem % 60} min" : $"încă {rem} min");
            if ((int?)st["pr_ly"] is int ly && (int?)st["pr_lys"] is int lys && lys > 0) parts.Add($"strat {ly}/{lys}");
            if ((int?)st["pr_nz"] is int nz) parts.Add($"duză {nz}°" + ((int?)st["pr_nzt"] is int nt && nt > 0 ? $"/{nt}°" : ""));
            if ((int?)st["pr_bed"] is int bd) parts.Add($"pat {bd}°" + ((int?)st["pr_bedt"] is int bt && bt > 0 ? $"/{bt}°" : ""));
            printInfo.Text = string.Join(" · ", parts);
        }

        // vremea
        weather.Text = (string?)st?["wx"] ?? (st?["wx_err"] != null ? "Nu pot lua vremea (cod " + st["wx_err"] + ")." : "--");

        // placa
        var sy = sys;
        if (sy != null)
        {
            long up = (long?)sy["up"] ?? 0;
            string upTxt = up >= 86400 ? $"{up / 86400} zile {up % 86400 / 3600} h" : up >= 3600 ? $"{up / 3600} h {up % 3600 / 60} min" : $"{up / 60} min";
            boardInfo.Text = $"Ecranul acum: {(string?)st?["mode"] ?? "?"} · pornită de {upTxt} · ultimul reset: {(string?)sy["reset"]}" +
                             ((int?)sy["rssi"] is int rssi && rssi != 0 ? $" · semnal WiFi {rssi} dBm" : "");
            if ((long?)sy["heap"] is long hf && (long?)sy["heapT"] is long ht && ht > 0)
                heap.Set($"{(ht - hf) / 1024} / {ht / 1024} KB", 100f * (ht - hf) / ht, Theme.Accent);
            if (sy["cpu"] is JsonArray ca && ca.Count > 0)
            {
                var vals = ca.Select(x => (int?)x ?? 0).ToArray();
                cores.Set(string.Join(" · ", vals.Select((v, i) => $"nucleu {i}: {v}%")), vals.Max(), Theme.Accent);
            }
        }
    }

    void RefreshMedia()
    {
        var m = app.Media;
        if (m == null) { mediaTitle.Text = "Nimic nu se redă"; mediaTitle.ForeColor = Theme.Muted; mediaInfo.Text = "Spotify, YouTube în browser, orice player din panoul de volum Windows."; mediaBar.Visible = false; return; }
        mediaTitle.Text = m.Title.Length > 0 ? m.Title : "?";
        mediaTitle.ForeColor = Theme.Fg;
        mediaInfo.Text = string.Join(" · ", new[] { m.Artist, m.App, m.St == 1 ? "se redă" : "pauză" }.Where(x => x.Length > 0));
        mediaBar.Visible = m.Dur is > 0;
        if (m.Dur is double du && du > 0)
        {
            double pos = Math.Min(du, (m.Pos ?? 0) + (m.St == 1 ? (DateTime.Now - m.At).TotalSeconds : 0));
            static string T(double s) => s >= 3600 ? $"{(int)s / 3600}:{(int)s / 60 % 60:00}:{(int)s % 60:00}" : $"{(int)s / 60}:{(int)s % 60:00}";
            mediaBar.Set($"{T(pos)} / {T(du)}", (float)(100 * pos / du), Theme.Accent);
        }
    }

    static Color Usage(double? p) => p switch { null => Theme.Muted, < 70 => Theme.Accent, < 90 => Theme.Warn, _ => Theme.Bad };
}
