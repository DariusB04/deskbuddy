using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Acasă: cum e legată placa și tot ce arată ea pe ecrane (PC, Claude, imprimantă, vreme, sistemul plăcii).
/// Datele plăcii vin din /api/status și /api/sys (pe USB sau WiFi), la câteva secunde cât pagina e deschisă.
sealed class HomePage : PageBase
{
    readonly TrayApp app;
    readonly TextBlock link = Ui.Label("", 17, null, true);
    readonly TextBlock linkInfo = Ui.Note("");
    readonly TextBlock linkHint = Ui.Label("", 12.5, Ui.Warn);
    readonly MetricBar cpu = new("CPU"), gpu = new("GPU"), ram = new("RAM");
    readonly TextBlock game = Ui.Label("");
    readonly TextBlock claude = Ui.Label("");
    readonly MetricBar h5 = new(L.T("5 ore", "5 hours")), d7 = new(L.T("7 zile", "7 days"));
    readonly TextBlock printer = Ui.Label("");
    readonly MetricBar printBar = new(L.T("Progres", "Progress"));
    readonly TextBlock printInfo = Ui.Note("");
    readonly TextBlock weather = Ui.Label("");
    readonly TextBlock mediaTitle = Ui.Label("", 17, null, true);
    readonly TextBlock mediaInfo = Ui.Note("");
    readonly MetricBar mediaBar = new(L.T("Progres", "Progress"));
    readonly TextBlock boardInfo = Ui.Note("");
    readonly MetricBar heap = new(L.T("RAM placă", "Board RAM")), cores = new(L.T("Procesor placă", "Board CPU"));
    readonly TextBlock boardError = Ui.Label("", 12.5, Ui.Muted);
    long statusAt = -60_000, sysAt = -60_000;
    bool busy;
    JsonNode? status, sys;

