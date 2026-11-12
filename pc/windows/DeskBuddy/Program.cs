using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeskBuddy;

/// DeskBuddy.exe                          fereastra + tray (o singură instanță; a doua pornire aduce fereastra în față)
/// DeskBuddy.exe --background             doar tray (așa pornește task-ul „Pornește cu Windows”)
/// DeskBuddy.exe claude-hook              apelat de Claude Code (hook async)
/// DeskBuddy.exe claude-statusline        apelat de Claude Code (status line)
/// DeskBuddy.exe claude-install|uninstall la fel ca bifa din fereastră (pune / scoate hook-urile din ~/.claude/settings.json)
/// DeskBuddy.exe --autostart on|off       intern: creează / șterge task-ul de pornire (rulează ca admin)
static class Program
{
    const string MutexName = @"Local\DeskBuddyTray";
    const string ShowEventName = @"Local\DeskBuddyShow";

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        switch (mode)
        {
            case "claude-hook":
                ClaudeBridge.Hook();
                return 0;
            case "claude-statusline":
                ClaudeBridge.StatusLine(args.Contains("--wrap"));
                return 0;
            case "claude-install":
                ClaudeBridge.Install();
                return 0;
            case "claude-uninstall":
                ClaudeBridge.Uninstall();
                return 0;
            case "usb-flash":                         // DeskBuddy.exe usb-flash COM3 <fișier.bin|-> probe|keep|full (pentru teste / depanare)
                return EspFlasher.Cli(args);
            case "--autostart":
                return Autostart.Apply(args.Length > 1 && args[1] == "on") ? 0 : 1;
            case "--via-task":                        // repornire ca admin prin task: așteaptă să se închidă instanța veche
            {
                long until = Environment.TickCount64 + 10_000;
                while (Environment.TickCount64 < until && OtherInstanceRunning()) Thread.Sleep(150);
                if (!Autostart.RunTask()) return 1;
                until = Environment.TickCount64 + 10_000;
                while (Environment.TickCount64 < until && !OtherInstanceRunning()) Thread.Sleep(150);
                if (args.Contains("--show")) SignalShow();
                return 0;
            }
        }

        // Mutex-ul și evenimentul au drepturi pentru toți: o instanță pornită ca admin (task-ul de pornire)
        // poate fi găsită și adusă în față și de una pornită normal (dublu-click pe exe).
        if (args.Contains("--restarted"))             // repornire ca admin: așteaptă să se închidă instanța veche
        {
            long until = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < until && OtherInstanceRunning()) Thread.Sleep(200);
        }
        if (OtherInstanceRunning())
        {
            SignalShow();
            return 0;
        }

        // Dublu-click pe exe, fără admin, dar cu „Pornește cu Windows” bifat: pornim task-ul, care rulează ca admin
        // fără prompt UAC (temperatura CPU), și îi cerem să-și arate fereastra.
        if (!Autostart.IsAdmin && !args.Contains("--background") && !args.Contains("--restarted") && Autostart.IsEnabled() && Autostart.RunTask())
        {
            long until = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < until && !OtherInstanceRunning()) Thread.Sleep(150);
            if (OtherInstanceRunning())
            {
                SignalShow();
                return 0;
            }
        }

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var mutexSec = new MutexSecurity();
        mutexSec.AddAccessRule(new MutexAccessRule(everyone, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        using var mutex = MutexAcl.Create(true, MutexName, out bool created, mutexSec);
        if (!created)                                 // pornite exact în același timp: rămâne cealaltă
        {
            SignalShow();
            return 0;
        }
        var eventSec = new EventWaitHandleSecurity();
        eventSec.AddAccessRule(new EventWaitHandleAccessRule(everyone, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        using var showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, eventSec);

        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        bool background = args.Contains("--background");
        bool show = args.Contains("--show") || (!background && !settings.StartMinimized);
        Application.Run(new TrayApp(show, showEvent));
        mutex.ReleaseMutex();
        return 0;
    }

    static bool OtherInstanceRunning()
    {
        try
        {
            if (!MutexAcl.TryOpenExisting(MutexName, MutexRights.Synchronize, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }   // există, dar e al unei versiuni vechi pornite ca admin
    }

    static void SignalShow()
    {
        AllowSetForegroundWindow(-1);                 // instanța care rulează are voie să-și aducă fereastra în față
        for (int i = 0; i < 25; i++)                  // abia pornită, își creează evenimentul imediat după mutex
        {
            try
            {
                if (EventWaitHandleAcl.TryOpenExisting(ShowEventName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out var e))
                {
                    using (e) e.Set();
                    return;
                }
            }
            catch (Exception) { return; }
            Thread.Sleep(200);
        }
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
