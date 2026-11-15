using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Ecranul plăcii, live (imaginea exactă de pe OLED, de câteva ori pe secundă), plus butoanele plăcii:
/// înapoi / următorul (apăsare scurtă), LED (apăsare lungă) și saltul direct pe un ecran. Merge pe USB și pe WiFi.
/// Săgețile stânga / dreapta și Space fac același lucru când pagina e deschisă.
sealed class ScreenPage : Page
{
    static readonly string[] LedNames = ["AUTO", "MAX", "OFF"];
    readonly TrayApp app;
    readonly OledView oled = new();
    readonly Button prev = Theme.Button(L.T("◀  Înapoi", "◀  Back")), next = Theme.Button(L.T("Următorul  ▶", "Next  ▶"), primary: true), led = Theme.Button("LED: ?"), tap = Theme.Button(L.T("Apasă butonul", "Press button"));
    readonly FlowLayoutPanel jump = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0), BackColor = Theme.Card, Tag = "fill" };
    readonly Label info = Note("");
    readonly List<Button> jumpButtons = [];
    List<string> names = [];
    int mode = -1;
    bool running, sending;

    public ScreenPage(TrayApp app) : base(L.T("Ecran", "Screen"), L.T("Ecranul plăcii, live, și butonul ei: îl poți folosi de aici dacă butonul fizic nu merge.", "The board's screen, live, and its button: you can use it from here if the physical button doesn't work."))
    {
        this.app = app;
        var c = AddCard(L.T("Placa acum", "The board now"));
        c.Controls.Add(oled);
        c.Controls.Add(Theme.Row(prev, next, tap, led));
        c.Controls.Add(Theme.FieldLabel(L.T("Sari direct pe", "Jump straight to")));
        c.Controls.Add(jump);
        c.Controls.Add(info);
        c.Controls.Add(Note(L.T("Săgețile ← → schimbă ecranul cât fereastra e deschisă pe pagina asta; Space = butonul apăsat scurt (în JOC sare). „LED” = apăsarea lungă a butonului.", "The ← → arrows change the screen while this page is open; Space = a short press of the button (jumps in GAME). “LED” = a long press of the button.")));
        prev.Click += async (_, _) => await PressAsync(new() { ["a"] = "prev" });
        next.Click += async (_, _) => await PressAsync(new() { ["a"] = "next" });
        tap.Click += async (_, _) => await PressAsync(new() { ["a"] = "tap" });
        led.Click += async (_, _) => await PressAsync(new() { ["a"] = "led" });
        PreviewKeyDown += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Space) e.IsInputKey = true; };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Left) { e.Handled = true; await PressAsync(new() { ["a"] = "prev" }); }
            else if (e.KeyCode == Keys.Right) { e.Handled = true; await PressAsync(new() { ["a"] = "next" }); }
            else if (e.KeyCode == Keys.Space) { e.Handled = true; await PressAsync(new() { ["a"] = "tap" }); }
        };
        VisibleChanged += (_, _) => { if (Visible) Start(); };
    }

    public override void OnShown()
    {
        base.OnShown();
        Focus();
        Start();
    }

    void Start()
    {
        if (running) return;
        running = true;
        _ = LoopAsync();
    }

    /// cât pagina e vizibilă: cere imaginea ecranului în buclă (pe USB ~4 / s, pe WiFi mai des)
    async Task LoopAsync()
    {
        try
        {
            while (!IsDisposed && Visible && FindForm()?.Visible == true)
            {
                if (!app.DeviceFound) { oled.Clear(L.T("placa nu e conectată", "the board isn't connected")); SetButtons(false); await Task.Delay(1000); continue; }
                if (names.Count == 0) await LoadNamesAsync();
                try
                {
                    var r = await app.ApiAsync("/api/screen", timeoutMs: 3000);
                    if (r?["px"] is JsonValue px) oled.SetFrame(Convert.FromBase64String((string)px!), r["off"]?.GetValue<bool>() == true);
                    UpdateState((int?)r?["mode"] ?? -1, (int?)r?["led"] ?? -1);
                    info.Text = app.UsbBoard != null ? L.T("prin USB", "over USB") : L.T("prin WiFi", "over WiFi");
                    info.ForeColor = Theme.Muted;
                    SetButtons(true);
                }
                catch (UnauthorizedAccessException) { oled.Clear(L.T("placa are parolă", "the board has a password")); info.Text = L.T("Pune parola plăcii în pagina Placă → Securitate.", "Enter the board's password on Board → Security."); await Task.Delay(3000); }
                catch (Exception e) { info.Text = L.T("Placa nu răspunde: ", "The board isn't responding: ") + e.Message; info.ForeColor = Theme.Warn; await Task.Delay(1000); }
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
            jump.Controls.Clear();
            jumpButtons.Clear();
            for (int i = 0; i < names.Count; i++)
            {
                int idx = i;
                var b = Theme.Button(names[i]);
                b.Click += async (_, _) => await PressAsync(new() { ["m"] = idx.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                jumpButtons.Add(b);
                jump.Controls.Add(b);
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
        catch (Exception e) { info.Text = "N-a mers: " + e.Message; info.ForeColor = Theme.Bad; }
        finally { sending = false; }
    }

    void UpdateState(int m, int l)
    {
        if (l is >= 0 and < 3) led.Text = "LED: " + LedNames[l];
        if (m == mode) return;
        mode = m;
        for (int i = 0; i < jumpButtons.Count; i++)
        {
            bool on = i == m;
            jumpButtons[i].BackColor = on ? Theme.Accent : Theme.Input;
            jumpButtons[i].ForeColor = on ? Theme.Bg : Theme.Fg;
            jumpButtons[i].FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Line;
        }
    }

    void SetButtons(bool on) { prev.Enabled = next.Enabled = led.Enabled = tap.Enabled = on; foreach (var b in jumpButtons) b.Enabled = on; }
}

/// OLED-ul plăcii, mărit: 128 x 64 de pixeli albaștri, fiecare desenat ca un pătrățel (cu spațiu între ei, ca pe ecranul real)
sealed class OledView : Control
{
    static readonly Color On = Color.FromArgb(110, 200, 255), Off = Color.FromArgb(10, 16, 22), Bezel = Color.FromArgb(4, 6, 8);
    byte[] frame = new byte[1024];
    bool dark = true;
    string? message = L.T("se încarcă...", "loading...");

    public OledView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Tag = "fill";
        Margin = new Padding(0, 6, 0, 10);
        Height = 64 * 4 + 28;
    }

    public void SetFrame(byte[] buf, bool screenOff)
    {
        if (buf.Length < 1024) return;
        frame = buf;
        dark = screenOff;
        message = screenOff ? L.T("ecranul e stins (noaptea)", "the screen is off (night)") : null;
        Invalidate();
    }

    public void Clear(string text)
    {
        if (message == text && dark) return;
        dark = true;
        message = text;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int scale = Math.Clamp((Width - 28) / 128, 2, 6);
        int h = 64 * scale + 28;
        if (Height != h) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int scale = Math.Clamp((Width - 28) / 128, 2, 6);
        int w = 128 * scale, h = 64 * scale;
        int x0 = (Width - w) / 2, y0 = (Height - h) / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bezel = Theme.Rounded(new RectangleF(x0 - 12, y0 - 12, w + 24, h + 24), 10))
        using (var bb = new SolidBrush(Bezel)) g.FillPath(bb, bezel);
        g.SmoothingMode = SmoothingMode.None;
        using (var off = new SolidBrush(Off)) g.FillRectangle(off, x0, y0, w, h);
        if (!dark)
        {
            using var on = new SolidBrush(On);
            int px = Math.Max(1, scale - (scale >= 4 ? 1 : 0));        // pătrățel cu spațiu între pixeli
            for (int page = 0; page < 8; page++)
                for (int col = 0; col < 128; col++)
                {
                    byte b = frame[page * 128 + col];
                    if (b == 0) continue;
                    for (int bit = 0; bit < 8; bit++)
                        if ((b & (1 << bit)) != 0) g.FillRectangle(on, x0 + col * scale, y0 + (page * 8 + bit) * scale, px, px);
                }
        }
        if (message != null)
            TextRenderer.DrawText(g, message, Theme.Body, new Rectangle(x0, y0, w, h), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
