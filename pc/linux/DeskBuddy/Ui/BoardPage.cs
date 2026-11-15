using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Setările plăcii, native (fără browser): citite cu /api/config, salvate cu /api/set (doar câmpurile cardului salvat).
/// Merg pe USB și pe WiFi; pagina web a plăcii rămâne pentru telefon / browser.
sealed class BoardPage : PageBase
{
    static (string Tz, string Name)[] Zones =>
    [
        ("EET-2EEST,M3.5.0/3,M10.5.0/4", L.T("România / Europa de Est", "Romania / Eastern Europe")), ("CET-1CEST,M3.5.0,M10.5.0/3", L.T("Europa Centrală", "Central Europe")),
        ("GMT0BST,M3.5.0/1,M10.5.0", L.T("UK / Portugalia", "UK / Portugal")), ("UTC0", "UTC"),
    ];
    static (int Sec, string Name)[] Rotations =>
    [
        (0, L.T("nu", "no")), (10, L.T("la 10 secunde", "every 10 seconds")), (20, L.T("la 20 secunde", "every 20 seconds")),
        (30, L.T("la 30 secunde", "every 30 seconds")), (60, L.T("la 1 minut", "every minute")), (120, L.T("la 2 minute", "every 2 minutes")),
        (300, L.T("la 5 minute", "every 5 minutes")),
    ];
    static readonly int[] IdleMins = [1, 5, 10, 15, 30, 60];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    readonly TrayApp app;
    readonly TextBlock state = Ui.Label("", 14, Ui.Muted);
    readonly Button reload = Ui.Button(L.T("Reîncarcă", "Reload"));
    readonly List<Card> cards = [];
    JsonNode? cfg;
    bool loading;

    // Ecrane
    readonly StackPanel screenList = new();
    readonly Button addCustom = Ui.Button(L.T("+ Ecran custom", "+ Custom screen"));
    readonly TextBlock screensNote = Ui.Note("");
    /// un ecran din listă: Index = indexul de pe placă (la ecranele custom noi, -1 până la salvare)
    sealed class ScreenEntry { public int Index = -1; public string Name = ""; public bool Custom; public bool On = true; public List<int> Items = []; }
    readonly List<ScreenEntry> screens = [];
    List<string> catalog = [];
    int custMax = 24, itemMax = 6;
    const int Builtins = 8;
    ScreenEntry? idlePick;
    readonly ComboBox rot = Ui.Combo(200), idleMode = Ui.Combo(200), idleMin = Ui.Combo(170);
    readonly CheckBox autoClaude = Ui.Check(L.T("Sari pe CLAUDE când Claude Code începe să lucreze", "Jump to CLAUDE when Claude Code starts working"));
    readonly CheckBox autoGame = Ui.Check(L.T("Sari pe PC (temperaturi) când pornești un joc", "Jump to PC (temperatures) when you start a game"));
    readonly CheckBox fanIcons = Ui.Check(L.T("VENTILATOARE: iconițe animate în loc de text (se învârt cu turația lor)", "FANS: animated icons instead of text (they spin with their speed)"));
    // Afișaj și LED
    readonly NumericUpDown ctr = Ui.Num(0, 100), nctr = Ui.Num(0, 100), ns = Ui.Num(0, 23, 64), ne = Ui.Num(0, 23, 64), np = Ui.Num(0, 100), ledp = Ui.Num(5, 100);
    readonly CheckBox nightOff = Ui.Check(L.T("Noaptea, ecranul stins (butonul îl aprinde 15 s)", "Screen off at night (the button turns it on for 15 s)"));
    readonly CheckBox fps60 = Ui.Check(L.T("Ecranul la 60 fps (dacă apar erori pe ecran, debifează: 30 fps, legătură mai lentă)",
                                              "Screen at 60 fps (if the screen shows glitches, untick it: 30 fps, slower link)"));
    readonly CheckBox h12 = Ui.Check(L.T("Ceas cu 12 ore (AM / PM)", "12-hour clock (AM / PM)")), clockSec = Ui.Check(L.T("Secundele pe ceas", "Seconds on the clock"));
    readonly Button ambient = Ui.Button("      ");
    readonly ComboBox tz = Ui.Combo(260);
    Color ambientColor = Color.FromRgb(255, 120, 45);
    // Vreme
    readonly RadioButton wxAuto = Radio(L.T("După IP (automat)", "By IP (automatic)")), wxCity = Radio(L.T("Aleg orașul", "I'll pick the city"));
    readonly TextBox citySearch = Ui.TextBox();
    readonly ComboBox cityResults = Ui.Combo(330);
    readonly CheckBox fahrenheit = Ui.Check(L.T("Grade Fahrenheit", "Degrees Fahrenheit"));
    readonly TextBlock cityNow = Ui.Note("");
    (double Lat, double Lon, string Name)? cityPick;
    // WiFi
    readonly TextBlock wifiNow = Ui.Label("");
    readonly EditCombo ssid = new() { Width = 260, Margin = new Avalonia.Thickness(0, 2, 8, 2) };
    readonly TextBox wifiPass = Ui.TextBox();
    // Imprimantă
    readonly CheckBox bambuOn = Ui.Check(L.T("Arată imprimanta Bambu Lab", "Show the Bambu Lab printer"));
    readonly CheckBox printPopup = Ui.Check(L.T("Sari pe IMPRIMANTA când se termină un print", "Jump to PRINTER when a print finishes"));
    readonly RadioButton prCloud = Radio(L.T("Cloud (Bambu Handy merge în continuare)", "Cloud (Bambu Handy keeps working)")), prLan = Radio("LAN (LAN Only + Developer Mode)");
    readonly ComboBox region = Ui.Combo(200);
    readonly TextBox email = Ui.TextBox(), loginCode = Ui.TextBox(), loginPass = Ui.TextBox(), uid = Ui.TextBox(), token = Ui.TextBox();
    readonly TextBox printerIp = Ui.TextBox(), accessCode = Ui.TextBox(), serial = Ui.TextBox();
    readonly TextBlock loginInfo = Ui.Note("");
    readonly Panel cloudBox = Stack(), lanBox = Stack();
    // Claude
    readonly CheckBox clPopup = Ui.Check(L.T("Sari pe CLAUDE și clipește LED-ul când Claude cere permisiune", "Jump to CLAUDE and blink the LED when Claude asks for permission"));
    readonly CheckBox clDone = Ui.Check(L.T("Anunță și când termină un răspuns lung", "Also notify when it finishes a long reply"));
    readonly NumericUpDown clDoneSec = Ui.Num(0, 250);
    readonly ComboBox character = Ui.Combo(200);
    readonly TextBlock characterLabel = Ui.FieldLabel(L.T("Personaj", "Character"));
    // Securitate
    readonly TextBox pagePass = Ui.TextBox(), appPass = Ui.TextBox(), pcIp = Ui.TextBox();
    readonly TextBlock pagePassInfo = Ui.Note("");

