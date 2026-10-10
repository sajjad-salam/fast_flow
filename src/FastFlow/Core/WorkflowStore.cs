using System.Text.Json;
using System.Text.Json.Serialization;
using FastFlow.Models;

namespace FastFlow.Core;

/// <summary>Persists workflows as JSON in %APPDATA%\FastFlow\workflows.json.</summary>
internal sealed class WorkflowStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastFlow", "workflows.json");

    public List<Workflow> Load()
    {
        if (!File.Exists(FilePath))
            return new List<Workflow>();
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<Workflow>>(json, Options) ?? new List<Workflow>();
    }

    public void Save(IEnumerable<Workflow> workflows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(workflows, Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}
