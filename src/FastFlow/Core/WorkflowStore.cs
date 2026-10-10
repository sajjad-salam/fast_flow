using System.Text.Json;
using System.Text.Json.Serialization;
using FastFlow.Models;

namespace FastFlow.Core;

/// <summary>Persists workflows (workflows.json) and app settings (settings.json) as JSON in %APPDATA%\FastFlow.</summary>
internal sealed class WorkflowStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastFlow", "workflows.json");

    public string SettingsPath => Path.Combine(Path.GetDirectoryName(FilePath)!, "settings.json");

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

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
        }
        catch
        {
            // unreadable settings – fall back to the defaults
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
