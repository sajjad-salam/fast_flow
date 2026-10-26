using FastFlow.Core;

namespace FastFlow.UI;

/// <summary>Edits the list of apps that "Close all open apps" leaves running, one executable name per line.</summary>
internal sealed class KeepOpenDialog : Form
{
    private readonly TextBox _input;
    private readonly ComboBox _running;

    private KeepOpenDialog(IEnumerable<string> current)
    {
        Theme.Apply(this);
        Text = "Apps to keep open";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var label = new Label
        {
            Text = "These apps are not closed when a workflow runs.\n"
                   + "One app per line, by executable name (e.g. MEmu or MEmu.exe):",
            AutoSize = true, Margin = new Padding(0, 0, 0, 12),
        };
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 2);

        _input = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
            Text = string.Join(Environment.NewLine, current),
        };
        var field = new FieldBox(_input, 400, 180);
        layout.Controls.Add(field, 0, 1);
        layout.SetColumnSpan(field, 2);

        // Offer the apps that are open right now, so the exact executable name doesn't have to be known.
        _running = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, Anchor = AnchorStyles.Left, Margin = new Padding(0, 12, 8, 0),
        };
        _running.Items.AddRange(WindowEnumerator.GetAppWindows()
            .Select(w => w.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray());
        if (_running.Items.Count > 0)
            _running.SelectedIndex = 0;
        var add = new PillButton { Text = "Add open app", Margin = new Padding(0, 12, 0, 0) };
        add.Click += (_, _) => AddApp(_running.SelectedItem as string);
        layout.Controls.Add(_running, 0, 2);
        layout.Controls.Add(add, 1, 2);

        var ok = new PillButton { Text = "OK", Primary = true, DialogResult = DialogResult.OK };
        var cancel = new PillButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = Theme.ButtonRow(ok, cancel);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        CancelButton = cancel;
    }

    private List<string> Apps => _input.Lines
        .Select(AppCloser.NormalizeAppName)
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void AddApp(string? name)
    {
        if (string.IsNullOrEmpty(name) || Apps.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        var text = _input.Text.TrimEnd();
        _input.Text = text.Length == 0 ? name : text + Environment.NewLine + name;
    }

    /// <summary>Returns the edited list of executable names (without ".exe"), or null if cancelled.</summary>
    public static List<string>? Show(IWin32Window owner, IEnumerable<string> current)
    {
        using var dialog = new KeepOpenDialog(current);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Apps : null;
    }
}
