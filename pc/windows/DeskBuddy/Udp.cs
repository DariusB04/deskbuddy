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

/// Hook-urile Claude Code rulează ca procese scurte, separate. Dacă aplicația din tray e pornită, îi dau ei pachetul
/// (UDP pe 127.0.0.1:4211), iar ea îl trimite pe unde e placa: USB sau WiFi. Altfel îl trimit direct pe WiFi, ca înainte.
static class Relay
{
    public const int Port = 4211;
    const string ReadyName = @"Local\DeskBuddyRelay";       // există cât timp aplicația ascultă pe portul de mai sus

    public static void Send(byte[] data)
    {
        if (AppListening())
        {
            try
            {
                using var c = new UdpClient();
                c.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, Port));
                return;
            }
            catch (SocketException) { }
        }
        UdpSender.SendOnce(data);
    }

    static bool AppListening()
    {
        try
        {
            if (!System.Threading.EventWaitHandleAcl.TryOpenExisting(ReadyName, System.Security.AccessControl.EventWaitHandleRights.Synchronize, out var h)) return false;
            h.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// aplicația: ascultă pe 127.0.0.1:4211 și dă mai departe fiecare pachet; null dacă portul e ocupat
    public static IDisposable? Listen(Action<byte[]> forward)
    {
        UdpClient client;
        try { client = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port)); }
        catch (SocketException) { return null; }
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
        var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
        sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(everyone,
            System.Security.AccessControl.EventWaitHandleRights.Synchronize, System.Security.AccessControl.AccessControlType.Allow));
        var ready = System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, ReadyName, out _, sec);
        var t = new Thread(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] d;
                try { d = client.Receive(ref from); }
                catch (SocketException) { continue; }        // ex. ICMP „port inaccesibil” de la un pachet trimis
                catch (ObjectDisposedException) { return; }
                if (d.Length is > 0 and <= UdpSender.MaxPacket) forward(d);
            }
        }) { IsBackground = true, Name = "relay" };
        t.Start();
        return new Listener(client, ready);
    }

    sealed class Listener(UdpClient client, EventWaitHandle ready) : IDisposable
    {
        public void Dispose() { ready.Dispose(); client.Dispose(); }
    }
}
