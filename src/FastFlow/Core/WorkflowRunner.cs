using System.Diagnostics;
using FastFlow.Models;
using FastFlow.VirtualDesktops;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>
/// Restores a <see cref="Workflow"/>: launches the apps, waits for their windows, then puts each window
/// on its virtual desktop at its saved size and position.
///
/// Runs on the UI thread (awaiting delays keeps the UI responsive) because the shell's virtual
/// desktop COM objects must be called from the STA thread that created them. The apps are restored in
/// parallel, but only ever interleave at awaits on that one thread, so they share state without locks.
/// </summary>
/// <param name="Settled">Completes once launched apps have stopped moving their windows and been put back.</param>
internal sealed record RunResult(int Restored, int Total, Task Settled);

internal sealed class WorkflowRunner
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ClassMismatchGrace = TimeSpan.FromSeconds(5);
    // Listing the windows is cheap now that process details are cached, so new windows can be spotted sooner.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan[] CorrectionDelays = { TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500) };

    private readonly VirtualDesktopService _desktops;
    private readonly Action<string> _log;

    public WorkflowRunner(VirtualDesktopService desktops, Action<string> log)
    {
        _desktops = desktops;
        _log = log;
    }

    /// <param name="reuseOpenWindows">
    /// Use a matching window that is already open instead of launching a new instance; such windows are
    /// also spared by <paramref name="closeOtherApps"/> and just moved and resized into place.
    /// </param>
    /// <param name="closeOtherApps">First close every open app window on every virtual desktop.</param>
    /// <param name="minimizeInsteadOfClose">
    /// With <paramref name="closeOtherApps"/>: minimize those windows instead of closing them, and bring back the
    /// windows this workflow used last time (browser windows with tabs too) instead of launching new ones.
    /// Implies <paramref name="reuseOpenWindows"/>.
    /// </param>
    /// <param name="keepOpenApps">Executable names of apps that <paramref name="closeOtherApps"/> leaves running.</param>
    /// <param name="settleToken">Cancels <see cref="RunResult.Settled"/>, e.g. because another workflow starts.</param>
    /// <returns>
    /// How many of the workflow's enabled windows were restored, as soon as every window is in place. Apps that were
    /// launched may still move themselves afterwards; <see cref="RunResult.Settled"/> puts them back and completes
    /// a few seconds later.
    /// </returns>
    public async Task<RunResult> RunAsync(Workflow workflow, bool reuseOpenWindows, bool closeOtherApps,
        bool minimizeInsteadOfClose, IEnumerable<string> keepOpenApps, CancellationToken token = default,
        CancellationToken settleToken = default)
    {
        var minimize = closeOtherApps && minimizeInsteadOfClose;
        reuseOpenWindows |= minimize;
        var entries = workflow.Windows.Where(w => w.Enabled).ToList();
        _log($"Running \"{workflow.Name}\" ({entries.Count} windows)...");

        // Pick the open windows to reuse before closing anything, so that switching between workflows
        // that share an app (e.g. Obsidian) moves its window instead of closing and relaunching it.
        var claimed = new HashSet<IntPtr>();
        var reused = new Dictionary<WindowEntry, IntPtr>();
        var memory = WindowMemory.Load();
        // One look at the open windows serves all of the matching below, and the minimizing or closing.
        var openWindows = WindowEnumerator.GetAppWindows();
        if (minimize)
        {
            // Other workflows' windows are minimized, not closed, so the windows this workflow was placed in
            // last time may still be open: take those first, browser windows with tabs included, since they
            // still show the workflow's tabs.
            var open = openWindows.ToDictionary(w => w.Handle);
            foreach (var entry in entries)
            {
                if (memory.Find(workflow, entry) is { } hwnd && !claimed.Contains(hwnd)
                    && open.TryGetValue(hwnd, out var window) && Matches(entry, window))
                {
                    claimed.Add(hwnd);
                    reused[entry] = hwnd;
                }
            }
        }

        if (reuseOpenWindows)
        {
            // Terminal windows are only reused when they still run the same scripts.
            var hostedScripts = entries.Any(e => !reused.ContainsKey(e)
                                                 && (e.TerminalScripts.Count > 0 || TerminalCommands.IsWindowsTerminal(e.ProcessName)))
                ? TerminalCommands.FindHostedScripts()
                : new Dictionary<IntPtr, List<HostedScript>>();

            // A browser window with saved tabs is otherwise opened fresh, so that its tabs are restored.
            // Terminals running a script pick first, so that a plain terminal entry cannot take their window.
            foreach (var entry in entries.Where(e => e.Tabs.Count == 0 && !reused.ContainsKey(e))
                         .OrderBy(e => e.TerminalScripts.Count == 0))
            {
                if (FindOpenWindow(entry, openWindows, claimed, hostedScripts) is { } hwnd)
                {
                    claimed.Add(hwnd);
                    reused[entry] = hwnd;
                }
            }
        }

        if (minimize)
            AppMinimizer.MinimizeAll(openWindows, keepOpenApps, claimed, _log);
        else if (closeOtherApps)
            await AppCloser.CloseAllAsync(openWindows, keepOpenApps, claimed, _log, token);

        if (entries.Count == 0)
            return new RunResult(0, 0, Task.CompletedTask);

        var needed = Math.Max(entries.Max(e => e.DesktopIndex) + 1, workflow.FinishDesktop);
        try
        {
            var created = _desktops.EnsureDesktopCount(needed);
            if (created > 0)
                _log($"Created {created} virtual desktop(s).");
        }
        catch (Exception ex)
        {
            _log($"Could not create virtual desktops: {ex.Message}");
        }

        // Minimizing doesn't open or close windows, so the first look is still current; closing changed them.
        var preExisting = (minimize || !closeOtherApps ? openWindows : WindowEnumerator.GetAppWindows())
            .Select(w => w.Handle).ToHashSet();
        var windows = new WindowSnapshot();
        var placed = new List<(WindowEntry Entry, IntPtr Handle)>();
        var launched = new HashSet<IntPtr>();

        // Restores the windows of one app, one after the other.
        async Task RestoreAsync(IEnumerable<WindowEntry> appEntries)
        {
            foreach (var entry in appEntries)
            {
                token.ThrowIfCancellationRequested();

                IntPtr? hwnd = null;
                if (reused.TryGetValue(entry, out var open) && IsWindow(open))
                {
                    hwnd = open;
                    _log($"  {entry.ProcessName}: reusing open window \"{WindowEnumerator.GetTitle(open)}\"");
                }

                if (hwnd == null)
                {
                    if (!Launch(entry))
                        continue;
                    hwnd = await WaitForNewWindowAsync(entry, preExisting, claimed, windows, token);
                    if (hwnd == null)
                    {
                        _log($"  {entry.ProcessName}: no window appeared within {WindowTimeout.TotalSeconds:0}s, skipped.");
                        continue;
                    }
                    // Give the app a moment to finish restoring its own saved layout before we override it.
                    await Task.Delay(500, token);
                    launched.Add(hwnd.Value);
                }

                claimed.Add(hwnd.Value);
                Apply(entry, hwnd.Value, firstPass: true);
                placed.Add((entry, hwnd.Value));
            }
        }

        // Start every app at once and place each window as soon as it appears. Windows of the same app
        // (two browser windows, two folders, ...) still go one at a time, so that each new window is
        // matched to the right entry.
        await Task.WhenAll(entries.GroupBy(AppKey, StringComparer.OrdinalIgnoreCase).Select(RestoreAsync));
        memory.Save(workflow, placed);

        // Take the user to the desktop chosen for this workflow, or – by default – to the desktop of a
        // layout that lives on a single desktop, wherever they started the run from.
        var desktops = entries.Select(e => e.DesktopIndex).Distinct().ToList();
        int? finish = workflow.FinishDesktop switch
        {
            Workflow.FinishDesktopStay => null,
            Workflow.FinishDesktopAutomatic => desktops.Count == 1 ? desktops[0] : null,
            var number => number - 1,
        };
        if (finish is { } index && placed.Count > 0)
        {
            if (_desktops.SwitchToDesktop(index))
                _log($"Switched to desktop {index + 1}.");
            else if (_desktops.CanMoveWindows)
                _log($"Could not switch to desktop {index + 1}.");
        }

        _log($"Done: {placed.Count} of {entries.Count} windows restored.");

        // Some apps (browsers, Electron apps, ...) move or resize themselves once they finish loading,
        // sometimes seconds later; enforce the layout again on the windows that drifted. Only freshly launched
        // apps do that, and the run is already finished for the user, so this happens in the background.
        var toSettle = placed.Where(p => launched.Contains(p.Handle)).ToList();
        var settled = toSettle.Count == 0 ? Task.CompletedTask : SettleAsync(toSettle, settleToken);
        return new RunResult(placed.Count, entries.Count, settled);
    }

    private async Task SettleAsync(List<(WindowEntry Entry, IntPtr Handle)> windows, CancellationToken token)
    {
        try
        {
            var corrected = new HashSet<IntPtr>();
            foreach (var delay in CorrectionDelays)
            {
                await Task.Delay(delay, token);
                foreach (var (entry, handle) in windows)
                {
                    if (IsWindow(handle) && Apply(entry, handle, firstPass: false))
                        corrected.Add(handle);
                }
            }
            if (corrected.Count > 0)
                _log($"  Put back {corrected.Count} window(s) that moved themselves after starting.");
        }
        catch (OperationCanceledException)
        {
            // another workflow took over these windows
        }
    }

    private static bool Matches(WindowEntry entry, WindowInfo window)
    {
        if (entry.IsPackaged)
            return string.Equals(entry.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(entry.ExecutablePath, window.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return false;

        // explorer.exe also owns the taskbar etc.; only folder windows count.
        return entry.WindowClass != "CabinetWClass" || window.ClassName == "CabinetWClass";
    }

    /// <summary>Entries with the same key are windows of the same app and cannot be told apart while launching.</summary>
    private static string AppKey(WindowEntry entry) =>
        entry.IsPackaged ? entry.AppUserModelId : entry.ExecutablePath;

    private static IntPtr? FindOpenWindow(WindowEntry entry, List<WindowInfo> openWindows,
        HashSet<IntPtr> claimed, Dictionary<IntPtr, List<HostedScript>> hostedScripts)
    {
        var candidates = openWindows
            .Where(w => !claimed.Contains(w.Handle) && Matches(entry, w) && RunsSameScripts(entry, w, hostedScripts))
            .ToList();

        var best = candidates.FirstOrDefault(w => w.Title == entry.Title)
                   ?? candidates.FirstOrDefault(w => w.ClassName == entry.WindowClass)
                   ?? candidates.FirstOrDefault();
        return best?.Handle;
    }

    /// <summary>A terminal entry only matches a window running the scripts it saved (or none, for a plain shell).</summary>
    private static bool RunsSameScripts(WindowEntry entry, WindowInfo window, Dictionary<IntPtr, List<HostedScript>> hostedScripts)
    {
        var running = hostedScripts.TryGetValue(window.Handle, out var scripts)
            ? scripts.Select(s => s.ScriptPath).ToList()
            : new List<string>();
        if (entry.TerminalScripts.Count == 0)
            return running.Count == 0;
        return entry.TerminalScripts.All(path => running.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    private bool Launch(WindowEntry entry)
    {
        try
        {
            ProcessStartInfo psi;
            if (entry.IsPackaged && string.IsNullOrEmpty(entry.LaunchExecutablePath))
            {
                // Store / MSIX apps cannot be started from their exe; go through the shell instead.
                psi = new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.AppUserModelId}");
            }
            else
            {
                var executablePath = string.IsNullOrEmpty(entry.LaunchExecutablePath)
                    ? entry.ExecutablePath
                    : entry.LaunchExecutablePath;
                var arguments = entry.Tabs.Count > 0
                    ? BrowserTabs.BuildArguments(entry.ProcessName, entry.Arguments, entry.Tabs)
                    : entry.Arguments;
                // A script runs in its own folder, as when it is double-clicked.
                var workingDirectory = entry.TerminalScripts.Count > 0
                    ? Path.GetDirectoryName(entry.TerminalScripts[0])
                    : Path.GetDirectoryName(executablePath);
                psi = new ProcessStartInfo(executablePath, arguments)
                {
                    WorkingDirectory = workingDirectory ?? "",
                };
            }
            psi.UseShellExecute = true;

            _log(entry.Tabs.Count > 0
                ? $"  {entry.ProcessName}: launching with {entry.Tabs.Count} tab(s)..."
                : entry.TerminalScripts.Count > 0
                    ? $"  {entry.ProcessName}: launching {string.Join(", ", entry.TerminalScripts.Select(Path.GetFileName))}..."
                    : $"  {entry.ProcessName}: launching...");
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            _log($"  {entry.ProcessName}: failed to launch ({ex.Message}).");
            return false;
        }
    }

    private static async Task<IntPtr?> WaitForNewWindowAsync(
        WindowEntry entry, HashSet<IntPtr> preExisting, HashSet<IntPtr> claimed, WindowSnapshot windows, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < WindowTimeout)
        {
            await Task.Delay(PollInterval, token);

            var candidates = windows.Get()
                .Where(w => !preExisting.Contains(w.Handle) && !claimed.Contains(w.Handle) && Matches(entry, w))
                .ToList();

            // Prefer the same window class (skips splash screens); accept anything after a grace period.
            var match = candidates.FirstOrDefault(w => w.ClassName == entry.WindowClass);
            if (match == null && timer.Elapsed > ClassMismatchGrace)
                match = candidates.FirstOrDefault();
            if (match != null)
            {
                // Claim it before awaiting anything, so that no other app being waited for can take it.
                claimed.Add(match.Handle);
                return match.Handle;
            }
        }
        return null;
    }

    /// <summary>The open app windows, enumerated at most once per poll however many apps are being waited for.</summary>
    private sealed class WindowSnapshot
    {
        private readonly Stopwatch _age = new();
        private List<WindowInfo> _windows = new();

        public List<WindowInfo> Get()
        {
            if (!_age.IsRunning || _age.Elapsed >= PollInterval / 2)
            {
                _windows = WindowEnumerator.GetAppWindows();
                _age.Restart();
            }
            return _windows;
        }
    }

    /// <returns>For a correction pass, whether the window had drifted and was put back.</returns>
    private bool Apply(WindowEntry entry, IntPtr hwnd, bool firstPass)
    {
        // Position the window while it is still on the current desktop: changing the state of a
        // window on another desktop can make Windows switch to that desktop.
        if (firstPass)
            ApplyPlacement(entry, hwnd);

        if (!_desktops.MoveWindowToDesktop(hwnd, entry.DesktopIndex, out var error) && firstPass)
            _log($"  {entry.ProcessName}: could not move to desktop {entry.DesktopIndex + 1} ({error}).");

        if (!firstPass)
            return CorrectPlacement(entry, hwnd);
        _log($"  {entry.ProcessName}: placed on desktop {entry.DesktopIndex + 1}.");
        return false;
    }

    /// <summary>Saved bounds in both coordinate systems: screen (SetWindowPos) and workspace (SetWindowPlacement).</summary>
    private static (RECT Screen, RECT Workspace) GetTargetBounds(WindowEntry entry)
    {
        var rect = new RECT
        {
            Left = entry.Left,
            Top = entry.Top,
            Right = entry.Left + entry.Width,
            Bottom = entry.Top + entry.Height,
        };
        return entry.ScreenCoordinates
            ? (rect, WindowGeometry.ToWorkspace(rect))
            : (WindowGeometry.ToScreen(rect), rect);
    }

    private static void ApplyPlacement(WindowEntry entry, IntPtr hwnd)
    {
        var (screen, workspace) = GetTargetBounds(entry);

        // Sets the state, and the bounds a maximized/minimized window returns to (on the right monitor).
        var placement = WINDOWPLACEMENT.Create();
        GetWindowPlacement(hwnd, ref placement);
        placement.flags = 0;
        placement.rcNormalPosition = workspace;
        placement.showCmd = entry.State switch
        {
            WindowState.Maximized => SW_SHOWMAXIMIZED,
            WindowState.Minimized => SW_SHOWMINNOACTIVE,
            _ => SW_SHOWNOACTIVATE,
        };
        SetWindowPlacement(hwnd, ref placement);

        if (entry.State != WindowState.Normal)
            return;

        // Then put a normal window at its exact on-screen rectangle. Done twice: when the window moves
        // to a monitor with a different DPI, the app rescales itself after the first move.
        for (var i = 0; i < 2; i++)
        {
            SetWindowPos(hwnd, IntPtr.Zero, screen.Left, screen.Top, screen.Right - screen.Left, screen.Bottom - screen.Top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    /// <summary>Correction pass: only touch windows that drifted, and never re-activate maximized ones.</summary>
    private static bool CorrectPlacement(WindowEntry entry, IntPtr hwnd)
    {
        var placement = WINDOWPLACEMENT.Create();
        if (!GetWindowPlacement(hwnd, ref placement))
            return false;

        var (screen, workspace) = GetTargetBounds(entry);
        bool boundsOk;
        if (entry.State == WindowState.Normal)
            boundsOk = GetWindowRect(hwnd, out var actual) && SameRect(actual, screen);
        else
            boundsOk = SameRect(placement.rcNormalPosition, workspace);

        var stateOk = entry.State switch
        {
            WindowState.Maximized => IsZoomed(hwnd),
            WindowState.Minimized => placement.showCmd is SW_SHOWMINIMIZED or SW_MINIMIZE or SW_SHOWMINNOACTIVE,
            _ => placement.showCmd is not (SW_SHOWMAXIMIZED or SW_SHOWMINIMIZED or SW_MINIMIZE or SW_SHOWMINNOACTIVE),
        };

        if (boundsOk && stateOk)
            return false;
        ApplyPlacement(entry, hwnd);
        return true;
    }

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