    public BoardPage(TrayApp app) : base(L.T("Placă", "Board"), L.T("Setările plăcii. Se salvează pe ea (prin USB sau WiFi) și rămân și după repornire.", "The board's settings. They are saved on it (over USB or WiFi) and survive a restart."))
    {
        this.app = app;
        var top = AddCard(L.T("Placa", "Board"));
        top.Children.Add(state);
        reload.Click += async (_, _) => await LoadAsync();
        var web = Ui.Button(L.T("Pagina plăcii în browser", "Board page in the browser"));
        web.Click += (_, _) => { if (app.DeviceUrl is { } u) Ui.Open(u); else Ui.Fail(state, L.T("Pagina web se deschide doar când placa e în aceeași rețea cu PC-ul.", "The web page only opens when the board is on the same network as the PC.")); };
        top.Children.Add(Ui.Row(reload, web));
        AddRow(sectionBar);

        BuildScreens();
        BuildDisplay();
        BuildWeather();
        BuildWifi();
        BuildPrinter();
        BuildClaude();
        BuildSecurity();
        SetEnabled(false);

        // o singură secțiune odată: controalele celorlalte nu-și creează ferestrele Windows până nu intri în ele
        // (înainte, toate cele 7 carduri: ~0.8 s doar ca pagina să devină vizibilă, de câteva ori mai mult pe un PC slab)
        for (int i = 0; i < cards.Count; i++)
        {
            int k = i;
            var b = Ui.Button(cards[i].Title);
            b.Margin = new Avalonia.Thickness(0, 0, 6, 6);
            b.Click += (_, _) => ShowSection(k);
            sectionButtons.Add(b);
            sectionBar.Children.Add(b);
        }
        ShowSection(0);
    }

    readonly WrapPanel sectionBar = new() { Margin = new Avalonia.Thickness(0, 0, 0, 8) };
    readonly List<Button> sectionButtons = [];
    int section = -1;

    void ShowSection(int k)
    {
        if (k == section) return;
        section = k;
        for (int i = 0; i < cards.Count; i++)
        {
            cards[i].IsVisible = i == k;
            Ui.SetPrimary(sectionButtons[i], i == k);
        }
        Offset = default;
    }

    static RadioButton Radio(string text) => new() { Content = text, Foreground = Ui.Fg, Margin = new Avalonia.Thickness(0, 2, 16, 2) };
    static StackPanel Stack() => new();
    static Control Field(string label, Control c) => Ui.Field(label, c);

    Card NewCard(string title)
    {
        var c = AddCard(title);
        cards.Add(c);
        return c;
    }

    /// butonul „Salvează” + rândul de stare al unui card
    void SaveRow(Card c, Func<Dictionary<string, string>?> fields)
    {
        var btn = Ui.Button(L.T("Salvează", "Save"), primary: true);
        var info = Ui.Note("");
        btn.Click += async (_, _) =>
        {
            var f = fields();
            if (f == null || f.Count == 0) return;
            await SaveAsync(f, info, btn);
        };
        c.Children.Add(Ui.Row(btn));
        c.Children.Add(info);
    }

