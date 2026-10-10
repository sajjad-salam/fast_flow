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

    public static async Task CloseAllAsync(Action<string> log, CancellationToken token = default)
    {
        // Never close FastFlow itself, including another FastFlow instance (e.g. the tray one when run with --run).
        var ownExe = Environment.ProcessPath ?? "";
        var windows = WindowEnumerator.GetAppWindows()
            .Where(w => !string.Equals(w.ExecutablePath, ownExe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (windows.Count == 0)
            return;

        log($"Closing {windows.Count} open window(s) on all desktops...");
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

        // Let the closed apps finish shutting down, otherwise single-instance apps (browsers, editors) may
        // hand the new launch to the dying process. Apps that keep running in the tray never exit; don't wait on them forever.
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
            var deadline = DateTime.UtcNow + ExitTimeout;
            while (DateTime.UtcNow < deadline && exiting.Any(p => !HasExited(p)))
                await Task.Delay(PollInterval, token);
        }
        finally
        {
            exiting.ForEach(p => p.Dispose());
        }

        var closed = windows.Count(w => !IsOpen(w.Handle));
        log($"Closed {closed} of {windows.Count} window(s).");
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
