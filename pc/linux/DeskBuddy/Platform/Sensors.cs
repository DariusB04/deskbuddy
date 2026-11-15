using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DeskBuddy;

/// Senzorii pe Linux, fără programe în plus: /sys/class/hwmon (temperaturi, ventilatoare, AMD GPU), /proc/stat (load),
/// /proc/meminfo (RAM) și nvidia-smi pentru plăcile NVIDIA. Aceeași interfață ca Sensors.cs de pe Windows (LibreHardwareMonitor).
sealed class Sensors : IDisposable
{
    const int MaxFans = 8;
    readonly HashSet<string> spun = [];
    readonly Dictionary<string, float> maxRpm = [];
    long prevIdle, prevTotal;
    (float? T, float? L, long At) nvidia;

    public string? Error { get; }

    public Sensors()
    {
        if (!Directory.Exists("/sys/class/hwmon")) Error = L.T("nu găsesc /sys/class/hwmon", "can't find /sys/class/hwmon");
    }

    static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static float? ReadNum(string path, float div = 1) =>
        float.TryParse(ReadText(path), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v / div : null;

    /// cipurile hwmon: (folder, nume), ex. ("/sys/class/hwmon/hwmon2", "k10temp")
    static IEnumerable<(string Dir, string Name)> Hwmon()
    {
        if (!Directory.Exists("/sys/class/hwmon")) yield break;
        foreach (var d in Directory.EnumerateDirectories("/sys/class/hwmon"))
            if (ReadText(Path.Combine(d, "name")) is { } n) yield return (d, n);
    }

    public Stats Read()
    {
        var chips = Hwmon().ToList();
        float? cpuT = null, gpuT = null, gpuL = null;
        foreach (var (dir, name) in chips)
        {
            if (cpuT == null && name is "k10temp" or "coretemp" or "zenpower" or "cpu_thermal")
                cpuT = ReadNum(Path.Combine(dir, "temp1_input"), 1000);          // Tctl / Package id 0
            if (gpuT == null && name == "amdgpu")
            {
                gpuT = ReadNum(Path.Combine(dir, "temp1_input"), 1000);
                gpuL = ReadNum(Path.Combine(dir, "device", "gpu_busy_percent"));
            }
        }
        if (gpuT == null && Nvidia() is { } nv) { gpuT = nv.T; gpuL = nv.L; }

        var (ramU, ramT) = Memory();
        return new Stats(cpuT, CpuLoad(), gpuT, gpuL, ramT > 0 ? 100f * ramU / ramT : null, ramT > 0 ? ramU : null, ramT > 0 ? ramT : null)
        { Fans = ReadFans(chips) };
    }

    float? CpuLoad()
    {
        // /proc/stat: "cpu user nice system idle iowait irq softirq steal ..."
        var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
        if (line == null) return null;
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => long.TryParse(x, out long n) ? n : 0).ToArray();
        if (v.Length < 4) return null;
        long idle = v[3] + (v.Length > 4 ? v[4] : 0), total = v.Take(Math.Min(v.Length, 8)).Sum();
        long di = idle - prevIdle, dt = total - prevTotal;
        prevIdle = idle; prevTotal = total;
        return dt > 0 ? Math.Clamp(100f * (dt - di) / dt, 0, 100) : null;
    }

