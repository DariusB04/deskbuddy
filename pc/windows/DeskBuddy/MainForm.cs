namespace DeskBuddy;

/// Fereastra DeskBuddy: meniu în stânga (Acasă / Placă / PC / Claude Code / Aplicație), pagina aleasă în dreapta.
/// X o ascunde în tray (setare); aplicația continuă să trimită datele.
sealed class MainForm : Form
{
    readonly TrayApp app;
    readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    readonly FlowLayoutPanel navList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Nav };
    readonly Label navStatus = Theme.Label("", Theme.Small, Theme.Muted);
    readonly List<(NavButton Btn, Control Page)> pages = [];
    int current = -1;

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
        logo.Controls.Add(new Label { Text = "pentru Windows", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(S(14), S(30)) });
        navStatus.Dock = DockStyle.Bottom;
        navStatus.Padding = new Padding(S(12), 0, 0, 0);
        nav.Controls.Add(navList);                    // ordinea contează la Dock: ultimul adăugat se așază primul
        nav.Controls.Add(logo);
        nav.Controls.Add(navStatus);
        nav.Resize += (_, _) => { foreach (Control c in navList.Controls) c.Width = nav.ClientSize.Width - nav.Padding.Horizontal; };

        Controls.Add(content);                        // Fill întâi, ca să ia ce rămâne după meniul din stânga
        Controls.Add(nav);

        AddPage("Acasă", new HomePage(app));
        AddPage("Ecran", new ScreenPage(app));
        AddPage("Placă", new BoardPage(app));
        AddPage("PC", new PcPage(app));
        AddPage("Claude Code", new ClaudePage(app));
        AddPage("Aplicație", new AppPage(app));
        ResumeLayout(true);

        app.Updated += OnUpdated;
        OnUpdated();
        Select(0);
    }

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    void AddPage(string name, Control page)
    {
        int index = pages.Count;
        var btn = new NavButton(name) { Width = S(200) };
        btn.Click += (_, _) => Select(index);
        navList.Controls.Add(btn);
        page.Visible = false;
        content.Controls.Add(page);
        pages.Add((btn, page));
    }

    public void ShowPage(string name)
    {
        int i = pages.FindIndex(p => p.Btn.Text == name);
        if (i >= 0) Select(i);
    }

    void Select(int index)
    {
        if (index == current) return;
        current = index;
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Btn.Selected = i == index;
            pages[i].Page.Visible = i == index;
        }
        switch (pages[index].Page)
        {
            case Page p: p.OnShown(); break;
        }
    }

    void OnUpdated()
    {
        if (app.UsbBoard is { } ub)
        {
            navStatus.Text = $"● pe USB ({ub.Port})";
            navStatus.ForeColor = Theme.Accent;
        }
        else if (app.WifiBoard is { } wb)
        {
            navStatus.Text = $"● pe WiFi ({wb.Ip})";
            navStatus.ForeColor = Theme.Accent;
        }
        else
        {
            navStatus.Text = "● caut DeskBuddy...";
            navStatus.ForeColor = Theme.Warn;
        }
        if (!Visible || current < 0) return;
        switch (pages[current].Page)
        {
            case Page p: p.RefreshData(); break;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!app.Quitting && e.CloseReason == CloseReason.UserClosing && app.Settings.CloseToTray)
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
        if (!app.Quitting) app.Quit();                // X fără „ascunde în tray” = ieșire
    }
}
