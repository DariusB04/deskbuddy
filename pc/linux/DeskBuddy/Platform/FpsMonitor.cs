using System.Globalization;
using System.Text.RegularExpressions;

namespace DeskBuddy;

/// FPS-ul pe Linux, din log-ul MangoHud scris în timp real (în MangoHud.conf: autostart_log=1, log_duration=0 și
/// output_folder=...). Aceeași interfață ca FpsMonitor.cs de pe Windows (ETW); pid-ul nu contează aici.
sealed class FpsMonitor : IDisposable
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    readonly Dictionary<string, string[]> headers = [];

    /// executabilul din numele ultimului log MangoHud activ (pentru jocurile pornite fără Steam)
    public static string? LastExe { get; private set; }

    public string? Error { get; private set; }

    public (float Fps, float FrameMs)? For(int pid) => Read();

    /// fără joc Steam: tot MangoHud-ul dă numele (GameDetector îl folosește)
    public (float Fps, float FrameMs)? Read()
    {
        string? folder = Folder();
        if (folder == null || !Directory.Exists(folder))
        {
            Error = L.T("FPS: pornește MangoHud cu log (autostart_log=1)", "FPS: run MangoHud with logging (autostart_log=1)");
            LastExe = null;
            return null;
        }
        try
        {
            var f = new DirectoryInfo(folder).EnumerateFiles("*.csv").Where(x => !x.Name.EndsWith("_summary.csv"))
                .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (f == null || (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds > 3) { LastExe = null; return null; }
            if (!headers.TryGetValue(f.FullName, out var cols))
            {
                cols = File.ReadLines(f.FullName).Skip(2).FirstOrDefault()?.Trim().Split(',') ?? [];   // rândul 3 = coloanele
                headers[f.FullName] = cols;
            }
            int iFps = Array.IndexOf(cols, "fps"), iFt = Array.IndexOf(cols, "frametime");
            if (iFps < 0) return null;
            string tail;
            using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(Math.Max(0, fs.Length - 4096), SeekOrigin.Begin);
                tail = new StreamReader(fs).ReadToEnd();
            }
            var rows = tail.Split('\n').Skip(1).Where(l => l.Length > 0 && char.IsDigit(l[0])).Select(l => l.Split(','))
                .Where(r => r.Length == cols.Length).TakeLast(10).ToList();                  // ~1 s la log_interval=100
            if (rows.Count == 0) return null;
            float fps = rows.Average(r => float.Parse(r[iFps], CultureInfo.InvariantCulture));
            float ft = iFt >= 0 ? rows.Average(r => float.Parse(r[iFt], CultureInfo.InvariantCulture)) : 0;
            string exe = Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), @"_\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d$", "");
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
            LastExe = exe.Length > 24 ? exe[..24] : exe;
            Error = null;
            return (fps, ft);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    static string? Folder()
    {
        string conf = Environment.GetEnvironmentVariable("MANGOHUD_CONFIGFILE") ?? Path.Combine(Home, ".config/MangoHud/MangoHud.conf");
        try
        {
            foreach (var line in File.ReadLines(conf))
                if (line.Trim().StartsWith("output_folder="))
                {
                    string p = line.Split('=', 2)[1].Trim();
                    return p.StartsWith('~') ? Home + p[1..] : p;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public void Dispose() { }
}
