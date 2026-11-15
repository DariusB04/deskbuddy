namespace DeskBuddy;

/// Fereastra DeskBuddy: meniu în stânga (Acasă / Placă / PC / Claude Code / Aplicație), pagina aleasă în dreapta.
/// X o ascunde în tray (setare); aplicația continuă să trimită datele.
sealed class MainForm : Form
{
    readonly TrayApp app;
    readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    readonly FlowLayoutPanel navList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Nav };
    readonly Label navStatus = Theme.Label("", Theme.Small, Theme.Muted);
    readonly List<(NavButton Btn, Func<Control> Make)> pages = [];
    readonly Dictionary<int, Control> built = [];
    int current = -1;
    public bool Replacing;
    public static bool PerfLog;                            // fereastra e refăcută (altă limbă): închiderea nu ascunde și nu iese

    public MainForm(TrayApp app)
    {
        this.app = app;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;           // mărimile vin din font / DPI, nu se mai scalează o dată
        Text = "DeskBuddy";
        Icon = app.WindowIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(1040), S(720));
        MinimumSize = new Size(S(760), S(520));

        var nav = new Panel { Dock = DockStyle.Left, Width = S(220), BackColor = Theme.Nav, Padding = new Padding(S(10), S(18), S(10), S(14)) };
        var logo = new Panel { Dock = DockStyle.Top, Height = S(64), BackColor = Theme.Nav };
        logo.Controls.Add(new Label { Text = "DeskBuddy", Font = Theme.Logo, ForeColor = Theme.Fg, AutoSize = true, Location = new Point(S(12), 0) });
        logo.Controls.Add(new Label { Text = L.T("pentru Windows", "for Windows"), Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(S(14), S(30)) });
        navStatus.Dock = DockStyle.Bottom;
        navStatus.Padding = new Padding(S(12), 0, 0, 0);
        nav.Controls.Add(navList);                    // ordinea contează la Dock: ultimul adăugat se așază primul
        nav.Controls.Add(logo);
        nav.Controls.Add(navStatus);
        nav.Resize += (_, _) => { foreach (Control c in navList.Controls) c.Width = nav.ClientSize.Width - nav.Padding.Horizontal; };

        Controls.Add(content);                        // Fill întâi, ca să ia ce rămâne după meniul din stânga
        Controls.Add(nav);

        // paginile se construiesc abia când intri prima dată în ele (Placă are câteva sute de controale)
        AddPage(L.T("Acasă", "Home"), () => new HomePage(app));
        AddPage(L.T("Ecran", "Screen"), () => new ScreenPage(app));
        AddPage(L.T("Placă", "Board"), () => new BoardPage(app));
        AddPage("PC", () => new PcPage(app));
        AddPage("Claude Code", () => new ClaudePage(app));
        AddPage(L.T("Aplicație", "App"), () => new AppPage(app));
        ResumeLayout(true);

        app.Updated += OnUpdated;
        OnUpdated();
        Select(0);
    }

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    void AddPage(string name, Func<Control> make)
    {
        int index = pages.Count;
        var btn = new NavButton(name) { Width = S(200) };
        btn.Click += (_, _) => Select(index);
        navList.Controls.Add(btn);
        pages.Add((btn, make));
    }

    Control PageAt(int i)
    {
        if (built.TryGetValue(i, out var p)) return p;
        p = pages[i].Make();
        p.Visible = false;
        content.Controls.Add(p);
        built[i] = p;
        return p;
    }

    public void ShowAppPage() => Select(pages.Count - 1);

    // pentru DeskBuddy.exe perf
    public int PageCount => pages.Count;
    public string PageName(int i) => pages[i].Btn.Text;
    public void SelectPage(int i) => Select(i);
    public void TickForPerf() => OnUpdated();

    void Select(int index)
    {
        if (index == current) return;
        current = index;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = PageAt(index);
        long tMake = sw.ElapsedMilliseconds;
        content.SuspendLayout();
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Btn.Selected = i == index;
            if (built.TryGetValue(i, out var p)) p.Visible = i == index;
        }
        content.ResumeLayout(true);
        long tVis = sw.ElapsedMilliseconds;
        if (page is Page pg) pg.OnShown();
        if (PerfLog) Console.WriteLine($"   [{pages[index].Btn.Text}] construit {tMake} ms, vizibil {tVis - tMake} ms, OnShown {sw.ElapsedMilliseconds - tVis} ms");
    }

    void OnUpdated()
    {
        if (app.UsbBoard is { } ub)
        {
            navStatus.Text = L.T($"● pe USB ({ub.Port})", $"● on USB ({ub.Port})");
            navStatus.ForeColor = Theme.Accent;
        }
        else if (app.WifiBoard is { } wb)
        {
            navStatus.Text = L.T($"● pe WiFi ({wb.Ip})", $"● on WiFi ({wb.Ip})");
            navStatus.ForeColor = Theme.Accent;
        }
        else
        {
            navStatus.Text = L.T("● caut DeskBuddy...", "● looking for DeskBuddy...");
            navStatus.ForeColor = Theme.Warn;
        }
        if (!Visible || current < 0 || !built.TryGetValue(current, out var page)) return;
        if (page is Page p) { p.RefreshData(); p.FitIfNeeded(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!Replacing && !app.Quitting && e.CloseReason == CloseReason.UserClosing && app.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            app.NotifyHiddenToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        app.Updated -= OnUpdated;
        base.OnFormClosed(e);
        if (!app.Quitting && !Replacing) app.Quit();  // X fără „ascunde în tray” = ieșire
    }
}
