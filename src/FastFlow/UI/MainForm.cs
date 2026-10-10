using System.ComponentModel;
using FastFlow.Core;
using FastFlow.Models;
using FastFlow.VirtualDesktops;

namespace FastFlow.UI;

internal sealed class MainForm : Form
{
    private readonly WorkflowStore _store = new();
    private readonly VirtualDesktopService _desktops = new();
    private readonly BindingList<Workflow> _workflows;
    private readonly string? _autoRunName;

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
    private readonly CheckBox _reuse = new()
    {
        Text = "Reuse windows that are already open", Checked = true, AutoSize = true,
    };
    private readonly FlowLayoutPanel _buttons = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
    };

    private bool _running;

    public MainForm(string? autoRunName = null)
    {
        _autoRunName = autoRunName;

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
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        options.Controls.Add(_reuse);
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

        ShowSelected();
        Log($"Workflows are stored in {_store.FilePath}");
    }

    private Workflow? Selected => _list.SelectedItem as Workflow;

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
            _workflows[_workflows.IndexOf(replace)] = captured;
        }
        else
        {
            _workflows.Add(captured);
        }
        _list.SelectedItem = captured;
        ShowSelected();
        SaveAll();

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
            await new WorkflowRunner(_desktops, Log).RunAsync(workflow, _reuse.Checked);
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
