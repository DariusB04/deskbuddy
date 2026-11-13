using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DeskBuddy;

/// Aceleași culori ca pagina de setări a plăcii, ca fereastra și pagina să arate la fel.
static class Theme
{
    public static readonly Color Bg = Hex(0x0f1210);
    public static readonly Color Nav = Hex(0x131714);
    public static readonly Color NavHover = Hex(0x1b211c);
    public static readonly Color Card = Hex(0x181d19);
    public static readonly Color Input = Hex(0x0c0f0d);
    public static readonly Color Line = Hex(0x2a322c);
    public static readonly Color Fg = Hex(0xe7ece8);
    public static readonly Color Muted = Hex(0x8d998f);
    public static readonly Color Accent = Hex(0x9fd36b);
    public static readonly Color Warn = Hex(0xf0b35a);
    public static readonly Color Bad = Hex(0xef6b6b);

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Title = new("Segoe UI Semibold", 16f);
    public static readonly Font Logo = new("Segoe UI Semibold", 14f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 9f);
    public static readonly Font Value = new("Segoe UI Semibold", 12.5f);

    static Color Hex(int v) => Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text, AutoSize = true, Font = font ?? Body, ForeColor = color ?? Fg, Margin = new Padding(0, 2, 0, 6),
    };

    public static Button Button(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Body, Cursor = Cursors.Hand,
            ForeColor = primary ? Bg : Fg, BackColor = primary ? Accent : Input, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 4, 8, 6),
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, 0.2f) : NavHover;
        return b;
    }

    public static CheckBox Check(string text) => new()
    {
        Text = text, AutoSize = true, Font = Body, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 4, 0, 6),
    };

    public static TextBox TextBox() => new()
    {
        Font = Body, ForeColor = Fg, BackColor = Input, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 8, 6),
    };

    public static NumericUpDown Num(int min, int max, int width = 80) => new()
    {
        Minimum = min, Maximum = max, Width = width, Font = Body, ForeColor = Fg, BackColor = Input,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 8, 6), TextAlign = HorizontalAlignment.Right,
    };

    /// eticheta mică de deasupra unui câmp
    public static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Font = Small, ForeColor = Muted, Margin = new Padding(0, 8, 0, 0),
    };

    public static ComboBox Combo(int width) => new()
    {
        Font = Body, ForeColor = Fg, BackColor = Input, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList,
        Width = width, Margin = new Padding(0, 6, 8, 6), Cursor = Cursors.Hand,
    };

    /// rând orizontal (ex. câmp + buton)
    public static FlowLayoutPanel Row(params Control[] items)
    {
        var r = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        r.Controls.AddRange(items);
        return r;
    }

    public static Color TempColor(float? t) => t switch
    {
        null => Muted,
        < 60 => Accent,
        < 80 => Warn,
        _ => Bad,
    };

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// bara de titlu întunecată (Windows 10 20H1+ / 11)
    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Card cu colțuri rotunjite: titlu mic sus, apoi controalele una sub alta.
/// Lățimea o dă pagina (FitWidth); controalele cu Tag = "fill" iau toată lățimea, etichetele se rup pe rânduri.
sealed class Card : FlowLayoutPanel
{
    public Card(string title)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18, 14, 18, 12);
        Margin = new Padding(0, 0, 0, 14);
        BackColor = Theme.Card;
        DoubleBuffered = true;
        Controls.Add(Theme.Label(title.ToUpperInvariant(), Theme.CardTitle, Theme.Muted));
    }

    public void FitWidth(int w)
    {
        MinimumSize = new Size(w, 0);
        MaximumSize = new Size(w, 0);
        int inner = w - Padding.Horizontal;
        foreach (Control c in Controls)
        {
            if (c is Label l) l.MaximumSize = new Size(inner, 0);
            else if (c.Tag as string == "fill") c.Width = inner;
            else if (c is CheckBox cb) cb.MaximumSize = new Size(inner, 0);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10);
        using var fill = new SolidBrush(Theme.Card);
        using var pen = new Pen(Theme.Line);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }
}

/// O valoare cu bară: "CPU      57°C · 30%" + bara de procent dedesubt.
sealed class MetricBar : Control
{
    string label, value = "--", sub = "";
    float? pct;
    Color color = Theme.Accent;

    public MetricBar(string label)
    {
        this.label = label;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Tag = "fill";
        Margin = new Padding(0, 4, 0, 8);
        Height = TextRenderer.MeasureText("Ag", Theme.Value).Height + 16;
    }

    public void Set(string value, float? percent, Color barColor, string subText = "")
    {
        if (value == this.value && percent == pct && barColor == color && subText == sub) return;
        this.value = value; pct = percent; color = barColor; sub = subText;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var ls = TextRenderer.MeasureText(label, Theme.Body);
        TextRenderer.DrawText(g, label, Theme.Body, new Point(0, 3), Theme.Muted);
        if (sub.Length > 0) TextRenderer.DrawText(g, sub, Theme.Small, new Point(ls.Width + 4, 5), Theme.Muted);
        var vs = TextRenderer.MeasureText(value, Theme.Value);
        TextRenderer.DrawText(g, value, Theme.Value, new Point(Width - vs.Width, 0), Theme.Fg);

        int barH = 6, y = Height - barH - 2;
        using (var track = Theme.Rounded(new RectangleF(0, y, Width - 1, barH), 3))
        using (var b = new SolidBrush(Theme.Line)) g.FillPath(b, track);
        if (pct is float p && p > 0)
        {
            float w = Math.Max(barH, (Width - 1) * Math.Clamp(p, 0, 100) / 100f);
            using var fillPath = Theme.Rounded(new RectangleF(0, y, w, barH), 3);
            using var b = new SolidBrush(color);
            g.FillPath(b, fillPath);
        }
    }
}

/// Buton din meniul din stânga.
sealed class NavButton : Control
{
    bool selected, hover;

    public NavButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Body;
        BackColor = Theme.Nav;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 0, 2);
        Height = TextRenderer.MeasureText("Ag", Theme.Body).Height + 20;
        TabStop = true;
    }

    public void PerformClick() => OnClick(EventArgs.Empty);

    // tastatură (Tab + Enter / Space) și cititoare de ecran: se comportă ca un buton
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    sealed class NavAccessible(NavButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override string DefaultAction => "Deschide";
        public override AccessibleStates State => base.State | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);
        public override void DoDefaultAction() => owner.PerformClick();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(selected ? Theme.Card : hover ? Theme.NavHover : Theme.Nav);
        if (selected)
        {
            using var b = new SolidBrush(Theme.Accent);
            g.FillRectangle(b, 0, 6, 3, Height - 12);
        }
        var r = new Rectangle(22, 0, Width - 22, Height);
        TextRenderer.DrawText(g, Text, Font, r, selected ? Theme.Fg : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }
}
