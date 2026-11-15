using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DeskBuddy;

/// Aplicația Avalonia: tema întunecată și nucleul (TrayApp), creat după ce interfața e gata.
sealed class App : Application
{
    public static TrayApp? Core { get; private set; }
    public static IClassicDesktopStyleApplicationLifetime? Desktop { get; private set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Ui.AddStyles(Styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            Desktop = d;
            var settings = AppSettings.Load();
            bool background = Program.Args.Contains("--background");
            bool show = Program.Args.Contains("--show") || (!background && !settings.StartMinimized);
            Core = new TrayApp(show, null);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
