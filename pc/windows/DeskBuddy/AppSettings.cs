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
    public string Language { get; set; } = "";           // "ro" / "en"; gol = instalare nouă: după limba Windows-ului (L.DefaultCode)

    [JsonIgnore]
    public string LanguageCode => Language is "ro" or "en" ? Language : L.DefaultCode;

    /// secretele: DPAPI pe Windows (doar userul ăsta, pe PC-ul ăsta); pe Linux fișierul de setări e citibil doar de utilizator
    static string Protect(string v) =>
#if WINDOWS
        Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(v), null, DataProtectionScope.CurrentUser));
#else
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v));
#endif

    static string? Unprotect(string v)
    {
        try
        {
#if WINDOWS
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), null, DataProtectionScope.CurrentUser));
#else
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(v));
#endif
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    [JsonIgnore]
    public string? FirmwareToken
    {
        get
        {
            return FirmwareTokenProtected.Length == 0 ? null : Unprotect(FirmwareTokenProtected);
        }
        set => FirmwareTokenProtected = string.IsNullOrEmpty(value) ? "" : Protect(value);
    }

    public string BoardPasswordProtected { get; set; } = "";   // parola paginii plăcii (pentru setări pe WiFi), DPAPI

    [JsonIgnore]
    public string? BoardPassword
    {
        get
        {
            return BoardPasswordProtected.Length == 0 ? null : Unprotect(BoardPasswordProtected);
        }
        set => BoardPasswordProtected = string.IsNullOrEmpty(value) ? "" : Protect(value);
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
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (s.Language.Length == 0) s.Language = "ro";        // instalare de dinainte de engleză: rămâne în română
            return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