    // ------------------------------------------------------------ carduri
    void BuildScreens()
    {
        var c = NewCard(L.T("Ecrane", "Screens"));
        c.Children.Add(Ui.FieldLabel(L.T("Ce ecrane apar la apăsarea butonului, în ordinea în care vin (▲ ▼ le mută)", "Which screens the button cycles through, in order (▲ ▼ move them)")));
        c.Children.Add(screenList);
        addCustom.Click += async (_, _) =>
        {
            if (screens.Count(x => x.Custom) >= custMax) { await Dialogs.Info(this, L.T($"Placa ține maxim {custMax} ecrane custom.", $"The board holds at most {custMax} custom screens.")); return; }
            var r = await CustomScreenDialog.Edit(this, L.T("Ecran custom nou", "New custom screen"), "", [], catalog, itemMax, FanLabels());
            if (r == null) return;
            screens.Add(new ScreenEntry { Name = r.Value.Name, Custom = true, Items = r.Value.Items });
            RenderScreens();
        };
        c.Children.Add(Ui.Row(addCustom));
        c.Children.Add(screensNote);
        c.Children.Add(fanIcons);
        foreach (var r in Rotations) rot.Items.Add(r.Name);
        c.Children.Add(Field(L.T("Schimbă ecranul singur", "Change the screen automatically"), rot));
        c.Children.Add(Ui.FieldLabel(L.T("Ecrane automate", "Automatic screens")));
        c.Children.AddRange([autoClaude, autoGame]);
        foreach (int m in IdleMins) idleMin.Items.Add(m == 60 ? L.T("după o oră", "after an hour") : m == 1 ? L.T("după 1 minut", "after 1 minute") : L.T($"după {m} minute", $"after {m} minutes"));
        idleMode.SelectionChanged += (_, _) => idleMin.IsEnabled = idleMode.SelectedIndex > 0;
        c.Children.Add(Field(L.T("Ecran de repaus", "Idle screen"), Ui.Row(idleMode, idleMin)));
        c.Children.Add(Ui.Note(L.T("Repausul vine când nu te joci, Claude nu lucrează și n-ai apăsat butonul de atâta timp, și imediat ce stingi PC-ul.", "The idle screen comes when you're not gaming, Claude isn't working and you haven't pressed the button for that long, and as soon as you turn the PC off.")));
        SaveRow(c, () =>
        {
            if (!screens.Any(x => x.On)) { _ = Dialogs.Info(this, L.T("Bifează cel puțin un ecran.", "Tick at least one screen.")); return null; }
            // indexurile noi: cele fixe rămân 0-7, cele custom primesc 8, 9, ... în ordinea în care apar în listă
            var customs = screens.Where(x => x.Custom).ToList();
            int Idx(ScreenEntry e) => e.Custom ? Builtins + customs.IndexOf(e) : e.Index;
            ulong bits = 0;
            foreach (var e in screens) if (e.On) bits |= 1UL << Idx(e);
            var idle = idleMode.SelectedIndex > 0 && idleMode.SelectedIndex - 1 < screens.Count ? screens[idleMode.SelectedIndex - 1] : null;
            return new()
            {
                ["modes"] = bits.ToString(CultureInfo.InvariantCulture),
                ["mord"] = string.Join(",", screens.Select(e => Idx(e).ToString(CultureInfo.InvariantCulture))),
                ["cust"] = string.Join(";", customs.Select(e => CustomScreenDialog.CleanName(e.Name) + "|" + string.Join(",", e.Items.Select(i => i.ToString(CultureInfo.InvariantCulture))))),
                ["rot"] = Rotations[Math.Max(0, rot.SelectedIndex)].Sec.ToString(CultureInfo.InvariantCulture),
                ["aclaude"] = B(autoClaude), ["agame"] = B(autoGame), ["fico"] = B(fanIcons),
                ["idlem"] = (idle == null ? -1 : Idx(idle)).ToString(CultureInfo.InvariantCulture),
                ["idlet"] = IdleMins[Math.Max(0, idleMin.SelectedIndex)].ToString(CultureInfo.InvariantCulture),
            };
        });
    }

    List<string> FanLabels() => app.Latest?.Fans.Select(f => f.Label).ToList() ?? [];

    static Button Small(string text)
    {
        var b = Ui.Button(text);
        b.Padding = new Avalonia.Thickness(4, 0, 4, 0);
        b.Margin = new Avalonia.Thickness(0, 2, 4, 2);
        b.FontSize = 12;
        return b;
    }

