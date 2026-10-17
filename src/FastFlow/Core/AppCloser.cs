using System.Diagnostics;
using System.Runtime.InteropServices;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>
/// Closes every open application window on every virtual desktop, the same way clicking its X button would,
/// so apps can still save their state or ask about unsaved work.
/// </summary>
internal static class AppCloser
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReopenWatch = TimeSpan.FromSeconds(1);
    private const int MaxRetries = 3;

    /// <param name="keepOpenApps">Executable names (with or without ".exe") of apps to leave running.</param>
    /// <param name="keepWindows">Individual windows to leave open, e.g. ones the next workflow will reuse.</param>
    public static async Task CloseAllAsync(IEnumerable<string> keepOpenApps, IReadOnlySet<IntPtr> keepWindows,
        Action<string> log, CancellationToken token = default)
    {
        // Never close FastFlow itself, including another FastFlow instance (e.g. the tray one when run with --run).
        var ownExe = Environment.ProcessPath ?? "";
        var keep = keepOpenApps.Select(NormalizeAppName).Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = WindowEnumerator.GetAppWindows()
            .Where(w => !string.Equals(w.ExecutablePath, ownExe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var kept = candidates.Where(w => keep.Contains(w.ProcessName)).ToList();
        var reused = candidates.Where(w => keepWindows.Contains(w.Handle)).Except(kept).ToList();
        var windows = candidates.Except(kept).Except(reused).ToList();
        if (kept.Count > 0)
            log($"Keeping {kept.Count} window(s) open: {string.Join(", ", kept.Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))}.");
        if (reused.Count > 0)
            log($"Keeping {reused.Count} window(s) the workflow uses: {string.Join(", ", reused.Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))}.");
        if (windows.Count == 0)
            return;

        log($"Closing {windows.Count} open window(s) on all desktops...");
        var closing = windows.Select(w => w.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A kept window of an app whose other windows are closed is not one it "reopened".
        var handled = windows.Select(w => w.Handle).Concat(keepWindows).ToHashSet();
        var pending = windows;
        for (var attempt = 0; ; attempt++)
        {
            await CloseWindowsAsync(pending, log, token);

            // Some apps don't exit on the first close: CapCut, for example, closes the open project and shows
            // its home screen instead. Close whatever new windows the closed apps open, a few times at most.
            pending = await WaitForExitAsync(windows, closing, handled, ownExe, token);
            if (pending.Count == 0)
                break;
            if (attempt == MaxRetries)
            {
                foreach (var window in pending)
                    log($"  {window.ProcessName}: keeps opening new windows (\"{window.Title}\"), giving up.");
                break;
            }

            foreach (var window in pending)
                log($"  {window.ProcessName}: opened \"{window.Title}\" instead of exiting, closing it again.");
            handled.UnionWith(pending.Select(w => w.Handle));
            windows.AddRange(pending);
        }

        var closed = windows.Count(w => !IsOpen(w.Handle));
        log($"Closed {closed} of {windows.Count} window(s).");
    }

    private static async Task CloseWindowsAsync(List<WindowInfo> windows, Action<string> log, CancellationToken token)
    {
        foreach (var window in windows)
        {
            if (!PostMessage(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                log($"  {window.ProcessName}: could not close \"{window.Title}\" (error {Marshal.GetLastWin32Error()}; it may be running as administrator).");
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < CloseTimeout && windows.Any(w => IsOpen(w.Handle)))
            await Task.Delay(PollInterval, token);

        foreach (var window in windows.Where(w => IsOpen(w.Handle)))
            log($"  {window.ProcessName}: \"{window.Title}\" is still open (it may be asking to save), continuing.");
    }

    /// <summary>
    /// Lets the closed apps finish shutting down, otherwise single-instance apps (browsers, editors) may
    /// hand the new launch to the dying process. Stops early when one of them opens a new window instead.
    /// </summary>
    /// <returns>New windows the closed apps opened while shutting down; empty once they have all exited.</returns>
    private static async Task<List<WindowInfo>> WaitForExitAsync(List<WindowInfo> windows, HashSet<string> closing,
        HashSet<IntPtr> handled, string ownExe, CancellationToken token)
    {
        // Apps that keep running in the tray never exit; don't wait on them forever.
        var stillOpenPids = WindowEnumerator.GetAppWindows().Select(w => w.ProcessId).ToHashSet();
        var exiting = windows
            .Select(w => w.ProcessId)
            .Distinct()
            .Where(pid => !stillOpenPids.Contains(pid) && !IsShell(pid))
            .Select(TryGetProcess)
            .OfType<Process>()
            .ToList();
        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                // An app whose window is still open may be asking to save; leave its other windows alone.
                var busy = windows.Where(w => IsOpen(w.Handle)).Select(w => w.ProcessId).ToHashSet();
                var reopened = WindowEnumerator.GetAppWindows()
                    .Where(w => closing.Contains(w.ProcessName) && !handled.Contains(w.Handle) && !busy.Contains(w.ProcessId)
                        && !string.Equals(w.ExecutablePath, ownExe, StringComparison.OrdinalIgnoreCase)
                        && !IsShell(w.ProcessId))
                    .ToList();
                if (reopened.Count > 0)
                    return reopened;
                // Always watch briefly: the new window can take a moment to appear, even from a fresh process.
                if (timer.Elapsed >= ExitTimeout || (timer.Elapsed >= ReopenWatch && exiting.All(HasExited)))
                    return [];
                await Task.Delay(PollInterval, token);
            }
        }
        finally
        {
            exiting.ForEach(p => p.Dispose());
        }
    }

    /// <summary>"C:\Program Files\Microvirt\MEmu\MEmu.exe", "MEmu.exe" and " MEmu " all become "MEmu".</summary>
    public static string NormalizeAppName(string name)
    {
        name = name.Trim().Trim('"');
        if (name.Length == 0)
            return "";
        name = Path.GetFileName(name);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    // Hidden counts as closed: apps that minimise to the tray hide their window instead of destroying it.
    private static bool IsOpen(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    // File Explorer folder windows live in the shell process, which must keep running.
    private static bool IsShell(uint pid) =>
        string.Equals(WindowEnumerator.GetProcessPath(pid) is { } path ? Path.GetFileName(path) : null,
            "explorer.exe", StringComparison.OrdinalIgnoreCase);

    private static Process? TryGetProcess(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid);
        }
        catch
        {
            return null; // already gone
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true; // access denied – don't wait on it
        }
    }
}
