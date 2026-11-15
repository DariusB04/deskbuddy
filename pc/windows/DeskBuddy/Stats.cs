using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// O citire: temperaturi + load. null = senzor indisponibil.
sealed record Stats(float? CpuT, float? CpuL, float? GpuT, float? GpuL, float? RamL, float? RamU, float? RamT)
{
    /// numele PC-ului pe placă (ASCII, max 12)
    public static readonly string PcName = MakeName(Environment.MachineName);
    static string MakeName(string n)
    {
        var s = new string(n.Where(c => c >= 32 && c < 127).ToArray());
        return s.Length == 0 ? "PC" : s.Length > 12 ? s[..12] : s;
    }

    /// ventilatoarele și pompa AIO, în ordinea de pe ecranul plăcii (pompa întâi)
    public IReadOnlyList<Fan> Fans { get; init; } = [];

    /// Același format ca pc_stats_sender.py (cheile pe care le citește firmware-ul în handleUdp).
    public byte[] ToPacket(string? game = null, float? fps = null, float? frameMs = null)
    {
        var o = new JsonObject { ["id"] = PcName };       // placa ține separat până la 4 PC-uri, după nume
        if (!string.IsNullOrEmpty(game))                       // jocul pornit (placa sare pe ecranul PC) + FPS-ul lui
        {
            o["g"] = game;
            if (fps is float f && float.IsFinite(f)) o["fps"] = Math.Round(f);
            if (frameMs is float ft && float.IsFinite(ft)) o["ft"] = Math.Round(ft, 1);
        }
        void Add(string k, float? v) { if (v is float f && float.IsFinite(f)) o[k] = Math.Round(f, 1); }
        Add("ct", CpuT); Add("cl", CpuL);
        Add("gt", GpuT); Add("gl", GpuL);
        Add("rl", RamL); Add("ru", RamU); Add("rt", RamT);
        if (Fans.Count > 0)                                    // "fn": [[eticheta, rpm, procent], ...] (ecranul VENTILATOARE)
        {
            var fn = new JsonArray();
            foreach (var x in Fans) fn.Add(new JsonArray(x.Label, (int)MathF.Round(x.Rpm), (int)MathF.Round(x.Pct)));
            o["fn"] = fn;
        }
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    public string Summary()
    {
        static string T(float? v) => v is float f ? $"{f:0}°C" : "--";
        static string P(float? v) => v is float f ? $"{f:0}%" : "--";
        return string.Create(CultureInfo.InvariantCulture,
            $"CPU {T(CpuT)} {P(CpuL)} · GPU {T(GpuT)} {P(GpuL)} · RAM {P(RamL)}");
    }
}

/// Un ventilator sau o pompă: eticheta scurtă (max 5 litere), turația și cât de tare merge (0-100):
/// comanda PWM dacă placa de bază / controlerul o raportează, altfel turația față de cea mai mare văzută.
sealed record Fan(string Label, float Rpm, float Pct, bool Pump);
