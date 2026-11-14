using System.Globalization;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Setările plăcii, native (fără browser): citite cu /api/config, salvate cu /api/set (doar câmpurile cardului salvat).
/// Merg pe USB și pe WiFi; pagina web a plăcii rămâne pentru telefon / browser.
sealed class BoardPage : Page
{
    static readonly (string Tz, string Name)[] Zones =
    [
        ("EET-2EEST,M3.5.0/3,M10.5.0/4", "România / Europa de Est"), ("CET-1CEST,M3.5.0,M10.5.0/3", "Europa Centrală"),
        ("GMT0BST,M3.5.0/1,M10.5.0", "UK / Portugalia"), ("UTC0", "UTC"),
    ];
    static readonly (int Sec, string Name)[] Rotations = [(0, "nu"), (10, "la 10 secunde"), (20, "la 20 secunde"), (30, "la 30 secunde"), (60, "la 1 minut"), (120, "la 2 minute"), (300, "la 5 minute")];
    static readonly int[] IdleMins = [1, 5, 10, 15, 30, 60];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    readonly TrayApp app;
    readonly Label state = Theme.Label("", Theme.Body, Theme.Muted);
    readonly Button reload = Theme.Button("Reîncarcă");
    readonly List<Card> cards = [];
    JsonNode? cfg;
    bool loading;

    // Ecrane
    readonly FlowLayoutPanel screenList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
    readonly Button addCustom = Theme.Button("+ Ecran custom");
    readonly Label screensNote = Note("");
    /// un ecran din listă: Index = indexul de pe placă (la ecranele custom noi, -1 până la salvare)
    sealed class ScreenEntry { public int Index = -1; public string Name = ""; public bool Custom; public bool On = true; public List<int> Items = []; }
    readonly List<ScreenEntry> screens = [];
    List<string> catalog = [];
    int custMax = 24, itemMax = 6;
    const int Builtins = 8;
    ScreenEntry? idlePick;
    readonly ComboBox rot = Theme.Combo(200), idleMode = Theme.Combo(200), idleMin = Theme.Combo(170);
    readonly CheckBox autoClaude = Theme.Check("Sari pe CLAUDE când Claude Code începe să lucreze");
    readonly CheckBox autoGame = Theme.Check("Sari pe PC (temperaturi) când pornești un joc");
    // Afișaj și LED
    readonly NumericUpDown ctr = Theme.Num(0, 100), nctr = Theme.Num(0, 100), ns = Theme.Num(0, 23, 64), ne = Theme.Num(0, 23, 64), np = Theme.Num(0, 100), ledp = Theme.Num(5, 100);
    readonly CheckBox nightOff = Theme.Check("Noaptea, ecranul stins (butonul îl aprinde 15 s)");
    readonly CheckBox h12 = Theme.Check("Ceas cu 12 ore (AM / PM)"), clockSec = Theme.Check("Secundele pe ceas");
    readonly Button ambient = Theme.Button("      ");
    readonly ComboBox tz = Theme.Combo(260);
    Color ambientColor = Color.FromArgb(255, 120, 45);
    // Vreme
    readonly RadioButton wxAuto = Radio("După IP (automat)"), wxCity = Radio("Aleg orașul");
    readonly TextBox citySearch = Theme.TextBox();
    readonly ComboBox cityResults = Theme.Combo(330);
    readonly CheckBox fahrenheit = Theme.Check("Grade Fahrenheit");
    readonly Label cityNow = Note("");
    (double Lat, double Lon, string Name)? cityPick;
    // WiFi
    readonly Label wifiNow = Theme.Label("");
    readonly ComboBox ssid = Theme.Combo(260);
    readonly TextBox wifiPass = Theme.TextBox();
    // Imprimantă
    readonly CheckBox bambuOn = Theme.Check("Arată imprimanta Bambu Lab");
    readonly CheckBox printPopup = Theme.Check("Sari pe IMPRIMANTA când se termină un print");
    readonly RadioButton prCloud = Radio("Cloud (Bambu Handy merge în continuare)"), prLan = Radio("LAN (LAN Only + Developer Mode)");
    readonly ComboBox region = Theme.Combo(200);
    readonly TextBox email = Theme.TextBox(), loginCode = Theme.TextBox(), loginPass = Theme.TextBox(), uid = Theme.TextBox(), token = Theme.TextBox();
    readonly TextBox printerIp = Theme.TextBox(), accessCode = Theme.TextBox(), serial = Theme.TextBox();
    readonly Label loginInfo = Note("");
    readonly Panel cloudBox = Stack(), lanBox = Stack();
    // Claude
    readonly CheckBox clPopup = Theme.Check("Sari pe CLAUDE și clipește LED-ul când Claude cere permisiune");
    readonly CheckBox clDone = Theme.Check("Anunță și când termină un răspuns lung");
    readonly NumericUpDown clDoneSec = Theme.Num(0, 250);
    readonly ComboBox character = Theme.Combo(200);
    readonly Label characterLabel = Theme.FieldLabel("Personaj");
    // Securitate
    readonly TextBox pagePass = Theme.TextBox(), appPass = Theme.TextBox(), pcIp = Theme.TextBox();
    readonly Label pagePassInfo = Note("");

