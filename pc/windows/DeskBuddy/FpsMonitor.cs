using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace DeskBuddy;

/// FPS-ul jocului pe Windows, fără program instalat în plus: aceleași evenimente ETW pe care le folosește PresentMon.
/// Fiecare cadru afișat de un joc DirectX 10/11/12 (DXGI) sau DirectX 9 produce un eveniment „Present”;
/// numărăm câte are procesul jocului în ultima secundă. Cere drepturi de admin (aplicația pornește ca admin prin task).
/// Jocurile Vulkan / OpenGL care nu trec prin DXGI nu apar (FPS „--”).
sealed class FpsMonitor : IDisposable
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
    static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
    const int DxgiPresentStart = 42, D3d9PresentStart = 1;
    const string SessionName = "DeskBuddy-FPS";

    readonly object gate = new();
    readonly Dictionary<int, Queue<double>> presents = [];         // pid -> momentele cadrelor (ms), ultimele 2 s
    TraceEventSession? session;
    public string? Error { get; private set; }

    public FpsMonitor()
    {
        if (TraceEventSession.IsElevated() != true) { Error = "FPS-ul cere drepturi de administrator"; return; }
        new Thread(Run) { IsBackground = true, Name = "fps-etw" }.Start();
    }

    void Run()
    {
        try
        {
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational);
            session.EnableProvider(D3d9, TraceEventLevel.Informational);
            session.Source.AllEvents += OnEvent;
            session.Source.Process();                               // blochează până la Dispose
        }
        catch (Exception e) { Error = "nu pot citi FPS-ul: " + e.Message; }
    }

    void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        if (!(e.ProviderGuid == Dxgi && id == DxgiPresentStart) && !(e.ProviderGuid == D3d9 && id == D3d9PresentStart)) return;
        double t = e.TimeStampRelativeMSec;
        lock (gate)
        {
            if (!presents.TryGetValue(e.ProcessID, out var q)) { if (presents.Count > 64) presents.Clear(); presents[e.ProcessID] = q = new Queue<double>(); }
            q.Enqueue(t);
            lastTick[e.ProcessID] = Environment.TickCount64;
            while (q.Count > 0 && t - q.Peek() > 2000) q.Dequeue();
        }
    }

    /// FPS-ul și durata medie a unui cadru pentru proces, din ultima secundă (null = n-a afișat nimic în ultima secundă)
    public (float Fps, float FrameMs)? For(int pid)
    {
        lock (gate)
        {
            if (!presents.TryGetValue(pid, out var q) || q.Count < 2) return null;
            if (!lastTick.TryGetValue(pid, out long tick) || Environment.TickCount64 - tick > 1500) return null;   // pauză / minimizat
            double last = q.Last();
            var recent = q.Where(x => last - x <= 1000).ToArray();
            if (recent.Length < 2) return null;
            double span = recent[^1] - recent[0];
            if (span <= 0) return null;
            float frame = (float)(span / (recent.Length - 1));
            return (1000f / frame, frame);
        }
    }

    readonly Dictionary<int, long> lastTick = [];

    public void Dispose()
    {
        try { session?.Dispose(); } catch (Exception) { }
    }
}
