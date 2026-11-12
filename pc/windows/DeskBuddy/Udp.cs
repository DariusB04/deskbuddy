using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskBuddy;

/// Trimite pachete JSON la DeskBuddy: broadcast pe fiecare rețea a PC-ului (ethernet + WiFi), ca scripturile Python.
/// 255.255.255.255 pleacă doar pe ruta implicită, deci n-ar ajunge la DeskBuddy dacă el e pe cealaltă rețea.
/// Variabila DESKBUDDY_HOST=192.168.1.77 trimite doar la IP-ul ăla.
sealed class UdpSender : IDisposable
{
    public const int Port = 4210;
    public const int MaxPacket = 500;           // ESP32 citește maxim 511 octeți
    static readonly bool Debug = Environment.GetEnvironmentVariable("DESKBUDDY_DEBUG") == "1";   // scrie în stderr ce trimite

    readonly UdpClient client = new() { EnableBroadcast = true };
    List<IPAddress> targets = [];
    long targetsAt = -60_000;                   // „demult”: prima trimitere calculează adresele (long.MinValue ar da overflow la scădere)

    public IReadOnlyList<IPAddress> Targets => targets;

    /// IP-ul plăcii setat în aplicație (gol = broadcast). Variabila DESKBUDDY_HOST are prioritate.
    public static IPAddress? HostOverride { get; set; }

    public void ResetTargets() => targetsAt = -60_000;

    public void Send(byte[] data)
    {
        if (data.Length > MaxPacket) return;
        long now = Environment.TickCount64;
        if (now - targetsAt > 30_000)           // rețelele se pot schimba (WiFi pornit / oprit)
        {
            targets = BroadcastTargets();
            targetsAt = now;
        }
        foreach (var t in targets)
        {
            try { client.Send(data, data.Length, new IPEndPoint(t, Port)); }
            catch (SocketException e)           // DeskBuddy / rețeaua indisponibile: nu e grav
            {
                if (Debug) Console.Error.WriteLine($"[udp] {t}: {e.Message}");
            }
            if (Debug) Console.Error.WriteLine($"[udp] -> {t}:{Port} {System.Text.Encoding.UTF8.GetString(data)}");
        }
    }

    public static List<IPAddress> BroadcastTargets()
    {
        var env = Environment.GetEnvironmentVariable("DESKBUDDY_HOST");
        if (!string.IsNullOrWhiteSpace(env) && IPAddress.TryParse(env.Trim(), out var host)) return [host];
        if (HostOverride is { } fixedHost) return [fixedHost];

        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    int prefix = ua.PrefixLength;
                    if (prefix <= 0 || prefix >= 31) continue;          // tailscale / punct-la-punct
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;           // fără DHCP (APIPA)
                    uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                    uint bc = ip | ~(uint.MaxValue << (32 - prefix));
                    var addr = new IPAddress(new[] { (byte)(bc >> 24), (byte)(bc >> 16), (byte)(bc >> 8), (byte)bc });
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
        }
        catch (NetworkInformationException) { }
        if (list.Count == 0) list.Add(IPAddress.Broadcast);
        return list;
    }

    public static void SendOnce(byte[] data)
    {
        using var s = new UdpSender();
        s.Send(data);
    }

    public void Dispose() => client.Dispose();
}
