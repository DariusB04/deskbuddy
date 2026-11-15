using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DeskBuddy;

/// Ecranul plăcii, live (imaginea exactă de pe OLED), plus butoanele ei: înapoi / următorul, LED și saltul direct pe un ecran.
/// Săgețile stânga / dreapta și Space fac același lucru cât pagina e deschisă. (Ca ScreenPage.cs de pe Windows.)
sealed class ScreenPage : PageBase
{
    static readonly string[] LedNames = ["AUTO", "MAX", "OFF"];
    readonly TrayApp app;
    readonly OledView oled = new();
    readonly Button prev = Ui.Button(L.T("◀  Înapoi", "◀  Back")), next = Ui.Button(L.T("Următorul  ▶", "Next  ▶"), primary: true), led = Ui.Button("LED: ?");
    readonly WrapPanel jump = new() { Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock info = Ui.Note("");
    readonly List<Button> jumpButtons = [];
    List<string> names = [];
    int mode = -1;
    bool running, sending;

    public ScreenPage(TrayApp app) : base(L.T("Ecran", "Screen"), L.T("Ecranul plăcii, live, și butonul ei: îl poți folosi de aici dacă butonul fizic nu merge.", "The board's screen, live, and its button: you can use it from here if the physical button doesn't work."))
    {
        this.app = app;
        var c = AddCard(L.T("Placa acum", "The board now"));
        c.Children.Add(oled);
        c.Children.Add(Ui.Row(prev, next, led));
        c.Children.Add(Ui.FieldLabel(L.T("Sari direct pe", "Jump straight to")));
        c.Children.Add(jump);
        c.Children.Add(info);
        c.Children.Add(Ui.Note(L.T("Săgețile ← → și Space schimbă ecranul cât fereastra e deschisă pe pagina asta. „LED” = apăsarea lungă a butonului.", "The ← → arrows and Space change the screen while this page is open. “LED” = a long press of the button.")));
        prev.Click += async (_, _) => await PressAsync(new() { ["a"] = "prev" });
        next.Click += async (_, _) => await PressAsync(new() { ["a"] = "next" });
        led.Click += async (_, _) => await PressAsync(new() { ["a"] = "led" });
    }

    public async void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Left) { e.Handled = true; await PressAsync(new() { ["a"] = "prev" }); }
        else if (e.Key is Key.Right or Key.Space) { e.Handled = true; await PressAsync(new() { ["a"] = "next" }); }
    }

    public override void OnShown()
    {
        base.OnShown();
        if (running) return;
        running = true;
        _ = LoopAsync();
    }

    async Task LoopAsync()
    {
        try
        {
            while (IsVisible && TopLevel.GetTopLevel(this) is Window { IsVisible: true })
            {
                if (!app.DeviceFound) { oled.Clear(L.T("placa nu e conectată", "the board isn't connected")); SetButtons(false); await Task.Delay(1000); continue; }
                if (names.Count == 0) await LoadNamesAsync();
                try
                {
                    var r = await app.ApiAsync("/api/screen", timeoutMs: 3000);
                    if (r?["px"] is JsonValue px) oled.SetFrame(Convert.FromBase64String((string)px!), r["off"]?.GetValue<bool>() == true);
                    UpdateState((int?)r?["mode"] ?? -1, (int?)r?["led"] ?? -1);
                    info.Text = app.UsbBoard != null ? L.T("prin USB", "over USB") : L.T("prin WiFi", "over WiFi");
                    info.Foreground = Ui.Muted;
                    SetButtons(true);
                }
                catch (UnauthorizedAccessException) { oled.Clear(L.T("placa are parolă", "the board has a password")); info.Text = L.T("Pune parola plăcii în pagina Placă → Securitate.", "Enter the board's password on Board → Security."); await Task.Delay(3000); }
                catch (Exception e) { info.Text = L.T("Placa nu răspunde: ", "The board isn't responding: ") + e.Message; info.Foreground = Ui.Warn; await Task.Delay(1000); }
                await Task.Delay(app.UsbBoard != null ? 120 : 80);
            }
        }
        finally { running = false; }
    }

    async Task LoadNamesAsync()
    {
        try
        {
            var c = await app.ApiAsync("/api/config");
            names = c?["mnames"]?.AsArray().Select(x => (string?)x ?? "?").ToList() ?? [];
            jump.Children.Clear();
            jumpButtons.Clear();
            for (int i = 0; i < names.Count; i++)
            {
                int idx = i;
                var b = Ui.Button(names[i]);
                b.Click += async (_, _) => await PressAsync(new() { ["m"] = idx.ToString(CultureInfo.InvariantCulture) });
                jumpButtons.Add(b);
                jump.Children.Add(b);
            }
            mode = -1;
        }
        catch (Exception) { }
    }

    async Task PressAsync(Dictionary<string, string> args)
    {
        if (sending) return;
        sending = true;
        try
        {
            var r = await app.ApiAsync("/api/mode", args, 4000);
            UpdateState((int?)r?["mode"] ?? -1, (int?)r?["led"] ?? -1);
        }
        catch (Exception e) { info.Text = L.T("N-a mers: ", "It didn't work: ") + e.Message; info.Foreground = Ui.Bad; }
        finally { sending = false; }
    }

    void UpdateState(int m, int l)
    {
        if (l is >= 0 and < 3) led.Content = "LED: " + LedNames[l];
        if (m == mode) return;
        mode = m;
        for (int i = 0; i < jumpButtons.Count; i++) Ui.SetPrimary(jumpButtons[i], i == m);
    }

    void SetButtons(bool on) { prev.IsEnabled = next.IsEnabled = led.IsEnabled = on; foreach (var b in jumpButtons) b.IsEnabled = on; }
}

