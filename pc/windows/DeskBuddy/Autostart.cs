using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DeskBuddy;

/// Pornire cu Windows printr-un task programat "la logare", cu drepturi de admin fără prompt UAC
/// (temperatura CPU cere admin). Crearea task-ului cere o singură dată acordul UAC.
static class Autostart
{
    const string TaskName = "DeskBuddy";

    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// driverul prin care LibreHardwareMonitor citește temperatura CPU (https://pawnio.eu)
    public static bool PawnIoInstalled()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return k != null;
    }

    public static bool IsEnabled() => Run("schtasks.exe", "/Query", "/TN", TaskName) == 0;

    /// pornește task-ul acum: aplicația rulează ca admin fără prompt UAC (folosit la dublu-click pe exe)
    public static bool RunTask() => Run("schtasks.exe", "/Run", "/TN", TaskName) == 0;

    /// Din tray: dacă nu suntem admin, pornim o copie cu "runas" care face doar asta (prompt UAC).
    public static bool Set(bool on)
    {
        if (IsAdmin) return Apply(on);
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = on ? "--autostart on" : "--autostart off" };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(60_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }          // ai apăsat "Nu" la UAC
    }

    /// Rulează deja ca admin (apelat cu --autostart on|off).
    public static bool Apply(bool on)
    {
        if (!on) return Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F") == 0;
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Environment.ProcessPath!);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>DeskBuddy: trimite temperaturile PC-ului și starea Claude Code la DeskBuddy</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--background</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "deskbuddy_task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try { return Run("schtasks.exe", "/Create", "/TN", TaskName, "/XML", file, "/F") == 0; }
        finally { File.Delete(file); }
    }

    static int Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(15_000) ? p.ExitCode : -1;
        }
        catch (Win32Exception) { return -1; }
    }
}
