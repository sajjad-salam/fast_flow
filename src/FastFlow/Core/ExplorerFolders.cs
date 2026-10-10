namespace FastFlow.Core;

/// <summary>Asks the shell which folder each open File Explorer window is showing.</summary>
internal static class ExplorerFolders
{
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