    public BoardPage(TrayApp app) : base("Placă", "Setările plăcii. Se salvează pe ea (prin USB sau WiFi) și rămân și după repornire.")
    {
        this.app = app;
        var top = AddCard("Placa");
        top.Controls.Add(state);
        reload.Click += async (_, _) => await LoadAsync();
        var web = Theme.Button("Pagina plăcii în browser");
        web.Click += (_, _) => { if (app.DeviceUrl is { } u) Open(u); else Fail(state, "Pagina web se deschide doar când placa e în aceeași rețea cu PC-ul."); };
        top.Controls.Add(Theme.Row(reload, web));

        BuildScreens();
        BuildDisplay();
        BuildWeather();
        BuildWifi();
        BuildPrinter();
        BuildClaude();
        BuildSecurity();
        SetEnabled(false);
    }

    static RadioButton Radio(string text) => new() { Text = text, AutoSize = true, Font = Theme.Body, ForeColor = Theme.Fg, Margin = new Padding(0, 4, 16, 4), Cursor = Cursors.Hand };
    static Panel Stack() => new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), BackColor = Theme.Card };
    static Control Field(string label, Control c) { var p = (FlowLayoutPanel)Stack(); p.Controls.Add(Theme.FieldLabel(label)); p.Controls.Add(c); return p; }

    Card NewCard(string title)
    {
        var c = AddCard(title);
        cards.Add(c);
        return c;
    }

    /// butonul „Salvează” + rândul de stare al unui card
    void SaveRow(Card c, Func<Dictionary<string, string>?> fields)
    {
        var btn = Theme.Button("Salvează", primary: true);
        var info = Note("");
        btn.Click += async (_, _) =>
        {
            var f = fields();
            if (f == null || f.Count == 0) return;
            await SaveAsync(f, info, btn);
        };
        c.Controls.Add(Theme.Row(btn));
        c.Controls.Add(info);
    }

    // ------------------------------------------------------------ carduri
    void BuildScreens()
    {
        var c = NewCard("Ecrane");
        c.Controls.Add(Theme.FieldLabel("Ce ecrane apar la apăsarea butonului, în ordinea în care vin (▲ ▼ le mută)"));
        c.Controls.Add(screenList);
        addCustom.Click += (_, _) =>
        {
            if (screens.Count(x => x.Custom) >= custMax) { MessageBox.Show(FindForm(), $"Placa ține maxim {custMax} ecrane custom.", "DeskBuddy"); return; }
            var r = CustomScreenDialog.Edit(FindForm(), "Ecran custom nou", "", [], catalog, itemMax, FanLabels());
            if (r == null) return;
            screens.Add(new ScreenEntry { Name = r.Value.Name, Custom = true, Items = r.Value.Items });
            RenderScreens();
        };
        c.Controls.Add(Theme.Row(addCustom));
        c.Controls.Add(screensNote);
        foreach (var r in Rotations) rot.Items.Add(r.Name);
        c.Controls.Add(Field("Schimbă ecranul singur", rot));
        c.Controls.Add(Theme.FieldLabel("Ecrane automate"));
        c.Controls.AddRange([autoClaude, autoGame]);
        foreach (int m in IdleMins) idleMin.Items.Add(m == 60 ? "după o oră" : m == 1 ? "după 1 minut" : $"după {m} minute");
        idleMode.SelectedIndexChanged += (_, _) => idleMin.Enabled = idleMode.SelectedIndex > 0;
        c.Controls.Add(Field("Ecran de repaus", Theme.Row(idleMode, idleMin)));
        c.Controls.Add(Note("Repausul vine când nu te joci, Claude nu lucrează și n-ai apăsat butonul de atâta timp, și imediat ce stingi PC-ul."));
        SaveRow(c, () =>
        {
            if (!screens.Any(x => x.On)) { MessageBox.Show(FindForm(), "Bifează cel puțin un ecran.", "DeskBuddy"); return null; }
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
                ["aclaude"] = B(autoClaude), ["agame"] = B(autoGame),
                ["idlem"] = (idle == null ? -1 : Idx(idle)).ToString(CultureInfo.InvariantCulture),
                ["idlet"] = IdleMins[Math.Max(0, idleMin.SelectedIndex)].ToString(CultureInfo.InvariantCulture),
            };
        });
    }

    List<string> FanLabels() => app.Latest?.Fans.Select(f => f.Label).ToList() ?? [];

    static Button Small(string text)
    {
        var b = Theme.Button(text);
        b.Padding = new Padding(4, 0, 4, 0);
        b.Margin = new Padding(0, 2, 4, 2);
        b.Font = Theme.Small;
        return b;
    }

    /// lista de ecrane (după orice schimbare: mutare, ecran nou, ștergere)
    void RenderScreens()
    {
        screenList.SuspendLayout();
        screenList.Controls.Clear();
        for (int i = 0; i < screens.Count; i++)
        {
            var e = screens[i];
            int pos = i;
            var cb = Theme.Check(e.Custom ? $"{e.Name}  ·  custom ({e.Items.Count})" : e.Name);
            cb.AutoSize = false;
            cb.Width = 250;
            cb.Height = 26;
            cb.Checked = e.On;
            cb.CheckedChanged += (_, _) => e.On = cb.Checked;
            var up = Small("▲");
            var down = Small("▼");
            up.Enabled = pos > 0;
            down.Enabled = pos < screens.Count - 1;
            up.Click += (_, _) => { (screens[pos - 1], screens[pos]) = (screens[pos], screens[pos - 1]); RenderScreens(); };
            down.Click += (_, _) => { (screens[pos + 1], screens[pos]) = (screens[pos], screens[pos + 1]); RenderScreens(); };
            var row = Theme.Row(cb, up, down);
            if (e.Custom)
            {
                var edit = Small("Editează");
                var del = Small("Șterge");
                edit.Click += (_, _) =>
                {
                    var r = CustomScreenDialog.Edit(FindForm(), "Ecran custom", e.Name, e.Items, catalog, itemMax, FanLabels());
                    if (r == null) return;
                    e.Name = r.Value.Name;
                    e.Items = r.Value.Items;
                    RenderScreens();
                };
                del.Click += (_, _) =>
                {
                    if (MessageBox.Show(FindForm(), $"Ștergi ecranul „{e.Name}”? (se aplică la Salvează)", "DeskBuddy", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    screens.Remove(e);
                    if (idlePick == e) idlePick = null;
                    RenderScreens();
                };
                row.Controls.AddRange([edit, del]);
            }
            screenList.Controls.Add(row);
        }
        screenList.ResumeLayout();
        addCustom.Enabled = catalog.Count > 0 && screens.Count(x => x.Custom) < custMax;
        screensNote.Text = catalog.Count == 0
            ? "Ecranele custom și ordinea cer firmware 2.3.0 sau mai nou pe placă."
            : $"Ecrane custom: {screens.Count(x => x.Custom)} din {custMax}. Schimbările se aplică la Salvează.";

        // ecranul de repaus: aceeași listă, păstrez alegerea
        if (idleMode.SelectedIndex > 0 && idleMode.SelectedIndex - 1 < idleItems.Count) idlePick = idleItems[idleMode.SelectedIndex - 1];
        idleItems = [.. screens];
        idleMode.Items.Clear();
        idleMode.Items.Add("fără");
        foreach (var x in screens) idleMode.Items.Add(x.Name);
        int k = idlePick == null ? -1 : screens.IndexOf(idlePick);
        idleMode.SelectedIndex = k + 1;
        idleMin.Enabled = idleMode.SelectedIndex > 0;
    }
    List<ScreenEntry> idleItems = [];

    void BuildDisplay()
    {
        var c = NewCard("Afișaj și LED");
        c.Controls.Add(Theme.Row(Field("Contrast ziua (%)", ctr), Field("Contrast noaptea (%)", nctr), Field("LED maxim (%)", ledp)));
        c.Controls.Add(Theme.Row(Field("Noaptea de la ora", ns), Field("până la ora", ne), Field("LED noaptea (%)", np)));
        c.Controls.Add(nightOff);
        ambient.Click += (_, _) =>
        {
            using var d = new ColorDialog { Color = ambientColor, FullOpen = true };
            if (d.ShowDialog(FindForm()) == DialogResult.OK) { ambientColor = d.Color; ambient.BackColor = d.Color; }
        };
        ambient.FlatAppearance.MouseOverBackColor = Color.Empty;
        c.Controls.Add(Field("Culoarea LED-ului la CEAS / în repaus", ambient));
        c.Controls.AddRange([h12, clockSec]);
        foreach (var z in Zones) tz.Items.Add(z.Name);
        c.Controls.Add(Field("Fusul orar", tz));
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string>
            {
                ["ctr"] = N(ctr), ["nctr"] = N(nctr), ["ledp"] = N(ledp), ["ns"] = N(ns), ["ne"] = N(ne), ["np"] = N(np),
                ["noff"] = B(nightOff), ["h12"] = B(h12), ["csec"] = B(clockSec),
                ["amb"] = $"#{ambientColor.R:x2}{ambientColor.G:x2}{ambientColor.B:x2}",
            };
            if (tz.SelectedIndex >= 0 && tz.SelectedIndex < Zones.Length) f["tz"] = Zones[tz.SelectedIndex].Tz;
            return f;
        });
    }

    void BuildWeather()
    {
        var c = NewCard("Vreme");
        c.Controls.Add(Theme.Row(wxAuto, wxCity));
        citySearch.Width = 240;
        citySearch.PlaceholderText = "ex. Brașov";
        var find = Theme.Button("Caută");
        int pad = Math.Max(0, (find.GetPreferredSize(Size.Empty).Height - citySearch.PreferredHeight) / 2);
        citySearch.Margin = new Padding(0, find.Margin.Top + pad, 8, 0);
        find.Click += async (_, _) => await FindCityAsync();
        citySearch.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await FindCityAsync(); } };
        cityResults.SelectedIndexChanged += (_, _) => { if (cityResults.SelectedItem is CityItem ci) { cityPick = (ci.Lat, ci.Lon, ci.Name); wxCity.Checked = true; } };
        c.Controls.Add(Theme.Row(citySearch, find));
        c.Controls.Add(cityResults);
        c.Controls.Add(cityNow);
        c.Controls.Add(fahrenheit);
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string> { ["wauto"] = wxAuto.Checked ? "1" : "0", ["wf"] = B(fahrenheit) };
            if (wxCity.Checked)
            {
                if (cityPick is not { } p) { MessageBox.Show(FindForm(), "Caută și alege orașul.", "DeskBuddy"); return null; }
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
            var j = JsonNode.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=6&language=ro&name=" + Uri.EscapeDataString(q)));
            foreach (var r in j?["results"]?.AsArray() ?? [])
            {
                if (r == null) continue;
                string name = (string?)r["name"] ?? "";
                string label = string.Join(", ", new[] { name, (string?)r["admin1"], (string?)r["country"] }.Where(x => !string.IsNullOrEmpty(x)));
                cityResults.Items.Add(new CityItem(Ascii(name), label, (double?)r["latitude"] ?? 0, (double?)r["longitude"] ?? 0));
            }
            if (cityResults.Items.Count > 0) cityResults.SelectedIndex = 0;
            else cityNow.Text = "N-am găsit orașul.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { cityNow.Text = "Căutarea n-a mers: " + e.Message; }
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
        c.Controls.Add(wifiNow);
        ssid.DropDownStyle = ComboBoxStyle.DropDown;
        var scan = Theme.Button("Caută rețele");
        scan.Click += async (_, _) =>
        {
            scan.Enabled = false;
            try
            {
                var list = await app.ApiAsync("/api/scan", timeoutMs: 15_000);
                string keep = ssid.Text;
                ssid.Items.Clear();
                foreach (var n in list?.AsArray() ?? []) if ((string?)n?["s"] is { Length: > 0 } s) ssid.Items.Add(s);
                ssid.Text = keep.Length > 0 ? keep : ssid.Items.Count > 0 ? (string)ssid.Items[0]! : "";
            }
            catch (Exception e) { Fail(wifiNow, "Căutarea n-a mers: " + e.Message); }
            scan.Enabled = true;
        };
        c.Controls.Add(Theme.Row(ssid, scan));
        wifiPass.Width = 260;
        wifiPass.UseSystemPasswordChar = true;
        wifiPass.PlaceholderText = "parola (gol = păstrează parola salvată)";
        c.Controls.Add(Field("Parola", wifiPass));
        c.Controls.Add(Note("Placa vede doar rețele de 2.4 GHz. După salvare repornește și se conectează."));
        SaveRow(c, () =>
        {
            string s = ssid.Text.Trim();
            if (s.Length == 0) { MessageBox.Show(FindForm(), "Alege rețeaua.", "DeskBuddy"); return null; }
            var f = new Dictionary<string, string> { ["ssid"] = s };
            if (wifiPass.Text.Length > 0) f["pass"] = wifiPass.Text;
            wifiPass.Text = "";
            return f;
        });
    }

    void BuildPrinter()
    {
        var c = NewCard("Imprimantă");
        c.Controls.Add(bambuOn);
        c.Controls.Add(Theme.Row(prCloud, prLan));
        prCloud.CheckedChanged += (_, _) => { cloudBox.Visible = prCloud.Checked; lanBox.Visible = !prCloud.Checked; };

        region.Items.AddRange(["Global / Europa", "China"]);
        cloudBox.Controls.Add(Field("Serverul", region));
        email.Width = 260; email.PlaceholderText = "emailul contului Bambu / MakerWorld";
        loginCode.Width = 120; loginCode.PlaceholderText = "codul";
        loginPass.Width = 200; loginPass.UseSystemPasswordChar = true; loginPass.PlaceholderText = "sau parola";
        var sendCode = Theme.Button("Trimite cod pe email");
        var verify = Theme.Button("Conectează", primary: true);
        var withPass = Theme.Button("Conectează cu parola");
        sendCode.Click += async (_, _) => await LoginAsync("/api/login/code", new() { ["email"] = email.Text.Trim() });
        verify.Click += async (_, _) => await LoginAsync("/api/login/verify", new() { ["code"] = loginCode.Text.Trim() });
        withPass.Click += async (_, _) => { await LoginAsync("/api/login/pass", new() { ["email"] = email.Text.Trim(), ["password"] = loginPass.Text }); loginPass.Text = ""; };
        cloudBox.Controls.Add(Field("Conectare cu contul", Theme.Row(email, sendCode)));
        cloudBox.Controls.Add(Theme.Row(loginCode, verify, loginPass, withPass));
        cloudBox.Controls.Add(loginInfo);
        uid.Width = 160; uid.PlaceholderText = "doar cifrele";
        token.Width = 260; token.UseSystemPasswordChar = true;
        cloudBox.Controls.Add(Field("Sau manual: user ID și token", Theme.Row(uid, token)));
        printerIp.Width = 160; printerIp.PlaceholderText = "ex. 192.168.1.50";
        accessCode.Width = 160; accessCode.UseSystemPasswordChar = true;
        lanBox.Controls.Add(Field("IP-ul imprimantei și access code-ul", Theme.Row(printerIp, accessCode)));
        c.Controls.Add(cloudBox);
        c.Controls.Add(lanBox);
        serial.Width = 220; serial.PlaceholderText = "ex. 22E8...";
        c.Controls.Add(Field("Serialul imprimantei", serial));
        c.Controls.Add(printPopup);
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string>
            {
                ["bambu"] = B(bambuOn), ["cloud"] = prCloud.Checked ? "1" : "0", ["popup"] = B(printPopup),
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
        loginInfo.Text = "Aștept serverul Bambu...";
        loginInfo.ForeColor = Theme.Muted;
        try
        {
            var r = await app.ApiAsync(path, args, 30_000);
            if (r?["ok"]?.GetValue<bool>() != true) { Fail(loginInfo, (string?)r?["err"] ?? "Eroare"); return; }
            switch ((string?)r?["step"])
            {
                case "code": loginInfo.Text = "Ți-am trimis un cod pe email (verifică și Spam). Scrie-l și apasă Conectează."; loginInfo.ForeColor = Theme.Accent; break;
                case "done":
                    int n = r?["devices"]?.AsArray().Count ?? 0;
                    loginInfo.Text = $"Conectat. Imprimante în cont: {n}." + (n > 1 ? " Scrie serialul celei dorite și salvează." : "");
                    loginInfo.ForeColor = Theme.Accent;
                    await LoadAsync();
                    break;
            }
        }
        catch (Exception e) { Fail(loginInfo, e.Message); }
    }

    void BuildClaude()
    {
        var c = NewCard("Claude Code");
        c.Controls.AddRange([clPopup, clDone]);
        c.Controls.Add(Field("Răspuns „lung” = peste (secunde)", clDoneSec));
        c.Controls.Add(characterLabel);
        c.Controls.Add(character);
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string> { ["clpop"] = B(clPopup), ["cldone"] = B(clDone), ["cldt"] = N(clDoneSec) };
            if (character.Visible && character.SelectedIndex >= 0) f["char"] = character.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            return f;
        });
    }

    void BuildSecurity()
    {
        var c = NewCard("Securitate și conexiune");
        pagePass.Width = 220; pagePass.UseSystemPasswordChar = true; pagePass.PlaceholderText = "parolă nouă (utilizator admin)";
        c.Controls.Add(Field("Parola paginii plăcii (pentru browser și pentru setări pe WiFi)", pagePass));
        c.Controls.Add(pagePassInfo);
        pcIp.Width = 260; pcIp.PlaceholderText = "se completează singur";
        c.Controls.Add(Field("IP-ul PC-ului (placa îi trimite semnale, ca aplicația s-o găsească și din altă rețea)", pcIp));
        SaveRow(c, () =>
        {
            var f = new Dictionary<string, string> { ["pcip"] = pcIp.Text.Trim() };
            if (pagePass.Text.Length > 0) { f["wp"] = pagePass.Text; app.Settings.BoardPassword = pagePass.Text; app.Settings.Save(); }
            pagePass.Text = "";
            return f;
        });
        appPass.Width = 220; appPass.UseSystemPasswordChar = true; appPass.PlaceholderText = "parola paginii";
        var saveApp = Theme.Button("Ține minte");
        saveApp.Click += async (_, _) => { app.Settings.BoardPassword = appPass.Text; app.Settings.Save(); appPass.Text = ""; await LoadAsync(); };
        c.Controls.Add(Field("Parola salvată în aplicație (dacă placa are deja parolă)", Theme.Row(appPass, saveApp)));
        var reset = Theme.Button("Șterge toate setările plăcii");
        reset.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), "Ștergi toate setările plăcii (WiFi, imprimantă, ecrane)? Placa repornește în modul setup.", "DeskBuddy",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            try { await app.ApiAsync("/api/reset"); state.Text = "Setările plăcii au fost șterse; repornește."; }
            catch (Exception e) { Fail(state, e.Message); }
        };
        c.Controls.Add(Theme.Row(reset));
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
        if (!app.DeviceFound) { state.Text = "Placa nu e conectată (USB sau WiFi)."; state.ForeColor = Theme.Warn; SetEnabled(false); }
    }

    async Task LoadAsync()
    {
        if (loading) return;
        loading = true;
        state.Text = "Citesc setările plăcii...";
        state.ForeColor = Theme.Muted;
        try
        {
            cfg = await app.ApiAsync("/api/config");
            Fill(cfg!);
            SetEnabled(true);
            var b = app.UsbBoard ?? app.WifiBoard;
            state.Text = $"Firmware {b?.Version} · " + (app.UsbBoard != null ? "prin USB" : "prin WiFi");
            state.ForeColor = Theme.Accent;
        }
        catch (UnauthorizedAccessException)
        {
            Fail(state, "Placa are parolă: scrie-o mai jos, la „Parola salvată în aplicație”.");
            SetEnabled(false);
            foreach (Control x in cards[^1].Controls) x.Enabled = true;
            cards[^1].Enabled = true;
        }
        catch (Exception e) { Fail(state, "Nu pot citi setările: " + e.Message); }
        finally { loading = false; }
    }

    async Task SaveAsync(Dictionary<string, string> f, Label info, Button btn)
    {
        btn.Enabled = false;
        info.Text = "Salvez...";
        info.ForeColor = Theme.Muted;
        try
        {
            var r = await app.ApiAsync("/api/set", f);
            if (r?["ok"]?.GetValue<bool>() != true) { Fail(info, (string?)r?["err"] ?? "Placa a refuzat."); return; }
            bool restart = r?["restart"]?.GetValue<bool>() == true;
            info.Text = restart ? "Salvat. Placa repornește..." : "Salvat.";
            info.ForeColor = Theme.Accent;
            if (restart) { cfg = null; await Task.Delay(8000); }
            await LoadAsync();
        }
        catch (Exception e) { Fail(info, e.Message); }
        finally { btn.Enabled = true; }
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
        autoClaude.Checked = c["aclaude"] == null || G("aclaude");
        autoGame.Checked = c["agame"] == null || G("agame");
        int it = I("idlet", 10);
        idleMin.SelectedIndex = Math.Max(0, Array.FindIndex(IdleMins, x => x >= it));
        idleMin.Enabled = idleMode.SelectedIndex > 0;

        // afișaj
        ctr.Value = Math.Clamp(I("ctr", 100), 0, 100); nctr.Value = Math.Clamp(I("nctr", 10), 0, 100);
        ns.Value = Math.Clamp(I("ns", 22), 0, 23); ne.Value = Math.Clamp(I("ne", 7), 0, 23);
        np.Value = Math.Clamp(I("np", 10), 0, 100); ledp.Value = Math.Clamp(I("ledp", 100), 5, 100);
        nightOff.Checked = G("noff"); h12.Checked = G("h12"); clockSec.Checked = G("csec");
        try { ambientColor = ColorTranslator.FromHtml(S("amb")); } catch (Exception) { }
        ambient.BackColor = ambientColor;
        string z = S("tz");
        int zi = Array.FindIndex(Zones, x => x.Tz == z);
        if (zi < 0 && z.Length > 0) { if (tz.Items.Count == Zones.Length) tz.Items.Add("personalizat: " + z); zi = Zones.Length; }
        tz.SelectedIndex = zi;

        // vreme
        wxAuto.Checked = c["wauto"] == null || G("wauto");
        wxCity.Checked = !wxAuto.Checked;
        fahrenheit.Checked = G("wf");
        if (c["wlat"] is JsonValue la && c["wlon"] is JsonValue lo)
        {
            cityPick = (la.GetValue<double>(), lo.GetValue<double>(), S("wcity"));
            cityNow.Text = "Orașul salvat: " + (S("wcity").Length > 0 ? S("wcity") : "?");
        }
        else cityNow.Text = "";

        // WiFi
        var b = app.UsbBoard ?? app.WifiBoard;
        ssid.Text = S("ssid");
        wifiNow.Text = G("online") ? $"Conectată la {S("ssid")} ({S("ip")})" : S("ssid").Length > 0 ? $"Rețeaua salvată: {S("ssid")}, neconectată" : "Nicio rețea salvată.";
        wifiNow.ForeColor = G("online") ? Theme.Accent : Theme.Warn;

        // imprimantă
        bambuOn.Checked = G("bambu");
        prCloud.Checked = G("cloud"); prLan.Checked = !prCloud.Checked;
        cloudBox.Visible = prCloud.Checked; lanBox.Visible = !prCloud.Checked;
        region.SelectedIndex = S("host").StartsWith("cn.", StringComparison.Ordinal) ? 1 : 0;
        uid.Text = S("uid");
        token.PlaceholderText = G("hasToken") ? "token salvat (gol = îl păstrez)" : "token";
        printerIp.Text = S("bip");
        accessCode.PlaceholderText = G("hasCode") ? "salvat (gol = îl păstrez)" : "access code";
        serial.Text = S("serial");
        printPopup.Checked = G("popup");

        // Claude
        clPopup.Checked = G("clpop"); clDone.Checked = G("cldone");
        clDoneSec.Value = Math.Clamp(I("cldt", 15), 0, 250);
        var chars = c["chars"]?.AsArray();
        character.Visible = characterLabel.Visible = chars is { Count: > 1 };
        character.Items.Clear();
        if (chars != null) { foreach (var ch in chars) character.Items.Add((string?)ch ?? "?"); character.SelectedIndex = Math.Clamp(I("char"), 0, chars.Count - 1); }

        // securitate
        pagePassInfo.Text = G("hasWp") ? "Placa are parolă. Scrie una nouă doar dacă vrei s-o schimbi." : "Placa n-are parolă: oricine din rețea îi poate schimba setările.";
        pagePassInfo.ForeColor = G("hasWp") ? Theme.Muted : Theme.Warn;
        pcIp.Text = S("pcip");
    }

    void SetEnabled(bool on)
    {
        foreach (var c in cards) c.Enabled = on;
    }

    static string B(CheckBox c) => c.Checked ? "1" : "0";
    static string N(NumericUpDown n) => ((int)n.Value).ToString(CultureInfo.InvariantCulture);
    static void Fail(Label l, string text) { l.Text = text; l.ForeColor = Theme.Bad; }
}
