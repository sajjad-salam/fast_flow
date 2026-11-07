using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>
/// Minimizes every open application window on every virtual desktop instead of closing it, so the apps keep
/// running and a later workflow can bring their windows straight back.
/// </summary>
internal static class AppMinimizer
{
    /// <param name="keepOpenApps">Executable names (with or without ".exe") of apps to leave alone.</param>
    /// <param name="keepWindows">Individual windows to leave alone, e.g. ones the next workflow will reuse.</param>
    public static void MinimizeAll(IEnumerable<string> keepOpenApps, IReadOnlySet<IntPtr> keepWindows, Action<string> log)
    {
        // Never touch FastFlow itself, including another FastFlow instance (e.g. the tray one when run with --run).
        var ownExe = Environment.ProcessPath ?? "";
        var keep = keepOpenApps.Select(AppCloser.NormalizeAppName).Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = WindowEnumerator.GetAppWindows()
            .Where(w => !string.Equals(w.ExecutablePath, ownExe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var kept = candidates.Where(w => keep.Contains(w.ProcessName)).ToList();
        var reused = candidates.Where(w => keepWindows.Contains(w.Handle)).Except(kept).ToList();
        var windows = candidates.Except(kept).Except(reused).Where(w => !IsIconic(w.Handle)).ToList();
        if (kept.Count > 0)
            log($"Leaving {kept.Count} window(s) as they are: {string.Join(", ", kept.Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))}.");
        if (reused.Count > 0)
            log($"Keeping {reused.Count} window(s) the workflow uses: {string.Join(", ", reused.Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))}.");
        if (windows.Count == 0)
            return;

        // Minimize without activating anything, so focus doesn't jump around (or to another desktop).
        var minimized = windows.Count(w => ShowWindowAsync(w.Handle, SW_SHOWMINNOACTIVE) || IsIconic(w.Handle));
        log($"Minimized {minimized} of {windows.Count} window(s) on all desktops.");
    }
}
