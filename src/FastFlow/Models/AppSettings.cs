namespace FastFlow.Models;

/// <summary>Options that apply to every workflow run, stored in %APPDATA%\FastFlow\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Close every open app window on every virtual desktop before a workflow is launched.</summary>
    public bool CloseOtherApps { get; set; } = true;

    /// <summary>Use a matching window that is already open instead of launching a new instance.</summary>
    public bool ReuseOpenWindows { get; set; } = true;
}
