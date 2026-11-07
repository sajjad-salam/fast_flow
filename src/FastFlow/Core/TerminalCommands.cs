namespace FastFlow.Core;

internal sealed record TerminalCommand(string ExecutablePath, string Arguments);

/// <summary>Finds the shell command hosted inside a Windows Terminal window.</summary>
internal static class TerminalCommands
{
    private static readonly HashSet<string> TerminalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal",
        "WindowsTerminalPreview",
    };

    private static readonly HashSet<string> CommandShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "powershell",
        "pwsh",
        "wsl",
        "wslhost",
    };

    public static bool TryGetHostedCommand(WindowInfo window, out TerminalCommand command)
    {
        command = null!;
        if (!TerminalHosts.Contains(window.ProcessName))
            return false;

        var candidate = WindowEnumerator.GetDescendantProcesses(window.ProcessId)
            .Select(process => new
            {
                Process = process,
                Name = Path.GetFileNameWithoutExtension(process.ExecutablePath),
                Arguments = WindowEnumerator.StripExecutable(process.CommandLine),
            })
            .Where(x => CommandShells.Contains(x.Name) && !string.IsNullOrWhiteSpace(x.Arguments))
            .Select(x => new
            {
                x.Process,
                x.Arguments,
                Score = Score(x.Name, x.Arguments, window.Title, x.Process.Depth),
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (candidate == null)
            return false;

        command = new TerminalCommand(candidate.Process.ExecutablePath, candidate.Arguments);
        return true;
    }

    private static int Score(string processName, string arguments, string windowTitle, int depth)
    {
        var score = Math.Max(0, 50 - depth);
        if (processName.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            score += 200;
            if (ContainsScriptExtension(arguments, ".cmd") || ContainsScriptExtension(arguments, ".bat"))
                score += 1000;
        }
        else if (processName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                 || processName.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Contains("-File", StringComparison.OrdinalIgnoreCase)
                || ContainsScriptExtension(arguments, ".ps1"))
                score += 900;
            else if (arguments.Contains("-Command", StringComparison.OrdinalIgnoreCase))
                score += 150;
        }
        else if (processName.StartsWith("wsl", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        var scriptName = GetScriptFileName(arguments);
        if (!string.IsNullOrEmpty(scriptName) && windowTitle.Contains(scriptName, StringComparison.OrdinalIgnoreCase))
            score += 300;
        return score;
    }

    private static bool ContainsScriptExtension(string value, string extension) =>
        value.Contains(extension, StringComparison.OrdinalIgnoreCase);

    private static string GetScriptFileName(string arguments)
    {
        var tokens = arguments.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Select(Path.GetFileName)
            .FirstOrDefault(token => token != null
                                     && (token.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                         || token.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                                         || token.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
               ?? "";
    }
}
