using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace DeskBuddy;

/// Ferestrele mici: mesaj, confirmare, o întrebare cu răspuns text (parolă) și alegerea unui fișier.
static class Dialogs
{
    static Window? Owner(Control c) => TopLevel.GetTopLevel(c) as Window;

    static Window Make(string text, out StackPanel buttons, Control? extra = null)
    {
        var w = new Window
        {
            Title = "DeskBuddy", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.CardBg, ShowInTaskbar = false,
        };
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10, MaxWidth = 460 };
        stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Fg });
        if (extra != null) stack.Children.Add(extra);
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        stack.Children.Add(buttons);
        w.Content = stack;
        return w;
    }

    public static async Task Info(Control from, string text)
    {
        var w = Make(text, out var buttons);
        var ok = Ui.Button("OK", primary: true);
        ok.Click += (_, _) => w.Close();
        buttons.Children.Add(ok);
        if (Owner(from) is { } o) await w.ShowDialog(o); else w.Show();
    }

    public static async Task<bool> Confirm(Control from, string text, string? yes = null, string? no = null)
    {
        var w = Make(text, out var buttons);
        bool result = false;
        var y = Ui.Button(yes ?? "OK", primary: true);
        var n = Ui.Button(no ?? L.T("Anulează", "Cancel"));
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        buttons.Children.AddRange([y, n]);
        if (Owner(from) is { } o) await w.ShowDialog(o);
        return result;
    }

    /// null = anulat
    public static async Task<string?> Ask(Control from, string text, bool password = false)
    {
        var box = Ui.TextBox(380);
        if (password) box.PasswordChar = '•';
        var w = Make(text, out var buttons, box);
        string? result = null;
        var ok = Ui.Button("OK", primary: true);
        var cancel = Ui.Button(L.T("Anulează", "Cancel"));
        ok.Click += (_, _) => { result = box.Text ?? ""; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        buttons.Children.AddRange([ok, cancel]);
        w.Opened += (_, _) => box.Focus();
        if (Owner(from) is { } o) await w.ShowDialog(o);
        return result;
    }

    public static async Task<string?> PickFile(Control from, string title, string pattern)
    {
        if (TopLevel.GetTopLevel(from) is not { } top) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(pattern) { Patterns = [pattern] }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
