using System.Runtime.InteropServices;
using System.Text;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>A visible, user-facing top-level window together with the app that owns it.</summary>
internal sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ClassName,
    uint ProcessId,
    string ExecutablePath,
    string AppUserModelId)
{
    public string ProcessName => Path.GetFileNameWithoutExtension(ExecutablePath);
}

/// <summary>Finds the "real" application windows – the ones that show up in Alt+Tab / Task View – on all desktops.</summary>
internal static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame",
    };

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "LockApp", "SystemSettingsBroker", "ShellHost", "Widgets", "WidgetBoard",
    };

    public static List<WindowInfo> GetAppWindows()
    {
        var ownPid = (uint)Environment.ProcessId;
        var result = new List<WindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                var info = TryDescribe(hwnd);
                if (info != null && info.ProcessId != ownPid && !IgnoredProcesses.Contains(info.ProcessName))
                    result.Add(info);
            }
            catch
            {
                // window vanished or access denied – ignore it
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static WindowInfo? TryDescribe(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            return null;

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return null;

        // Windows on other virtual desktops are cloaked by the shell; anything cloaked for another reason is hidden UI.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
            && cloaked != 0 && cloaked != DWM_CLOAKED_SHELL)
            return null;

        var title = GetTitle(hwnd);
        var className = GetClass(hwnd);
        if (string.IsNullOrWhiteSpace(title) || IgnoredClasses.Contains(className))
            return null;

        GetWindowThreadProcessId(hwnd, out var pid);

        // UWP apps live inside an ApplicationFrameHost window; the real app owns a CoreWindow child.
        if (className == "ApplicationFrameWindow")
        {
            var childPid = FindCoreWindowProcess(hwnd, pid);
            if (childPid == 0)
                return null; // suspended or still starting – cannot tell which app it is
            pid = childPid;
        }

        var exe = GetProcessPath(pid);
        if (exe == null)
            return null;

        return new WindowInfo(hwnd, title, className, pid, exe, GetAppUserModelId(pid) ?? "");
    }

    private static uint FindCoreWindowProcess(IntPtr frame, uint framePid)
    {
        uint found = 0;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid != framePid && GetClass(child) == "Windows.UI.Core.CoreWindow")
            {
                found = childPid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string GetTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length == 0)
            return "";
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? GetProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>AppUserModelID of a packaged (Store/MSIX) process, or null for ordinary desktop apps.</summary>
    public static string? GetAppUserModelId(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var length = 512;
            var sb = new StringBuilder(length);
            return GetApplicationUserModelId(handle, ref length, sb) == 0 ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Full command line of another process (Windows 8.1+), or null if it cannot be read.</summary>
    public static string? GetCommandLine(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
                return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                    return null;
                var str = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
                return Marshal.PtrToStringUni(str.Buffer, str.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Removes the executable (first token) from a command line, leaving just the arguments.</summary>
    public static string StripExecutable(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end < 0 ? "" : s[(end + 1)..].Trim();
        }
        var space = s.IndexOf(' ');
        return space < 0 ? "" : s[(space + 1)..].Trim();
    }
}
