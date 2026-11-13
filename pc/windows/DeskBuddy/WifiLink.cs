using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DeskBuddy;

/// Placa pe WiFi, prin UDP (portul aplicației: 4212):
///   - placa „sună acasă”: {"t":"hb"} la 3 s spre IP-ul PC-ului (îl știe de la aplicație, pe USB, sau din pachetele primite);
///     răspundem pe același drum, deci merge și când placa e în spatele altui router (NAT), unde PC-ul n-ar ajunge singur la ea
///   - căutare: {"t":"hi"} prin broadcast pe portul plăcii (4210) când n-am auzit-o de 10 s; placa răspunde cu versiunea
///   - cereri cu răspuns ({"t":"req"} -> {"res":id,...}); răspunsurile mari vin în bucăți „P<id>/<k>/<n>\n...”
/// Datele (temperaturi, Claude) pleacă direct la placă când o știm, altfel prin broadcast, ca înainte.
sealed class WifiLink : IDisposable
{
    public const int AppPort = 4212;
    const long SeenMs = 12_000;

    readonly UdpClient? sock;
    readonly UdpSender broadcast = new();
    readonly object gate = new();
    readonly List<(string Prefix, TaskCompletionSource<string> Reply)> pending = [];
    readonly Dictionary<string, (int N, string?[] Parts)> chunks = [];
    readonly Thread? thread;
    volatile bool stop;
    IPEndPoint? endpoint;
    long seenAt = -SeenMs, helloAt = -60_000;
    volatile UsbLink.Board? board;
    public string? Error { get; }

    /// placa a apărut / a dispărut pe WiFi
    public event Action? Changed;

    public WifiLink()
    {
        try
        {
            sock = new UdpClient(new IPEndPoint(IPAddress.Any, AppPort)) { EnableBroadcast = true };
            sock.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null);   // fără erori când placa dispare
            thread = new Thread(Loop) { IsBackground = true, Name = "wifi-link" };
            thread.Start();
        }
        catch (SocketException e) { Error = "portul UDP " + AppPort + " e ocupat: " + e.Message; }
    }

    /// placa pe WiFi (null = n-am auzit-o de 12 s)
    public UsbLink.Board? Connected => Environment.TickCount64 - Interlocked.Read(ref seenAt) < SeenMs ? board : null;
    public IPEndPoint? Endpoint => Connected != null ? endpoint : null;
    /// IP-ul plăcii în rețeaua ei (poate fi în spatele altui router: atunci nu e accesibil direct, doar prin Endpoint)
    public IPAddress? BoardIp => Connected?.Ip is { } s && IPAddress.TryParse(s, out var a) ? a : null;
    /// placa e în aceeași rețea (îi putem deschide pagina / urca firmware-ul prin HTTP)
    public bool Direct => Endpoint is { } ep && BoardIp is { } ip && ep.Address.Equals(ip);
    public IReadOnlyList<IPAddress> BroadcastTargets => broadcast.Targets;

    public void ResetTargets() { broadcast.ResetTargets(); Interlocked.Exchange(ref helloAt, -60_000); }

    public void Send(byte[] data)
    {
        if (Endpoint is { } ep && sock != null)
        {
            try { sock.Send(data, data.Length, ep); return; }
            catch (SocketException) { }
        }
        broadcast.Send(data);
    }

    public async Task<string?> RequestAsync(string json, string prefix, int timeoutMs)
    {
        if (Endpoint is not { } ep || sock == null) return null;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = (prefix, tcs);
        lock (gate) pending.Add(entry);
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            for (int attempt = 0; attempt < 3; attempt++)           // UDP: dacă se pierde, mai încercăm
            {
                try { sock.Send(b, b.Length, ep); } catch (SocketException) { return null; }
                if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs / 3)) == tcs.Task) return tcs.Task.Result;
            }
            return null;
        }
        finally { lock (gate) pending.Remove(entry); }
    }

    void Loop()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        var discover = new Thread(DiscoverLoop) { IsBackground = true, Name = "wifi-discover" };
        discover.Start();
        while (!stop)
        {
            byte[] d;
            try { d = sock!.Receive(ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            if (d.Length == 0) continue;
            string text = Encoding.UTF8.GetString(d);
            if (text[0] == 'P') { OnChunk(text, from); continue; }
            OnLine(text.Trim(), from);
        }
    }

    void OnChunk(string text, IPEndPoint from)
    {
        int nl = text.IndexOf('\n');
        if (nl < 0) return;
        var head = text[1..nl].Split('/');
        if (head.Length != 3 || !int.TryParse(head[1], out int k) || !int.TryParse(head[2], out int n) || n <= 0 || n > 64 || k >= n) return;
        string key = from + "#" + head[0];
        string? whole = null;
        lock (gate)
        {
            if (!chunks.TryGetValue(key, out var c) || c.N != n) c = (n, new string?[n]);
            c.Parts[k] = text[(nl + 1)..];
            chunks[key] = c;
            if (c.Parts.All(p => p != null)) { whole = string.Concat(c.Parts); chunks.Remove(key); }
            if (chunks.Count > 16) chunks.Clear();
        }
        if (whole != null) OnLine(whole, from);
    }

    void OnLine(string line, IPEndPoint from)
    {
        if (line.Length == 0 || line[0] != '{') return;
        bool hb = line.StartsWith("{\"t\":\"hb\"", StringComparison.Ordinal);
        if (hb || line.StartsWith("{\"deskbuddy\"", StringComparison.Ordinal))
        {
            OnHello(line, from);
            if (hb && sock != null)                                   // ține deschis drumul prin router (NAT)
                try { sock.Send("{\"t\":\"ack\"}"u8.ToArray(), 11, from); } catch (SocketException) { }
            return;
        }
        lock (gate)
            foreach (var p in pending)
                if (line.StartsWith(p.Prefix, StringComparison.Ordinal)) { p.Reply.TrySetResult(line); break; }
    }

    void OnHello(string line, IPEndPoint from)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Bool(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            int st = r.TryGetProperty("st", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
            var b = new UsbLink.Board("WiFi", Str("deskbuddy"), Str("variant"), Bool("wifi"), Str("ip") is { Length: > 0 } ip ? ip : null,
                                      Str("ssid"), st, Bool("setup"), Bool("pw"));
            bool was = Connected != null;
            bool changed = board != b || !Equals(endpoint, from) || !was;
            board = b;
            endpoint = new IPEndPoint(from.Address, from.Port);
            Interlocked.Exchange(ref seenAt, Environment.TickCount64);
            if (changed) ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke());
        }
        catch (JsonException) { }
    }

    /// când n-am auzit placa de 10 s: „hi” prin broadcast (+ IP-ul setat de mână); și anunță dispariția ei
    void DiscoverLoop()
    {
        bool had = false;
        while (!stop)
        {
            Thread.Sleep(1000);
            long now = Environment.TickCount64;
            bool seen = Connected != null;
            if (had && !seen) ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke());
            had = seen;
            if (sock == null || (seen && now - Interlocked.Read(ref seenAt) < 10_000)) continue;
            if (now - Interlocked.Read(ref helloAt) < 5000) continue;
            Interlocked.Exchange(ref helloAt, now);
            byte[] hi = "{\"t\":\"hi\"}"u8.ToArray();
            foreach (var t in UdpSender.BroadcastTargets())
                try { sock.Send(hi, hi.Length, new IPEndPoint(t, UdpSender.Port)); } catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        stop = true;
        sock?.Dispose();
        broadcast.Dispose();
    }
}