    /// lista de ecrane (după orice schimbare: mutare, ecran nou, ștergere)
    void RenderScreens()
    {
        screenList.Children.Clear();
        for (int i = 0; i < screens.Count; i++)
        {
            var e = screens[i];
            int pos = i;
            var cb = Ui.Check(e.Custom ? $"{e.Name}  ·  custom ({e.Items.Count})" : e.Name);
            cb.Width = 260;
            cb.IsChecked = e.On;
            cb.IsCheckedChanged += (_, _) => e.On = (cb.IsChecked == true);
            var up = Small("▲");
            var down = Small("▼");
            up.IsEnabled = pos > 0;
            down.IsEnabled = pos < screens.Count - 1;
            up.Click += (_, _) => { (screens[pos - 1], screens[pos]) = (screens[pos], screens[pos - 1]); RenderScreens(); };
            down.Click += (_, _) => { (screens[pos + 1], screens[pos]) = (screens[pos], screens[pos + 1]); RenderScreens(); };
            var row = Ui.Row(cb, up, down);
            if (e.Custom)
            {
                var edit = Small(L.T("Editează", "Edit"));
                var del = Small(L.T("Șterge", "Delete"));
                edit.Click += async (_, _) =>
                {
                    var r = await CustomScreenDialog.Edit(this, L.T("Ecran custom", "Custom screen"), e.Name, e.Items, catalog, itemMax, FanLabels());
                    if (r == null) return;
                    e.Name = r.Value.Name;
                    e.Items = r.Value.Items;
                    RenderScreens();
                };
                del.Click += async (_, _) =>
                {
                    if (!await Dialogs.Confirm(this, L.T($"Ștergi ecranul „{e.Name}”? (se aplică la Salvează)", $"Delete the screen “{e.Name}”? (applied when you Save)"))) return;
                    screens.Remove(e);
                    if (idlePick == e) idlePick = null;
                    RenderScreens();
                };
                row.Children.AddRange([edit, del]);
            }
            screenList.Children.Add(row);
        }
        addCustom.IsEnabled = catalog.Count > 0 && screens.Count(x => x.Custom) < custMax;
        screensNote.Text = catalog.Count == 0
            ? L.T("Ecranele custom și ordinea cer firmware 2.3.0 sau mai nou pe placă.", "Custom screens and the order need firmware 2.3.0 or newer on the board.")
            : L.T($"Ecrane custom: {screens.Count(x => x.Custom)} din {custMax}. Schimbările se aplică la Salvează.", $"Custom screens: {screens.Count(x => x.Custom)} of {custMax}. Changes apply when you Save.");

        // ecranul de repaus: aceeași listă, păstrez alegerea
        if (idleMode.SelectedIndex > 0 && idleMode.SelectedIndex - 1 < idleItems.Count) idlePick = idleItems[idleMode.SelectedIndex - 1];
        idleItems = [.. screens];
        idleMode.Items.Clear();
        idleMode.Items.Add(L.T("fără", "none"));
        foreach (var x in screens) idleMode.Items.Add(x.Name);
        int k = idlePick == null ? -1 : screens.IndexOf(idlePick);
        idleMode.SelectedIndex = k + 1;
        idleMin.IsEnabled = idleMode.SelectedIndex > 0;
    }
    List<ScreenEntry> idleItems = [];

