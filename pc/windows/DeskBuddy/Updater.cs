using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeskBuddy;

/// Ultimul release de pe GitHub: aplicația (DeskBuddy-Windows.zip) și firmware-ul plăcii (DeskBuddy.ino.bin).
sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, Asset? App, Asset? Firmware);

/// Url = link-ul public de descărcare; ApiUrl = același fișier prin API (merge și pe repo privat, cu token)
sealed record Asset(string Url, string ApiUrl, long Size, string? Sha256);

/// Update-uri din GitHub Releases (repo public, fără token). Repo-ul vine din build (-p:GitHubRepo=owner/repo).
static class Updater
{
    public const string AppAsset = "DeskBuddy-Windows.zip";
    public const string FirmwareAsset = "DeskBuddy.ino.bin";

    static readonly HttpClient Http = CreateClient();

    public static string? Repo { get; } = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value is { Length: > 0 } r ? r : null;

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentText => Current.ToString(3);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBuddy-Windows", Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// "v1.2" / "1.2.3" -> 1.2.3 (fără revision, ca să se compare corect)
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        if (!t.Contains('.')) t += ".0";
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// ultimul release din repo-ul public al aplicației (sau din altul, ex. repo-ul privat cu firmware, cu token)
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default) =>
        Repo == null ? null : await GetLatestAsync(Repo, null, ct);

    public static async Task<ReleaseInfo?> GetLatestAsync(string repo, string? token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("token-ul GitHub nu e valid sau nu are acces la " + repo);
        if (!resp.IsSuccessStatusCode) return null;          // 404 = încă niciun release
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not Version v) return null;
        Asset? app = null, fw = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var asset = new Asset(a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("url").GetString() ?? "", a.GetProperty("size").GetInt64(),
                                  digest is { } s && s.StartsWith("sha256:", StringComparison.Ordinal) ? s[7..] : null);
            if (name == AppAsset) app = asset;
            else if (name == FirmwareAsset) fw = asset;
        }
        return new ReleaseInfo(v, tag, root.GetProperty("html_url").GetString() ?? "", app, fw);
    }

    /// Descarcă și verifică mărimea și SHA-256 (dacă GitHub îl dă).
    public static async Task<byte[]> DownloadAsync(Asset asset, string? token = null, CancellationToken ct = default)
    {
        byte[] data;
        if (token == null) data = await Http.GetByteArrayAsync(asset.Url, ct);
        else
        {
            // repo privat: prin API, cu token; GitHub redirecționează spre stocare, iar HttpClient nu trimite token-ul mai departe
            using var req = new HttpRequestMessage(HttpMethod.Get, asset.ApiUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            data = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        if (data.LongLength != asset.Size) throw new InvalidDataException($"descărcare incompletă ({data.Length} din {asset.Size} B)");
        if (asset.Sha256 != null && !Convert.ToHexString(SHA256.HashData(data)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 diferit de cel din release");
        return data;
    }

    /// Pune noul DeskBuddy.exe în locul celui curent. Un .exe care rulează nu poate fi suprascris, dar poate fi redenumit:
    /// curent -> DeskBuddy.exe.old (șters la următoarea pornire), nou -> DeskBuddy.exe. Calea rămâne aceeași, deci
    /// hook-urile Claude și task-ul de pornire merg mai departe.
    public static void InstallApp(byte[] zip)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("nu știu unde e DeskBuddy.exe");
        string fresh = exe + ".new", old = exe + ".old";
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("DeskBuddy.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("arhiva nu conține DeskBuddy.exe");
            entry.ExtractToFile(fresh, overwrite: true);
        }
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch { File.Move(old, exe); throw; }            // pune la loc versiunea veche
    }

    public static void CleanupOld()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // încă blocat: data viitoare
    }
}
