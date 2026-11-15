using System.Diagnostics;
using System.Net;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DeskBuddy;

/// Partea de Linux a nucleului: iconița din tray (Avalonia TrayIcon, StatusNotifierItem pe KDE / GNOME cu extensia AppIndicator),
/// meniul ei, fereastra și notificările (notify-send).
sealed partial class TrayApp
{
    TrayIcon? trayIcon;
    readonly NativeMenuItem miOpen = new(), miBrowser = new(), miStats = new() { IsEnabled = false }, miExit = new();
    MainWindow? window;

    void MenuTexts()
    {
        miOpen.Header = L.T("Deschide DeskBuddy", "Open DeskBuddy");
        miBrowser.Header = L.T("Pagina plăcii în browser", "Board page in the browser");
        miExit.Header = L.T("Ieșire", "Exit");
    }

    partial void PlatformInit()
    {
        ui = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
        MenuTexts();
        var menu = new NativeMenu();
        menu.Items.Add(miOpen);
        menu.Items.Add(miBrowser);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(miStats);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(miExit);
        miOpen.Click += (_, _) => ShowWindow();
        miBrowser.Click += (_, _) => Ui.Open(DeviceUrl ?? "http://deskbuddy.local/");
        miExit.Click += (_, _) => Quit();
        trayIcon = new TrayIcon { ToolTipText = "DeskBuddy", Menu = menu, Icon = Icons.Window(Avalonia.Media.Color.FromRgb(150, 150, 150)), IsVisible = true };
        trayIcon.Clicked += (_, _) => ShowWindow();
        if (Avalonia.Application.Current is { } a) TrayIcon.SetIcons(a, [trayIcon]);

        // a doua pornire a aplicației trimite „show” pe 127.0.0.1:4213
        if (Program.ShowListener is { } l)
            new Thread(() =>
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                while (!Quitting)
                {
                    try { l.Receive(ref from); ui.Post(_ => ShowWindow(), null); }
                    catch (Exception) { return; }
                }
            }) { IsBackground = true, Name = "show" }.Start();
    }

    partial void PlatformStart(bool showWindow)
    {
        if (showWindow) ShowWindow();
    }

    public void ShowWindow()
    {
        if (Quitting) return;
        window ??= new MainWindow(this);
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    partial void PlatformQuit()
    {
        if (window != null) { window.Replacing = true; window.Close(); }
        if (trayIcon != null) trayIcon.IsVisible = false;
        trayIcon?.Dispose();
        Program.ShowListener?.Dispose();
        App.Desktop?.Shutdown();
    }

    partial void OnHealth(Health h)
    {
        var dot = h switch
        {
            Health.Ok => Ui.AccentC,
            Health.NoDevice => Ui.WarnC,
            Health.Error => Ui.BadC,
            _ => Avalonia.Media.Color.FromRgb(150, 150, 150),
        };
        if (trayIcon != null) trayIcon.Icon = Icons.Window(dot);
    }

    partial void OnStats(Stats s)
    {
        miStats.Header = s.Summary();
        if (trayIcon != null) trayIcon.ToolTipText = $"DeskBuddy\n{s.Summary()}";
    }

    partial void NotifyUpdate(string title, string text)
    {
        try { Process.Start(new ProcessStartInfo("notify-send", ["-a", "DeskBuddy", title, text]) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    partial void RebuildWindow()
    {
        MenuTexts();
        if (window == null) return;
        bool visible = window.IsVisible;
        var old = window;
        window = null;
        old.Replacing = true;
        old.Close();
        if (visible) { ShowWindow(); window?.ShowAppPage(); }
    }
}
