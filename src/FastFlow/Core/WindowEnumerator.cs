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

    /// <summary>
    /// Executable path and AppUserModelID per process, so that listing the windows (which happens several times
    /// a second while a workflow runs) doesn't query every process again each time. Each entry keeps a handle to
    /// its process open: while that handle is held, Windows cannot give the process id to a new process, so a
    /// cached entry always describes the process that now has that id.
    /// </summary>
    private sealed record ProcessInfo(IntPtr Handle, string ExecutablePath, string AppUserModelId);

    private static readonly Dictionary<uint, ProcessInfo> ProcessCache = new();
    private static readonly object CacheLock = new();

    public static List<WindowInfo> GetAppWindows()
    {
        var ownPid = (uint)Environment.ProcessId;
        var result = new List<WindowInfo>();

        lock (CacheLock)
        {
            var seen = new HashSet<uint>();
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    var info = TryDescribe(hwnd, seen);
                    if (info != null && info.ProcessId != ownPid && !IgnoredProcesses.Contains(info.ProcessName))
                        result.Add(info);
                }
                catch
                {
                    // window vanished or access denied – ignore it
                }
                return true;
            }, IntPtr.Zero);

            // Let go of processes that no longer have a window, so their ids can be reused.
            foreach (var pid in ProcessCache.Keys.Where(pid => !seen.Contains(pid)).ToList())
            {
                CloseHandle(ProcessCache[pid].Handle);
                ProcessCache.Remove(pid);
            }
        }

        return result;
    }

    /// <summary>Cached path and AppUserModelID of a process; null when it cannot be opened. Call with <see cref="CacheLock"/> held.</summary>
    private static ProcessInfo? GetProcessInfo(uint pid, HashSet<uint> seen)
    {
        seen.Add(pid);
        if (ProcessCache.TryGetValue(pid, out var cached))
            return cached;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        var size = 1024;
        var path = new StringBuilder(size);
        if (!QueryFullProcessImageName(handle, 0, path, ref size))
        {
            CloseHandle(handle);
            return null;
        }
        var length = 512;
        var aumid = new StringBuilder(length);
        var info = new ProcessInfo(handle, path.ToString(),
            GetApplicationUserModelId(handle, ref length, aumid) == 0 ? aumid.ToString() : "");
        ProcessCache[pid] = info;
        return info;
    }

    private static WindowInfo? TryDescribe(IntPtr hwnd, HashSet<uint> seen)
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

        if (GetProcessInfo(pid, seen) is not { } process)
            return null;

        return new WindowInfo(hwnd, title, className, pid, process.ExecutablePath, process.AppUserModelId);
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

    /// <summary>Every running process: its id, its parent's id and its executable's file name.</summary>
    public static List<(uint ProcessId, uint ParentProcessId, string ExeFile)> GetProcessSnapshot()
    {
        var result = new List<(uint, uint, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return result;

        try
        {
            var entry = PROCESSENTRY32.Create();
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    result.Add((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                } while (Process32Next(snapshot, ref entry));
            }
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
