namespace FastFlow.Models;

/// <summary>Options that apply to every workflow run, stored in %APPDATA%\FastFlow\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Close every open app window on every virtual desktop before a workflow is launched.</summary>
    public bool CloseOtherApps { get; set; } = true;

    /// <summary>
    /// With <see cref="CloseOtherApps"/>: minimize the other apps instead of closing them, and bring back the
    /// windows a workflow used last time, so switching between workflows doesn't relaunch anything.
    /// </summary>
    public bool MinimizeInsteadOfClose { get; set; }

    /// <summary>
    /// Apps that are left running when <see cref="CloseOtherApps"/> closes everything else,
    /// by executable name (e.g. "MEmu" or "MEmu.exe"; case-insensitive).
    /// </summary>
    public List<string> KeepOpenApps { get; set; } = new();

    /// <summary>Use a matching window that is already open instead of launching a new instance.</summary>
    public bool ReuseOpenWindows { get; set; } = true;
}
