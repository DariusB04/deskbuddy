using System.Net;
using System.Net.Sockets;
using Avalonia;

namespace DeskBuddy;

/// DeskBuddy pentru Linux (AppImage):
///   DeskBuddy                         fereastra + tray (o singură instanță; a doua pornire aduce fereastra în față)
///   DeskBuddy --background            doar tray (așa pornește la logare)
///   DeskBuddy claude-hook|claude-statusline|claude-install|claude-uninstall   ca pe Windows
///   DeskBuddy usb-flash /dev/ttyUSB0 <fișier.bin|-> keep|full                  flash prin USB, din terminal
///   DeskBuddy fans                    senzorii de ventilatoare (depanare)
static class Program
{
    /// o singură instanță: cea pornită ascultă aici; a doua îi trimite „show” și iese
    public const int ShowPort = 4213;
    public static UdpClient? ShowListener;
    public static string[] Args = [];

    [STAThread]
    static int Main(string[] args)
    {
        Args = args;
        switch (args.Length > 0 ? args[0] : "")
        {
            case "claude-hook": ClaudeBridge.Hook(); return 0;
            case "claude-statusline": ClaudeBridge.StatusLine(args.Contains("--wrap")); return 0;
            case "claude-install": ClaudeBridge.Install(); return 0;
            case "claude-uninstall": ClaudeBridge.Uninstall(); return 0;
            case "usb-flash": return EspFlasher.Cli(args);
            case "fans":
            {
                using var s = new Sensors();
                s.Read();
                Thread.Sleep(1500);
                Console.Write(s.DumpFans());
                return 0;
            }
        }

        if (args.Contains("--restarted")) Thread.Sleep(1500);       // după update: lasă instanța veche să se închidă
        try { ShowListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, ShowPort)); }
        catch (SocketException)
        {
            try
            {
                using var c = new UdpClient();
                c.Send("show"u8.ToArray(), 4, new IPEndPoint(IPAddress.Loopback, ShowPort));
            }
            catch (SocketException) { }
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
