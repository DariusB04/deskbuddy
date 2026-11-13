using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;

namespace DeskBuddy;

/// O citire: temperaturi + load. null = senzor indisponibil.
sealed record Stats(float? CpuT, float? CpuL, float? GpuT, float? GpuL, float? RamL, float? RamU, float? RamT)
{
    /// Același format ca pc_stats_sender.py (cheile pe care le citește firmware-ul în handleUdp).
    public byte[] ToPacket(string? game = null)
    {
        var o = new JsonObject();
        if (!string.IsNullOrEmpty(game)) o["g"] = game;        // jocul pornit (placa sare pe ecranul PC)
        void Add(string k, float? v) { if (v is float f && float.IsFinite(f)) o[k] = Math.Round(f, 1); }
        Add("ct", CpuT); Add("cl", CpuL);
        Add("gt", GpuT); Add("gl", GpuL);
        Add("rl", RamL); Add("ru", RamU); Add("rt", RamT);
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

/// Citește senzorii prin LibreHardwareMonitorLib. Fără admin merg load-ul, RAM-ul și GPU-ul NVIDIA;
/// temperatura CPU (AMD / Intel) cere drepturi de admin și driverul PawnIO instalat.
sealed class Sensors : IDisposable
{
    static readonly string[] CpuTempNames = ["Core (Tctl/Tdie)", "CPU Package", "Package", "Tctl/Tdie", "Core (Tdie)", "CCD1 (Tdie)", "Core Average"];
    static readonly string[] GpuTempNames = ["GPU Core", "GPU Temperature", "GPU Hot Spot"];

    readonly Computer pc = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
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

        float? used = Pick(mem, SensorType.Data, "Memory Used", "Used Memory");
        float? avail = Pick(mem, SensorType.Data, "Memory Available", "Available Memory");
        return new Stats(
            CpuT: Pick(cpu, SensorType.Temperature, CpuTempNames),
            CpuL: Pick(cpu, SensorType.Load, "CPU Total"),
            GpuT: Pick(gpu, SensorType.Temperature, GpuTempNames),
            GpuL: Pick(gpu, SensorType.Load, "GPU Core"),
            RamL: Pick(mem, SensorType.Load, "Memory"),
            RamU: used,
            RamT: used + avail);
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
