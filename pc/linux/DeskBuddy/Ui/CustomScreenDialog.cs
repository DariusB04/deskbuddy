using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeskBuddy;

/// Editorul unui ecran custom: numele și până la 6 elemente (lista vine de la placă, "citems"). Ca CustomScreenDialog de pe Windows.
static class CustomScreenDialog
{
    /// null = anulat
    public static async Task<(string Name, List<int> Items)?> Edit(Control from, string title, string current, IReadOnlyList<int> items,
        IReadOnlyList<string> catalog, int maxItems, IReadOnlyList<string> fanLabels)
    {
        string none = L.T("— nimic —", "— nothing —");
        var shown = catalog.Select(c =>
        {
            foreach (string pre in new[] { "Ventilator ", "Fan " })
                if (c.StartsWith(pre, StringComparison.Ordinal) && int.TryParse(c[pre.Length..], out int k) && k >= 1 && k <= fanLabels.Count)
                    return $"{c} ({fanLabels[k - 1]})";
            return c;
        }).ToList();

        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.CardBg, ShowInTaskbar = false,
        };
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 4 };
        var name = Ui.TextBox(260);
        name.MaxLength = 12;
        name.Text = current;
        stack.Children.Add(Ui.FieldLabel(L.T("Numele ecranului (apare când îl schimbi, max 12 litere)", "Screen name (shown when you switch to it, max 12 letters)")));
        stack.Children.Add(name);
        stack.Children.Add(Ui.FieldLabel(L.T($"Ce apare pe el (până la {maxItems}; ecranul se așază după câte alegi)", $"What it shows (up to {maxItems}; the layout adapts to how many you pick)")));
        var slots = new List<ComboBox>();
        for (int i = 0; i < maxItems; i++)
        {
            var cb = Ui.Combo(320);
            cb.Items.Add(none);
            foreach (var s in shown) cb.Items.Add(s);
            cb.SelectedIndex = i < items.Count && items[i] >= 0 && items[i] < catalog.Count ? items[i] + 1 : 0;
            slots.Add(cb);
            stack.Children.Add(cb);
        }
        stack.Children.Add(Ui.Note(L.T("1 element = mare, pe tot ecranul · 2–3 = rânduri · 4–6 = două coloane", "1 item = large, full screen · 2–3 = rows · 4–6 = two columns")));
        bool ok = false;
        var okB = Ui.Button("OK", primary: true);
        var cancel = Ui.Button(L.T("Anulează", "Cancel"));
        okB.Click += (_, _) => { ok = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { okB, cancel } });
        w.Content = stack;
        if (TopLevel.GetTopLevel(from) is Window owner) await w.ShowDialog(owner);
        if (!ok) return null;
        var picked = slots.Where(s => s.SelectedIndex > 0).Select(s => s.SelectedIndex - 1).ToList();
        string n = CleanName(name.Text ?? "");
        return (n.Length > 0 ? n : "CUSTOM", picked);
    }

    /// pentru placă: fără diacritice, fără | ; , (separatorii din setare), majuscule, max 12
    public static string CleanName(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = ch switch { 'ș' or 'ş' => 's', 'Ș' or 'Ş' => 'S', 'ț' or 'ţ' => 't', 'Ț' or 'Ţ' => 'T', _ => ch };
            if (c >= 0x20 && c < 0x7F && c is not ('|' or ';' or ',')) sb.Append(c);
        }
        var r = sb.ToString().Trim().ToUpperInvariant();
        return r.Length > 12 ? r[..12].TrimEnd() : r;
    }
}
