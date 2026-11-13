using System.Text;
using Windows.Media.Control;

namespace DeskBuddy;

/// Ce se redă acum pe PC: aceeași informație ca în panoul de volum Windows (Spotify, YouTube / alte site-uri în browser,
/// Media Player, VLC...). Citită din sesiunea media „curentă” a Windows-ului, la ~1.5 s.
sealed class MediaWatcher : IDisposable
{
    /// St: 0 = nimic, 1 = redă, 2 = pauză; Pos / Dur în secunde (null = necunoscut / live), Pos e la momentul At
    public sealed record Now(int St, string Title, string Artist, string App, double? Pos, double? Dur, DateTime At);

    volatile Now? current;
    readonly CancellationTokenSource cts = new();
    public string? Error { get; private set; }

    public MediaWatcher() => _ = Task.Run(LoopAsync);

    public Now? Current => current;

    async Task LoopAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? mgr = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                current = await ReadAsync(mgr.GetCurrentSession());
                Error = null;
            }
            catch (Exception e) { Error = e.Message; current = null; mgr = null; }
            try { await Task.Delay(1500, cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    static async Task<Now?> ReadAsync(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s == null) return null;
        var props = await s.TryGetMediaPropertiesAsync();
        var play = s.GetPlaybackInfo();
        int st = play?.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 1,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 2,
            _ => 0,
        };
        string title = props?.Title ?? "", artist = props?.Artist ?? "";
        if (artist.Length == 0) artist = props?.AlbumArtist ?? "";
        if (st == 0 || (title.Length == 0 && artist.Length == 0)) return null;

        double? pos = null, dur = null;
        var tl = s.GetTimelineProperties();
        if (tl != null && tl.EndTime > TimeSpan.Zero && tl.EndTime > tl.StartTime)
        {
            dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            double p = (tl.Position - tl.StartTime).TotalSeconds;
            // poziția e cea de la ultima actualizare a playerului: o aducem la „acum” cât se redă
            if (st == 1 && tl.LastUpdatedTime > DateTimeOffset.MinValue)
                p += Math.Max(0, (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds);
            pos = Math.Clamp(p, 0, dur.Value);
        }
        return new Now(st, title.Trim(), artist.Trim(), AppName(s.SourceAppUserModelId ?? ""), pos, dur, DateTime.Now);
    }

    /// „Spotify.exe”, „Chrome”, „308046B0AF4A39CB” (Firefox), „Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic” -> nume scurt
    static string AppName(string id)
    {
        string l = id.ToLowerInvariant();
        (string Key, string Name)[] known =
        [
            ("spotify", "Spotify"), ("chrome", "Chrome"), ("msedge", "Edge"), ("firefox", "Firefox"), ("308046b0af4a39cb", "Firefox"),
            ("opera", "Opera"), ("brave", "Brave"), ("vivaldi", "Vivaldi"), ("zunemusic", "Media Player"), ("zunevideo", "Filme si TV"),
            ("vlc", "VLC"), ("foobar", "foobar2000"), ("musicbee", "MusicBee"), ("aimp", "AIMP"), ("itunes", "iTunes"), ("applemusic", "Apple Music"),
            ("tidal", "TIDAL"), ("deezer", "Deezer"), ("youtube", "YouTube"), ("discord", "Discord"), ("plex", "Plex"), ("steam", "Steam"),
        ];
        foreach (var (k, n) in known) if (l.Contains(k)) return n;
        string name = id.Split('!')[^1];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 15 ? name[..15] : name;
    }

    /// fonturile plăcii au doar ASCII: „Șotron – Live” -> „Sotron - Live”
    public static string Ascii(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Replace('–', '-').Replace('—', '-').Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Normalize(NormalizationForm.FormD))
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
        string r = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return r.Length > max ? r[..max].TrimEnd() : r;
    }

    public void Dispose() => cts.Cancel();
}
