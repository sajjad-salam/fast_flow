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

    public override string ToString() => $"{Name}  ({Windows.Count} windows)";
}
