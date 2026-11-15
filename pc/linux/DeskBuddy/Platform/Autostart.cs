namespace DeskBuddy;

/// Pornirea la logare pe Linux: ~/.config/autostart/deskbuddy.desktop (standardul freedesktop: GNOME, KDE, Cinnamon, XFCE...).
static class Autostart
{
    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "deskbuddy.desktop");

    public static bool IsAdmin => false;

    public static bool IsEnabled() => System.IO.File.Exists(File);

    public static bool Set(bool on)
    {
        try
        {
            if (!on) { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); return true; }
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File,
                "[Desktop Entry]\nType=Application\nName=DeskBuddy\nComment=DeskBuddy: PC stats on the desk gadget\n" +
                $"Exec=\"{Updater.SelfPath}\" --background\nIcon=deskbuddy\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
