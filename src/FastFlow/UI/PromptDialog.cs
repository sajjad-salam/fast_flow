namespace FastFlow.UI;

/// <summary>Minimal single-line text input dialog.</summary>
internal sealed class PromptDialog : Form
{
    private readonly TextBox _input;

    private PromptDialog(string title, string label, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);

        _input = new TextBox { Text = initial, Width = 320 };
        layout.Controls.Add(_input, 0, 1);
        layout.SetColumnSpan(_input, 2);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(ok, 0, 2);
        layout.Controls.Add(cancel, 1, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static string? Show(IWin32Window owner, string title, string label, string initial = "")
    {
        using var dialog = new PromptDialog(title, label, initial);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return null;
        var value = dialog._input.Text.Trim();
        return value.Length == 0 ? null : value;
    }
}
