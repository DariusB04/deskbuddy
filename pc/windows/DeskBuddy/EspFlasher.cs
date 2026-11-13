using System.Buffers.Binary;
using System.IO.Ports;
using System.Security.Cryptography;

namespace DeskBuddy;

/// Scrie firmware-ul pe ESP32 prin USB, direct prin bootloader-ul din ROM (același protocol ca esptool, fără esptool):
/// reset în modul de programare (DTR/RTS), SYNC, viteză mai mare, apoi fiecare zonă scrisă necomprimat și verificată cu MD5.
sealed class EspFlasher : IDisposable
{
    const byte CmdSync = 0x08, CmdReadReg = 0x0A, CmdSpiSetParams = 0x0B, CmdSpiAttach = 0x0D, CmdChangeBaud = 0x0F,
               CmdFlashBegin = 0x02, CmdFlashData = 0x03, CmdMd5 = 0x13;
    const int BlockSize = 0x400;                          // cât primește ROM-ul ESP32 într-un pachet
    const uint ChipMagicReg = 0x40001000, Esp32Magic = 0x00F01D83;
    public const int FlashSize = 4 * 1024 * 1024;
    public const int FastBaud = 460800;                   // merge pe CP2102 și CH340; ROM-ul pornește la 115200

    // harta memoriei (Minimal SPIFFS): 0x1000 bootloader, 0x8000 partiții, 0x9000 NVS (setările), 0xE000 otadata, 0x10000 aplicația
    const int NvsStart = 0x9000, NvsEnd = 0xE000, OtaData = 0xE000, AppStart = 0x10000;

    public sealed record Region(int Offset, byte[] Data);

    readonly SerialPort port;
    readonly IProgress<string> status;

    public EspFlasher(string portName, IProgress<string> status)
    {
        this.status = status;
        port = new SerialPort(portName, 115200) { DtrEnable = false, RtsEnable = false, Handshake = Handshake.None, ReadTimeout = 100, WriteTimeout = 3000 };
    }

    // ------------------------------------------------------------ ce se scrie
    /// imaginea completă (DeskBuddy-flash.bin, de la 0x0): tot, inclusiv setările (NVS) șterse
    public static List<Region> PlanFull(byte[] merged) => [new(0, Trim(merged, 0, merged.Length))];

    /// imaginea completă, dar fără zona NVS: bootloader, partiții, otadata și aplicația; WiFi-ul și restul setărilor rămân
    public static List<Region> PlanKeepSettings(byte[] merged) =>
        [new(0x1000, merged[0x1000..NvsStart]), new(NvsEnd, Trim(merged, NvsEnd, merged.Length))];

    /// doar aplicația (DeskBuddy.ino.bin): otadata resetată (pornește din app0, ca după prima instalare) + aplicația la 0x10000
    public static List<Region> PlanApp(byte[] app)
    {
        var ota = new byte[0x2000];
        Array.Fill(ota, (byte)0xFF);                      // = boot_app0.bin din core-ul ESP32
        ota[0] = 1;
        ota[0x1C] = 0x9A; ota[0x1D] = 0x98; ota[0x1E] = 0x43; ota[0x1F] = 0x47;
        ota[0x1000] = ota[0x1001] = ota[0x1002] = ota[0x1003] = 0;
        return [new(OtaData, ota), new(AppStart, app)];
    }

    public static bool LooksLikeMerged(byte[] b) =>
        b.Length > AppStart + 0x100 && b.Length <= FlashSize && b[0x1000] == 0xE9 && b[0x8000] == 0xAA && b[0x8001] == 0x50 &&
        DeviceFirmware.LooksLikeApp(b[AppStart..]);

    /// fără umplutura de 0xFF de la final (memoria ștearsă e oricum 0xFF), aliniat la 4 octeți
    static byte[] Trim(byte[] b, int from, int to)
    {
        int end = to;
        while (end > from && b[end - 1] == 0xFF) end--;
        end = Math.Min(to, (end + 3) & ~3);
        return b[from..end];
    }

