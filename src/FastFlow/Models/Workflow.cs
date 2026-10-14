namespace FastFlow.Models;

/// <summary>A saved workspace: every window to launch, which virtual desktop it lives on, and where.</summary>
public sealed class Workflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Number of virtual desktops that existed when the workflow was captured.</summary>
    public int DesktopCount { get; set; }

    /// <summary>Desktop names at capture time, indexed by desktop position (informational).</summary>
    public List<string> DesktopNames { get; set; } = new();

    public List<WindowEntry> Windows { get; set; } = new();

    /// <summary>Global keyboard shortcut that runs this workflow, e.g. "Ctrl+Alt+1" (empty = none).</summary>
    public string Shortcut { get; set; } = "";

    public const int FinishDesktopAutomatic = 0;
    public const int FinishDesktopStay = -1;

    /// <summary>
    /// Virtual desktop (1-based) to switch to once every app is open. <see cref="FinishDesktopAutomatic"/>
    /// switches only when all windows are on one desktop; <see cref="FinishDesktopStay"/> never switches.
    /// </summary>
    public int FinishDesktop { get; set; } = FinishDesktopAutomatic;

    public Hotkey? GetHotkey() => Hotkey.TryParse(Shortcut, out var hotkey) ? hotkey : null;

    public override string ToString() =>
        GetHotkey() is { } hotkey
            ? $"{Name}  [{hotkey}]  ({Windows.Count} windows)"
            : $"{Name}  ({Windows.Count} windows)";
}
