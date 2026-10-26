using FastFlow.Models;

namespace FastFlow.UI;

/// <summary>Picks the virtual desktop a workflow switches to once all of its apps are open.</summary>
internal sealed class FinishDesktopDialog : Form
{
    private readonly ComboBox _choice;

    private FinishDesktopDialog(string workflowName, int current, int desktopCount)
    {
        Theme.Apply(this);
        Text = "Desktop after run";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var label = new Label
        {
            Text = $"When \"{workflowName}\" has opened all of its apps, go to:",
            AutoSize = true, Font = Theme.BodyMedium, Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 2);

        // Items: automatic, stay, then desktop 1..N – the index maps to the setting via ToSetting.
        _choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, Margin = Padding.Empty };
        _choice.Items.Add("Automatic (its desktop, if all apps are on one desktop)");
        _choice.Items.Add("Stay on the current desktop");
        var count = Math.Max(desktopCount, current);
        for (var i = 1; i <= count; i++)
            _choice.Items.Add($"Desktop {i}");
        _choice.SelectedIndex = current switch
        {
            Workflow.FinishDesktopAutomatic => 0,
            Workflow.FinishDesktopStay => 1,
            var number => number + 1,
        };
        layout.Controls.Add(_choice, 0, 1);
        layout.SetColumnSpan(_choice, 2);

        var ok = new PillButton { Text = "OK", Primary = true, DialogResult = DialogResult.OK };
        var cancel = new PillButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = Theme.ButtonRow(ok, cancel);
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private int ToSetting() => _choice.SelectedIndex switch
    {
        0 => Workflow.FinishDesktopAutomatic,
        1 => Workflow.FinishDesktopStay,
        var index => index - 1,
    };

    /// <summary>Returns the new <see cref="Workflow.FinishDesktop"/> value, or null if cancelled.</summary>
    public static int? Show(IWin32Window owner, string workflowName, int current, int desktopCount)
    {
        using var dialog = new FinishDesktopDialog(workflowName, current, desktopCount);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.ToSetting() : null;
    }
}
