using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeskBuddy;

/// Placa prin cablul USB: caută portul COM al cipului USB-serial (CP210x, CH340, FTDI), îl ține deschis și trimite
/// pachetele ca linii JSON, exact ca pe UDP. Placa răspunde la {"t":"hi"} cu versiunea, așa știm că e DeskBuddy.
/// DTR și RTS rămân jos: pe plăcile ESP32 ele resetează placa / o pun în modul de programare.
sealed class UsbLink : IDisposable
{
    public const int Baud = 115200;

    /// cipurile USB-serial de pe plăcile ESP32 (VID&PID)
    static readonly (string Id, string Chip)[] Known =
    [
        ("VID_10C4&PID_EA60", "CP210x"), ("VID_1A86&PID_7523", "CH340"), ("VID_1A86&PID_55D4", "CH9102"),
        ("VID_0403&PID_6001", "FTDI"), ("VID_0403&PID_6015", "FTDI"),
    ];

    public sealed record PortInfo(string Name, string Chip)
    {
        public override string ToString() => $"{Name} ({Chip})";
    }

    /// placa găsită pe USB: portul + ce a răspuns la "hi"
    /// Ssid = rețeaua salvată pe placă; WifiStatus = WiFi.status() (1 rețeaua nu se vede, 4 conectare eșuată, 6 deconectat)
    public sealed record Board(string Port, string Version, string Variant, bool Wifi, string? Ip, string Ssid, int WifiStatus, bool Setup);

    readonly List<(string Prefix, TaskCompletionSource<string> Reply)> pending = [];
    int nextId;

    /// placa cere ceva de la PC (fără WiFi): {"need":"wx"|"pr"|"http",...} (din thread-ul de fundal)
    public event Action<string>? Need;

    public int NextId() => Interlocked.Increment(ref nextId);

    /// trimite o comandă și așteaptă linia de răspuns care începe cu prefix (ex. {"scan"); mai multe pot aștepta deodată
    public async Task<string?> RequestAsync(string json, string prefix, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = (prefix, tcs);
        lock (gate) pending.Add(entry);
        try
        {
            if (!Send(System.Text.Encoding.UTF8.GetBytes(json))) return null;
            return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task ? tcs.Task.Result : null;
        }
        finally { lock (gate) pending.Remove(entry); }
    }

    readonly object gate = new();
    readonly Thread thread;
    readonly Dictionary<string, long> retryAt = [];
    SerialPort? port;
    string? probing;                         // port deschis, dar placa n-a răspuns încă (poate pornește)
    long probeSince, lastHi, lastReply;
    readonly StringBuilder rx = new();
    volatile Board? board;
    volatile bool enabled = true, paused, stop;

    /// placa a apărut / a dispărut pe USB (din thread-ul de fundal)
    public event Action? Changed;

    public Board? Connected => board;

