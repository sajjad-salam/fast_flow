using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FastFlow.Models;

/// <summary>One application window inside a workflow.</summary>
public sealed class WindowEntry
{
    /// <summary>Unchecked entries are kept in the workflow but skipped when it runs.</summary>
    public bool Enabled { get; set; } = true;

    public string ProcessName { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Full path of the executable to start.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Command-line arguments used when launching (editable).</summary>
    public string Arguments { get; set; } = "";

    /// <summary>For packaged (Store / MSIX) apps: AppUserModelID used to launch and to recognise the window.</summary>
    public string AppUserModelId { get; set; } = "";

    public string WindowClass { get; set; } = "";

    /// <summary>Zero-based virtual desktop index (Desktop 1 = 0).</summary>
    [Browsable(false)]
    public int DesktopIndex { get; set; }

    /// <summary>One-based desktop number, as shown in Task View. Used by the editor grid.</summary>
    [JsonIgnore, DisplayName("Desktop")]
    public int DesktopNumber
    {
        get => DesktopIndex + 1;
        set => DesktopIndex = Math.Max(0, value - 1);
    }

    /// <summary>Restored (normal) window bounds, in workspace coordinates as reported by GetWindowPlacement.</summary>
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public WindowState State { get; set; } = WindowState.Normal;

    [JsonIgnore, Browsable(false)]
    public bool IsPackaged => !string.IsNullOrEmpty(AppUserModelId);
}

public enum WindowState
{
    Normal,
    Minimized,
    Maximized,
}