    /// linie de comandă: usb-flash COM3 fișier.bin keep|full, sau usb-flash COM3 - probe (doar intră în bootloader și repornește)
    public static int Cli(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usb-flash <COMx> <fisier.bin|-> probe|keep|full"); return 2; }
        var log = new Progress<string>(s => Console.WriteLine(s));
        try
        {
            using var f = new EspFlasher(args[1], new SyncProgress(Console.WriteLine));
            if (args[3] == "raw")                      // depanare: ce scrie placa după reset-ul în bootloader
            {
                f.port.Open();
                f.EnterBootloader(50, discard: false);
                Thread.Sleep(400);
                byte[] buf = new byte[f.port.BytesToRead];
                f.port.Read(buf, 0, buf.Length);
                Console.WriteLine($"{buf.Length} B: " + System.Text.Encoding.ASCII.GetString(buf).Replace("\r", "\\r").Replace("\n", "\\n"));
                f.port.DiscardInBuffer();
                bool ok = f.TrySync();
                Console.WriteLine("sync: " + ok);
                if (!ok)
                {
                    Thread.Sleep(200);
                    byte[] b2 = new byte[f.port.BytesToRead];
                    f.port.Read(b2, 0, b2.Length);
                    Console.WriteLine($"după sync {b2.Length} B: " + Convert.ToHexString(b2));
                }
                f.HardReset();
                return 0;
            }
            if (args[3] == "nvs")                      // depanare: zona de setări e goală (doar 0xFF) sau are date?
            {
                f.Connect();
                var ff = new byte[0x1000];
                Array.Fill(ff, (byte)0xFF);
                string empty = Convert.ToHexString(MD5.HashData(ff));
                for (int s = 0x8000; s < 0x10000; s += 0x1000)          // partițiile, NVS (5 sectoare), otadata (2)
                {
                    var r = f.Command(CmdMd5, U32(s, 0x1000, 0, 0), timeoutMs: 8000, payload: 32);
                    string got = System.Text.Encoding.ASCII.GetString(r.Data, 0, 32);
                    Console.WriteLine($"0x{s:X5}: " + (got.Equals(empty, StringComparison.OrdinalIgnoreCase) ? "gol" : "date ") + " " + got);
                }
                f.HardReset();
                return 0;
            }
            if (args[3] == "nvskeys")                  // depanare: ce chei sunt în NVS (doar citire; fără valori)
            {
                f.Connect();
                var nvs = new byte[NvsEnd - NvsStart];
                for (int o = 0; o < nvs.Length; o += 64)
                {
                    var r = f.Command(0x0E, U32(NvsStart + o, 64), timeoutMs: 3000, payload: 64);   // READ_FLASH_SLOW (doar în ROM)
                    Buffer.BlockCopy(r.Data, 0, nvs, o, 64);
                }
                for (int sct = 0; sct < nvs.Length; sct += 0x1000)           // citirea e corectă? (MD5 calculat de placă)
                {
                    var r = f.Command(CmdMd5, U32(NvsStart + sct, 0x1000, 0, 0), timeoutMs: 8000, payload: 32);
                    bool same = System.Text.Encoding.ASCII.GetString(r.Data, 0, 32).Equals(Convert.ToHexString(MD5.HashData(nvs.AsSpan(sct, 0x1000))), StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"sector 0x{NvsStart + sct:X5}: citire {(same ? "corectă" : "GREȘITĂ")}");
                }
                f.HardReset();
                var names = new Dictionary<int, string> { [0] = "(namespace)" };
                var lines = new List<string>();
                for (int page = 0; page < nvs.Length / 0x1000; page++)
                {
                    int pb = page * 0x1000;
                    uint state = BinaryPrimitives.ReadUInt32LittleEndian(nvs.AsSpan(pb));
                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(nvs.AsSpan(pb + 4));
                    lines.Add($"pagina 0x{NvsStart + pb:X5}: stare 0x{state:X8} seq {seq}");
                    for (int e = 0; e < 126; e++)
                    {
                        int st = nvs[pb + 32 + e / 4] >> (e % 4 * 2) & 3;
                        if (st != 2) continue;                         // 2 = scris (3 gol, 0 șters)
                        int eb = pb + 64 + e * 32;
                        int ns = nvs[eb], type = nvs[eb + 1], span = nvs[eb + 2];
                        string key = System.Text.Encoding.ASCII.GetString(nvs, eb + 8, 16).Split('\0')[0];
                        if (ns == 0 && type == 0x01) names[nvs[eb + 24]] = key;
                        string extra = type == 0x21 ? $" șir {BinaryPrimitives.ReadUInt16LittleEndian(nvs.AsSpan(eb + 24))} B" : $" tip 0x{type:X2}";
                        lines.Add($"   ns {ns,3} {key,-16}{extra}");
                        e += Math.Max(0, span - 1);
                    }
                }
                foreach (var l in lines) Console.WriteLine(l);
                Console.WriteLine("namespace-uri: " + string.Join(", ", names.Select(kv => $"{kv.Key}={kv.Value}")));
                return 0;
            }
            if (args[3] == "erase-nvs")                // reparație: șterge doar zona de setări (placa pornește în SETUP)
            {
                f.Connect();
                f.Command(CmdFlashBegin, U32(NvsEnd - NvsStart, 0, BlockSize, NvsStart), timeoutMs: 10_000);   // FLASH_BEGIN șterge zona
                var ff = new byte[NvsEnd - NvsStart];
                Array.Fill(ff, (byte)0xFF);
                bool clean = f.FlashMd5(NvsStart, NvsEnd - NvsStart) == Convert.ToHexString(MD5.HashData(ff));
                Console.WriteLine(clean ? "OK: zona de setări e ștearsă." : "EROARE: zona de setări nu e goală după ștergere.");
                f.HardReset();
                return clean ? 0 : 1;
            }
            if (args[3] == "probe")
            {
                f.Connect();
                Console.WriteLine("OK: ESP32 găsit, repornesc placa.");
                f.HardReset();
                return 0;
            }
            byte[] img = File.ReadAllBytes(args[2]);
            bool merged = LooksLikeMerged(img);
            if (!merged && !DeviceFirmware.LooksLikeApp(img)) { Console.Error.WriteLine("fișierul nu e un firmware ESP32"); return 2; }
            var plan = !merged ? PlanApp(img) : args[3] == "full" ? PlanFull(img) : PlanKeepSettings(img);
            foreach (var r in plan) Console.WriteLine($"zona 0x{r.Offset:X5}: {r.Data.Length} B");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            f.Flash(plan);
            Console.WriteLine($"OK în {sw.Elapsed.TotalSeconds:0.0} s");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("EROARE: " + e.Message); return 1; }
    }

