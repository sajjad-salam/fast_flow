using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>
/// Converts between screen coordinates (GetWindowRect / SetWindowPos) and the workspace coordinates
/// used by Get/SetWindowPlacement, which are relative to the work area (screen minus taskbar) of the
/// monitor the window is on.
/// </summary>
internal static class WindowGeometry
{
    public static RECT ToScreen(RECT workspace) => Offset(workspace, GetWorkAreaOffset(workspace), 1);

    public static RECT ToWorkspace(RECT screen) => Offset(screen, GetWorkAreaOffset(screen), -1);

    private static (int X, int Y) GetWorkAreaOffset(RECT rect)
    {
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        var info = MONITORINFO.Create();
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return (0, 0);
        return (info.rcWork.Left - info.rcMonitor.Left, info.rcWork.Top - info.rcMonitor.Top);
    }

    private static RECT Offset(RECT rect, (int X, int Y) offset, int sign) => new()
    {
        Left = rect.Left + sign * offset.X,
        Top = rect.Top + sign * offset.Y,
        Right = rect.Right + sign * offset.X,
        Bottom = rect.Bottom + sign * offset.Y,
    };
}
