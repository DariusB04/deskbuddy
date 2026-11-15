using System.Diagnostics;

namespace DeskBuddy;

/// DeskBuddy.exe perf: măsoară cât durează deschiderea ferestrei, fiecare pagină (prima dată și a doua oară)
/// și actualizarea de fiecare secundă, cu desenarea inclusă. Pentru optimizări; nu atinge setările.
static class Perf
{
    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        var app = new TrayApp(false, null);
        MainForm.PerfLog = true;
        var sw = Stopwatch.StartNew();
        var f = new MainForm(app);
        long ctor = sw.ElapsedMilliseconds;
        f.Show();
        Pump();
        Console.WriteLine($"fereastra: constructor {ctor} ms, afișată în {sw.ElapsedMilliseconds} ms");
        Thread.Sleep(1500);                           // lasă USB-ul / senzorii să pornească
        Pump();
        for (int pass = 1; pass <= 2; pass++)
            for (int i = 0; i < f.PageCount; i++)
            {
                sw.Restart();
                f.SelectPage(i);
                Pump();
                long show = sw.ElapsedMilliseconds;
                sw.Restart();
                for (int k = 0; k < 5; k++) { f.TickForPerf(); Pump(); }
                Console.WriteLine($"trecerea {pass}  {f.PageName(i),-12} deschidere {show,5} ms   actualizare {sw.ElapsedMilliseconds / 5.0,6:0.0} ms");
                if (pass == 1 && Environment.GetEnvironmentVariable("DESKBUDDY_SHOTS") is { Length: > 0 } dir)
                {
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(Path.Combine(dir, $"page{i}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        app.Quit();
        return 0;
    }

    static void Pump()
    {
        Application.DoEvents();
        foreach (Form fm in Application.OpenForms) fm.Update();   // desenarea făcută acum, nu mai târziu
        Application.DoEvents();
    }
}
