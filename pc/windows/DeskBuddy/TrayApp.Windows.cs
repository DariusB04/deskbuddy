using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DeskBuddy;

/// Partea de Windows a nucleului: iconița din tray (NotifyIcon), meniul, fereastra (MainForm) și repornirea ca admin.
sealed partial class TrayApp : ApplicationContext
{
    public Icon WindowIcon => (Icon)(currentIcon ?? MakeIcon(Color.Gray)).Clone();
    MainForm? form;
    Icon? currentIcon;
    bool trayHintShown, balloonOpensUpdates;

    NotifyIcon tray = null!;
    readonly ContextMenuStrip menu = new();
    readonly ToolStripMenuItem miStats = new() { Enabled = false };
    readonly ToolStripMenuItem miOpen = new(), miBrowser = new(), miExit = new();
    void MenuTexts()
    {
        miOpen.Text = L.T("Deschide DeskBuddy", "Open DeskBuddy");
        miBrowser.Text = L.T("Pagina plăcii în browser", "Board page in the browser");
        miExit.Text = L.T("Ieșire", "Exit");
    }

    partial void PlatformInit()
    {
        ui = new WindowsFormsSynchronizationContext();
        miOpen.Font = new Font(SystemFonts.MenuFont ?? Theme.Body, FontStyle.Bold);
        MenuTexts();
        menu.Items.AddRange([miOpen, miBrowser, new ToolStripSeparator(), miStats, new ToolStripSeparator(), miExit]);
        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "DeskBuddy", Visible = true };
        menu.Opening += (_, _) => miStats.Text = Latest?.Summary() ?? L.T("se citește...", "reading...");
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        miOpen.Click += (_, _) => ShowWindow();
        miBrowser.Click += (_, _) => Shell(DeviceUrl ?? "http://deskbuddy.local/");
        miExit.Click += (_, _) => Quit();
        tray.BalloonTipClicked += (_, _) =>
        {
            if (!balloonOpensUpdates) return;
            ShowWindow();
            form?.ShowAppPage();
        };
        tray.BalloonTipClosed += (_, _) => balloonOpensUpdates = false;
    }

    partial void PlatformStart(bool showWindow)
    {
        if (showWindow) ShowWindow();
    }

    partial void PlatformQuit()
    {
        form?.Close();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    partial void OnStats(Stats s)
    {
        string tip = $"DeskBuddy\n{s.Summary()}";
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    partial void NotifyUpdate(string title, string text)
    {
        balloonOpensUpdates = true;
        tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);
    }

    partial void RebuildWindow()
    {
        MenuTexts();
        if (form == null) return;
        bool visible = form.Visible;
        var old = form;
        form = null;
        old.Replacing = true;
        old.Close();
        if (visible) { ShowWindow(); form?.ShowAppPage(); }
    }

    // ------------------------------------------------------------ fereastra
    public void ShowWindow()
    {
        if (Quitting) return;
        form ??= new MainForm(this);
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.BringToFront();
    }

    public void NotifyHiddenToTray()
    {
        if (trayHintShown) return;
        trayHintShown = true;
        tray.ShowBalloonTip(3000, "DeskBuddy", L.T("Rulează în continuare în tray. Click pe iconiță deschide fereastra.", "Still running in the tray. Click the icon to open the window."), ToolTipIcon.Info);
    }


    public void RestartAsAdmin()
    {
        bool visible = form?.Visible == true;
        // cu „Pornește cu Windows” bifat, repornim prin task (admin fără prompt UAC); altfel cerem UAC
        var psi = Autostart.IsEnabled()
            ? new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, Arguments = visible ? "--via-task --show" : "--via-task" }
            : new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = visible ? "--restarted --show" : "--restarted" };
        try { Process.Start(psi); }
        catch (Win32Exception) { return; }            // UAC refuzat: rămânem cum suntem
        Quit();
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }

    // ------------------------------------------------------------ iconița: un DeskBuddy mic cu ecran, plus un punct de stare
    partial void OnHealth(Health h)
    {
        var dot = h switch
        {
            Health.Ok => Theme.Accent,
            Health.NoDevice => Theme.Warn,
            Health.Error => Theme.Bad,
            _ => Color.FromArgb(150, 150, 150),
        };
        var old = currentIcon;
        currentIcon = MakeIcon(dot);
        tray.Icon = currentIcon;
        old?.Dispose();
    }

    static Icon MakeIcon(Color dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) DrawLogo(g, 32, dot);
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    /// iconița exe-ului (DeskBuddy.ico) = cea din tray cu punctul verde, în toate mărimile Windows, ca PNG-uri într-un .ico
    ///   DeskBuddy.exe make-icon pc\windows\DeskBuddy\DeskBuddy.ico
    public static int WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var pngs = sizes.Select(s =>
        {
            using var bmp = new Bitmap(s, s);
            using (var g = Graphics.FromImage(bmp)) DrawLogo(g, s, Theme.Accent);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var f = new BinaryWriter(File.Create(path));
        f.Write((ushort)0); f.Write((ushort)1); f.Write((ushort)sizes.Length);          // antet ICO
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            f.Write((byte)0); f.Write((byte)0); f.Write((ushort)1); f.Write((ushort)32);
            f.Write(pngs[i].Length); f.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) f.Write(p);
        return 0;
    }

    /// desenat pe o grilă de 32, scalat la mărimea cerută
    static void DrawLogo(Graphics g, float size, Color dot)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 32f, size / 32f);
        {
            using var body = Theme.Rounded(new RectangleF(2, 3, 28, 25), 6);
            using var bodyBrush = new SolidBrush(Color.FromArgb(235, 238, 240));
            g.FillPath(bodyBrush, body);
            using var screen = Theme.Rounded(new RectangleF(5.5f, 7, 21, 14), 3);
            using var screenBrush = new SolidBrush(Color.FromArgb(25, 30, 36));
            g.FillPath(screenBrush, screen);
            using var eye = new SolidBrush(Color.FromArgb(110, 200, 255));
            g.FillRectangle(eye, 10, 11, 3, 6);
            g.FillRectangle(eye, 19, 11, 3, 6);
            using var dotBrush = new SolidBrush(dot);
            using var ring = new Pen(Color.FromArgb(30, 30, 30), 1.5f);
            g.FillEllipse(dotBrush, 21, 20, 10, 10);
            g.DrawEllipse(ring, 21, 20, 10, 10);
        }
        g.ResetTransform();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
