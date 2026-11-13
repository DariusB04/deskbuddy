using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Când placa n-are WiFi, dar e pe USB, îi cere PC-ului ce îi lipsește (setările ei rămân pe placă, în NVS):
///   {"need":"wx",...}   vremea (Open-Meteo, aceeași cerere ca placa)          -> {"t":"wx","city","d":{current,daily}}
///   {"need":"pr",...}   imprimanta Bambu: PC-ul ține sesiunea MQTT              -> {"t":"pr","ok","rc","p":{câmpurile din "print"}}
///   {"need":"http",...} cererile HTTPS ale login-ului Bambu (doar domenii Bambu) -> {"t":"httpres","id","c","b"}
sealed class BoardNeeds(Func<byte[], bool> send) : IDisposable
{
    static readonly HttpClient Http = CreateClient();
    static readonly HttpClient BambuHttp = new() { Timeout = TimeSpan.FromSeconds(15) };   // fără User-Agent implicit: îl pune cererea
    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly string[] BambuHosts = ["api.bambulab.com", "bambulab.com", "api.bambulab.cn", "bambulab.cn"];

    readonly object gate = new();
    (double Lat, double Lon, string City)? ipLoc;
    BambuMqtt? mqtt;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBuddy-Windows");
        return c;
    }

    void Send(JsonObject o) => send(Encoding.UTF8.GetBytes(o.ToJsonString(Relaxed)));

    public void Handle(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement.Clone();
            switch (r.GetProperty("need").GetString())
            {
                case "wx": _ = WeatherAsync(r); break;
                case "http": _ = HttpAsync(r); break;
                case "pr": Bambu(r); break;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static bool Bool(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    // ------------------------------------------------------------ vremea
    async Task WeatherAsync(JsonElement r)
    {
        var res = new JsonObject { ["t"] = "wx" };
        try
        {
            double lat, lon;
            string city;
            if (Bool(r, "auto"))
            {
                ipLoc ??= await IpLocationAsync();       // o dată pe pornirea aplicației, ca placa
                if (ipLoc is not { } loc) { res["err"] = -3; Send(res); return; }
                (lat, lon, city) = loc;
            }
            else
            {
                lat = r.GetProperty("lat").GetDouble();
                lon = r.GetProperty("lon").GetDouble();
                city = Str(r, "city");
            }
            string url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={lat:0.0000}&longitude={lon:0.0000}") +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=3" +
                (Bool(r, "f") ? "&temperature_unit=fahrenheit" : "");
            var j = JsonNode.Parse(await Http.GetStringAsync(url));
            if (j?["current"] is not JsonNode cur) { res["err"] = -4; Send(res); return; }
            res["city"] = city;
            res["d"] = new JsonObject { ["current"] = cur.DeepClone(), ["daily"] = j["daily"]?.DeepClone() };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            res.Remove("city");
            res.Remove("d");
            res["err"] = -1;
        }
        Send(res);
    }

    static async Task<(double, double, string)?> IpLocationAsync()
    {
        try
        {
            var j = JsonNode.Parse(await Http.GetStringAsync("http://ip-api.com/json/?fields=status,lat,lon,city"));
            if ((string?)j?["status"] != "success") return null;
            return ((double)j!["lat"]!, (double)j["lon"]!, Ascii((string?)j["city"] ?? ""));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    /// fonturile OLED n-au diacritice: „Brașov” -> „Brasov” (placa ar sări peste litere)
    static string Ascii(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (c < 0x7F && c >= 0x20) sb.Append(c);
        return sb.ToString();
    }

    // ------------------------------------------------------------ login Bambu (HTTPS făcut de PC)
    async Task HttpAsync(JsonElement r)
    {
        var res = new JsonObject { ["t"] = "httpres", ["id"] = r.GetProperty("id").GetInt64() };
        try
        {
            string url = Str(r, "u");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ||
                !BambuHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase))
            {
                res["c"] = -100;                          // placa poate cere prin PC doar serverele Bambu
                Send(res);
                return;
            }
            using var req = new HttpRequestMessage(Str(r, "m") == "POST" ? HttpMethod.Post : HttpMethod.Get, u);
            if (r.TryGetProperty("b", out var b)) req.Content = new StringContent(b.GetString() ?? "", Encoding.UTF8, "application/json");
            // aceleași antete ca firmware-ul (și ca OrcaSlicer)
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "bambu_network_agent/01.09.05.01");
            req.Headers.TryAddWithoutValidation("X-BBL-Client-Name", "OrcaSlicer");
            req.Headers.TryAddWithoutValidation("X-BBL-Client-Type", "slicer");
            req.Headers.TryAddWithoutValidation("X-BBL-Client-Version", "01.09.05.51");
            req.Headers.TryAddWithoutValidation("X-BBL-Language", "en-US");
            if (Str(r, "bearer") is { Length: > 0 } t) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + t);
            using var resp = await BambuHttp.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            try { if (body.Length > 4000) body = JsonNode.Parse(body)?.ToJsonString(Relaxed) ?? body; } catch (JsonException) { }
            res["c"] = (int)resp.StatusCode;
            if (body.Length > 15000) res["c"] = -5;       // nu încape în linia pe care o citește placa
            else res["b"] = body;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { res["c"] = -1; }
        Send(res);
    }

    // ------------------------------------------------------------ imprimanta (MQTT)
    void Bambu(JsonElement r)
    {
        bool cloud = Bool(r, "cloud");
        var p = cloud
            ? new BambuMqtt.Params(Str(r, "host"), "u_" + Str(r, "uid"), Str(r, "token"), Str(r, "serial"), true)
            : new BambuMqtt.Params(Str(r, "ip"), "bblp", Str(r, "code"), Str(r, "serial"), false);
        if (p.Host.Length == 0 || p.Serial.Length == 0) return;
        lock (gate)
        {
            if (mqtt == null || mqtt.P != p || mqtt.Stopped)
            {
                mqtt?.Dispose();
                mqtt = new BambuMqtt(p, o => Send(o));
            }
            mqtt.Touch();
        }
    }

    public void Dispose()
    {
        lock (gate) { mqtt?.Dispose(); mqtt = null; }
    }
}

/// Client MQTT 3.1.1 minimal (TLS, QoS 0) pentru imprimanta Bambu, ca bambuTask din firmware: abonare la device/<serial>/report,
/// „pushall” la conectare și la 10 min. Se oprește singur dacă placa nu-l mai cere 90 s (a prins WiFi, a fost scoasă din USB).
sealed class BambuMqtt : IDisposable
{
    public sealed record Params(string Host, string User, string Pass, string Serial, bool Cloud);

    static readonly string[] Keys = ["gcode_state", "subtask_name", "mc_percent", "mc_remaining_time", "layer_num",
                                     "total_layer_num", "nozzle_temper", "nozzle_target_temper", "bed_temper", "bed_target_temper"];
    const string Pushall = "{\"pushing\":{\"sequence_id\":\"0\",\"command\":\"pushall\",\"version\":1,\"push_target\":1}}";

    public Params P { get; }
    public bool Stopped { get; private set; }
    readonly Action<JsonObject> report;
    readonly CancellationTokenSource cts = new();
    readonly SemaphoreSlim writeLock = new(1, 1);
    long touched = Environment.TickCount64;

    public BambuMqtt(Params p, Action<JsonObject> report)
    {
        P = p;
        this.report = report;
        _ = Task.Run(RunAsync);
    }

    public void Touch() => Interlocked.Exchange(ref touched, Environment.TickCount64);
    bool Wanted => Environment.TickCount64 - Interlocked.Read(ref touched) < 90_000;

    async Task RunAsync()
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested && Wanted)
            {
                int rc = await SessionAsync(ct);
                if (ct.IsCancellationRequested || !Wanted) break;
                Report(false, rc, null);
                await Task.Delay(P.Cloud ? 60_000 : 15_000, ct);   // nu bombardăm cloud-ul dacă token-ul e greșit
            }
        }
        catch (OperationCanceledException) { }
        Stopped = true;
    }

    void Report(bool ok, int rc, JsonObject? p)
    {
        var o = new JsonObject { ["t"] = "pr", ["ok"] = ok, ["rc"] = rc };
        if (p != null) o["p"] = p;
        report(o);
    }

    /// o conexiune; întoarce codul de eroare ca PubSubClient (-2 rețea, -4 timeout, 4/5 user/token greșite)
    async Task<int> SessionAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(10_000);
            await tcp.ConnectAsync(P.Host, 8883, t.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return -4; }
        catch (SocketException) { return -2; }

        // ca firmware-ul (setInsecure): imprimanta are certificat self-signed, iar în cloud datele sunt doar de citit
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = P.Host }, ct); }
        catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { return -2; }

        try
        {
            string clientId = "deskbuddy-pc-" + Random.Shared.Next(0x10000000, int.MaxValue).ToString("x");
            await WriteAsync(ssl, Connect(clientId, P.User, P.Pass), ct);
            // o singură citire continuă (o citire anulată la jumătate poate strica SslStream-ul); ping-ul merge în paralel
            var first = ReadPacketAsync(ssl, ct);
            if (await Task.WhenAny(first, Task.Delay(10_000, ct)) != first) return -4;
            var connack = await first;
            if (connack.Type != 2 || connack.Body.Length < 2) return -2;
            if (connack.Body[1] != 0) return connack.Body[1];             // 4 = user/parolă, 5 = neautorizat

            string report = $"device/{P.Serial}/report", request = $"device/{P.Serial}/request";
            await WriteAsync(ssl, Subscribe(1, report), ct);
            await WriteAsync(ssl, Publish(request, Encoding.UTF8.GetBytes(Pushall)), ct);
            Report(true, 0, null);

            long lastRx = Environment.TickCount64;
            var reader = Task.Run(async () =>
            {
                while (true)
                {
                    var p = await ReadPacketAsync(ssl, ct);
                    Interlocked.Exchange(ref lastRx, Environment.TickCount64);
                    if (p.Type == 3) OnPublish(p.Flags, p.Body);
                }
            }, ct);
            long lastPing = Environment.TickCount64, lastPush = lastPing;
            while (!ct.IsCancellationRequested && Wanted)
            {
                if (await Task.WhenAny(reader, Task.Delay(5000, ct)) == reader) return -2;   // conexiunea s-a închis
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastRx) > 70_000) return -4;                    // nici răspuns la ping
                if (now - lastPing > 25_000) { await WriteAsync(ssl, [0xC0, 0x00], ct); lastPing = now; }
                if (now - lastPush > 10 * 60_000) { await WriteAsync(ssl, Publish(request, Encoding.UTF8.GetBytes(Pushall)), ct); lastPush = now; }
            }
            try { await WriteAsync(ssl, [0xE0, 0x00], CancellationToken.None); } catch (IOException) { }   // DISCONNECT
            return 0;
        }
        catch (IOException) { return -2; }
        catch (ObjectDisposedException) { return -2; }
    }

    void OnPublish(byte flags, byte[] body)
    {
        if (body.Length < 2) return;
        int tlen = body[0] << 8 | body[1];
        int off = 2 + tlen + ((flags >> 1 & 3) > 0 ? 2 : 0);
        if (off > body.Length) return;
        try
        {
            var j = JsonNode.Parse(body.AsSpan(off));
            if (j?["print"] is not JsonObject print) return;
            var o = new JsonObject();
            foreach (var k in Keys)
                if (print[k] is JsonNode v) o[k] = v.DeepClone();
            if (o.Count > 0) Report(true, 0, o);
        }
        catch (JsonException) { }
    }

    // ------------------------------------------------------------ pachete MQTT
    static byte[] Connect(string clientId, string user, string pass)
    {
        var v = new List<byte> { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0xC2, 0, 30 };   // user + parolă + clean session, keepalive 30 s
        Str(v, clientId); Str(v, user); Str(v, pass);
        return Frame(0x10, v);
    }

    static byte[] Subscribe(ushort id, string topic)
    {
        var v = new List<byte> { (byte)(id >> 8), (byte)id };
        Str(v, topic);
        v.Add(0);                                     // QoS 0
        return Frame(0x82, v);
    }

    static byte[] Publish(string topic, byte[] payload)
    {
        var v = new List<byte>();
        Str(v, topic);
        v.AddRange(payload);
        return Frame(0x30, v);
    }

    static void Str(List<byte> v, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        v.Add((byte)(b.Length >> 8)); v.Add((byte)b.Length);
        v.AddRange(b);
    }

    static byte[] Frame(byte type, List<byte> body)
    {
        var o = new List<byte>(body.Count + 5) { type };
        int n = body.Count;
        do { byte d = (byte)(n % 128); n /= 128; if (n > 0) d |= 0x80; o.Add(d); } while (n > 0);
        o.AddRange(body);
        return o.ToArray();
    }

    async Task WriteAsync(Stream s, byte[] data, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try { await s.WriteAsync(data, ct); await s.FlushAsync(ct); }
        finally { writeLock.Release(); }
    }

    /// un pachet întreg; aruncă IOException când se închide conexiunea
    static async Task<(byte Type, byte Flags, byte[] Body)> ReadPacketAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        await s.ReadExactlyAsync(one, ct);
        int first = one[0];
        int len = 0, mul = 1;
        while (true)
        {
            await s.ReadExactlyAsync(one, ct);
            len += (one[0] & 0x7F) * mul;
            if ((one[0] & 0x80) == 0) break;
            mul *= 128;
            if (mul > 128 * 128 * 128) throw new IOException("lungime MQTT invalidă");
        }
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return ((byte)(first >> 4), (byte)(first & 0x0F), body);
    }

    public void Dispose()
    {
        cts.Cancel();
        Stopped = true;
    }
}
