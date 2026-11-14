using System.Globalization;
using System.Text;

namespace DeskBuddy;

/// Editorul unui ecran custom: numele și până la 6 elemente (lista de elemente vine de la placă, "citems").
sealed class CustomScreenDialog : Form
{
    const string None = "— nimic —";
    readonly TextBox name = Theme.TextBox();
    readonly List<ComboBox> slots = [];

    CustomScreenDialog(string title, IReadOnlyList<string> catalog, int maxItems, IReadOnlyList<string> fanLabels)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        Font = Theme.Body;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        name.Width = 260;
        name.MaxLength = 12;
        // ventilatoarele: cu eticheta lor de acum de pe PC, ca să știi care e care
        var shown = catalog.Select((c, i) =>
        {
            const string pre = "Ventilator ";
            if (c.StartsWith(pre, StringComparison.Ordinal) && int.TryParse(c[pre.Length..], out int k) && k >= 1 && k <= fanLabels.Count)
                return $"{c} ({fanLabels[k - 1]})";
            return c;
        }).ToList();

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Card };
        stack.Controls.Add(Theme.FieldLabel("Numele ecranului (apare când îl schimbi, max 12 litere)"));
        stack.Controls.Add(name);
        stack.Controls.Add(Theme.FieldLabel($"Ce apare pe el (până la {maxItems}; ecranul se așază după câte alegi)"));
        for (int i = 0; i < maxItems; i++)
        {
            var cb = Theme.Combo(300);
            cb.Items.Add(None);
            foreach (var s in shown) cb.Items.Add(s);
            cb.SelectedIndex = 0;
            cb.MaxDropDownItems = 16;
            slots.Add(cb);
            stack.Controls.Add(cb);
        }
        var note = Theme.Label("1 element = mare, pe tot ecranul · 2–3 = rânduri · 4–6 = două coloane", Theme.Small, Theme.Muted);
        stack.Controls.Add(note);

        var ok = Theme.Button("OK", primary: true);
        var cancel = Theme.Button("Anulează");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        stack.Controls.Add(Theme.Row(ok, cancel));
        Controls.Add(stack);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    /// null = anulat
    public static (string Name, List<int> Items)? Edit(IWin32Window? owner, string title, string current, IReadOnlyList<int> items,
        IReadOnlyList<string> catalog, int maxItems, IReadOnlyList<string> fanLabels)
    {
        using var d = new CustomScreenDialog(title, catalog, maxItems, fanLabels);
        d.name.Text = current;
        for (int i = 0; i < items.Count && i < d.slots.Count; i++)
            if (items[i] >= 0 && items[i] < catalog.Count) d.slots[i].SelectedIndex = items[i] + 1;
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        var picked = d.slots.Where(s => s.SelectedIndex > 0).Select(s => s.SelectedIndex - 1).ToList();
        string n = CleanName(d.name.Text);
        return (n.Length > 0 ? n : "CUSTOM", picked);
    }

    /// pentru placă: fără diacritice (fonturile OLED nu le au), fără | ; , (separatorii din setare), majuscule, max 12
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
