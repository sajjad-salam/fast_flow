using System.Text.Json;
using FastFlow.Models;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>
/// Remembers which window each workflow entry was placed in (%APPDATA%\FastFlow\windows.json), so that when
/// other apps are minimized instead of closed, running the workflow again brings back the very same windows –
/// including browser windows with their tabs – instead of launching new ones.
///
/// Stored in a file rather than in memory because "FastFlow.exe --run" restores a workflow from its own process.
/// </summary>
internal sealed class WindowMemory
{
    private sealed record Remembered(long Handle, uint ProcessId);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastFlow", "windows.json");

    private readonly Dictionary<string, Remembered> _windows;

    private WindowMemory(Dictionary<string, Remembered> windows) => _windows = windows;

    public static WindowMemory Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<Dictionary<string, Remembered>>(File.ReadAllText(FilePath)) is { } windows)
                return new WindowMemory(windows);
        }
        catch
        {
            // unreadable – start over
        }
        return new WindowMemory(new Dictionary<string, Remembered>());
    }

    /// <summary>The window this entry was placed in last time, if it is still open and owned by the same process.</summary>
    public IntPtr? Find(Workflow workflow, WindowEntry entry)
    {
        if (!_windows.TryGetValue(Key(workflow, entry), out var remembered))
            return null;
        var hwnd = new IntPtr(remembered.Handle);
        if (!IsWindow(hwnd))
            return null;
        // Window handles are recycled; the process id tells whether it is still the same window.
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == remembered.ProcessId ? hwnd : null;
    }

    /// <summary>Records the windows a run of <paramref name="workflow"/> placed, and forgets windows that are gone.</summary>
    public void Save(Workflow workflow, IEnumerable<(WindowEntry Entry, IntPtr Handle)> placed)
    {
        var prefix = workflow.Id + "/";
        foreach (var key in _windows.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _windows.Remove(key);
        foreach (var (entry, handle) in placed)
        {
            GetWindowThreadProcessId(handle, out var pid);
            _windows[Key(workflow, entry)] = new Remembered(handle.ToInt64(), pid);
        }
        foreach (var key in _windows.Where(p => !IsWindow(new IntPtr(p.Value.Handle))).Select(p => p.Key).ToList())
            _windows.Remove(key);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_windows));
        }
        catch
        {
            // not worth failing a run over; the windows are just matched by app next time
        }
    }

    // Entries have no id of their own; their position in the workflow identifies them. A re-capture may shift
    // them, which is why callers also check that the remembered window belongs to the entry's app.
    private static string Key(Workflow workflow, WindowEntry entry) =>
        $"{workflow.Id}/{workflow.Windows.IndexOf(entry)}";
}