    static (float UsedGb, float TotalGb) Memory()
    {
        long total = 0, avail = 0;
        foreach (var l in File.ReadLines("/proc/meminfo"))
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            if (p[0] == "MemTotal:") long.TryParse(p[1], out total);
            else if (p[0] == "MemAvailable:") long.TryParse(p[1], out avail);
        }
        const float Gb = 1024f * 1024f;                                          // kB -> GB
        return ((total - avail) / Gb, total / Gb);
    }

    /// nvidia-smi: temperatura și load-ul, citite la 2 s (pornirea procesului costă)
    (float? T, float? L)? Nvidia()
    {
        long now = Environment.TickCount64;
        if (now - nvidia.At < 2000) return nvidia.T == null ? null : (nvidia.T, nvidia.L);
        nvidia.At = now;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var f = o.Split('\n')[0].Split(',');
            if (f.Length >= 2 && float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
            {
                float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float l);
                nvidia.T = t; nvidia.L = l;
                return (t, l);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }   // nu e NVIDIA
        nvidia.T = null;
        return null;
    }

    // ------------------------------------------------------------ ventilatoarele: fanN_input (rpm) + pwmN (0-255) din hwmon
    List<Fan> ReadFans(List<(string Dir, string Name)> chips)
    {
        var found = new List<(string Id, string Name, float Rpm, float? Pwm, bool Gpu, int Index)>();
        foreach (var (dir, chip) in chips)
        {
            bool gpu = chip is "amdgpu" or "nouveau";
            foreach (var input in Directory.EnumerateFiles(dir, "fan*_input"))
            {
                string file = Path.GetFileName(input);
                if (!int.TryParse(file[3..file.IndexOf('_')], out int n)) continue;
                if (ReadNum(input) is not float rpm || rpm < 0) continue;
                string id = $"{chip}/{n}";
                if (rpm > 0) spun.Add(id);
                if (!spun.Contains(id) && !gpu) continue;                          // mufă goală
                string name = ReadText(Path.Combine(dir, $"fan{n}_label")) ?? (gpu ? "GPU Fan" : $"Fan {n}");
                float? pwm = ReadNum(Path.Combine(dir, $"pwm{n}")) is float pv ? pv * 100f / 255f : null;
                found.Add((id, name, rpm, pwm, gpu, n));
            }
        }
        string pumpLabel = L.T("POMPA", "PUMP");
        var typed = found.Select(f =>
        {
            string n = f.Name.ToLowerInvariant();
            bool pump = n.Contains("pump") || n.Contains("aio");
            var (rank, b) = pump ? (0, pumpLabel) : n.Contains("cpu") ? (1, "CPU") : f.Gpu ? (4, "GPU")
                : n.Contains("sys") || n.Contains("cha") || n.Contains("case") ? (3, "SYS") : (3, "FAN");
            return (f, rank, b);
        }).OrderBy(x => x.rank).ThenBy(x => x.f.Index).Take(MaxFans).ToList();
        var list = new List<Fan>();
        foreach (var g in typed.GroupBy(x => x.b))
        {
            int k = 0, count = g.Count();
            foreach (var (f, _, b) in g)
            {
                k++;
                maxRpm[f.Id] = Math.Max(maxRpm.GetValueOrDefault(f.Id), f.Rpm);
                float floor = b == pumpLabel ? 3000 : 1800;
                float pct = f.Pwm ?? 100f * f.Rpm / Math.Max(maxRpm[f.Id], floor);
                var num = System.Text.RegularExpressions.Regex.Match(f.Name, @"(\d+)\s*$");
                string label = count > 1 ? (b == pumpLabel ? L.T("POMP", "PUMP") : b) + (num.Success ? num.Groups[1].Value : k.ToString(CultureInfo.InvariantCulture)) : b;
                list.Add(new Fan(label, f.Rpm, Math.Clamp(pct, 0, 100), b == pumpLabel));
            }
        }
        return list.OrderBy(f => f.Pump ? 0 : 1).ToList();
    }

    public string DumpFans()
    {
        var sb = new StringBuilder();
        foreach (var (dir, chip) in Hwmon())
            foreach (var input in Directory.EnumerateFiles(dir, "fan*_input"))
                sb.AppendLine(CultureInfo.InvariantCulture, $"{chip,-12} {Path.GetFileName(input),-14} {ReadText(input)} rpm");
        sb.AppendLine("--- pe placă:");
        foreach (var f in Read().Fans) sb.AppendLine(CultureInfo.InvariantCulture, $"{f.Label,-6} {f.Rpm,6:0} rpm {f.Pct,4:0}%");
        return sb.ToString();
    }

    public void Dispose() { }
}