/// OLED-ul plăcii, mărit: 128 x 64 de pixeli albaștri, fiecare desenat ca un pătrățel (cu spațiu între ei, ca pe ecranul real)
sealed class OledView : Control
{
    static readonly IBrush On = new SolidColorBrush(Color.FromRgb(110, 200, 255)), Off = new SolidColorBrush(Color.FromRgb(10, 16, 22)),
        Bezel = new SolidColorBrush(Color.FromRgb(4, 6, 8));
    byte[] frame = new byte[1024];
    bool dark = true;
    string? message = L.T("se încarcă...", "loading...");

    public OledView() { Margin = new Thickness(0, 6, 0, 10); Height = 64 * 4 + 28; }

    public void SetFrame(byte[] buf, bool screenOff)
    {
        if (buf.Length < 1024) return;
        frame = buf;
        dark = screenOff;
        message = screenOff ? L.T("ecranul e stins (noaptea)", "the screen is off (night)") : null;
        InvalidateVisual();
    }

    public void Clear(string text)
    {
        if (message == text && dark) return;
        dark = true;
        message = text;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 560 : Math.Min(available.Width, 128 * 6 + 28);
        int scale = Math.Clamp((int)((w - 28) / 128), 2, 6);
        return new Size(w, 64 * scale + 28);
    }

    public override void Render(DrawingContext g)
    {
        // scara încape și pe lățime, și pe înălțimea primită (chenarul are 12 px pe fiecare parte)
        int scale = Math.Clamp(Math.Min((int)((Bounds.Width - 28) / 128), (int)((Bounds.Height - 28) / 64)), 1, 6);
        double w = 128 * scale, h = 64 * scale;
        double x0 = Math.Floor((Bounds.Width - w) / 2), y0 = Math.Floor((Bounds.Height - h) / 2);
        g.DrawRectangle(Bezel, null, new Rect(x0 - 12, y0 - 12, w + 24, h + 24), 10, 10);
        g.DrawRectangle(Off, null, new Rect(x0, y0, w, h));
        if (!dark)
        {
            int px = Math.Max(1, scale - (scale >= 4 ? 1 : 0));
            for (int page = 0; page < 8; page++)
                for (int col = 0; col < 128; col++)
                {
                    byte b = frame[page * 128 + col];
                    if (b == 0) continue;
                    for (int bit = 0; bit < 8; bit++)
                        if ((b & (1 << bit)) != 0) g.DrawRectangle(On, null, new Rect(x0 + col * scale, y0 + (page * 8 + bit) * scale, px, px));
                }
        }
        if (message != null)
        {
            var t = new FormattedText(message, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 14, Ui.Muted);
            g.DrawText(t, new Point(x0 + (w - t.Width) / 2, y0 + (h - t.Height) / 2));
        }
    }
}
