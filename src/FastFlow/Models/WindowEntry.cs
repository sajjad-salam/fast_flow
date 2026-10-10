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

    /// <summary>Browser windows: URLs of the open tabs, reopened in a new window when the workflow runs.</summary>
    [Browsable(false)]
    public List<string> Tabs { get; set; } = new();

    /// <summary>Space-separated tab URLs, so they can be edited in the grid.</summary>
    [JsonIgnore, DisplayName("Tabs")]
    public string TabUrls
    {
        get => string.Join(" ", Tabs);
        set => Tabs = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

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
