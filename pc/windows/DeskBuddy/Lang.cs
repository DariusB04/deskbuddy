using System.Globalization;

namespace DeskBuddy;

/// Limba aplicației (și a plăcii, care primește aceeași setare): T("text românesc", "English text").
/// Se alege în pagina Aplicație; fereastra se reface la schimbare, ca să ia textele noi.
static class L
{
    public static bool En { get; set; }

    public static string T(string ro, string en) => En ? en : ro;

    /// limba implicită: româna doar pe un Windows în română
    public static string DefaultCode => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ro" ? "ro" : "en";

    /// mesajele de reușită ale operațiilor (flash, update) încep cu „Gata” / „Done” sau „Firmware urcat” / „Firmware uploaded”
    public static bool IsOk(string? s) => s != null &&
        (s.StartsWith("Gata", StringComparison.Ordinal) || s.StartsWith("Done", StringComparison.Ordinal) ||
         s.StartsWith("Firmware urcat", StringComparison.Ordinal) || s.StartsWith("Firmware uploaded", StringComparison.Ordinal));

    public static string Cancelled => T("Anulat.", "Cancelled.");
}
