using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeskBuddy;

/// Firmware-ul de pe placă: versiunea (GET /api/version) și update prin WiFi (POST /api/update, ca din pagina plăcii).
static class DeviceFirmware
{
    public enum Result { Ok, NeedPassword, Failed }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };   // WiFi-ul plăcii e slab: upload-ul durează

    /// Version: "1.1.0" sau "" = firmware vechi, fără /api/version. Variant: "full" (repo privat), "public" sau "" (necunoscută)
    public sealed record Board(string Version, string Variant);

    /// null = placa nu răspunde
    public static async Task<Board?> GetVersionAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await Http.GetAsync(baseUrl.TrimEnd('/') + "/api/version", cts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new Board("", "");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new Board(S("fw"), S("variant"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    /// același test ca firmware-ul: aplicație ESP32 (0xE9 + esp_app_desc la octetul 32), nu bootloader / merged
    public static bool LooksLikeApp(byte[] bin) =>
        bin.Length > 36 && bin[0] == 0xE9 && bin[32] == 0x32 && bin[33] == 0x54 && bin[34] == 0xCD && bin[35] == 0xAB;

    public static async Task<(Result Result, string Message)> UploadAsync(string baseUrl, byte[] bin, string? password, CancellationToken ct = default)
    {
        if (!LooksLikeApp(bin)) return (Result.Failed, "Fișierul nu e un program ESP32 valid.");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bin);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "update", Updater.FirmwareAsset);
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/update") { Content = form };
        if (!string.IsNullOrEmpty(password))
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:" + password)));
        try
        {
            using var resp = await Http.SendAsync(req, ct);
            // cu parolă greșită / lipsă, placa ignoră fișierul și răspunde 401 la final
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return (Result.NeedPassword, string.IsNullOrEmpty(password) ? "Pagina plăcii are parolă." : "Parolă greșită.");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return (Result.Ok, "Firmware urcat. Placa repornește.");
            string err = r.TryGetProperty("err", out var e) ? e.GetString() ?? "" : "";
            return (Result.Failed, err.Length > 0 ? err : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (Result.Failed, "Placa nu răspunde: " + e.Message);
        }
    }
}
