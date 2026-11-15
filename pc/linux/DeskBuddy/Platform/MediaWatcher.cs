using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DeskBuddy;

/// Ce se redă pe Linux: playerctl (MPRIS: Spotify, browserele, VLC, orice player compatibil), la 2 s.
/// Aceeași interfață ca MediaWatcher.cs de pe Windows (WinRT).
sealed class MediaWatcher : IDisposable
{
    public sealed record Now(int St, string Title, string Artist, string App, double? Pos, double? Dur, DateTime At);

    readonly CancellationTokenSource cts = new();
    volatile Now? current;

    public string? Error { get; private set; }
    public Now? Current => current;

    public MediaWatcher() => _ = Task.Run(LoopAsync);

    async Task LoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try { current = Read(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                current = null;
                Error = L.T("instalează playerctl ca să vezi ce se redă", "install playerctl to see what's playing");
            }
            try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    /// playerul care redă acum (altfel primul pe pauză): status|player|titlu|artist|poziție µs|durată µs
    static Now? Read()
    {
        var psi = new ProcessStartInfo("playerctl", ["-a", "metadata", "--format",
            "{{status}}\t{{playerName}}\t{{title}}\t{{artist}}\t{{position}}\t{{mpris:length}}"])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(2000);
        Now? paused = null;
        foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 6 || f[2].Length == 0) continue;
            int st = f[0] == "Playing" ? 1 : f[0] == "Paused" ? 2 : 0;
            if (st == 0) continue;
            double? Us(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v / 1e6 : null;
            string app = f[1].Length > 0 ? char.ToUpperInvariant(f[1][0]) + f[1][1..] : "";
            var now = new Now(st, f[2], f[3], app, Us(f[4]), Us(f[5]), DateTime.Now);
            if (st == 1) return now;
            paused ??= now;
        }
        return paused;
    }

    /// textul pentru OLED (fără diacritice, doar ASCII), tăiat la max caractere
    public static string Ascii(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = ch switch { 'ș' or 'ş' => 's', 'Ș' or 'Ş' => 'S', 'ț' or 'ţ' => 't', 'Ț' or 'Ţ' => 'T', _ => ch };
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.ToString();
    }

    public void Dispose() => cts.Cancel();
}
