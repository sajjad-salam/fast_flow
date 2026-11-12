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

    /// <summary>
    /// Runs a desktop flow: goes to its desktop (creating it if needed) and makes sure every one of its apps is open
    /// there, shown – not minimized – at its recorded size and position. Nothing else is closed or minimized.
    /// An app that is already open, on any desktop and minimized or not, has its window brought over instead of a
    /// second copy being started; only apps that aren't open are launched.
    /// </summary>
    public async Task<RunResult> RunDesktopFlowAsync(Workflow workflow, CancellationToken token = default,
        CancellationToken settleToken = default)
    {
        var entries = workflow.Windows.Where(w => w.Enabled).ToList();
        var index = Math.Max(0, workflow.TargetDesktop - 1);
        FileLog.Debug($"=== Desktop flow \"{workflow.Name}\" ({workflow.Id}), target desktop {index + 1}, "
                      + $"{entries.Count}/{workflow.Windows.Count} entries enabled. Backend: {_desktops.Status}. "
                      + $"Desktops: {_desktops.GetDesktopIds().Count}. Foreground: {Describe(GetForegroundWindow())}");
        _log($"Running desktop flow \"{workflow.Name}\" on desktop {index + 1} ({entries.Count} windows)...");

        try
        {
            var created = _desktops.EnsureDesktopCount(index + 1);
            if (created > 0)
                _log($"Created {created} virtual desktop(s).");
        }
        catch (Exception ex)
        {
            _log($"Could not create virtual desktops: {ex.Message}");
        }

        // Go there first, so apps that are launched open on that desktop by themselves.
        var switched = _desktops.SwitchToDesktop(index);
        FileLog.Debug($"SwitchToDesktop({index + 1}) = {switched}");
        if (switched)
            _log($"Switched to desktop {index + 1}.");
        else if (_desktops.CanMoveWindows)
            _log($"Could not switch to desktop {index + 1}.");

        if (entries.Count == 0)
            return new RunResult(0, 0, Task.CompletedTask);

        // Every window goes to the flow's desktop, whatever the grid says, and is shown: a desktop flow is for
        // getting straight to work, so a window recorded while minimized comes back up too.
        foreach (var entry in entries)
        {
            entry.DesktopIndex = index;
            if (entry.State == WindowState.Minimized)
                entry.State = WindowState.Normal;
        }

        var memory = WindowMemory.Load();
        var openWindows = WindowEnumerator.GetAppWindows();
        var open = openWindows.ToDictionary(w => w.Handle);
        var ids = _desktops.GetDesktopIds();
        Guid? target = index < ids.Count ? ids[index] : null;
        FileLog.Debug($"Open app windows: {openWindows.Count}. Target desktop id: {target}");
        foreach (var entry in entries)
        {
            FileLog.Debug($"Entry #{workflow.Windows.IndexOf(entry)} {entry.ProcessName}: exe=\"{entry.ExecutablePath}\" "
                          + $"aumid=\"{entry.AppUserModelId}\" class={entry.WindowClass} title=\"{entry.Title}\" state={entry.State} "
                          + $"bounds=({entry.Left},{entry.Top} {entry.Width}x{entry.Height}) screen={entry.ScreenCoordinates} "
                          + $"tabs={entry.Tabs.Count} scripts={entry.TerminalScripts.Count} remembered={memory.Find(workflow, entry)?.ToString("X") ?? "none"}");
            var related = openWindows.Where(w =>
                string.Equals(w.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase)
                || (entry.IsPackaged && string.Equals(w.AppUserModelId, entry.AppUserModelId, StringComparison.OrdinalIgnoreCase))).ToList();
            if (related.Count == 0)
                FileLog.Debug("    no open window of this app");
            foreach (var w in related)
                FileLog.Debug($"    candidate matches={Matches(entry, w)} exe=\"{w.ExecutablePath}\" aumid=\"{w.AppUserModelId}\" {Describe(w.Handle)}");
        }
        var claimed = new HashSet<IntPtr>();
        var reused = new Dictionary<WindowEntry, IntPtr>();
        foreach (var entry in entries)
        {
            // The window this flow used last time, wherever it is now.
            if (memory.Find(workflow, entry) is { } hwnd && !claimed.Contains(hwnd)
                && open.TryGetValue(hwnd, out var window) && Matches(entry, window))
            {
                claimed.Add(hwnd);
                reused[entry] = hwnd;
            }
        }

        // Otherwise a window of the same app that is already open anywhere: bring it here rather than start a
        // second copy (which single-instance apps wouldn't open anyway). Terminals running a script pick first.
        var hostedScripts = entries.Any(e => !reused.ContainsKey(e)
                                             && (e.TerminalScripts.Count > 0 || TerminalCommands.IsWindowsTerminal(e.ProcessName)))
            ? TerminalCommands.FindHostedScripts()
            : new Dictionary<IntPtr, List<HostedScript>>();
        foreach (var entry in entries.Where(e => !reused.ContainsKey(e)).OrderBy(e => e.TerminalScripts.Count == 0))
        {
            if (FindWindowForFlow(entry, openWindows, claimed, hostedScripts, target) is { } hwnd)
            {
                claimed.Add(hwnd);
                reused[entry] = hwnd;
            }
        }

        foreach (var entry in entries)
            FileLog.Debug($"Decision for {entry.ProcessName}: "
                          + (reused.TryGetValue(entry, out var use) ? $"reuse {use:X}" : "launch"));

        var preExisting = openWindows.Select(w => w.Handle).ToHashSet();
        var windows = new WindowSnapshot();
        var placed = new List<(WindowEntry Entry, IntPtr Handle)>();
        var missing = new List<string>();

        async Task RestoreAsync(IEnumerable<WindowEntry> appEntries)
        {
            foreach (var entry in appEntries)
            {
                token.ThrowIfCancellationRequested();

                IntPtr? hwnd = null;
                if (reused.TryGetValue(entry, out var existing) && IsWindow(existing))
                {
                    hwnd = existing;
                    _log($"  {entry.ProcessName}: already open{(IsIconic(existing) ? " (minimized)" : "")}, "
                         + $"bringing \"{WindowEnumerator.GetTitle(existing)}\" here.");
                }

                if (hwnd == null)
                {
                    if (!Launch(entry))
                    {
                        missing.Add(entry.ProcessName);
                        continue;
                    }
                    // Apps that were hidden in the tray, or single-instance apps, may bring back a window they had.
                    hwnd = await WaitForNewWindowAsync(entry, preExisting, claimed, windows, token, fallbackToExisting: true);
                    FileLog.Debug($"{entry.ProcessName}: waited for window -> {(hwnd is { } got ? $"{got:X} (pre-existing={preExisting.Contains(got)})" : "none")}");
                    if (hwnd == null)
                    {
                        _log($"  {entry.ProcessName}: no window appeared within {WindowTimeout.TotalSeconds:0}s, skipped.");
                        missing.Add(entry.ProcessName);
                        continue;
                    }
                    // Give the app a moment to finish restoring its own saved layout before we override it.
                    if (!preExisting.Contains(hwnd.Value))
                        await Task.Delay(500, token);
                }

                claimed.Add(hwnd.Value);
                await ShowOnDesktopAsync(entry, hwnd.Value, index, token);
                placed.Add((entry, hwnd.Value));
            }
        }

        await Task.WhenAll(entries.GroupBy(AppKey, StringComparer.OrdinalIgnoreCase).Select(RestoreAsync));
        memory.Save(workflow, placed);

        // Restoring an app that was on another desktop may have pulled us there; come back.
        if (switched && placed.Count > 0)
            _desktops.SwitchToDesktop(index);

        // Stack the windows as recorded, all above whatever else is on the desktop, with the first one in front.
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            SetWindowPos(placed[i].Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        if (placed.Count > 0)
        {
            var foreground = SetForegroundWindow(placed[0].Handle);
            FileLog.Debug($"SetForegroundWindow({placed[0].Handle:X}) = {foreground}");
        }
        foreach (var (entry, handle) in placed)
            FileLog.Debug($"After run {entry.ProcessName}: {Describe(handle)}");
        FileLog.Debug($"Foreground now: {Describe(GetForegroundWindow())}");

        _log(missing.Count == 0
            ? $"Done: all {placed.Count} windows are open on desktop {index + 1}."
            : $"Done: {placed.Count} of {entries.Count} windows open on desktop {index + 1}; could not open {string.Join(", ", missing)}.");

        // Check every window again a few seconds later – launched apps move themselves once loaded, and restored
        // ones can be re-minimized or moved by their app – and put back any that aren't shown where they belong.
        var settled = placed.Count == 0 ? Task.CompletedTask : SettleAsync(placed, settleToken);
        return new RunResult(placed.Count, entries.Count, settled);
    }

    /// <summary>
    /// Moves a window to desktop <paramref name="index"/>, un-minimizes it and puts it at the entry's size and position.
    /// It is moved first: changing the state of a window on another desktop can make Windows switch there.
    /// </summary>
    private async Task ShowOnDesktopAsync(WindowEntry entry, IntPtr hwnd, int index, CancellationToken token)
    {
        FileLog.Debug($"{entry.ProcessName}: before   {Describe(hwnd)}");
        var moved = _desktops.MoveWindowToDesktop(hwnd, index, out var error);
        FileLog.Debug($"{entry.ProcessName}: MoveWindowToDesktop({index + 1}) = {moved}{(error != null ? $" ({error})" : "")} -> {Describe(hwnd)}");
        if (!moved)
            _log($"  {entry.ProcessName}: could not move to desktop {index + 1} ({error}).");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (IsIconic(hwnd))
            {
                // A real restore, like clicking it in the taskbar: some apps ignore a placement change while minimized.
                var shown = ShowWindowAsync(hwnd, SW_RESTORE);
                for (var wait = 0; wait < 10 && IsIconic(hwnd); wait++)
                    await Task.Delay(50, token);
                FileLog.Debug($"{entry.ProcessName}: attempt {attempt + 1} ShowWindowAsync(SW_RESTORE) = {shown} -> {Describe(hwnd)}");
            }
            ApplyPlacement(entry, hwnd);
            FileLog.Debug($"{entry.ProcessName}: attempt {attempt + 1} ApplyPlacement -> {Describe(hwnd)}");
            if (!IsIconic(hwnd))
                break;
            await Task.Delay(150, token);
        }

        if (IsIconic(hwnd))
            _log($"  {entry.ProcessName}: stayed minimized.");
        else
            _log($"  {entry.ProcessName}: shown on desktop {index + 1}.");
    }

    /// <summary>
    /// An open window a desktop flow can use for <paramref name="entry"/>, on any desktop: one already on the flow's
    /// desktop first, then one with the same title. Browser windows with saved tabs are opened fresh with their tabs,
    /// and a folder window is only used when it shows the same folder.
    /// </summary>
    private IntPtr? FindWindowForFlow(WindowEntry entry, List<WindowInfo> openWindows, HashSet<IntPtr> claimed,
        Dictionary<IntPtr, List<HostedScript>> hostedScripts, Guid? target)
    {
        if (entry.Tabs.Count > 0)
            return null;

        var candidates = openWindows
            .Where(w => !claimed.Contains(w.Handle) && IsWindow(w.Handle) && Matches(entry, w)
                        && RunsSameScripts(entry, w, hostedScripts))
            .Where(w => entry.WindowClass != "CabinetWClass" || w.Title == entry.Title)
            .ToList();
        if (candidates.Count == 0)
            return null;

        bool OnTarget(WindowInfo w) => target != null && _desktops.GetWindowDesktopId(w.Handle) == target;
        var best = candidates.FirstOrDefault(w => OnTarget(w) && w.Title == entry.Title)
                   ?? candidates.FirstOrDefault(OnTarget)
                   ?? candidates.FirstOrDefault(w => w.Title == entry.Title)
                   ?? candidates.FirstOrDefault(w => w.ClassName == entry.WindowClass)
                   ?? candidates[0];
        return best.Handle;
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
                    {
                        corrected.Add(handle);
                        FileLog.Debug($"Settle: put back {entry.ProcessName} -> {Describe(handle)}");
                    }
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

    /// <summary>One line describing a window's state, for the log file.</summary>
    private string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "(none)";
        if (!IsWindow(hwnd))
            return $"{hwnd:X} (gone)";
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var placement = WINDOWPLACEMENT.Create();
            GetWindowPlacement(hwnd, ref placement);
            GetWindowRect(hwnd, out var rect);
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int));
            var desktopId = _desktops.GetWindowDesktopId(hwnd);
            var desktop = desktopId is { } id ? _desktops.GetDesktopIds().IndexOf(id) + 1 : 0;
            return $"{hwnd:X} pid={pid} \"{WindowEnumerator.GetTitle(hwnd)}\" class={WindowEnumerator.GetClass(hwnd)} "
                   + $"visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} zoomed={IsZoomed(hwnd)} cloaked={cloaked} "
                   + $"desktop={desktop} showCmd={placement.showCmd} flags={placement.flags} "
                   + $"rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) "
                   + $"normal=({placement.rcNormalPosition.Left},{placement.rcNormalPosition.Top},{placement.rcNormalPosition.Right},{placement.rcNormalPosition.Bottom})";
        }
        catch (Exception ex)
        {
            return $"{hwnd:X} (describe failed: {ex.Message})";
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

    /// <param name="fallbackToExisting">
    /// When the app opens no new window (a single-instance app that just brings back the one it has), take its
    /// already-open window instead: right away once it comes to the front, otherwise when the wait times out.
    /// </param>
    private static async Task<IntPtr?> WaitForNewWindowAsync(
        WindowEntry entry, HashSet<IntPtr> preExisting, HashSet<IntPtr> claimed, WindowSnapshot windows, CancellationToken token,
        bool fallbackToExisting = false)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < WindowTimeout)
        {
            await Task.Delay(PollInterval, token);

            var all = windows.Get();
            var candidates = all
                .Where(w => !preExisting.Contains(w.Handle) && !claimed.Contains(w.Handle) && Matches(entry, w))
                .ToList();

            if (fallbackToExisting && candidates.Count == 0 && timer.Elapsed > ClassMismatchGrace)
            {
                var foreground = GetForegroundWindow();
                if (preExisting.Contains(foreground) && !claimed.Contains(foreground)
                    && all.FirstOrDefault(w => w.Handle == foreground) is { } active && Matches(entry, active))
                {
                    claimed.Add(foreground);
                    return foreground;
                }
            }

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

        if (fallbackToExisting
            && WindowEnumerator.GetAppWindows().FirstOrDefault(w => preExisting.Contains(w.Handle)
                && !claimed.Contains(w.Handle) && Matches(entry, w)) is { } running)
        {
            claimed.Add(running.Handle);
            return running.Handle;
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