    sealed class SyncProgress(Action<string> a) : IProgress<string> { public void Report(string v) => a(v); }

    // ------------------------------------------------------------ flash
    public void Flash(IReadOnlyList<Region> regions)
    {
        Connect();
        // dacă nicio zonă nu atinge NVS-ul, verificăm că a rămas neatins (MD5 înainte și după, calculat de placă)
        bool keepNvs = regions.All(r => r.Offset >= NvsEnd || r.Offset + r.Data.Length <= NvsStart);
        string? nvsBefore = keepNvs ? FlashMd5(NvsStart, NvsEnd - NvsStart) : null;
        long total = regions.Sum(r => (long)r.Data.Length), done = 0;
        foreach (var r in regions)
        {
            WriteRegion(r, total, done);
            done += r.Data.Length;
        }
        if (nvsBefore != null && FlashMd5(NvsStart, NvsEnd - NvsStart) != nvsBefore)
            throw new IOException("firmware-ul e scris, dar zona de setări s-a modificat: verifică setările plăcii");
        status.Report("Gata, repornesc placa...");
        HardReset();
    }

    string FlashMd5(int offset, int size) =>
        System.Text.Encoding.ASCII.GetString(Command(CmdMd5, U32((uint)offset, (uint)size, 0, 0), timeoutMs: PerMb(size, 8_000), payload: 32).Data, 0, 32).ToUpperInvariant();

    void Connect()
    {
        status.Report($"Mă conectez la placă pe {port.PortName}...");
        port.Open();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            EnterBootloader(attempt % 2 == 0 ? 50 : 550);  // unele plăci au nevoie să țină IO0 jos mai mult
            for (int i = 0; i < 5; i++)
                if (TrySync()) goto synced;
        }
        throw new IOException("placa nu intră în modul de programare. Ține apăsat butonul BOOT de pe ea și încearcă din nou.");

