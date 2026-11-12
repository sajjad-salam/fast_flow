using FastFlow.Core;
using FastFlow.Models;

namespace FastFlow.UI;

/// <summary>
/// Sets up a desktop flow: a name, the open windows to record (usually one or two), and the virtual desktop they
/// open on when the flow runs.
/// </summary>
internal sealed class DesktopFlowDialog : Form
{
    public sealed record Result(string Name, List<IntPtr> Windows, int Desktop);

    private sealed record WindowItem(WindowInfo Window)
    {
        public override string ToString() =>
            (string.IsNullOrWhiteSpace(Window.Title) ? Window.ProcessName : $"{Window.ProcessName}  —  {Window.Title}")
            + (FastFlow.Native.NativeMethods.IsIconic(Window.Handle) ? "  (minimized)" : "");
    }

    private readonly TextBox _name;
    private readonly CheckedListBox _windows;
    private readonly NumericUpDown _desktop;
    private readonly Label _error;

    private DesktopFlowDialog(string name, int desktop, int desktopCount, Workflow? existing)
    {
        Theme.Apply(this);
        Text = existing == null ? "New desktop flow" : "Re-capture desktop flow";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void AddRow(Control control)
        {
            layout.Controls.Add(control, 0, layout.RowCount++);
            layout.SetColumnSpan(control, 2);
        }
        Label Heading(string text, int top = 0) =>
            new() { Text = text, AutoSize = true, Font = Theme.BodyMedium, Margin = new Padding(0, top, 0, 8) };

        AddRow(new Label
        {
            Text = "Opens these apps on one virtual desktop and takes you there.\n"
                   + "Nothing else is closed or minimized.",
            AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 14),
        });

        AddRow(Heading("Name:"));
        _name = new TextBox { Text = name };
        AddRow(new FieldBox(_name, 460));

        AddRow(Heading("Windows to open (pick one or two):", 12));
        _windows = new CheckedListBox
        {
            CheckOnClick = true, BorderStyle = BorderStyle.None, BackColor = Theme.Card, IntegralHeight = false,
            Dock = DockStyle.Fill,
        };
        var openWindows = WindowEnumerator.GetAppWindows()
            .Where(w => !string.Equals(w.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preselect = new HashSet<IntPtr>();
        if (existing != null)
        {
            // Tick the windows of the apps the flow already has.
            foreach (var entry in existing.Windows)
            {
                var match = openWindows.FirstOrDefault(w => !preselect.Contains(w.Handle) && SameApp(entry, w));
                if (match != null)
                    preselect.Add(match.Handle);
            }
        }
        foreach (var window in openWindows)
            _windows.Items.Add(new WindowItem(window), preselect.Contains(window.Handle));
        var listCard = new Card { Radius = 12, Padding = new Padding(10, 8, 10, 8), Size = new Size(460, 220), Margin = Padding.Empty };
        listCard.Controls.Add(_windows);
        AddRow(listCard);
        if (openWindows.Count == 0)
            AddRow(new Label { Text = "No app windows are open. Open the apps first.", AutoSize = true, ForeColor = Theme.Warning });

        AddRow(Heading("Open them on desktop:", 14));
        _desktop = new NumericUpDown
        {
            Minimum = 1, Maximum = 20, Value = Math.Clamp(desktop, 1, 20), Width = 80, Margin = new Padding(0, 0, 12, 0),
            Anchor = AnchorStyles.Left,
        };
        var desktopHint = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left,
            Text = desktopCount == 1 ? "You have 1 desktop now; a missing one is created."
                : $"You have {desktopCount} desktops now; a missing one is created.",
        };
        var desktopRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        desktopRow.Controls.Add(_desktop);
        desktopRow.Controls.Add(desktopHint);
        AddRow(desktopRow);

        _error = new Label { AutoSize = true, ForeColor = Theme.Warning, Margin = new Padding(0, 10, 0, 0), Visible = false };
        AddRow(_error);

        var ok = new PillButton { Text = existing == null ? "Create flow" : "Save", Primary = true };
        ok.Click += (_, _) => Accept();
        var cancel = new PillButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        AddRow(Theme.ButtonRow(ok, cancel));

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static bool SameApp(WindowEntry entry, WindowInfo window) =>
        entry.IsPackaged
            ? string.Equals(entry.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry.ExecutablePath, window.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private Result? _result;

    private void Accept()
    {
        var name = _name.Text.Trim();
        var windows = _windows.CheckedItems.Cast<WindowItem>().Select(i => i.Window.Handle).ToList();
        string? problem = name.Length == 0 ? "Give the flow a name."
            : windows.Count == 0 ? "Tick at least one window."
            : null;
        if (problem != null)
        {
            _error.Text = problem;
            _error.Visible = true;
            return;
        }
        _result = new Result(name, windows, (int)_desktop.Value);
        DialogResult = DialogResult.OK;
    }

    /// <param name="existing">The flow being re-captured: its apps are ticked if they are open.</param>
    public static Result? Show(IWin32Window owner, string name, int desktop, int desktopCount, Workflow? existing = null)
    {
        using var dialog = new DesktopFlowDialog(name, desktop, desktopCount, existing);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._result : null;
    }
}

/// <summary>Picks the desktop a desktop flow opens its apps on.</summary>
internal sealed class TargetDesktopDialog : Form
{
    private readonly NumericUpDown _desktop;

    private TargetDesktopDialog(string workflowName, int current, int desktopCount)
    {
        Theme.Apply(this);
        Text = "Flow desktop";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 20, 24, 20);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = $"\"{workflowName}\" opens its apps on, and takes you to, desktop:",
            AutoSize = true, Font = Theme.BodyMedium, Margin = new Padding(0, 0, 0, 10),
        });
        _desktop = new NumericUpDown { Minimum = 1, Maximum = 20, Value = Math.Clamp(current, 1, 20), Width = 80, Margin = Padding.Empty };
        layout.Controls.Add(_desktop);
        layout.Controls.Add(new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 8, 0, 0),
            Text = $"You have {desktopCount} desktop(s) now; a missing one is created when the flow runs.",
        });
        var ok = new PillButton { Text = "OK", Primary = true, DialogResult = DialogResult.OK };
        var cancel = new PillButton { Text = "Cancel", DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Theme.ButtonRow(ok, cancel));

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Returns the new 1-based desktop number, or null if cancelled.</summary>
    public static int? Show(IWin32Window owner, string workflowName, int current, int desktopCount)
    {
        using var dialog = new TargetDesktopDialog(workflowName, current, desktopCount);
        return dialog.ShowDialog(owner) == DialogResult.OK ? (int)dialog._desktop.Value : null;
    }
}
