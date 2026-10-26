using System.ComponentModel;
using System.Text.RegularExpressions;
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
    private readonly NotifyIcon _tray = new() { Text = "FastFlow", Icon = Theme.AppIcon };
    private readonly Dictionary<int, Workflow> _hotkeys = new();

    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawFixed, BackColor = Theme.Card,
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Color.FromArgb(90, 90, 90), Font = Theme.Mono,
    };
    private readonly ToggleSwitch _closeOthers = new() { Text = "Close all open apps before running" };
    private readonly ToggleSwitch _reuse = new() { Text = "Reuse windows that are already open" };
    private readonly ToggleSwitch _startup = new() { Text = "Start with Windows" };
    private readonly ToolTip _tips = new();

    private readonly TwoToneLabel _listTitle = new() { Font = Theme.Heading, ForeColor = Theme.Ink, Strong = "Workflows" };
    private readonly Label _emptyList = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 8, 0, 0),
        Text = "No workflows yet. Arrange your apps across your desktops, then capture them.",
        MaximumSize = new Size(300, 0),
    };
    private readonly Label _title = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Ink, Margin = Padding.Empty };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 0, 0) };
    private readonly FlowLayoutPanel _actions = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 16, 0, 4),
    };
    private PillButton _runButton = null!;

    /// <summary>Command buttons, disabled while a workflow runs; the value says whether one needs a selected workflow.</summary>
    private readonly Dictionary<PillButton, bool> _commands = new();

    private bool _running;
    private bool _exiting;
    private bool _startHidden;
    private bool _trayHintShown;
    private Task _toast = Task.CompletedTask;

    public MainForm(string? autoRunName = null, bool startInTray = false)
    {
        _autoRunName = autoRunName;
        _startHidden = startInTray && autoRunName == null;
        _settings = _store.LoadSettings();

        Theme.Apply(this);
        Text = "FastFlow";
        Width = 1280;
        Height = 880;
        MinimumSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;

        _workflows = new BindingList<Workflow>(LoadWorkflows());
        _list.DataSource = _workflows;
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += async (_, _) => await RunSelectedAsync();
        _list.DrawItem += DrawWorkflow;
        _list.Resize += (_, _) => _list.Invalidate();
        _workflows.ListChanged += (_, _) => ShowListState();

        StyleGrid();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit checkbox clicks immediately so they get saved.
            if (_grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) SaveAll(); };
        _grid.DataError += (_, e) => { e.ThrowException = false; };
        _grid.DataBindingComplete += (_, _) =>
        {
            // "ExecutablePath" -> "Executable path"
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                var text = Regex.Replace(column.HeaderText, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
                if (text.Length > 0)
                    column.HeaderText = char.ToUpperInvariant(text[0]) + text[1..];
            }
        };

        var capture = AddButton("Capture new workflow", async () => await CaptureAsync(replace: null),
            primary: true, needsSelection: false);
        capture.Anchor = AnchorStyles.Right;
        capture.Margin = Padding.Empty;
        _runButton = AddButton("Run workflow", RunSelectedAsync, primary: true);
        _runButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _runButton.Margin = new Padding(16, 0, 0, 0);
        _actions.Controls.Add(AddButton("Re-capture (update)", async () =>
        {
            if (Selected is { } wf)
                await CaptureAsync(replace: wf);
        }));
        _actions.Controls.Add(AddButton("Set shortcut...", () => { SetShortcut(); return Task.CompletedTask; }));
        _actions.Controls.Add(AddButton("Desktop after run...", () => { SetFinishDesktop(); return Task.CompletedTask; }));
        _actions.Controls.Add(AddButton("Rename", () => { Rename(); return Task.CompletedTask; }));
        _actions.Controls.Add(AddButton("Copy run command", () => { CopyCommand(); return Task.CompletedTask; }));
        _actions.Controls.Add(AddButton("Delete", () => { Delete(); return Task.CompletedTask; }));

        // Header: brand on the left, the main action on the right, a two-tone line underneath.
        var brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        brand.Controls.Add(new LogoMark { Margin = new Padding(0, 2, 10, 0) });
        brand.Controls.Add(new Label { Text = "FastFlow", AutoSize = true, Font = Theme.Brand, Margin = Padding.Empty });
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 20),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(brand, 0, 0);
        header.Controls.Add(capture, 1, 0);
        var hero = new TwoToneLabel
        {
            Font = Theme.Hero, ForeColor = Theme.Ink, Margin = new Padding(0, 16, 0, 0),
            Strong = "Save your workspace,", Soft = " restore it in one click",
        };
        header.Controls.Add(hero, 0, 1);
        header.SetColumnSpan(hero, 2);

        // Left column: the workflows, then the options.
        var listHeader = new Panel { Dock = DockStyle.Top, Height = 34 };
        listHeader.Controls.Add(_listTitle);
        var listCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 18, 14, 14), Margin = new Padding(0, 0, 16, 16) };
        _listTitle.Location = new Point(6, 0);
        _emptyList.Padding = new Padding(6, 0, 0, 0);
        listCard.Controls.Add(_list);
        listCard.Controls.Add(_emptyList);
        listCard.Controls.Add(listHeader);

        _closeOthers.Checked = _settings.CloseOtherApps;
        _reuse.Checked = _settings.ReuseOpenWindows;
        RefreshStartupEntry();
        _startup.Checked = IsStartupEnabled();
        _closeOthers.CheckedChanged += (_, _) => { _settings.CloseOtherApps = _closeOthers.Checked; SaveSettings(); };
        _reuse.CheckedChanged += (_, _) => { _settings.ReuseOpenWindows = _reuse.Checked; SaveSettings(); };
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        _tips.SetToolTip(_closeOthers, "Closes the apps on every virtual desktop first, so the workflow starts from a clean slate.");
        _tips.SetToolTip(_reuse, "Keeps a window of an app the workflow also uses and moves it into place instead of relaunching it.");
        _tips.SetToolTip(_startup, "Starts FastFlow hidden in the tray at sign-in, so workflow shortcuts always work.");

        var keepOpen = new PillButton { Radius = 12, Margin = new Padding(47, 0, 0, 6), Font = Theme.SmallMedium };
        void ShowKeepOpenCount() => keepOpen.Text = $"Apps to keep open ({_settings.KeepOpenApps.Count})...";
        ShowKeepOpenCount();
        keepOpen.Enabled = _closeOthers.Checked;
        _closeOthers.CheckedChanged += (_, _) => keepOpen.Enabled = _closeOthers.Checked;
        keepOpen.Click += (_, _) =>
        {
            if (KeepOpenDialog.Show(this, _settings.KeepOpenApps) is not { } apps)
                return;
            _settings.KeepOpenApps = apps;
            SaveSettings();
            ShowKeepOpenCount();
            Log(apps.Count == 0 ? "No apps are kept open." : $"Apps kept open when a workflow runs: {string.Join(", ", apps)}");
        };

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.Add(new Label { Text = "Options", AutoSize = true, Font = Theme.Heading, Margin = new Padding(0, 0, 0, 8) });
        options.Controls.Add(_closeOthers);
        options.Controls.Add(keepOpen);
        options.Controls.Add(_reuse);
        options.Controls.Add(_startup);
        options.Controls.Add(new Label
        {
            Text = _desktops.Status, AutoSize = true, Font = Theme.Small, MaximumSize = new Size(290, 0),
            Margin = new Padding(0, 8, 0, 0),
            ForeColor = _desktops.CanMoveWindows ? Theme.Muted : Theme.Warning,
        });
        var optionsCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 0) };
        optionsCard.Controls.Add(options);

        // Right column: the selected workflow, then the activity log.
        var titleBlock = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty,
        };
        titleBlock.Controls.Add(_title);
        titleBlock.Controls.Add(_summary);
        var gridCard = new Card { Dock = DockStyle.Fill, Fill = Color.White, Radius = 18, Padding = new Padding(8, 6, 8, 8) };
        gridCard.Controls.Add(_grid);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail.Controls.Add(titleBlock, 0, 0);
        detail.Controls.Add(_runButton, 1, 0);
        detail.Controls.Add(_actions, 0, 1);
        detail.SetColumnSpan(_actions, 2);
        var gridHint = new Label
        {
            AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 8),
            Text = "Windows in this workflow  ·  edit cells to tweak, uncheck Enabled to skip a window",
        };
        detail.Controls.Add(gridHint, 0, 2);
        detail.SetColumnSpan(gridHint, 2);
        detail.Controls.Add(gridCard, 0, 3);
        detail.SetColumnSpan(gridCard, 2);
        var detailCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(24, 22, 20, 20), Margin = new Padding(0, 0, 0, 16) };
        detailCard.Controls.Add(detail);

        var logCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 8, 12) };
        logCard.Controls.Add(_log);
        logCard.Controls.Add(new Label
        {
            Text = "Activity", AutoSize = true, Font = Theme.Heading, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 10),
        });

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 264));
        body.Controls.Add(listCard, 0, 0);
        body.Controls.Add(optionsCard, 0, 1);
        body.Controls.Add(detailCard, 1, 0);
        body.Controls.Add(logCard, 1, 1);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(28, 18, 28, 28),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        Controls.Add(root);

        Load += (_, _) =>
        {
            // Keep the window on screen on small or highly scaled displays.
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            CenterToScreen();
            ApplyDpiMetrics();
        };
        DpiChanged += (_, _) => BeginInvoke(ApplyDpiMetrics);
        Shown += async (_, _) => await AutoRunAsync();
        FormClosing += OnFormClosing;

        SetUpTray();
        ShowListState();
        ShowSelected();
        Log($"Workflows are stored in {_store.FilePath}");
    }

    private void StyleGrid()
    {
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackgroundColor = Color.White;
        _grid.GridColor = Theme.Line;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White, ForeColor = Theme.Muted, Font = Theme.SmallMedium,
            SelectionBackColor = Color.White, SelectionForeColor = Theme.Muted,
            Padding = new Padding(6, 0, 6, 0), WrapMode = DataGridViewTriState.False,
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White, ForeColor = Theme.Ink, Font = Theme.Body,
            SelectionBackColor = Theme.Card, SelectionForeColor = Theme.Ink,
            Padding = new Padding(6, 0, 6, 0),
        };
    }

    /// <summary>Row and item heights in device pixels; the layout itself is scaled by WinForms.</summary>
    private void ApplyDpiMetrics()
    {
        _list.ItemHeight = Math.Min(255, LogicalToDeviceUnits(64));
        _grid.ColumnHeadersHeight = LogicalToDeviceUnits(38);
        _grid.RowTemplate.Height = LogicalToDeviceUnits(36);
        ShowSelected();
    }

    private void ShowListState()
    {
        _listTitle.Soft = _workflows.Count == 0 ? "" : $"  {_workflows.Count}";
        _emptyList.Visible = _workflows.Count == 0;
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        // Binding the list selects its first item while the constructor is still building the window.
        if (_runButton == null)
            return;
        foreach (var (button, needsSelection) in _commands)
            button.Enabled = !_running && (!needsSelection || Selected != null);
        _runButton.Text = _running ? "Running..." : "Run workflow";
    }

    private void DrawWorkflow(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(_list.BackColor))
            g.FillRectangle(back, e.Bounds);
        if (e.Index < 0 || e.Index >= _list.Items.Count || _list.Items[e.Index] is not Workflow wf)
            return;

        int Px(int value) => _list.LogicalToDeviceUnits(value);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var item = Rectangle.Inflate(e.Bounds, 0, -Px(3));
        if ((e.State & DrawItemState.Selected) != 0)
            Theme.FillRounded(g, Color.White, item, Px(16));

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                                      | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var left = item.Left + Px(16);
        var right = item.Right - Px(14);

        // Shortcut as a key cap on the right.
        if (wf.GetHotkey() is { } hotkey)
        {
            var text = hotkey.ToString();
            var size = TextRenderer.MeasureText(g, text, Theme.SmallMedium, Size.Empty, TextFormatFlags.NoPadding);
            var cap = new Rectangle(right - size.Width - Px(16), item.Top + (item.Height - Px(24)) / 2,
                size.Width + Px(16), Px(24));
            Theme.FillRounded(g, Theme.Chip, cap, Px(7));
            TextRenderer.DrawText(g, text, Theme.SmallMedium, cap, Theme.Ink,
                flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            right = cap.Left - Px(10);
        }

        var nameHeight = TextRenderer.MeasureText(g, "Ag", Theme.BodyMedium, Size.Empty, flags).Height;
        var infoHeight = TextRenderer.MeasureText(g, "Ag", Theme.Small, Size.Empty, flags).Height;
        var top = item.Top + (item.Height - nameHeight - Px(3) - infoHeight) / 2;
        TextRenderer.DrawText(g, wf.Name, Theme.BodyMedium,
            new Rectangle(left, top, right - left, nameHeight), Theme.Ink, flags);
        TextRenderer.DrawText(g, Describe(wf, withShortcut: false), Theme.Small,
            new Rectangle(left, top + nameHeight + Px(3), right - left, infoHeight), Theme.Muted, flags);
    }

    private static string Describe(Workflow wf, bool withShortcut)
    {
        var parts = new List<string>
        {
            wf.Windows.Count == 1 ? "1 window" : $"{wf.Windows.Count} windows",
        };
        var desktops = wf.Windows.Select(w => w.DesktopIndex).Distinct().Count();
        if (desktops > 0)
            parts.Add(desktops == 1 ? "1 desktop" : $"{desktops} desktops");
        if (withShortcut)
        {
            if (wf.GetHotkey() is { } hotkey)
                parts.Add(hotkey.ToString());
            parts.Add(wf.FinishDesktop switch
            {
                Workflow.FinishDesktopAutomatic => "ends on its desktop",
                Workflow.FinishDesktopStay => "stays on the current desktop",
                var number => $"ends on desktop {number}",
            });
            parts.Add($"captured {wf.CreatedAt:d MMM yyyy}");
        }
        return string.Join("  ·  ", parts);
    }

    private void ShowDetails()
    {
        if (Selected is { } wf)
        {
            _title.Text = wf.Name;
            _summary.Text = Describe(wf, withShortcut: true);
        }
        else
        {
            _title.Text = "No workflow selected";
            _summary.Text = "Arrange your apps across your desktops, then click Capture new workflow.";
        }
        UpdateCommands();
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

    private PillButton AddButton(string text, Func<Task> action, bool primary = false, bool needsSelection = true)
    {
        var button = new PillButton { Text = text, Primary = primary };
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
        _commands[button] = needsSelection;
        return button;
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
        ShowDetails();
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
            captured.FinishDesktop = replace.FinishDesktop;
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
        UpdateCommands();
        try
        {
            var (restored, total) = await new WorkflowRunner(_desktops, Log)
                .RunAsync(workflow, _reuse.Checked, _closeOthers.Checked, _settings.KeepOpenApps);
            if (total > 0)
                _toast = ToastWindow.ShowAsync($"“{workflow.Name}” is ready", restored == total
                    ? total == 1 ? "FastFlow finished opening the window" : $"FastFlow finished opening all {total} windows"
                    : $"Opened {restored} of {total} windows");
        }
        catch (Exception ex)
        {
            Log($"Run failed: {ex.Message}");
        }
        finally
        {
            _running = false;
            UpdateCommands();
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
        await _toast;
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
        ShowDetails();
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
        ShowDetails();
        Log(hotkey.IsEmpty
            ? $"Removed the shortcut of \"{wf.Name}\"."
            : $"\"{wf.Name}\" now runs with {hotkey} from anywhere (while FastFlow is open or in the tray).");
    }

    private void SetFinishDesktop()
    {
        if (Selected is not { } wf)
            return;

        var desktopCount = Math.Max(_desktops.GetDesktopIds().Count,
            Math.Max(wf.DesktopCount, wf.Windows.Select(w => w.DesktopIndex + 1).DefaultIfEmpty(0).Max()));
        if (FinishDesktopDialog.Show(this, wf.Name, wf.FinishDesktop, desktopCount) is not { } choice)
            return;

        wf.FinishDesktop = choice;
        SaveAll();
        ShowDetails();
        Log(choice switch
        {
            Workflow.FinishDesktopAutomatic => $"\"{wf.Name}\" switches to its desktop after running if all of its apps are on one desktop.",
            Workflow.FinishDesktopStay => $"\"{wf.Name}\" stays on the current desktop after running.",
            _ => $"\"{wf.Name}\" switches to desktop {choice} after running.",
        });
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
        Theme.Apply(menu);
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