    synced:
        uint magic = Command(CmdReadReg, U32(ChipMagicReg)).Value;
        if (magic != Esp32Magic) throw new IOException($"nu e un ESP32 clasic (cip 0x{magic:X8})");
        status.Report("Placa a răspuns. Măresc viteza...");
        Command(CmdChangeBaud, U32(FastBaud, 0));
        port.BaudRate = FastBaud;
        Thread.Sleep(60);
        port.DiscardInBuffer();
        Command(CmdSpiAttach, new byte[8]);
        Command(CmdSpiSetParams, U32(0, FlashSize, 64 * 1024, 4096, 256, 0xFFFF));
    }

    /// reset cu IO0 jos (circuitul de auto-reset de pe plăci: RTS -> EN, DTR -> IO0, ca în esptool)
    void EnterBootloader(int holdMs, bool discard = true)
    {
        Lines(dtr: false, rts: true);                     // EN jos: placa în reset
        Thread.Sleep(100);
        Lines(dtr: true, rts: false);                     // EN sus, IO0 jos: pornește în bootloader
        Thread.Sleep(holdMs);
        Lines(dtr: false, rts: null);
        if (discard) port.DiscardInBuffer();
    }

    void HardReset()
    {
        Lines(dtr: false, rts: true);
        Thread.Sleep(120);
        Lines(dtr: null, rts: false);
    }

    /// DTR/RTS direct cu EscapeCommFunction: proprietățile SerialPort refac toată configurarea portului la fiecare
    /// schimbare (lent pe USB), iar între cele două linii placa apucă să iasă din reset cu IO0 sus (pornește normal)
    void Lines(bool? dtr, bool? rts)
    {
        var h = port.BaseStream.GetType().GetField("_handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(port.BaseStream) as Microsoft.Win32.SafeHandles.SafeFileHandle;
        if (h == null || h.IsInvalid)
        {
            if (dtr is bool d0) port.DtrEnable = d0;
            if (rts is bool r0) port.RtsEnable = r0;
            return;
        }
        if (dtr is bool d) EscapeCommFunction(h, d ? SETDTR : CLRDTR);
        if (rts is bool r) EscapeCommFunction(h, r ? SETRTS : CLRRTS);
    }

    const int SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EscapeCommFunction(Microsoft.Win32.SafeHandles.SafeFileHandle file, int func);

    bool TrySync()
    {
        var data = new byte[36];
        data[0] = 0x07; data[1] = 0x07; data[2] = 0x12; data[3] = 0x20;
        for (int i = 4; i < 36; i++) data[i] = 0x55;
        try
        {
            Command(CmdSync, data, timeoutMs: 150);
            for (int i = 0; i < 8; i++) if (ReadFrame(Environment.TickCount64 + 50) == null) break;   // ROM-ul mai trimite câteva răspunsuri
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException) { return false; }
    }

    void WriteRegion(Region r, long total, long before)
    {
        byte[] data = r.Data;
        if (data.Length % 4 != 0) { Array.Resize(ref data, (data.Length + 3) & ~3); Array.Fill(data, (byte)0xFF, r.Data.Length, data.Length - r.Data.Length); }
        // Necomprimat (FLASH_BEGIN / FLASH_DATA). Varianta comprimată (FLASH_DEFL_*) are o problemă în ROM-ul ESP32: când o zonă
        // are exact un multiplu de 32 KB (fereastra de decompresie), ROM-ul o mai scrie o dată imediat după ea, fără ștergere.
        // Zona bootloader + partiții are fix 32 KB, deci se scria peste NVS (0x9000) și placa își pierdea setările.
        int blocks = (data.Length + BlockSize - 1) / BlockSize;
        status.Report($"Șterg zona 0x{r.Offset:X5} ({data.Length / 1024} KB)...");
        Command(CmdFlashBegin, U32((uint)data.Length, (uint)blocks, BlockSize, (uint)r.Offset), timeoutMs: PerMb(data.Length, 30_000));

        for (int seq = 0; seq < blocks; seq++)
        {
            int n = Math.Min(BlockSize, data.Length - seq * BlockSize);
            var pkt = new byte[16 + BlockSize];
            BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(0), BlockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), (uint)seq);
            Array.Fill(pkt, (byte)0xFF, 16, BlockSize);   // ultimul bloc completat cu 0xFF (scrierea de 0xFF nu schimbă flash-ul)
            Buffer.BlockCopy(data, seq * BlockSize, pkt, 16, n);
            byte chk = 0xEF;
            for (int i = 16; i < pkt.Length; i++) chk ^= pkt[i];
            Command(CmdFlashData, pkt, chk, timeoutMs: 5_000);
            long doneBytes = before + (long)data.Length * (seq + 1) / blocks;
            if (seq % 16 == 0 || seq == blocks - 1) status.Report($"Scriu firmware-ul... {doneBytes * 100 / Math.Max(1, total)}%");
        }

        status.Report($"Verific zona 0x{r.Offset:X5}...");
        var md5 = Command(CmdMd5, U32((uint)r.Offset, (uint)data.Length, 0, 0), timeoutMs: PerMb(data.Length, 8_000), payload: 32);
        string got = System.Text.Encoding.ASCII.GetString(md5.Data, 0, 32);
        string want = Convert.ToHexString(MD5.HashData(data));
        if (!got.Equals(want, StringComparison.OrdinalIgnoreCase)) throw new IOException($"verificarea a eșuat la 0x{r.Offset:X5} (MD5 diferit)");
    }

    static int PerMb(int bytes, int msPerMb) => Math.Max(5000, (int)((long)msPerMb * bytes / (1024 * 1024)) + 3000);

    // ------------------------------------------------------------ protocolul (SLIP)
    readonly record struct Reply(uint Value, byte[] Data);

    Reply Command(byte op, byte[] data, uint chk = 0, int timeoutMs = 3000, int payload = 0)
    {
        var pkt = new byte[8 + data.Length];
        pkt[0] = 0x00;
        pkt[1] = op;
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), chk);
        Buffer.BlockCopy(data, 0, pkt, 8, data.Length);
        WriteFrame(pkt);

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var f = ReadFrame(deadline) ?? throw new TimeoutException($"placa nu răspunde (comanda 0x{op:X2})");
            if (f.Length < 8 || f[0] != 0x01 || f[1] != op) continue;          // răspunsuri vechi (ex. la SYNC)
            int len = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(2));
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(4));
            byte[] body = f[8..Math.Min(f.Length, 8 + len)];
            if (body.Length < payload + 2) throw new IOException($"răspuns scurt la comanda 0x{op:X2}");
            if (body[payload] != 0) throw new IOException($"placa a refuzat comanda 0x{op:X2} (eroare 0x{body[payload + 1]:X2})");
            return new Reply(value, body);
        }
    }

    void WriteFrame(byte[] pkt)
    {
        var o = new List<byte>(pkt.Length + 16) { 0xC0 };
        foreach (byte b in pkt)
        {
            if (b == 0xC0) { o.Add(0xDB); o.Add(0xDC); }
            else if (b == 0xDB) { o.Add(0xDB); o.Add(0xDD); }
            else o.Add(b);
        }
        o.Add(0xC0);
        port.Write(o.ToArray(), 0, o.Count);
    }

    byte[]? ReadFrame(long deadline)
    {
        var buf = new List<byte>(64);
        bool inFrame = false, esc = false;
        while (true)
        {
            int left = (int)(deadline - Environment.TickCount64);
            if (left <= 0) return null;
            port.ReadTimeout = Math.Max(1, left);
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { return null; }
            if (b < 0) return null;
            if (!inFrame) { if (b == 0xC0) inFrame = true; continue; }
            if (b == 0xC0)
            {
                if (buf.Count == 0) continue;                 // două 0xC0 la rând: începutul unui pachet nou
                return buf.ToArray();
            }
            if (esc) { buf.Add(b == 0xDC ? (byte)0xC0 : b == 0xDD ? (byte)0xDB : (byte)b); esc = false; }
            else if (b == 0xDB) esc = true;
            else buf.Add((byte)b);
        }
    }

    static byte[] U32(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    static byte[] U32(params int[] v) => U32(v.Select(x => (uint)x).ToArray());

    public void Dispose()
    {
        try { if (port.IsOpen) { port.DtrEnable = false; port.RtsEnable = false; port.Close(); } } catch (IOException) { }
        port.Dispose();
    }
}
