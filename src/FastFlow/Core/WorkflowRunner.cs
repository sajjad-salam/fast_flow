using System.Diagnostics;
using FastFlow.Models;
using FastFlow.VirtualDesktops;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>
/// Restores a <see cref="Workflow"/>: launches each app, waits for its window, then puts the window
/// on its virtual desktop at its saved size and position.
///
/// Runs on the UI thread (awaiting delays keeps the UI responsive) because the shell's virtual
/// desktop COM objects must be called from the STA thread that created them.
/// </summary>
internal sealed class WorkflowRunner
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ClassMismatchGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan[] CorrectionDelays = { TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500) };

    private readonly VirtualDesktopService _desktops;
    private readonly Action<string> _log;

    public WorkflowRunner(VirtualDesktopService desktops, Action<string> log)
    {
        _desktops = desktops;
        _log = log;
    }

    /// <param name="reuseOpenWindows">Use a matching window that is already open instead of launching a new instance.</param>
    /// <param name="closeOtherApps">First close every open app window on every virtual desktop.</param>
    /// <param name="keepOpenApps">Executable names of apps that <paramref name="closeOtherApps"/> leaves running.</param>
    /// <returns>How many of the workflow's enabled windows were restored.</returns>
    public async Task<(int Restored, int Total)> RunAsync(Workflow workflow, bool reuseOpenWindows, bool closeOtherApps,
        IEnumerable<string> keepOpenApps, CancellationToken token = default)
    {
        var entries = workflow.Windows.Where(w => w.Enabled).ToList();
        _log($"Running \"{workflow.Name}\" ({entries.Count} windows)...");

        if (closeOtherApps)
            await AppCloser.CloseAllAsync(keepOpenApps, _log, token);

        if (entries.Count == 0)
            return (0, 0);

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

        var preExisting = WindowEnumerator.GetAppWindows().Select(w => w.Handle).ToHashSet();
        var claimed = new HashSet<IntPtr>();
        var placed = new List<(WindowEntry Entry, IntPtr Handle)>();

        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();

            IntPtr? hwnd = null;
            // A browser window with saved tabs is always opened fresh, so that its tabs are restored.
            if (reuseOpenWindows && entry.Tabs.Count == 0)
            {
                hwnd = FindOpenWindow(entry, claimed);
                if (hwnd != null)
                    _log($"  {entry.ProcessName}: reusing open window \"{WindowEnumerator.GetTitle(hwnd.Value)}\"");
            }

            if (hwnd == null)
            {
                if (!Launch(entry))
                    continue;
                hwnd = await WaitForNewWindowAsync(entry, preExisting, claimed, token);
                if (hwnd == null)
                {
                    _log($"  {entry.ProcessName}: no window appeared within {WindowTimeout.TotalSeconds:0}s, skipped.");
                    continue;
                }
                // Give the app a moment to finish restoring its own saved layout before we override it.
                await Task.Delay(500, token);
            }

            claimed.Add(hwnd.Value);
            Apply(entry, hwnd.Value, firstPass: true);
            placed.Add((entry, hwnd.Value));
        }

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

        // Some apps (browsers, Electron apps, ...) move or resize themselves once they finish loading,
        // sometimes seconds later; enforce the layout again on the windows that drifted.
        foreach (var delay in CorrectionDelays)
        {
            await Task.Delay(delay, token);
            foreach (var (entry, handle) in placed)
            {
                if (IsWindow(handle))
                    Apply(entry, handle, firstPass: false);
            }
        }

        _log($"Done: {placed.Count} of {entries.Count} windows restored.");
        return (placed.Count, entries.Count);
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

    private static IntPtr? FindOpenWindow(WindowEntry entry, HashSet<IntPtr> claimed)
    {
        var candidates = WindowEnumerator.GetAppWindows()
            .Where(w => !claimed.Contains(w.Handle) && Matches(entry, w))
            .ToList();

        var best = candidates.FirstOrDefault(w => w.Title == entry.Title)
                   ?? candidates.FirstOrDefault(w => w.ClassName == entry.WindowClass)
                   ?? candidates.FirstOrDefault();
        return best?.Handle;
    }

    private bool Launch(WindowEntry entry)
    {
        try
        {
            ProcessStartInfo psi;
            if (entry.IsPackaged)
            {
                // Store / MSIX apps cannot be started from their exe; go through the shell instead.
                psi = new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.AppUserModelId}");
            }
            else
            {
                var arguments = entry.Tabs.Count > 0
                    ? BrowserTabs.BuildArguments(entry.ProcessName, entry.Arguments, entry.Tabs)
                    : entry.Arguments;
                psi = new ProcessStartInfo(entry.ExecutablePath, arguments)
                {
                    WorkingDirectory = Path.GetDirectoryName(entry.ExecutablePath) ?? "",
                };
            }
            psi.UseShellExecute = true;

            _log(entry.Tabs.Count > 0
                ? $"  {entry.ProcessName}: launching with {entry.Tabs.Count} tab(s)..."
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
        WindowEntry entry, HashSet<IntPtr> preExisting, HashSet<IntPtr> claimed, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < WindowTimeout)
        {
            await Task.Delay(PollInterval, token);

            var candidates = WindowEnumerator.GetAppWindows()
                .Where(w => !preExisting.Contains(w.Handle) && !claimed.Contains(w.Handle) && Matches(entry, w))
                .ToList();

            // Prefer the same window class (skips splash screens); accept anything after a grace period.
            var match = candidates.FirstOrDefault(w => w.ClassName == entry.WindowClass);
            if (match == null && timer.Elapsed > ClassMismatchGrace)
                match = candidates.FirstOrDefault();
            if (match != null)
                return match.Handle;
        }
        return null;
    }

    private void Apply(WindowEntry entry, IntPtr hwnd, bool firstPass)
    {
        // Position the window while it is still on the current desktop: changing the state of a
        // window on another desktop can make Windows switch to that desktop.
        if (firstPass)
            ApplyPlacement(entry, hwnd);

        if (!_desktops.MoveWindowToDesktop(hwnd, entry.DesktopIndex, out var error) && firstPass)
            _log($"  {entry.ProcessName}: could not move to desktop {entry.DesktopIndex + 1} ({error}).");

        if (!firstPass)
            CorrectPlacement(entry, hwnd);
        else
            _log($"  {entry.ProcessName}: placed on desktop {entry.DesktopIndex + 1}.");
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
    private static void CorrectPlacement(WindowEntry entry, IntPtr hwnd)
    {
        var placement = WINDOWPLACEMENT.Create();
        if (!GetWindowPlacement(hwnd, ref placement))
            return;

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

        if (!boundsOk || !stateOk)
            ApplyPlacement(entry, hwnd);
    }

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