    public HomePage(TrayApp app) : base(L.T("Acasă", "Home"), L.T("Placa și tot ce arată ea pe ecrane, într-un singur loc.", "The board and everything it shows on its screens, in one place."))
    {
        this.app = app;
        var c = AddCard(L.T("Conexiune", "Connection"));
        c.Children.AddRange([link, linkInfo, linkHint]);
        var pc = AddCard("PC");
        pc.Children.AddRange([cpu, gpu, ram, game]);
        var cl = AddCard("Claude Code");
        cl.Children.AddRange([claude, h5, d7]);
        var pr = AddCard(L.T("Imprimantă", "Printer"));
        pr.Children.AddRange([printer, printBar, printInfo]);
        var md = AddCard(L.T("Se ascultă", "Now playing"));
        md.Children.AddRange([mediaTitle, mediaInfo, mediaBar]);
        var wx = AddCard(L.T("Vreme", "Weather"));
        wx.Children.Add(weather);
        var b = AddCard(L.T("Placa", "Board"));
        b.Children.AddRange([boardInfo, heap, cores, boardError]);
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
            link.Text = L.T($"Pe USB ({usb.Port})", $"On USB ({usb.Port})");
            link.Foreground = Ui.Accent;
            linkInfo.Text = $"Firmware {usb.Version}" + (usb.Wifi ? $" · WiFi {usb.Ssid} ({usb.Ip})" : usb.Ssid.Length > 0 ? L.T($" · WiFi {usb.Ssid}: neconectat", $" · WiFi {usb.Ssid}: not connected") : L.T(" · fără WiFi", " · no WiFi"));
            if (usb.Wifi && wifi == null)
                linkHint.Text = L.T("Placa e pe WiFi, dar semnalele ei nu ajung la PC. Dacă Windows a întrebat de DeskBuddy în Firewall, permite-l ", "The board is on WiFi, but its signals don't reach the PC. If Windows asked about DeskBuddy in the Firewall, allow it ") +
                                L.T("(rețele private). Pe cablu merge oricum tot.", "(private networks). Everything works over the cable anyway.");
        }
        else if (wifi != null)
        {
            link.Text = app.WifiDirect ? L.T($"Pe WiFi ({wifi.Ip})", $"On WiFi ({wifi.Ip})") : L.T($"Pe WiFi, prin router ({wifi.Ip})", $"On WiFi, through a router ({wifi.Ip})");
            link.Foreground = Ui.Accent;
            linkInfo.Text = L.T($"Firmware {wifi.Version} · rețeaua {wifi.Ssid}", $"Firmware {wifi.Version} · network {wifi.Ssid}") +
                            (app.WifiDirect ? "" : L.T($" · placa e în altă rețea decât PC-ul; ajungem la ea prin {app.WifiEndpoint}", $" · the board is on a different network than the PC; we reach it through {app.WifiEndpoint}"));
        }
        else
        {
            link.Text = L.T("Placa nu e găsită", "Board not found");
            link.Foreground = Ui.Warn;
            linkInfo.Text = L.T("Leag-o prin USB, sau pune-o pe WiFi: o găsim singuri (și când e în altă rețea, după ce a fost o dată pe USB).", "Connect it over USB, or put it on WiFi: we find it ourselves (even on another network, once it has been on USB).");
        }
        if (app.WifiError != null) linkHint.Text = "WiFi: " + app.WifiError;
        if (app.Settings.Link == LinkMode.Usb) linkInfo.Text += L.T(" · trimit doar pe USB", " · sending over USB only");
        else if (app.Settings.Link == LinkMode.Wifi) linkInfo.Text += L.T(" · trimit doar pe WiFi", " · sending over WiFi only");
    }

    void RefreshPc()
    {
        var s = app.Latest;
        string T(float? v) => v is float f ? $"{f:0}°C" : "--";
        string P(float? v) => v is float f ? $"{f:0}%" : "--";
        if (s == null) { cpu.Set(L.T("se citește...", "reading..."), null, Ui.Muted); return; }
        cpu.Set($"{T(s.CpuT)} · {P(s.CpuL)}", s.CpuL, Ui.TempColor(s.CpuT));
        gpu.Set($"{T(s.GpuT)} · {P(s.GpuL)}", s.GpuL, Ui.TempColor(s.GpuT));
        string used = s.RamU is float u && s.RamT is float t ? string.Create(CultureInfo.InvariantCulture, $"{u:0.0} / {t:0} GB · ") : "";
        ram.Set(used + P(s.RamL), s.RamL, Ui.Accent);
        game.Text = app.Game is { } g ? L.T($"Joc: {g} · ", $"Game: {g} · ") + (app.Fps is float fp ? $"{fp:0} FPS" : app.FpsError ?? "FPS: --") : L.T("Niciun joc pornit.", "No game running.");
        game.Foreground = app.Game != null ? Ui.Fg : Ui.Muted;
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
        catch (UnauthorizedAccessException) { boardError.Text = L.T("Placa are parolă: pune-o în pagina Placă → Securitate.", "The board has a password: enter it on Board → Security."); statusAt = Environment.TickCount64 + 30_000; }
        catch (Exception e) { boardError.Text = L.T("Nu pot citi starea plăcii: ", "Can't read the board's status: ") + e.Message; statusAt = Environment.TickCount64; }
        finally { busy = false; }
        Show();
    }

    void Show()
    {
        var st = status;
        // Claude: starea de pe placă + usage-ul (din cache-ul de pe PC, cel mai proaspăt)
        string cs = (string?)st?["cl"] ?? "neconectat";
        claude.Text = L.T("Acum: ", "Now: ") + (L.En ? cs switch
        {
            "neconectat" => "not connected", "liber" => "idle", "gata" => "done", "eroare" => "error",
            "lucreaza" => "working", "te asteapta" => "waiting for you", _ => cs,
        } : cs);
        claude.Foreground = cs is "lucreaza" or "te asteapta" ? Ui.Accent : Ui.Fg;
        var u = CachedUsage();
        long nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double? h = u?.H, w = u?.W;
        if (u != null && u.Hr > 0 && nowS >= u.Hr) h = 0;
        if (u != null && u.Wr > 0 && nowS >= u.Wr) w = 0;
        h5.Set(h is double a ? $"{a:0}%" : "--", (float?)h, Usage(h));
        d7.Set(w is double b ? $"{b:0}%" : "--", (float?)w, Usage(w));

        // imprimanta
        if (st == null) { printer.Text = "--"; printBar.Set("--", null, Ui.Muted); }
        else if (st["pr_on"]?.GetValue<bool>() != true) { printer.Text = L.T("Oprită din setări.", "Turned off in settings."); printBar.Set("--", null, Ui.Muted); printInfo.Text = ""; }
        else if (st["pr_ok"]?.GetValue<bool>() != true)
        {
            int rc = (int?)st["pr_rc"] ?? 0;
            printer.Text = L.T("Neconectată", "Not connected") + rc switch { -2 => L.T(" (nu ajunge la server)", " (can't reach the server)"), -4 => " (timeout)", 4 or 5 => L.T(" (token / cod greșit)", " (wrong token / code)"), _ => "" };
            printBar.Set("--", null, Ui.Muted);
            printInfo.Text = "";
        }
        else
        {
            string state = (string?)st["pr_state"] ?? "";
            printer.Text = state switch
            {
                "RUNNING" => L.T("Printează", "Printing"), "PAUSE" => L.T("Pauză", "Paused"), "PREPARE" => L.T("Pregătire", "Preparing"),
                "FINISH" => L.T("Gata!", "Done!"), "FAILED" => L.T("Eșuat", "Failed"), "IDLE" => L.T("Liberă", "Idle"), "" => "--", _ => state,
            };
            int pct = (int?)st["pr_pct"] ?? -1;
            printBar.Set(pct >= 0 ? $"{pct}%" : "--", pct >= 0 ? pct : null, Ui.Accent, (string?)st["pr_file"] ?? "");
            var parts = new List<string>();
            if ((int?)st["pr_rem"] is int rem && state is "RUNNING" or "PAUSE") parts.Add(rem >= 60 ? L.T($"încă {rem / 60} h {rem % 60} min", $"{rem / 60} h {rem % 60} min left") : L.T($"încă {rem} min", $"{rem} min left"));
            if ((int?)st["pr_ly"] is int ly && (int?)st["pr_lys"] is int lys && lys > 0) parts.Add(L.T($"strat {ly}/{lys}", $"layer {ly}/{lys}"));
            if ((int?)st["pr_nz"] is int nz) parts.Add(L.T($"duză {nz}°", $"nozzle {nz}°") + ((int?)st["pr_nzt"] is int nt && nt > 0 ? $"/{nt}°" : ""));
            if ((int?)st["pr_bed"] is int bd) parts.Add(L.T($"pat {bd}°", $"bed {bd}°") + ((int?)st["pr_bedt"] is int bt && bt > 0 ? $"/{bt}°" : ""));
            printInfo.Text = string.Join(" · ", parts);
        }

        // vremea
        weather.Text = (string?)st?["wx"] ?? (st?["wx_err"] != null ? L.T("Nu pot lua vremea (cod ", "Can't get the weather (code ") + st["wx_err"] + ")." : "--");

        // placa
        var sy = sys;
        if (sy != null)
        {
            long up = (long?)sy["up"] ?? 0;
            string upTxt = up >= 86400 ? L.T($"{up / 86400} zile {up % 86400 / 3600} h", $"{up / 86400} days {up % 86400 / 3600} h") : up >= 3600 ? $"{up / 3600} h {up % 3600 / 60} min" : $"{up / 60} min";
            boardInfo.Text = L.T($"Ecranul acum: {(string?)st?["mode"] ?? "?"} · pornită de {upTxt} · ultimul reset: {(string?)sy["reset"]}", $"Screen now: {(string?)st?["mode"] ?? "?"} · up for {upTxt} · last reset: {(string?)sy["reset"]}") +
                             ((int?)sy["rssi"] is int rssi && rssi != 0 ? L.T($" · semnal WiFi {rssi} dBm", $" · WiFi signal {rssi} dBm") : "");
            if ((long?)sy["heap"] is long hf && (long?)sy["heapT"] is long ht && ht > 0)
                heap.Set($"{(ht - hf) / 1024} / {ht / 1024} KB", 100f * (ht - hf) / ht, Ui.Accent);
            if (sy["cpu"] is JsonArray ca && ca.Count > 0)
            {
                var vals = ca.Select(x => (int?)x ?? 0).ToArray();
                cores.Set(string.Join(" · ", vals.Select((v, i) => L.T($"nucleu {i}: {v}%", $"core {i}: {v}%"))), vals.Max(), Ui.Accent);
            }
        }
    }

    void RefreshMedia()
    {
        var m = app.Media;
        if (m == null) { mediaTitle.Text = L.T("Nimic nu se redă", "Nothing playing"); mediaTitle.Foreground = Ui.Muted; mediaInfo.Text = L.T("Spotify, YouTube în browser, orice player din panoul de volum Windows.", "Spotify, YouTube in a browser, any player in the Windows volume panel."); mediaBar.IsVisible = false; return; }
        mediaTitle.Text = m.Title.Length > 0 ? m.Title : "?";
        mediaTitle.Foreground = Ui.Fg;
        mediaInfo.Text = string.Join(" · ", new[] { m.Artist, m.App, m.St == 1 ? L.T("se redă", "playing") : L.T("pauză", "paused") }.Where(x => x.Length > 0));
        mediaBar.IsVisible = m.Dur is > 0;
        if (m.Dur is double du && du > 0)
        {
            double pos = Math.Min(du, (m.Pos ?? 0) + (m.St == 1 ? (DateTime.Now - m.At).TotalSeconds : 0));
            static string T(double s) => s >= 3600 ? $"{(int)s / 3600}:{(int)s / 60 % 60:00}:{(int)s % 60:00}" : $"{(int)s / 60}:{(int)s % 60:00}";
            mediaBar.Set($"{T(pos)} / {T(du)}", (float)(100 * pos / du), Ui.Accent);
        }
    }

    ClaudeBridge.Usage? usage;
    DateTime usageStamp;
    long usageAt = -10_000;

    /// usage-ul Claude din fișier: recitit doar la 5 s și doar dacă fișierul s-a schimbat (înainte: în fiecare secundă)
    ClaudeBridge.Usage? CachedUsage()
    {
        long now = Environment.TickCount64;
        if (now - usageAt < 5000) return usage;
        usageAt = now;
        try
        {
            var t = File.GetLastWriteTimeUtc(ClaudeBridge.UsageCache);
            if (t != usageStamp) { usageStamp = t; usage = ClaudeBridge.ReadUsage(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return usage;
    }

    static IBrush Usage(double? p) => p switch { null => Ui.Muted, < 70 => Ui.Accent, < 90 => Ui.Warn, _ => Ui.Bad };
}
