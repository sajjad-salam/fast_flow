using System.ComponentModel;
using FastFlow.Core;
using FastFlow.Models;
using FastFlow.VirtualDesktops;
using Microsoft.Win32;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.UI;

internal sealed class MainForm : Form
{
    /// <summary>Broadcast by a second FastFlow instance to bring this one out of the tray.</summary>
    public static readonly int ShowManagerMessage = RegisterWindowMessage("FastFlow.ShowManager");

    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValue = "FastFlow";

    private readonly WorkflowStore _store = new();
    private readonly VirtualDesktopService _desktops = new();
    private readonly BindingList<Workflow> _workflows;
    private readonly AppSettings _settings;
    private readonly string? _autoRunName;
    private readonly NotifyIcon _tray = new() { Text = "FastFlow", Icon = SystemIcons.Application };
    private readonly Dictionary<int, Workflow> _hotkeys = new();

    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersWidth = 24,
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
    };
    private readonly CheckBox _closeOthers = new()
    {
        Text = "Close all open apps (on every desktop) before running", AutoSize = true,
    };
    private readonly CheckBox _reuse = new()
    {
        Text = "Reuse windows that are already open", AutoSize = true,
    };
    private readonly CheckBox _startup = new()
    {
        Text = "Start with Windows (in the tray, so shortcuts always work)", AutoSize = true,
    };
    private readonly FlowLayoutPanel _buttons = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
    };

    private bool _running;
    private bool _exiting;
    private bool _startHidden;
    private bool _trayHintShown;

    public MainForm(string? autoRunName = null, bool startInTray = false)
    {
        _autoRunName = autoRunName;
        _startHidden = startInTray && autoRunName == null;
        _settings = _store.LoadSettings();

        Text = "FastFlow – Workspace Workflows";
        Width = 1200;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;

        _workflows = new BindingList<Workflow>(LoadWorkflows());
        _list.DataSource = _workflows;
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += async (_, _) => await RunSelectedAsync();

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit checkbox clicks immediately so they get saved.
            if (_grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) SaveAll(); };
        _grid.DataError += (_, e) => { e.ThrowException = false; };

        AddButton("Capture new workflow", async () => await CaptureAsync(replace: null));
        AddButton("Run workflow", RunSelectedAsync);
        AddButton("Re-capture (update)", async () =>
        {
            if (Selected is { } wf)
                await CaptureAsync(replace: wf);
        });
        AddButton("Set shortcut...", () => { SetShortcut(); return Task.CompletedTask; });
        AddButton("Rename", () => { Rename(); return Task.CompletedTask; });
        AddButton("Delete", () => { Delete(); return Task.CompletedTask; });
        AddButton("Copy run command", () => { CopyCommand(); return Task.CompletedTask; });

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.Controls.Add(_list, 0, 0);
        left.Controls.Add(_buttons, 1, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(_grid);
        split.Panel2.Controls.Add(new Label
        {
            Text = "Windows in this workflow (edit cells to tweak; uncheck Enabled to skip a window):",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4),
        });

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _closeOthers.Checked = _settings.CloseOtherApps;
        _reuse.Checked = _settings.ReuseOpenWindows;
        RefreshStartupEntry();
        _startup.Checked = IsStartupEnabled();
        _closeOthers.CheckedChanged += (_, _) => { _settings.CloseOtherApps = _closeOthers.Checked; SaveSettings(); };
        _reuse.CheckedChanged += (_, _) => { _settings.ReuseOpenWindows = _reuse.Checked; SaveSettings(); };
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);

        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        options.Controls.Add(_closeOthers);
        options.Controls.Add(_reuse);
        options.Controls.Add(_startup);
        options.Controls.Add(new Label
        {
            Text = _desktops.Status, AutoSize = true, Padding = new Padding(16, 4, 0, 0),
            ForeColor = _desktops.CanMoveWindows ? SystemColors.GrayText : Color.DarkOrange,
        });
        bottom.Controls.Add(options, 0, 0);
        bottom.Controls.Add(_log, 0, 1);

        var root = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        root.Panel1.Controls.Add(split);
        root.Panel2.Controls.Add(bottom);
        Controls.Add(root);

        Load += (_, _) =>
        {
            // Splitter distances can only be set once the containers have their real size.
            root.SplitterDistance = (int)(ClientSize.Height * 0.68);
            split.SplitterDistance = 440;
        };
        Shown += async (_, _) => await AutoRunAsync();
        FormClosing += OnFormClosing;

        SetUpTray();
        ShowSelected();
        Log($"Workflows are stored in {_store.FilePath}");
    }

    private Workflow? Selected => _list.SelectedItem as Workflow;

    protected override void SetVisibleCore(bool value)
    {
        // Started with --tray: create the window (needed for the global shortcuts) but keep it hidden.
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated)
                CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotkeys();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _hotkeys.TryGetValue(m.WParam.ToInt32(), out var workflow))
        {
            _ = RunFromShortcutAsync(workflow);
            return;
        }
        if (m.Msg == ShowManagerMessage && ShowManagerMessage != 0)
        {
            ShowFromTray();
            return;
        }
        base.WndProc(ref m);
    }

    private void AddButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, Width = 170, Height = 32 };
        button.Click += async (_, _) =>
        {
            if (_running)
                return;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                MessageBox.Show(this, ex.Message, "FastFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _buttons.Controls.Add(button);
    }

    private List<Workflow> LoadWorkflows()
    {
        try
        {
            return _store.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read {_store.FilePath}:\n{ex.Message}", "FastFlow",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return new List<Workflow>();
        }
    }

    private void SaveAll()
    {
        try
        {
            _store.Save(_workflows);
        }
        catch (Exception ex)
        {
            Log($"Could not save workflows: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            _store.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            Log($"Could not save settings: {ex.Message}");
        }
    }

    private void ShowSelected()
    {
        _grid.DataSource = Selected == null ? null : new BindingList<WindowEntry>(Selected.Windows);
    }

    private async Task CaptureAsync(Workflow? replace)
    {
        var name = replace?.Name
                   ?? PromptDialog.Show(this, "Capture workflow", "Name for this workflow:", $"Workflow {_workflows.Count + 1}");
        if (name == null)
            return;

        // Hide ourselves so FastFlow's own window is not part of the snapshot and nothing is obscured.
        WindowState = FormWindowState.Minimized;
        await Task.Delay(400);

        Workflow captured;
        try
        {
            captured = new WorkflowCapturer(_desktops).Capture(name);
        }
        finally
        {
            WindowState = FormWindowState.Normal;
            Activate();
        }

        if (replace != null)
        {
            captured.Id = replace.Id;
            captured.CreatedAt = replace.CreatedAt;
            captured.Shortcut = replace.Shortcut;
            _workflows[_workflows.IndexOf(replace)] = captured;
        }
        else
        {
            _workflows.Add(captured);
        }
        _list.SelectedItem = captured;
        ShowSelected();
        SaveAll();
        RegisterHotkeys();

        var perDesktop = captured.Windows
            .GroupBy(w => w.DesktopIndex)
            .OrderBy(g => g.Key)
            .Select(g => $"{(g.Key < captured.DesktopNames.Count ? captured.DesktopNames[g.Key] : $"Desktop {g.Key + 1}")}: {g.Count()}");
        Log($"Captured \"{name}\": {captured.Windows.Count} windows across {captured.DesktopCount} desktop(s) ({string.Join(", ", perDesktop)}).");
    }

    private async Task RunSelectedAsync()
    {
        if (Selected is { } wf)
            await RunAsync(wf);
    }

    private async Task RunAsync(Workflow workflow)
    {
        if (_running)
            return;
        _running = true;
        _buttons.Enabled = false;
        try
        {
            await new WorkflowRunner(_desktops, Log).RunAsync(workflow, _reuse.Checked, _closeOthers.Checked);
        }
        catch (Exception ex)
        {
            Log($"Run failed: {ex.Message}");
        }
        finally
        {
            _running = false;
            _buttons.Enabled = true;
        }
    }

    private async Task AutoRunAsync()
    {
        if (_autoRunName == null)
            return;

        var workflow = _workflows.FirstOrDefault(w =>
            string.Equals(w.Name, _autoRunName, StringComparison.OrdinalIgnoreCase));
        if (workflow == null)
        {
            Log($"No workflow named \"{_autoRunName}\".");
            return;
        }

        WindowState = FormWindowState.Minimized;
        await RunAsync(workflow);
        Close();
    }

    private void Rename()
    {
        if (Selected is not { } wf)
            return;
        var name = PromptDialog.Show(this, "Rename workflow", "New name:", wf.Name);
        if (name == null)
            return;
        wf.Name = name;
        _workflows.ResetItem(_workflows.IndexOf(wf));
        SaveAll();
    }

    private void Delete()
    {
        if (Selected is not { } wf)
            return;
        if (MessageBox.Show(this, $"Delete workflow \"{wf.Name}\"?", "FastFlow",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _workflows.Remove(wf);
        SaveAll();
        ShowSelected();
        RegisterHotkeys();
    }

    private void SetShortcut()
    {
        if (Selected is not { } wf)
            return;

        // Suspend the global shortcuts while recording, otherwise pressing one would run its workflow.
        UnregisterHotkeys();
        Hotkey? result;
        try
        {
            result = HotkeyDialog.Show(this, wf.Name, wf.GetHotkey());
        }
        finally
        {
            RegisterHotkeys();
        }
        if (result is not { } hotkey)
            return;

        if (!hotkey.IsEmpty)
        {
            var other = _workflows.FirstOrDefault(w => w != wf && w.GetHotkey() == hotkey);
            if (other != null
                && MessageBox.Show(this, $"{hotkey} is already the shortcut of \"{other.Name}\". Move it to \"{wf.Name}\"?",
                    "FastFlow", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            if (other != null)
            {
                other.Shortcut = "";
                _workflows.ResetItem(_workflows.IndexOf(other));
            }
        }

        wf.Shortcut = hotkey.IsEmpty ? "" : hotkey.ToString();
        _workflows.ResetItem(_workflows.IndexOf(wf));
        SaveAll();
        RegisterHotkeys();
        Log(hotkey.IsEmpty
            ? $"Removed the shortcut of \"{wf.Name}\"."
            : $"\"{wf.Name}\" now runs with {hotkey} from anywhere (while FastFlow is open or in the tray).");
    }

    private void RegisterHotkeys()
    {
        // OnHandleCreated can fire before the constructor has loaded the workflows.
        if (!IsHandleCreated || _workflows is null)
            return;
        UnregisterHotkeys();

        var id = 1;
        foreach (var workflow in _workflows)
        {
            if (workflow.GetHotkey() is not { } hotkey)
                continue;

            uint modifiers = MOD_NOREPEAT;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) modifiers |= MOD_CONTROL;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= MOD_ALT;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= MOD_SHIFT;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) modifiers |= MOD_WIN;

            if (RegisterHotKey(Handle, id, modifiers, (uint)hotkey.Key))
                _hotkeys[id++] = workflow;
            else
                Log($"Shortcut {hotkey} for \"{workflow.Name}\" could not be registered – another app or Windows already uses it. Pick a different one.");
        }
    }

    private void UnregisterHotkeys()
    {
        foreach (var id in _hotkeys.Keys)
            UnregisterHotKey(Handle, id);
        _hotkeys.Clear();
    }

    private async Task RunFromShortcutAsync(Workflow workflow)
    {
        if (_running)
            return;
        _tray.ShowBalloonTip(2000, "FastFlow", $"Switching to \"{workflow.Name}\"...", ToolTipIcon.Info);
        await RunAsync(workflow);
    }

    private void SetUpTray()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var open = new ToolStripMenuItem("Open FastFlow", null, (_, _) => ShowFromTray());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            if (_workflows.Count > 0)
                menu.Items.Add(new ToolStripSeparator());
            foreach (var workflow in _workflows)
            {
                var item = new ToolStripMenuItem(workflow.Name, null, async (_, _) => await RunAsync(workflow))
                {
                    ShortcutKeyDisplayString = workflow.GetHotkey()?.ToString(),
                    Enabled = !_running,
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => _startup.Checked = !_startup.Checked)
            {
                Checked = IsStartupEnabled(),
            });
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        };
        menu.Items.Add("Exit"); // placeholder so the menu opens; rebuilt in Opening
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = _autoRunName == null;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Closing the window keeps FastFlow in the tray so the workflow shortcuts keep working.
        if (!_exiting && _autoRunName == null && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "FastFlow is still running",
                    "Workflow shortcuts keep working. Right-click the tray icon to run a workflow or exit.", ToolTipIcon.Info);
            }
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        return key?.GetValue(StartupValue) is string;
    }

    private static string StartupCommand => $"\"{Application.ExecutablePath}\" --tray";

    /// <summary>Keeps an enabled startup entry pointing at this exe, in case FastFlow was moved or updated.</summary>
    private void RefreshStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
            if (key?.GetValue(StartupValue) is string command && command != StartupCommand)
                key.SetValue(StartupValue, StartupCommand);
        }
        catch
        {
            // non-essential
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
            if (enabled)
                key.SetValue(StartupValue, StartupCommand);
            else
                key.DeleteValue(StartupValue, throwOnMissingValue: false);
            Log(enabled
                ? "FastFlow will start with Windows (hidden in the tray)."
                : "FastFlow will no longer start with Windows.");
        }
        catch (Exception ex)
        {
            Log($"Could not change the Windows startup setting: {ex.Message}");
        }
    }

    private void CopyCommand()
    {
        if (Selected is not { } wf)
            return;
        var command = $"\"{Application.ExecutablePath}\" --run \"{wf.Name}\"";
        Clipboard.SetText(command);
        Log($"Copied to clipboard (use it as a desktop shortcut target or in a script): {command}");
    }

    private void Log(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
