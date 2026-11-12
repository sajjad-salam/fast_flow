using FastFlow.Models;
using FastFlow.VirtualDesktops;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>Snapshots every open application window on every virtual desktop into a <see cref="Workflow"/>.</summary>
internal sealed class WorkflowCapturer
{
    private readonly VirtualDesktopService _desktops;

    public WorkflowCapturer(VirtualDesktopService desktops) => _desktops = desktops;

    /// <summary>
    /// A desktop flow: just the windows in <paramref name="handles"/>, all set to open on
    /// <paramref name="targetDesktop"/> (1-based).
    /// </summary>
    public Workflow CaptureDesktopFlow(string name, IReadOnlyCollection<IntPtr> handles, int targetDesktop)
    {
        var workflow = Capture(name, handles);
        workflow.Kind = WorkflowKind.Desktop;
        workflow.TargetDesktop = Math.Max(1, targetDesktop);
        foreach (var entry in workflow.Windows)
            entry.DesktopIndex = workflow.TargetDesktop - 1;
        return workflow;
    }

    /// <param name="only">Capture just these windows instead of every open one.</param>
    public Workflow Capture(string name, IReadOnlyCollection<IntPtr>? only = null)
    {
        var desktopIds = _desktops.GetDesktopIds();
        var workflow = new Workflow
        {
            Name = name,
            DesktopCount = Math.Max(1, desktopIds.Count),
            DesktopNames = desktopIds.Select((id, i) => VirtualDesktopService.GetDesktopName(id, i)).ToList(),
        };

        var explorerFolders = ExplorerFolders.GetOpenFolders();
        var hostedScripts = TerminalCommands.FindHostedScripts();

        // EnumWindows returns windows top-most first. Reverse so that launching in list order
        // leaves the originally top-most window on top again.
        var windows = WindowEnumerator.GetAppWindows();
        if (only != null)
            windows = windows.Where(w => only.Contains(w.Handle)).ToList();
        windows.Reverse();

        foreach (var window in windows)
        {
            var desktopId = _desktops.GetWindowDesktopId(window.Handle);
            var desktopIndex = desktopId is { } id ? Math.Max(0, desktopIds.IndexOf(id)) : 0;

            var placement = WINDOWPLACEMENT.Create();
            if (!GetWindowPlacement(window.Handle, ref placement))
                continue;
            var state = placement.showCmd switch
            {
                SW_SHOWMAXIMIZED => WindowState.Maximized,
                SW_SHOWMINIMIZED or SW_MINIMIZE or SW_SHOWMINNOACTIVE => WindowState.Minimized,
                _ => WindowState.Normal,
            };
            var rect = GetBounds(window.Handle, state, placement);

            var tabs = BrowserTabs.IsBrowser(window.ProcessName) && string.IsNullOrEmpty(window.AppUserModelId)
                ? BrowserTabs.Capture(window.Handle)
                : new List<string>();
            var (launchExecutablePath, arguments, scripts) = GetLaunchCommand(window, explorerFolders, hostedScripts);
            if (tabs.Count > 0)
                arguments = BrowserTabs.KeepSwitches(arguments);

            workflow.Windows.Add(new WindowEntry
            {
                ProcessName = window.ProcessName,
                Title = window.Title,
                ExecutablePath = window.ExecutablePath,
                LaunchExecutablePath = launchExecutablePath,
                TerminalScripts = scripts,
                AppUserModelId = window.AppUserModelId,
                WindowClass = window.ClassName,
                Arguments = arguments,
                Tabs = tabs,
                DesktopIndex = desktopIndex,
                Left = rect.Left,
                Top = rect.Top,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top,
                ScreenCoordinates = true,
                State = state,
            });
        }

        // Stable sort: group by desktop while keeping the z-order inside each desktop.
        workflow.Windows = workflow.Windows.OrderBy(w => w.DesktopIndex).ToList();
        return workflow;
    }

    /// <summary>
    /// Screen bounds to save. A normal window is saved where it actually is: for snapped windows
    /// (Win+Arrow, Snap Layouts) the placement's normal position is the pre-snap size, not the layout
    /// on screen. Maximized and minimized windows keep the bounds they restore to.
    /// </summary>
    private static RECT GetBounds(IntPtr hwnd, WindowState state, WINDOWPLACEMENT placement)
    {
        if (state == WindowState.Normal && GetWindowRect(hwnd, out var actual)
            && actual.Right > actual.Left && actual.Bottom > actual.Top)
            return actual;
        return WindowGeometry.ToScreen(placement.rcNormalPosition);
    }

    private static (string LaunchExecutablePath, string Arguments, List<string> TerminalScripts) GetLaunchCommand(
        WindowInfo window, Dictionary<IntPtr, string> explorerFolders, Dictionary<IntPtr, List<HostedScript>> hostedScripts)
    {
        if (window.ClassName == "CabinetWClass")
            return ("", explorerFolders.TryGetValue(window.Handle, out var folder) ? $"\"{folder}\"" : "", new());

        // A terminal running a script: start the same script(s) again rather than an empty shell.
        if (hostedScripts.TryGetValue(window.Handle, out var scripts) && scripts.Count > 0)
        {
            var paths = scripts.Select(s => s.ScriptPath).ToList();
            if (TerminalCommands.IsWindowsTerminal(window.ProcessName))
                return (TerminalCommands.WindowsTerminalLauncher, TerminalCommands.BuildWindowsTerminalArguments(scripts), paths);
            return (scripts[0].ShellPath, scripts[0].ShellArguments, paths);
        }

        // Packaged apps are launched through their AppUserModelID, which takes no arguments.
        if (!string.IsNullOrEmpty(window.AppUserModelId))
            return ("", "", new());

        var commandLine = WindowEnumerator.GetCommandLine(window.ProcessId);
        return ("", commandLine == null ? "" : WindowEnumerator.StripExecutable(commandLine), new());
    }
}
