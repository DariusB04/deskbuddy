namespace DeskBuddy;

/// Cere parola paginii plăcii (dacă e setată) pentru update-ul de firmware. Nu se salvează nicăieri.
sealed class PasswordDialog : Form
{
    readonly TextBox box = Theme.TextBox();

    PasswordDialog(string prompt)
    {
        Text = "DeskBuddy";
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

        var label = Theme.Label(prompt);
        label.MaximumSize = new Size(380, 0);
        box.UseSystemPasswordChar = true;
        box.Width = 380;
        var ok = Theme.Button("OK", primary: true);
        var cancel = Theme.Button("Anulează");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Card };
        stack.Controls.AddRange([label, box, Theme.Row(ok, cancel)]);
        Controls.Add(stack);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    public static string? Ask(IWin32Window? owner, string prompt)
    {
        using var d = new PasswordDialog(prompt);
        return d.ShowDialog(owner) == DialogResult.OK ? d.box.Text : null;
    }
}
