using FastFlow.Models;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.UI;

/// <summary>Records a keyboard shortcut: the user presses the combination (e.g. Ctrl+Alt+Shift+1) in the box.</summary>
internal sealed class HotkeyDialog : Form
{
    private readonly TextBox _input;
    private readonly Label _hint;
    private readonly Button _ok;
    private Hotkey _hotkey;

    private HotkeyDialog(string workflowName, Hotkey? current)
    {
        Text = "Workflow shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        var label = new Label
        {
            Text = $"Press the shortcut that should run \"{workflowName}\" from anywhere\n"
                   + "(Ctrl / Alt / Shift / Win plus a key, e.g. Ctrl+Alt+1 or Ctrl+Alt+Shift+F5):",
            AutoSize = true, Margin = new Padding(0, 0, 0, 6),
        };
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 3);

        _input = new TextBox { Width = 360, ReadOnly = true, BackColor = SystemColors.Window, ShortcutsEnabled = false };
        _input.PreviewKeyDown += (_, e) => e.IsInputKey = true; // so Tab, arrows etc. can be recorded too
        _input.KeyDown += OnKeyDown;
        _input.KeyUp += (_, _) => { if (!_hotkey.IsValid) ShowCurrent(); };
        layout.Controls.Add(_input, 0, 1);
        layout.SetColumnSpan(_input, 3);

        _hint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 8) };
        layout.Controls.Add(_hint, 0, 2);
        layout.SetColumnSpan(_hint, 3);

        _ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var clear = new Button { Text = "Remove shortcut", AutoSize = true };
        clear.Click += (_, _) => { _hotkey = default; DialogResult = DialogResult.OK; };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(_ok, 0, 3);
        layout.Controls.Add(clear, 1, 3);
        layout.Controls.Add(cancel, 2, 3);

        Controls.Add(layout);
        CancelButton = cancel;

        _hotkey = current ?? default;
        ShowCurrent();
    }

    /// <summary>Returns the recorded shortcut, an empty <see cref="Hotkey"/> to remove it, or null if cancelled.</summary>
    public static Hotkey? Show(IWin32Window owner, string workflowName, Hotkey? current)
    {
        using var dialog = new HotkeyDialog(workflowName, current);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._hotkey : null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true; // no beeps or Alt-menu activation while recording

        var modifiers = HotkeyModifiers.None;
        if (e.Control) modifiers |= HotkeyModifiers.Ctrl;
        if (e.Alt) modifiers |= HotkeyModifiers.Alt;
        if (e.Shift) modifiers |= HotkeyModifiers.Shift;
        if (GetKeyState(VK_LWIN) < 0 || GetKeyState(VK_RWIN) < 0) modifiers |= HotkeyModifiers.Win;

        // Only modifiers held so far: show them while the user is still building the combination.
        var key = Hotkey.IsModifierKey(e.KeyCode) ? Keys.None : e.KeyCode;
        var pressed = new Hotkey(modifiers, key);
        if (key == Keys.None)
        {
            _input.Text = pressed + (modifiers == HotkeyModifiers.None ? "" : "+");
            return;
        }

        _hotkey = pressed;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        _input.Text = _hotkey.IsEmpty ? "" : _hotkey.ToString();
        _ok.Enabled = _hotkey.IsValid;
        _hint.Text = _hotkey.IsEmpty
            ? "No shortcut yet – press one now."
            : _hotkey.IsValid
                ? "Click OK to save this shortcut."
                : "Add Ctrl, Alt, Shift or Win (only F1–F24 work on their own).";
    }
}
