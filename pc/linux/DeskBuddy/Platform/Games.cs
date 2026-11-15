using System.Globalization;
using System.Text.RegularExpressions;

namespace DeskBuddy;

/// Jocul pornit pe Linux: Steam lansează jocurile cu „SteamLaunch AppId=<id>”; numele vine din appmanifest_<id>.acf.
/// Fără Steam, numele vine din log-ul MangoHud (executabilul). Aceeași interfață ca GameDetector.cs de pe Windows.
sealed class GameDetector
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string[] SteamRoots = [Path.Combine(Home, ".local/share/Steam"), Path.Combine(Home, ".steam/steam")];
    static readonly string[] NotGames = ["Proton", "Steam Linux Runtime", "Steamworks"];
    readonly Dictionary<int, string?> names = [];
    long scannedAt = -10_000;
    string? game;
    int? pid;

    public int? CurrentPid => pid ?? 0;              // mereu: FPS-ul (MangoHud) dă și numele jocurilor fără Steam

    public string? Current()
    {
        long now = Environment.TickCount64;
        if (now - scannedAt < 2000) return game ?? FpsMonitor.LastExe;     // /proc se scanează la 2 s
        scannedAt = now;
        (game, pid) = Scan();
        return game ?? FpsMonitor.LastExe;
    }

    (string?, int?) Scan()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out int p)) continue;
                byte[] cmd;
                try { cmd = File.ReadAllBytes(Path.Combine(dir, "cmdline")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var m = Regex.Match(System.Text.Encoding.UTF8.GetString(cmd), @"AppId=(\d+)");
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int appId)) continue;
                if (Name(appId) is { } n && !NotGames.Any(n.StartsWith)) return (n.Length > 24 ? n[..24] : n, p);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (null, null);
    }

    string? Name(int appId)
    {
        if (names.TryGetValue(appId, out var cached)) return cached;
        string? name = null;
        var libs = new List<string>();
        foreach (var root in SteamRoots)
        {
            try
            {
                string vdf = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
                libs.AddRange(Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(x => x.Groups[1].Value));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            libs.Add(root);
        }
        foreach (var lib in libs.Distinct())
        {
            try
            {
                string acf = File.ReadAllText(Path.Combine(lib, "steamapps", $"appmanifest_{appId.ToString(CultureInfo.InvariantCulture)}.acf"));
                var m = Regex.Match(acf, "\"name\"\\s+\"([^\"]+)\"");
                if (m.Success) { name = m.Groups[1].Value; break; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        names[appId] = name;
        return name;
    }
}
