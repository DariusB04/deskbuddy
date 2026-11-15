using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeskBuddy;

/// Fereastra: meniul din stânga (Acasă, Ecran, Placă, PC, Claude Code, Aplicație), pagina aleasă în dreapta.
/// Paginile se construiesc la prima deschidere. X o ascunde în tray (setare).
sealed class MainWindow : Window
{
    readonly TrayApp app;
    readonly Panel content = new();
    readonly StackPanel nav = new() { Spacing = 2 };
    readonly TextBlock navStatus = new() { FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly List<(Button Btn, Func<PageBase> Make)> pages = [];
    readonly Dictionary<int, PageBase> built = [];
    int current = -1;
    public bool Replacing;

    public MainWindow(TrayApp app)
    {
        this.app = app;
        Title = "DeskBuddy";
        Width = 1040; Height = 720; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Icons.Window(Ui.AccentC);

        var left = new DockPanel { Background = Ui.Nav, Width = 220 };
        var logo = new StackPanel { Margin = new Thickness(22, 18, 10, 18) };
        logo.Children.Add(new TextBlock { Text = "DeskBuddy", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Ui.Fg });
        logo.Children.Add(new TextBlock { Text = L.T("pentru Linux", "for Linux"), FontSize = 12, Foreground = Ui.Muted });
        DockPanel.SetDock(logo, Dock.Top);
        left.Children.Add(logo);
        var bottom = new Border { Child = navStatus, Padding = new Thickness(10, 0, 10, 14) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        left.Children.Add(bottom);
        nav.Margin = new Thickness(10, 0);
        left.Children.Add(nav);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(content);
        Content = root;

        AddPage(L.T("Acasă", "Home"), () => new HomePage(app));
        AddPage(L.T("Ecran", "Screen"), () => new ScreenPage(app));
        AddPage(L.T("Placă", "Board"), () => new BoardPage(app));
        AddPage("PC", () => new PcPage(app));
        AddPage("Claude Code", () => new ClaudePage(app));
        AddPage(L.T("Aplicație", "App"), () => new AppPage(app));

        app.Updated += OnUpdated;
        OnUpdated();
        Select(0);
        KeyDown += (_, e) => { if (built.GetValueOrDefault(current) is ScreenPage sp) sp.OnKey(e); };
        if (Environment.GetEnvironmentVariable("DESKBUDDY_SHOTS") is { Length: > 0 } dir) Opened += async (_, _) => await ShotsAsync(dir);
    }

    /// DESKBUDDY_SHOTS=<folder>: câte o captură pe pagină, apoi iese (pentru testarea interfeței, și pe Windows)
    async Task ShotsAsync(string dir)
    {
        await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("DESKBUDDY_SHOTS_WAIT"), out int wait) ? wait : 2500);
        for (int i = 0; i < pages.Count; i++)
        {
            Select(i);
            await Task.Delay(1200);
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(Path.Combine(dir, $"linux_page{i}.png"));
        }
        app.Quit();
    }

    void AddPage(string name, Func<PageBase> make)
    {
        int index = pages.Count;
        var b = new Button
        {
            Content = name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14, 10), Foreground = Ui.Muted,
            Margin = new Thickness(0),
        };
        b.Click += (_, _) => Select(index);
        nav.Children.Add(b);
        pages.Add((b, make));
    }

    public void ShowAppPage() => Select(pages.Count - 1);

    void Select(int index)
    {
        if (index == current) return;
        current = index;
        if (!built.TryGetValue(index, out var page))
        {
            page = pages[index].Make();
            built[index] = page;
            content.Children.Add(page);
        }
        for (int i = 0; i < pages.Count; i++)
        {
            bool on = i == index;
            pages[i].Btn.Foreground = on ? Ui.Fg : Ui.Muted;
            pages[i].Btn.Background = on ? Ui.NavHover : Brushes.Transparent;
            if (built.TryGetValue(i, out var p)) p.IsVisible = on;
        }
        page.OnShown();
    }

    void OnUpdated()
    {
        if (app.UsbBoard is { } ub) { navStatus.Text = L.T($"● pe USB ({ub.Port})", $"● on USB ({ub.Port})"); navStatus.Foreground = Ui.Accent; }
        else if (app.WifiBoard is { } wb) { navStatus.Text = L.T($"● pe WiFi ({wb.Ip})", $"● on WiFi ({wb.Ip})"); navStatus.Foreground = Ui.Accent; }
        else { navStatus.Text = L.T("● caut DeskBuddy...", "● looking for DeskBuddy..."); navStatus.Foreground = Ui.Warn; }
        if (IsVisible && built.TryGetValue(current, out var page)) page.RefreshData();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Replacing && !app.Quitting && app.Settings.CloseToTray && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        app.Updated -= OnUpdated;
        base.OnClosing(e);
        if (!Replacing && !app.Quitting && !app.Settings.CloseToTray) app.Quit();
    }
}

/// pagina: titlu, subtitlu și carduri una sub alta, cu scroll; lățime maximă ca textul să nu se întindă pe tot ecranul
abstract class PageBase : ScrollViewer
{
    readonly StackPanel stack = new() { Margin = new Thickness(32, 24, 32, 24), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };

    protected PageBase(string title, string subtitle)
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        stack.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Ui.Fg });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 14, Foreground = Ui.Muted, Margin = new Thickness(0, 2, 0, 18), TextWrapping = TextWrapping.Wrap });
        Content = stack;
        // cardurile au lățimea paginii (max 780), nu cât textul din ele
        SizeChanged += (_, e) => stack.Width = Math.Clamp(e.NewSize.Width - 64, 320, 780);
    }

    protected Card AddCard(string title)
    {
        var card = new Card(title);
        stack.Children.Add(card);
        return card;
    }

    protected void AddRow(Control c) => stack.Children.Add(c);

    public virtual void OnShown() => RefreshData();
    public virtual void RefreshData() { }
}
