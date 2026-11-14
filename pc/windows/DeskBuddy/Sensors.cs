using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;

namespace DeskBuddy;

/// O citire: temperaturi + load. null = senzor indisponibil.
sealed record Stats(float? CpuT, float? CpuL, float? GpuT, float? GpuL, float? RamL, float? RamU, float? RamT)
{
    /// ventilatoarele și pompa AIO, în ordinea de pe ecranul plăcii (pompa întâi)
    public IReadOnlyList<Fan> Fans { get; init; } = [];

    /// Același format ca pc_stats_sender.py (cheile pe care le citește firmware-ul în handleUdp).
    public byte[] ToPacket(string? game = null, float? fps = null, float? frameMs = null)
    {
        var o = new JsonObject();
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

/// Citește senzorii prin LibreHardwareMonitorLib. Fără admin merg load-ul, RAM-ul și GPU-ul NVIDIA;
/// temperatura CPU (AMD / Intel) cere drepturi de admin și driverul PawnIO instalat.
sealed class Sensors : IDisposable
{
    static readonly string[] CpuTempNames = ["Core (Tctl/Tdie)", "CPU Package", "Package", "Tctl/Tdie", "Core (Tdie)", "CCD1 (Tdie)", "Core Average"];
    static readonly string[] GpuTempNames = ["GPU Core", "GPU Temperature", "GPU Hot Spot"];

    readonly Computer pc = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
                                   IsMotherboardEnabled = true, IsControllerEnabled = true };   // ventilatoare: placa de bază + AIO-uri
    const int MaxFans = 8;
    readonly HashSet<string> spun = [];                     // ventilatoarele care au mers măcar o dată (restul = mufe goale)
    readonly Dictionary<string, float> maxRpm = [];
    readonly bool opened;

    public string? Error { get; }

    public Sensors()
    {
        try { pc.Open(); opened = true; }
        catch (Exception e) { Error = e.Message; }
    }

    public Stats Read()
    {
        if (!opened) return new Stats(null, null, null, null, null, null, null);
        foreach (var hw in pc.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }

        var cpu = pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // placa video dedicată înaintea celei integrate (Ryzen 7000 are și iGPU Radeon)
        var gpu = pc.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderBy(h => h.HardwareType switch { HardwareType.GpuNvidia => 0, HardwareType.GpuAmd => 1, _ => 2 })
            .FirstOrDefault();
        var mem = pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

        var fans = ReadFans();
        float? used = Pick(mem, SensorType.Data, "Memory Used", "Used Memory");
        float? avail = Pick(mem, SensorType.Data, "Memory Available", "Available Memory");
        return new Stats(
            CpuT: Pick(cpu, SensorType.Temperature, CpuTempNames),
            CpuL: Pick(cpu, SensorType.Load, "CPU Total"),
            GpuT: Pick(gpu, SensorType.Temperature, GpuTempNames),
            GpuL: Pick(gpu, SensorType.Load, "GPU Core"),
            RamL: Pick(mem, SensorType.Load, "Memory"),
            RamU: used,
            RamT: used + avail) { Fans = fans };
    }

    List<Fan> ReadFans()
    {
        var found = new List<(ISensor S, IHardware Hw, int Rank, string Base)>();
        foreach (var hw in pc.Hardware)
            foreach (var h in hw.SubHardware.Prepend(hw))
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType != SensorType.Fan || s.Value is not float v || !float.IsFinite(v) || v < 0) continue;
                    bool gpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
                    string id = s.Identifier.ToString();
                    if (v > 0) spun.Add(id);
                    if (!spun.Contains(id) && !gpu) continue;      // mufă fără ventilator (merge mereu 0)
                    string n = s.Name.ToLowerInvariant();
                    bool pump = n.Contains("pump") || n.Contains("pompa") || n.Contains("aio");
                    var (rank, b) = pump ? (0, "POMPA")
                        : n.Contains("cpu") ? (1, "CPU")
                        : hw.HardwareType == HardwareType.Cooler ? (2, "AIO")
                        : gpu ? (4, "GPU")
                        : n.Contains("sys") || n.Contains("cha") || n.Contains("case") ? (3, "SYS")
                        : (3, "FAN");
                    found.Add((s, h, rank, b));
                }
        var list = new List<Fan>();
        foreach (var g in found.OrderBy(f => f.Rank).ThenBy(f => f.S.Index).Take(MaxFans).GroupBy(f => f.Base))
        {
            int k = 0, count = g.Count();
            foreach (var (s, hw, _, b) in g)
            {
                k++;
                float rpm = s.Value ?? 0;
                string id = s.Identifier.ToString();
                maxRpm[id] = Math.Max(maxRpm.GetValueOrDefault(id), rpm);
                // comanda PWM a aceluiași canal (Control cu același index), altfel turația față de maximul văzut
                var ctl = hw.Sensors.FirstOrDefault(c => c.SensorType == SensorType.Control && c.Index == s.Index && c.Value is float cv && float.IsFinite(cv));
                float floor = b == "POMPA" ? 3000 : 1800;
                float pct = ctl?.Value ?? 100f * rpm / Math.Max(maxRpm[id], floor);
                string label = count > 1 ? (b == "POMPA" ? "POMP" : b) + k : b;
                list.Add(new Fan(label, rpm, Math.Clamp(pct, 0, 100), b == "POMPA"));
            }
        }
        return list.OrderBy(f => f.Pump ? 0 : 1).ToList();
    }

    /// pentru depanare: toți senzorii de ventilatoare / comenzi, cu valori (DeskBuddy.exe fans)
    public string DumpFans()
    {
        Read();
        var sb = new StringBuilder();
        foreach (var hw in pc.Hardware)
            foreach (var h in hw.SubHardware.Prepend(hw))
                foreach (var s in h.Sensors.Where(s => s.SensorType is SensorType.Fan or SensorType.Control))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{h.HardwareType,-12} {h.Name,-28} {s.SensorType,-8} #{s.Index,-2} {s.Name,-24} {s.Value:0.#}");
        sb.AppendLine("--- pe placă:");
        foreach (var f in Read().Fans) sb.AppendLine(CultureInfo.InvariantCulture, $"{f.Label,-6} {f.Rpm,6:0} rpm {f.Pct,4:0}%");
        return sb.ToString();
    }

    static float? Pick(IHardware? hw, SensorType type, params string[] names)
    {
        if (hw == null) return null;
        var all = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors))
            // fără driver (PawnIO / admin) temperatura CPU vine 0, nu lipsă: o tratăm ca indisponibilă
            .Where(s => s.SensorType == type && s.Value is float v && float.IsFinite(v) && (type != SensorType.Temperature || v > 0))
            .ToList();
        foreach (var n in names)
        {
            var s = all.FirstOrDefault(x => x.Name == n);
            if (s != null) return s.Value;
        }
        return null;
    }

    public void Dispose()
    {
        if (opened) pc.Close();
    }
}