    void BuildDisplay()
    {
        var c = NewCard(L.T("Afișaj și LED", "Display and LED"));
        c.Children.Add(Ui.Row(Field(L.T("Contrast ziua (%)", "Day contrast (%)"), ctr), Field(L.T("Contrast noaptea (%)", "Night contrast (%)"), nctr), Field(L.T("LED maxim (%)", "Max LED (%)"), ledp)));
        c.Children.Add(Ui.Row(Field(L.T("Noaptea de la ora", "Night from"), ns), Field(L.T("până la ora", "until"), ne), Field(L.T("LED noaptea (%)", "Night LED (%)"), np)));
        c.Children.Add(nightOff);
        c.Children.Add(fps60);
        ambient.Click += async (_, _) =>
        {
            string? hex = await Dialogs.Ask(this, L.T("Culoarea LED-ului, ca #RRGGBB (ex. #ff782d):", "LED colour as #RRGGBB (e.g. #ff782d):"));
            if (hex != null && Color.TryParse(hex.Trim(), out var col)) { ambientColor = col; ambient.Background = new SolidColorBrush(col); }
        };
        c.Children.Add(Field(L.T("Culoarea LED-ului la CEAS / în repaus", "LED colour on CLOCK / when idle"), ambient));
        c.Children.AddRange([h12, clockSec]);
        foreach (var z in Zones) tz.Items.Add(z.Name);
        c.Children.Add(Field(L.T("Fusul orar", "Time zone"), tz));
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string>
            {
                ["ctr"] = N(ctr), ["nctr"] = N(nctr), ["ledp"] = N(ledp), ["ns"] = N(ns), ["ne"] = N(ne), ["np"] = N(np),
                ["noff"] = B(nightOff), ["h12"] = B(h12), ["csec"] = B(clockSec), ["f60"] = B(fps60),
                ["amb"] = $"#{ambientColor.R:x2}{ambientColor.G:x2}{ambientColor.B:x2}",
            };
            if (tz.SelectedIndex >= 0 && tz.SelectedIndex < Zones.Length) f["tz"] = Zones[tz.SelectedIndex].Tz;
            return f;
        });
    }

    void BuildWeather()
    {
        var c = NewCard(L.T("Vreme", "Weather"));
        c.Children.Add(Ui.Row(wxAuto, wxCity));
        citySearch.Width = 240;
        citySearch.Watermark = L.T("ex. Brașov", "e.g. London");
        var find = Ui.Button(L.T("Caută", "Search"));
        find.Click += async (_, _) => await FindCityAsync();
        citySearch.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await FindCityAsync(); } };
        cityResults.SelectionChanged += (_, _) => { if (cityResults.SelectedItem is CityItem ci) { cityPick = (ci.Lat, ci.Lon, ci.Name); wxCity.IsChecked = true; } };
        c.Children.Add(Ui.Row(citySearch, find));
        c.Children.Add(cityResults);
        c.Children.Add(cityNow);
        c.Children.Add(fahrenheit);
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string> { ["wauto"] = (wxAuto.IsChecked == true) ? "1" : "0", ["wf"] = B(fahrenheit) };
            if ((wxCity.IsChecked == true))
            {
                if (cityPick is not { } p) { _ = Dialogs.Info(this, L.T("Caută și alege orașul.", "Search for and pick the city.")); return null; }
                f["wlat"] = p.Lat.ToString("0.0000", CultureInfo.InvariantCulture);
                f["wlon"] = p.Lon.ToString("0.0000", CultureInfo.InvariantCulture);
                f["wcity"] = p.Name;
            }
            return f;
        });
    }

    sealed record CityItem(string Name, string Label, double Lat, double Lon) { public override string ToString() => Label; }

    async Task FindCityAsync()
    {
        string q = citySearch.Text.Trim();
        if (q.Length < 2) return;
        cityResults.Items.Clear();
        try
        {
            var j = JsonNode.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=6&language=" + (L.En ? "en" : "ro") + "&name=" + Uri.EscapeDataString(q)));
            foreach (var r in j?["results"]?.AsArray() ?? [])
            {
                if (r == null) continue;
                string name = (string?)r["name"] ?? "";
                string label = string.Join(", ", new[] { name, (string?)r["admin1"], (string?)r["country"] }.Where(x => !string.IsNullOrEmpty(x)));
                cityResults.Items.Add(new CityItem(Ascii(name), label, (double?)r["latitude"] ?? 0, (double?)r["longitude"] ?? 0));
            }
            if (cityResults.Items.Count > 0) cityResults.SelectedIndex = 0;
            else cityNow.Text = L.T("N-am găsit orașul.", "City not found.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { cityNow.Text = L.T("Căutarea n-a mers: ", "The search failed: ") + e.Message; }
    }

    static string Ascii(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s.Normalize(System.Text.NormalizationForm.FormD)) if (ch >= 0x20 && ch < 0x7F) sb.Append(ch);
        return sb.ToString();
    }

    void BuildWifi()
    {
        var c = NewCard("WiFi");
        c.Children.Add(wifiNow);
        var scan = Ui.Button(L.T("Caută rețele", "Scan networks"));
        scan.Click += async (_, _) =>
        {
            scan.IsEnabled = false;
            try
            {
                var list = await app.ApiAsync("/api/scan", timeoutMs: 15_000);
                string keep = ssid.Text ?? "";
                ssid.Items.Clear();
                foreach (var n in list?.AsArray() ?? []) if ((string?)n?["s"] is { Length: > 0 } s) ssid.Items.Add(s);
                ssid.Refresh();
                ssid.Text = keep.Length > 0 ? keep : ssid.Items.Count > 0 ? ssid.Items[0] : "";
            }
            catch (Exception e) { Ui.Fail(wifiNow, L.T("Căutarea n-a mers: ", "The search failed: ") + e.Message); }
            scan.IsEnabled = true;
        };
        c.Children.Add(Ui.Row(ssid, scan));
        wifiPass.Width = 260;
        wifiPass.PasswordChar = '•';
        wifiPass.Watermark = L.T("parola (gol = păstrează parola salvată)", "password (empty = keep the saved one)");
        c.Children.Add(Field(L.T("Parola", "Password"), wifiPass));
        c.Children.Add(Ui.Note(L.T("Placa vede doar rețele de 2.4 GHz. După salvare repornește și se conectează.", "The board only sees 2.4 GHz networks. After saving it restarts and connects.")));
        SaveRow(c, () =>
        {
            string s = (ssid.Text ?? "").Trim();
            if (s.Length == 0) { _ = Dialogs.Info(this, L.T("Alege rețeaua.", "Pick the network.")); return null; }
            var f = new Dictionary<string, string> { ["ssid"] = s };
            if ((wifiPass.Text ?? "").Length > 0) f["pass"] = wifiPass.Text!;
            wifiPass.Text = "";
            return f;
        });
    }

    void BuildPrinter()
    {
        var c = NewCard(L.T("Imprimantă", "Printer"));
        c.Children.Add(bambuOn);
        c.Children.Add(Ui.Row(prCloud, prLan));
        prCloud.IsCheckedChanged += (_, _) => { cloudBox.IsVisible = (prCloud.IsChecked == true); lanBox.IsVisible = !(prCloud.IsChecked == true); };

        region.Items.Add(L.T("Global / Europa", "Global / Europe"));
        region.Items.Add("China");
        cloudBox.Children.Add(Field(L.T("Serverul", "Server"), region));
        email.Width = 260; email.Watermark = L.T("emailul contului Bambu / MakerWorld", "Bambu / MakerWorld account email");
        loginCode.Width = 120; loginCode.Watermark = L.T("codul", "code");
        loginPass.Width = 200; loginPass.PasswordChar = '•'; loginPass.Watermark = L.T("sau parola", "or password");
        var sendCode = Ui.Button(L.T("Trimite cod pe email", "Email me a code"));
        var verify = Ui.Button(L.T("Conectează", "Connect"), primary: true);
        var withPass = Ui.Button(L.T("Conectează cu parola", "Connect with password"));
        sendCode.Click += async (_, _) => await LoginAsync("/api/login/code", new() { ["email"] = email.Text.Trim() });
        verify.Click += async (_, _) => await LoginAsync("/api/login/verify", new() { ["code"] = loginCode.Text.Trim() });
        withPass.Click += async (_, _) => { await LoginAsync("/api/login/pass", new() { ["email"] = email.Text.Trim(), ["password"] = loginPass.Text }); loginPass.Text = ""; };
        cloudBox.Children.Add(Field(L.T("Conectare cu contul", "Sign in with your account"), Ui.Row(email, sendCode)));
        cloudBox.Children.Add(Ui.Row(loginCode, verify, loginPass, withPass));
        cloudBox.Children.Add(loginInfo);
        uid.Width = 160; uid.Watermark = L.T("doar cifrele", "digits only");
        token.Width = 260; token.PasswordChar = '•';
        cloudBox.Children.Add(Field(L.T("Sau manual: user ID și token", "Or manually: user ID and token"), Ui.Row(uid, token)));
        printerIp.Width = 160; printerIp.Watermark = "ex. 192.168.1.50";
        accessCode.Width = 160; accessCode.PasswordChar = '•';
        lanBox.Children.Add(Field(L.T("IP-ul imprimantei și access code-ul", "Printer IP and access code"), Ui.Row(printerIp, accessCode)));
        c.Children.Add(cloudBox);
        c.Children.Add(lanBox);
        serial.Width = 220; serial.Watermark = "ex. 22E8...";
        c.Children.Add(Field(L.T("Serialul imprimantei", "Printer serial number"), serial));
        c.Children.Add(printPopup);
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string>
            {
                ["bambu"] = B(bambuOn), ["cloud"] = (prCloud.IsChecked == true) ? "1" : "0", ["popup"] = B(printPopup),
                ["host"] = region.SelectedIndex == 1 ? "cn.mqtt.bambulab.com" : "us.mqtt.bambulab.com",
                ["uid"] = uid.Text.Trim(), ["ip"] = printerIp.Text.Trim(), ["serial"] = serial.Text.Trim(),
            };
            if (token.Text.Trim().Length > 0) f["token"] = token.Text.Trim();
            if (accessCode.Text.Trim().Length > 0) f["code"] = accessCode.Text.Trim();
            token.Text = accessCode.Text = "";
            return f;
        });
    }

    async Task LoginAsync(string path, Dictionary<string, string> args)
    {
        args["host"] = region.SelectedIndex == 1 ? "cn.mqtt.bambulab.com" : "us.mqtt.bambulab.com";
        loginInfo.Text = L.T("Aștept serverul Bambu...", "Waiting for the Bambu server...");
        loginInfo.Foreground = Ui.Muted;
        try
        {
            var r = await app.ApiAsync(path, args, 30_000);
            if (r?["ok"]?.GetValue<bool>() != true) { Ui.Fail(loginInfo, (string?)r?["err"] ?? L.T("Eroare", "Error")); return; }
            switch ((string?)r?["step"])
            {
                case "code": loginInfo.Text = L.T("Ți-am trimis un cod pe email (verifică și Spam). Scrie-l și apasă Conectează.", "I emailed you a code (check Spam too). Enter it and press Connect."); loginInfo.Foreground = Ui.Accent; break;
                case "done":
                    int n = r?["devices"]?.AsArray().Count ?? 0;
                    loginInfo.Text = L.T($"Conectat. Imprimante în cont: {n}.", $"Connected. Printers on the account: {n}.") + (n > 1 ? L.T(" Scrie serialul celei dorite și salvează.", " Enter the serial of the one you want and save.") : "");
                    loginInfo.Foreground = Ui.Accent;
                    await LoadAsync();
                    break;
            }
        }
        catch (Exception e) { Ui.Fail(loginInfo, e.Message); }
    }

    void BuildClaude()
    {
        var c = NewCard("Claude Code");
        c.Children.AddRange([clPopup, clDone]);
        c.Children.Add(Field(L.T("Răspuns „lung” = peste (secunde)", "“Long” reply = over (seconds)"), clDoneSec));
        c.Children.Add(characterLabel);
        c.Children.Add(character);
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string> { ["clpop"] = B(clPopup), ["cldone"] = B(clDone), ["cldt"] = N(clDoneSec) };
            if (character.IsVisible && character.SelectedIndex >= 0) f["char"] = character.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            return f;
        });
    }

    void BuildSecurity()
    {
        var c = NewCard(L.T("Securitate și conexiune", "Security and connection"));
        pagePass.Width = 220; pagePass.PasswordChar = '•'; pagePass.Watermark = L.T("parolă nouă (utilizator admin)", "new password (user admin)");
        c.Children.Add(Field(L.T("Parola paginii plăcii (pentru browser și pentru setări pe WiFi)", "Board page password (for the browser and for settings over WiFi)"), pagePass));
        c.Children.Add(pagePassInfo);
        pcIp.Width = 260; pcIp.Watermark = L.T("se completează singur", "filled in automatically");
        c.Children.Add(Field(L.T("IP-ul PC-ului (placa îi trimite semnale, ca aplicația s-o găsească și din altă rețea)", "PC IP (the board sends it signals so the app can find it from another network)"), pcIp));
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string> { ["pcip"] = pcIp.Text.Trim() };
            if (pagePass.Text.Length > 0) { f["wp"] = pagePass.Text; app.Settings.BoardPassword = pagePass.Text; app.Settings.Save(); }
            pagePass.Text = "";
            return f;
        });
        appPass.Width = 220; appPass.PasswordChar = '•'; appPass.Watermark = L.T("parola paginii", "page password");
        var saveApp = Ui.Button(L.T("Ține minte", "Remember"));
        saveApp.Click += async (_, _) => { app.Settings.BoardPassword = appPass.Text; app.Settings.Save(); appPass.Text = ""; await LoadAsync(); };
        c.Children.Add(Field(L.T("Parola salvată în aplicație (dacă placa are deja parolă)", "Password saved in the app (if the board already has one)"), Ui.Row(appPass, saveApp)));
        var reset = Ui.Button(L.T("Șterge toate setările plăcii", "Erase all board settings"));
        reset.Click += async (_, _) =>
        {
            if (!await Dialogs.Confirm(this, L.T("Ștergi toate setările plăcii (WiFi, imprimantă, ecrane)? Placa repornește în modul setup.", "Erase all board settings (WiFi, printer, screens)? The board restarts in setup mode."))) return;
            try { await app.ApiAsync("/api/reset"); state.Text = L.T("Setările plăcii au fost șterse; repornește.", "The board's settings were erased; it is restarting."); }
            catch (Exception e) { Ui.Fail(state, e.Message); }
        };
        c.Children.Add(Ui.Row(reset));
    }

    // ------------------------------------------------------------ citire / salvare
    public override void OnShown()
    {
        base.OnShown();
        if (cfg == null && !loading) _ = LoadAsync();
    }

    public override void RefreshData()
    {
        if (cfg == null && !loading && app.DeviceFound) _ = LoadAsync();
        if (!app.DeviceFound) { state.Text = L.T("Placa nu e conectată (USB sau WiFi).", "The board isn't connected (USB or WiFi)."); state.Foreground = Ui.Warn; SetEnabled(false); }
    }

    async Task LoadAsync()
    {
        if (loading) return;
        loading = true;
        state.Text = L.T("Citesc setările plăcii...", "Reading the board's settings...");
        state.Foreground = Ui.Muted;
        try
        {
            cfg = await app.ApiAsync("/api/config");
            Fill(cfg!);
            SetEnabled(true);
            var b = app.UsbBoard ?? app.WifiBoard;
            state.Text = $"Firmware {b?.Version} · " + (app.UsbBoard != null ? L.T("prin USB", "over USB") : L.T("prin WiFi", "over WiFi"));
            state.Foreground = Ui.Accent;
        }
        catch (UnauthorizedAccessException)
        {
            Ui.Fail(state, L.T("Placa are parolă: scrie-o mai jos, la „Parola salvată în aplicație”.", "The board has a password: enter it below, under “Password saved in the app”."));
            SetEnabled(false);
            foreach (Control x in cards[^1].Children) x.IsEnabled = true;
            cards[^1].IsEnabled = true;
        }
        catch (Exception e) { Ui.Fail(state, L.T("Nu pot citi setările: ", "Can't read the settings: ") + e.Message); }
        finally { loading = false; }
    }

    async Task SaveAsync(Dictionary<string, string> f, TextBlock info, Button btn)
    {
        btn.IsEnabled = false;
        info.Text = L.T("Salvez...", "Saving...");
        info.Foreground = Ui.Muted;
        try
        {
            var r = await app.ApiAsync("/api/set", f);
            if (r?["ok"]?.GetValue<bool>() != true) { Ui.Fail(info, (string?)r?["err"] ?? L.T("Placa a refuzat.", "The board refused.")); return; }
            bool restart = r?["restart"]?.GetValue<bool>() == true;
            info.Text = restart ? L.T("Salvat. Placa repornește...", "Saved. The board is restarting...") : L.T("Salvat.", "Saved.");
            info.Foreground = Ui.Accent;
            if (restart) { cfg = null; await Task.Delay(8000); }
            await LoadAsync();
        }
        catch (Exception e) { Ui.Fail(info, e.Message); }
        finally { btn.IsEnabled = true; }
    }

    void Fill(JsonNode c)
    {
        bool G(string k) => c[k]?.GetValue<bool>() == true;
        int I(string k, int d = 0) => c[k] is JsonValue v && v.TryGetValue<int>(out int x) ? x : d;
        string S(string k) => (string?)c[k] ?? "";

        // ecrane
        var names = c["mnames"]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? [];
        ulong bits = c["modes"] is JsonValue mv && mv.TryGetValue<ulong>(out ulong mb) ? mb : 0xFF;
        catalog = c["citems"]?.AsArray().Select(x => (string?)x ?? "?").ToList() ?? [];
        custMax = I("cmax", 0);
        itemMax = I("cimax", 6);
        var cust = S("cust").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var all = new List<ScreenEntry>();
        for (int i = 0; i < names.Count; i++)
        {
            var e = new ScreenEntry { Index = i, Name = names[i], Custom = i >= Builtins, On = (bits >> i & 1) != 0 };
            if (e.Custom && i - Builtins < cust.Length)
            {
                var parts = cust[i - Builtins].Split('|');
                if (parts.Length > 1)
                    e.Items = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1).Where(v => v >= 0).ToList();
            }
            all.Add(e);
        }
        // în ordinea de pe placă ("mord"); placa veche n-o trimite: ordinea indexurilor
        var order = c["mord"]?.AsArray().Select(x => (int?)x ?? -1).ToList() ?? [];
        screens.Clear();
        foreach (int i in order) if (i >= 0 && i < all.Count && !screens.Contains(all[i])) screens.Add(all[i]);
        foreach (var e in all) if (!screens.Contains(e)) screens.Add(e);
        int im = I("idlem", -1);
        idlePick = im >= 0 && im < all.Count ? all[im] : null;
        idleMode.SelectedIndex = -1;
        idleItems = [];
        RenderScreens();
        int r = I("rot");
        rot.SelectedIndex = Math.Max(0, Array.FindIndex(Rotations, x => x.Sec == r));
        autoClaude.IsChecked = c["aclaude"] == null || G("aclaude");
        autoGame.IsChecked = c["agame"] == null || G("agame");
        fanIcons.IsChecked = G("fico");
        fanIcons.IsVisible = c["fico"] != null;                 // firmware mai vechi de 2.4.0 n-are opțiunea
        int it = I("idlet", 10);
        idleMin.SelectedIndex = Math.Max(0, Array.FindIndex(IdleMins, x => x >= it));
        idleMin.IsEnabled = idleMode.SelectedIndex > 0;

        // afișaj
        ctr.Value = Math.Clamp(I("ctr", 100), 0, 100); nctr.Value = Math.Clamp(I("nctr", 10), 0, 100);
        ns.Value = Math.Clamp(I("ns", 22), 0, 23); ne.Value = Math.Clamp(I("ne", 7), 0, 23);
        np.Value = Math.Clamp(I("np", 10), 0, 100); ledp.Value = Math.Clamp(I("ledp", 100), 5, 100);
        nightOff.IsChecked = G("noff"); h12.IsChecked = G("h12"); clockSec.IsChecked = G("csec");
        fps60.IsChecked = c["f60"] == null || G("f60");
        fps60.IsVisible = c["f60"] != null;                     // firmware mai vechi de 2.5.0 n-are opțiunea
        if (Color.TryParse(S("amb"), out var ac)) ambientColor = ac;
        ambient.Background = new SolidColorBrush(ambientColor);
        string z = S("tz");
        int zi = Array.FindIndex(Zones, x => x.Tz == z);
        if (zi < 0 && z.Length > 0) { if (tz.Items.Count == Zones.Length) tz.Items.Add(L.T("personalizat: ", "custom: ") + z); zi = Zones.Length; }
        tz.SelectedIndex = zi;

        // vreme
        wxAuto.IsChecked = c["wauto"] == null || G("wauto");
        wxCity.IsChecked = !(wxAuto.IsChecked == true);
        fahrenheit.IsChecked = G("wf");
        if (c["wlat"] is JsonValue la && c["wlon"] is JsonValue lo)
        {
            cityPick = (la.GetValue<double>(), lo.GetValue<double>(), S("wcity"));
            cityNow.Text = L.T("Orașul salvat: ", "Saved city: ") + (S("wcity").Length > 0 ? S("wcity") : "?");
        }
        else cityNow.Text = "";

        // WiFi
        var b = app.UsbBoard ?? app.WifiBoard;
        ssid.Text = S("ssid");
        wifiNow.Text = G("online") ? L.T($"Conectată la {S("ssid")} ({S("ip")})", $"Connected to {S("ssid")} ({S("ip")})") : S("ssid").Length > 0 ? L.T($"Rețeaua salvată: {S("ssid")}, neconectată", $"Saved network: {S("ssid")}, not connected") : L.T("Nicio rețea salvată.", "No saved network.");
        wifiNow.Foreground = G("online") ? Ui.Accent : Ui.Warn;

        // imprimantă
        bambuOn.IsChecked = G("bambu");
        prCloud.IsChecked = G("cloud"); prLan.IsChecked = !(prCloud.IsChecked == true);
        cloudBox.IsVisible = (prCloud.IsChecked == true); lanBox.IsVisible = !(prCloud.IsChecked == true);
        region.SelectedIndex = S("host").StartsWith("cn.", StringComparison.Ordinal) ? 1 : 0;
        uid.Text = S("uid");
        token.Watermark = G("hasToken") ? L.T("token salvat (gol = îl păstrez)", "token saved (empty = keep it)") : "token";
        printerIp.Text = S("bip");
        accessCode.Watermark = G("hasCode") ? L.T("salvat (gol = îl păstrez)", "saved (empty = keep it)") : "access code";
        serial.Text = S("serial");
        printPopup.IsChecked = G("popup");

        // Claude
        clPopup.IsChecked = G("clpop"); clDone.IsChecked = G("cldone");
        clDoneSec.Value = Math.Clamp(I("cldt", 15), 0, 250);
        var chars = c["chars"]?.AsArray();
        character.IsVisible = characterLabel.IsVisible = chars is { Count: > 1 };
        character.Items.Clear();
        if (chars != null) { foreach (var ch in chars) character.Items.Add((string?)ch ?? "?"); character.SelectedIndex = Math.Clamp(I("char"), 0, chars.Count - 1); }

        // securitate
        pagePassInfo.Text = G("hasWp") ? L.T("Placa are parolă. Scrie una nouă doar dacă vrei s-o schimbi.", "The board has a password. Only enter a new one if you want to change it.") : L.T("Placa n-are parolă: oricine din rețea îi poate schimba setările.", "The board has no password: anyone on the network can change its settings.");
        pagePassInfo.Foreground = G("hasWp") ? Ui.Muted : Ui.Warn;
        pcIp.Text = S("pcip");
    }

    void SetEnabled(bool on)
    {
        foreach (var c in cards) c.IsEnabled = on;
    }

    static string B(CheckBox c) => c.IsChecked == true ? "1" : "0";
    static string N(NumericUpDown n) => ((int)(n.Value ?? 0)).ToString(CultureInfo.InvariantCulture);
}
