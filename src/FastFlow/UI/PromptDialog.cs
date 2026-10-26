namespace FastFlow.UI;

/// <summary>Minimal single-line text input dialog.</summary>
internal sealed class PromptDialog : Form
{
    private readonly TextBox _input;

    private PromptDialog(string title, string label, string initial)
    {
        Theme.Apply(this);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Font = Theme.BodyMedium, Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);

        _input = new TextBox { Text = initial };
        var field = new FieldBox(_input, 360);
        layout.Controls.Add(field, 0, 1);
        layout.SetColumnSpan(field, 2);

        var ok = new PillButton { Text = "OK", Primary = true, DialogResult = DialogResult.OK };
        var cancel = new PillButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = Theme.ButtonRow(ok, cancel);
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 2);

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
