using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBuddy;

/// Ce joc rulează acum (pentru ecranul PC și pentru „sari pe PC când pornește un joc” de pe placă).
/// Un joc = fereastra din prim-plan aparține unui program instalat într-un folder de jocuri (Steam, Epic, Xbox, GOG...)
/// sau acoperă tot monitorul și nu e un program obișnuit (browser, player video, editor...).
/// Odată găsit, rămâne „jocul curent” cât timp procesul trăiește, chiar dacă dai alt-tab.
sealed class GameDetector
{
    static readonly string[] GameDirs =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\Riot Games\",
        @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\Battle.net\", @"\Rockstar Games\", @"\Games\",
    ];

    /// programe care pot sta pe tot ecranul fără să fie jocuri (sau lansatoarele jocurilor)
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "iexplore",
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "mpv", "PotPlayerMini64", "wmplayer", "Video.UI", "Microsoft.Media.Player",
        "Code", "devenv", "rider64", "idea64", "WindowsTerminal", "powershell", "pwsh", "cmd", "notepad", "notepad++",
        "WINWORD", "EXCEL", "POWERPNT", "Acrobat", "AcroRd32", "Teams", "ms-teams", "Discord", "Slack", "Spotify", "Zoom",
        "obs64", "DeskBuddy", "claude", "Claude", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost",
        "SearchHost", "StartMenuExperienceHost", "LockApp", "TextInputHost", "Taskmgr", "mmc",
        "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "GalaxyClient", "RiotClientServices",
        "EADesktop", "UbisoftConnect", "upc", "XboxPcApp", "GameBar", "Playnite.DesktopApp", "Playnite.FullscreenApp",
    };

    int? gamePid;
    /// procesul jocului curent (pentru FPS)
    public int? CurrentPid => gamePid;
    string? gameName;
    long checkedAt = -10_000;

    /// numele jocului (max. 24 de caractere ASCII, cât afișează placa) sau null
    public string? Current()
    {
        long now = Environment.TickCount64;
        if (now - checkedAt < 2000) return gameName;             // fereastra din prim-plan se verifică la 2 s
        checkedAt = now;

        if (gamePid is int pid && !Alive(pid)) { gamePid = null; gameName = null; }
        if (gamePid != null) return gameName;

        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out uint fpid);
        if (fpid == 0 || fpid == Environment.ProcessId) return null;
        try
        {
            using var p = Process.GetProcessById((int)fpid);
            if (NotGames.Contains(p.ProcessName)) return null;
            string? path = ImagePath(p);
            bool inGameDir = path != null && GameDirs.Any(d => path.Contains(d, StringComparison.OrdinalIgnoreCase))
                             && !path.Contains(@"\Battle.net\Battle.net", StringComparison.OrdinalIgnoreCase);
            if (!inGameDir && !CoversMonitor(h)) return null;
            gamePid = (int)fpid;
            gameName = Clean(WindowTitle(h) is { Length: > 0 } t ? t : ProductName(path) ?? p.ProcessName);
            return gameName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    static string? ImagePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        // proces protejat sau pe 32 de biți: calea direct de la Windows
        IntPtr hp = OpenProcess(0x1000, false, p.Id);               // PROCESS_QUERY_LIMITED_INFORMATION
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    static string? ProductName(string? path)
    {
        if (path == null) return null;
        try { return FileVersionInfo.GetVersionInfo(path).ProductName is { Length: > 0 } n ? n : null; }
        catch (FileNotFoundException) { return null; }
    }

    static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    /// fereastra acoperă tot monitorul (fullscreen sau „borderless”)
    static bool CoversMonitor(IntPtr h)
    {
        if (!GetWindowRect(h, out var r)) return false;
        var mon = MonitorFromWindow(h, 2);                          // MONITOR_DEFAULTTONEAREST
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// fonturile plăcii au doar ASCII: fără diacritice și simboluri, maxim 24 de caractere
    static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
        string r = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return r.Length > 24 ? r[..24].TrimEnd() : r;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