    public UsbLink()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "usb-link" };
        thread.Start();
    }

    /// „Doar WiFi” -> portul rămâne liber (ex. pentru Serial Monitor)
    public bool Enabled
    {
        get => enabled;
        set { enabled = value; if (!value) Close(); }
    }

    /// pentru flash: eliberează portul până la Resume()
    public void Pause() { paused = true; Close(); }
    public void Resume() { paused = false; lock (gate) retryAt.Clear(); }

    public bool Send(byte[] data)
    {
        lock (gate)
        {
            if (board == null || port == null) return false;
            try
            {
                port.Write(data, 0, data.Length);
                port.Write("\n");
                return true;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
                CloseLocked();
                return false;
            }
        }
    }

    /// porturile COM prezente acum care au un cip USB-serial cunoscut (din registry, fără WMI)
    public static List<PortInfo> CandidatePorts()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var list = new List<PortInfo>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return list;
            foreach (string dev in usb.GetSubKeyNames())
            {
                var chip = Known.FirstOrDefault(k => dev.StartsWith(k.Id, StringComparison.OrdinalIgnoreCase)).Chip;
                if (chip == null) continue;
                using var devKey = usb.OpenSubKey(dev);
                if (devKey == null) continue;
                foreach (string inst in devKey.GetSubKeyNames())
                {
                    using var p = devKey.OpenSubKey(inst + @"\Device Parameters");
                    if (p?.GetValue("PortName") is string name && present.Contains(name) && !list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new PortInfo(name, chip));
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// plăci conectate acum, dar fără driver (deci fără port COM): (cip, pagina driverului)
    public static List<(string Chip, string DriverUrl)> MissingDrivers()
    {
        var list = new List<(string, string)>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return list;
            foreach (string dev in usb.GetSubKeyNames())
            {
                var chip = Known.FirstOrDefault(k => dev.StartsWith(k.Id, StringComparison.OrdinalIgnoreCase)).Chip;
                if (chip == null) continue;
                using var devKey = usb.OpenSubKey(dev);
                foreach (string inst in devKey?.GetSubKeyNames() ?? [])
                {
                    if (CM_Locate_DevNodeW(out uint node, $@"USB\{dev}\{inst}", 0) != 0) continue;     // nu e conectată acum
                    if (CM_Get_DevNode_Status(out uint st, out _, node, 0) != 0 || (st & DN_HAS_PROBLEM) == 0) continue;
                    string url = chip switch
                    {
                        "CP210x" => "https://www.silabs.com/developer-tools/usb-to-uart-bridge-vcp-drivers",
                        "CH340" or "CH9102" => "https://www.wch-ic.com/downloads/CH341SER_EXE.html",
                        _ => "https://ftdichip.com/drivers/vcp-drivers/",
                    };
                    if (!list.Any(x => x.Item1 == chip)) list.Add((chip, url));
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    const uint DN_HAS_PROBLEM = 0x400;
    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);
    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);

    // ------------------------------------------------------------ bucla de fundal
    void Loop()
    {
        while (!stop)
        {
            try { Step(); }
            catch (Exception) { Close(); }
            Thread.Sleep(pending.Count > 0 ? 20 : board != null || probing != null ? 200 : 3000);   // cu o cerere în așteptare: răspuns rapid
        }
    }

    void Step()
    {
        if (!enabled || paused) return;
        long now = Environment.TickCount64;
        lock (gate)
        {
            if (port == null)
            {
                foreach (var c in CandidatePorts())
                {
                    if (retryAt.TryGetValue(c.Name, out long at) && now < at) continue;
                    var sp = new SerialPort(c.Name, Baud)
                    {
                        DtrEnable = false, RtsEnable = false, Handshake = Handshake.None,
                        ReadTimeout = 50, WriteTimeout = 3000, Encoding = Encoding.UTF8,
                        ReadBufferSize = 1 << 16, WriteBufferSize = 1 << 16,   // pagina de setări vine pe o linie de ~13 KB
                    };
                    try { sp.Open(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        sp.Dispose();                   // ocupat (Arduino, alt program): mai încercăm peste 30 s
                        retryAt[c.Name] = now + 30_000;
                        continue;
                    }
                    port = sp;
                    probing = c.Name;
                    probeSince = now;
                    lastHi = 0;
                    rx.Clear();
                    break;
                }
                if (port == null) return;
            }

            // citește ce a trimis placa (răspunsul la "hi" + mesajele ei de depanare, pe care le ignorăm)
            string got;
            try { got = port.BytesToRead > 0 ? port.ReadExisting() : ""; }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { CloseLocked(); return; }
            if (got.Length > 0)
            {
                rx.Append(got);
                if (rx.Length > 1 << 18) rx.Remove(0, rx.Length - (1 << 17));
                string all = rx.ToString();
                int nl;
                while ((nl = all.IndexOf('\n')) >= 0)
                {
                    string line = all[..nl].Trim();
                    all = all[(nl + 1)..];
                    if (line.Length == 0 || line[0] != '{') continue;          // mesajele de depanare ale plăcii
                    if (board != null) lastReply = now;
                    if (line.StartsWith("{\"deskbuddy\"", StringComparison.Ordinal)) OnHello(line, now);
                    else if (line.StartsWith("{\"need\"", StringComparison.Ordinal))
                    {
                        var l = line;
                        ThreadPool.QueueUserWorkItem(_ => Need?.Invoke(l));
                    }
                    else
                        foreach (var p in pending)
                            if (line.StartsWith(p.Prefix, StringComparison.Ordinal)) { p.Reply.TrySetResult(line); break; }
                }
                rx.Clear().Append(all);
            }

            // "hi" la 1 s cât căutăm, apoi la 10 s; fără răspuns 35 s -> placa a dispărut / a fost resetată
            long every = board == null ? 1000 : 10_000;
            if (now - lastHi >= every)
            {
                lastHi = now;
                try { port.Write("\n{\"t\":\"hi\"}\n"); }
                catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException) { CloseLocked(); return; }
            }
            if (board == null && now - probeSince > 25_000)
            {
                retryAt[probing ?? port.PortName] = now + 60_000;   // nu e DeskBuddy (sau are firmware vechi, fără USB)
                CloseLocked();
            }
            else if (board != null && now - lastReply > 35_000) CloseLocked();
        }
    }

    void OnHello(string line, long now)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Bool(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            int st = r.TryGetProperty("st", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
            var b = new Board(port!.PortName, Str("deskbuddy"), Str("variant"), Bool("wifi"), Str("ip") is { Length: > 0 } ip ? ip : null,
                              Str("ssid"), st, Bool("setup"));
            lastReply = now;
            probing = null;
            bool changed = board != b;
            board = b;
            if (changed) ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke());
        }
        catch (JsonException) { }
    }

    void Close() { lock (gate) CloseLocked(); }

    void CloseLocked()
    {
        bool had = board != null;
        board = null;
        probing = null;
        try { port?.Close(); } catch (IOException) { }
        port?.Dispose();
        port = null;
        if (had) ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke());
    }

    public void Dispose()
    {
        stop = true;
        Close();
    }
}
