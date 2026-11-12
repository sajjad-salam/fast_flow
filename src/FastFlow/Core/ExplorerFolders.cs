namespace FastFlow.Core;

/// <summary>Asks the shell which folder each open File Explorer window is showing.</summary>
internal static class ExplorerFolders
{
    /// <summary>
    /// Every folder open in each File Explorer window: on Windows 11 a window can hold several tabs, and its title
    /// only shows the selected one.
    /// </summary>
    public static Dictionary<IntPtr, List<string>> GetOpenTabs()
    {
        var result = new Dictionary<IntPtr, List<string>>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return result;

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    var hwnd = new IntPtr((long)window.HWND);
                    string path = window.Document.Folder.Self.Path;
                    if (string.IsNullOrEmpty(path))
                        continue;
                    if (!result.TryGetValue(hwnd, out var tabs))
                        result[hwnd] = tabs = new List<string>();
                    tabs.Add(path);
                }
                catch
                {
                    // Internet Explorer windows or windows that are closing
                }
            }
        }
        catch
        {
            // shell not available
        }
        return result;
    }

    /// <summary>The folder an Explorer entry opens: its argument, without quotes or a trailing backslash.</summary>
    public static string FolderOf(string arguments) => Normalize(arguments.Trim().Trim('"'));

    public static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

    public static Dictionary<IntPtr, string> GetOpenFolders()
    {
        var result = new Dictionary<IntPtr, string>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return result;

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    var hwnd = new IntPtr((long)window.HWND);
                    // Self.Path is a file-system path, or "::{CLSID}" for virtual folders such as This PC.
                    string path = window.Document.Folder.Self.Path;
                    if (!string.IsNullOrEmpty(path))
                        result[hwnd] = path;
                }
                catch
                {
                    // Internet Explorer windows or windows that are closing
                }
            }
        }
        catch
        {
            // shell not available
        }
        return result;
    }
}
