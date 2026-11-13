using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBuddy;

/// Auto = USB dacă placa e conectată prin cablu la PC, altfel WiFi
[JsonConverter(typeof(JsonStringEnumConverter<LinkMode>))]
enum LinkMode { Auto, Usb, Wifi }

/// Setările aplicației (nu ale plăcii: acelea stau pe DeskBuddy, în NVS). %LOCALAPPDATA%\DeskBuddy\settings.json
sealed class AppSettings
{
    public string Host { get; set; } = "";              // IP-ul plăcii; gol = broadcast pe toate rețelele + căutare deskbuddy.local
    public bool StartMinimized { get; set; }             // la pornirea manuală: direct în tray
    public bool CloseToTray { get; set; } = true;        // X ascunde fereastra (aplicația continuă să trimită)
    public bool AutoUpdateCheck { get; set; } = true;    // verifică GitHub Releases la pornire și o dată pe zi
    public string FirmwareRepo { get; set; } = "";       // opțional: repo privat cu firmware-ul plăcii (owner/nume)
    public string FirmwareTokenProtected { get; set; } = "";   // token-ul pentru el, criptat cu DPAPI (doar userul ăsta, pe PC-ul ăsta)
    public LinkMode Link { get; set; } = LinkMode.Auto;  // pe unde pleacă datele spre placă

    [JsonIgnore]
    public string? FirmwareToken
    {
        get
        {
            if (FirmwareTokenProtected.Length == 0) return null;
            try { return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(FirmwareTokenProtected), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
        set => FirmwareTokenProtected = string.IsNullOrEmpty(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public string BoardPasswordProtected { get; set; } = "";   // parola paginii plăcii (pentru setări pe WiFi), DPAPI

    [JsonIgnore]
    public string? BoardPassword
    {
        get
        {
            if (BoardPasswordProtected.Length == 0) return null;
            try { return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(BoardPasswordProtected), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
        set => BoardPasswordProtected = string.IsNullOrEmpty(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    [JsonIgnore]
    public bool HasPrivateFirmware => FirmwareRepo.Contains('/') && FirmwareTokenProtected.Length > 0;

    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBuddy");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    [JsonIgnore]
    public IPAddress? HostIp => IPAddress.TryParse(Host.Trim(), out var ip) ? ip : null;

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
