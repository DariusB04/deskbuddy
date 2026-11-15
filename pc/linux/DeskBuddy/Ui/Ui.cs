using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace DeskBuddy;

/// Culorile și piesele de interfață, la fel ca aplicația de Windows (Theme.cs) și pagina plăcii.
static class Ui
{
    public static readonly Color BgC = Color.Parse("#0f1210"), NavC = Color.Parse("#131714"), NavHoverC = Color.Parse("#1b211c"),
        CardC = Color.Parse("#181d19"), InputC = Color.Parse("#0c0f0d"), LineC = Color.Parse("#2a322c"), FgC = Color.Parse("#e7ece8"),
        MutedC = Color.Parse("#8d998f"), AccentC = Color.Parse("#9fd36b"), WarnC = Color.Parse("#f0b35a"), BadC = Color.Parse("#ef6b6b");
    public static readonly IBrush Bg = new SolidColorBrush(BgC), Nav = new SolidColorBrush(NavC), NavHover = new SolidColorBrush(NavHoverC),
        CardBg = new SolidColorBrush(CardC), Input = new SolidColorBrush(InputC), Line = new SolidColorBrush(LineC), Fg = new SolidColorBrush(FgC),
        Muted = new SolidColorBrush(MutedC), Accent = new SolidColorBrush(AccentC), Warn = new SolidColorBrush(WarnC), Bad = new SolidColorBrush(BadC);

    public static IBrush TempColor(float? t) => t switch { null => Muted, < 60 => Accent, < 80 => Warn, _ => Bad };

    /// stilurile comune: butoane închise la culoare (clasa "primary" = verde), câmpuri cu fundal închis
    public static void AddStyles(Styles s)
    {
        s.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Bg), new Setter(TemplatedControl.ForegroundProperty, Fg) } });
        s.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Input), new Setter(TemplatedControl.ForegroundProperty, Fg),
                new Setter(TemplatedControl.BorderBrushProperty, Line), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(14, 6)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
            },
        });
        s.Add(new Style(x => x.OfType<Button>().Class("primary"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Accent), new Setter(TemplatedControl.ForegroundProperty, Bg), new Setter(TemplatedControl.BorderBrushProperty, Accent) },
        });
        s.Add(new Style(x => x.OfType<TextBox>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Input), new Setter(TemplatedControl.BorderBrushProperty, Line) } });
        s.Add(new Style(x => x.OfType<ComboBox>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Input), new Setter(TemplatedControl.BorderBrushProperty, Line) } });
        s.Add(new Style(x => x.OfType<NumericUpDown>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Input), new Setter(TemplatedControl.BorderBrushProperty, Line) } });
    }

    public static TextBlock Label(string text, double size = 14, IBrush? color = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Fg, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
    };

    public static TextBlock Note(string text) => Label(text, 12.5, Muted);
    public static TextBlock FieldLabel(string text) => new() { Text = text, FontSize = 12.5, Foreground = Muted, Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };

    public static Button Button(string text, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 4, 8, 4) };
        if (primary) b.Classes.Add("primary");
        return b;
    }

    public static void SetPrimary(Button b, bool on)
    {
        if (on && !b.Classes.Contains("primary")) b.Classes.Add("primary");
        else if (!on) b.Classes.Remove("primary");
    }

    public static CheckBox Check(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Foreground = Fg, Margin = new Thickness(0, 2) };
    public static TextBox TextBox(double width = 260) => new() { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 8, 2) };
    public static ComboBox Combo(double width = 260) => new() { Width = width, Margin = new Thickness(0, 2, 8, 2) };
    public static NumericUpDown Num(int min, int max, double width = 120) => new() { Minimum = min, Maximum = max, Width = width, Increment = 1, FormatString = "0", Margin = new Thickness(0, 2, 8, 2) };

    public static StackPanel Row(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var c in items) p.Children.Add(c);
        return p;
    }

    public static StackPanel Field(string label, Control c)
    {
        var p = new StackPanel();
        p.Children.Add(FieldLabel(label));
        p.Children.Add(c);
        return p;
    }



    public static void Fail(TextBlock t, string msg) { t.Text = msg; t.Foreground = Bad; }
    public static void Ok(TextBlock t, string msg) { t.Text = msg; t.Foreground = Accent; }

    public static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? target : "xdg-open", OperatingSystem.IsWindows() ? "" : target) { UseShellExecute = OperatingSystem.IsWindows() }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// bară cu titlu, valoare în dreapta și umplere (temperaturi, load, usage), ca MetricBar de pe Windows
sealed class MetricBar : Control
{
    readonly string label;
    string value = "--", sub = "";
    float? pct;
    IBrush color = Ui.Muted;

    public MetricBar(string label) { this.label = label; Height = 44; Margin = new Thickness(0, 2, 0, 6); }

    public void Set(string value, float? pct, IBrush color, string sub = "")
    {
        if (this.value == value && this.pct == pct && this.sub == sub && ReferenceEquals(this.color, color)) return;
        this.value = value; this.pct = pct; this.color = color; this.sub = sub;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        double w = Bounds.Width;
        var tf = new Typeface(FontFamily.Default);
        var l = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 13.5, Ui.Muted);
        var v = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 15, Ui.Fg);
        g.DrawText(l, new Point(0, 2));
        g.DrawText(v, new Point(w - v.Width, 0));
        if (sub.Length > 0)
        {
            var s = new FormattedText(sub, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11.5, Ui.Muted);
            g.DrawText(s, new Point(l.Width + 10, 4));
        }
        var track = new Rect(0, 28, w, 6);
        g.DrawRectangle(Ui.Line, null, track, 3, 3);
        if (pct is float p && p > 0) g.DrawRectangle(color, null, new Rect(0, 28, w * Math.Clamp(p, 0, 100) / 100, 6), 3, 3);
    }
}

/// câmp de text cu listă (rețelele WiFi găsite), ca ComboBox-ul editabil de pe Windows
sealed class EditCombo : AutoCompleteBox
{
    public List<string> Items { get; } = [];
    public EditCombo() { MinimumPrefixLength = 0; FilterMode = AutoCompleteFilterMode.Contains; }
    public void Refresh() => ItemsSource = Items.ToList();
    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);
}
