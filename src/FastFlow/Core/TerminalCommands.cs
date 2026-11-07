using System.Text.RegularExpressions;
using static FastFlow.Native.NativeMethods;

namespace FastFlow.Core;

/// <summary>A script (.cmd, .bat, .ps1) running in a console, and the shell command that starts it again.</summary>
internal sealed record HostedScript(
    uint ProcessId,
    uint ParentProcessId,
    string ShellPath,
    string ShellArguments,
    string ScriptPath)
{
    /// <summary>A double-clicked script runs in its own folder, so that is where it is started again.</summary>
    public string WorkingDirectory => Path.GetDirectoryName(ScriptPath) ?? "";
}

/// <summary>
/// Finds the scripts running in console windows (Windows Terminal or the classic console host), so a
/// captured terminal starts the same script again instead of an empty shell.
///
/// A script that was double-clicked runs in a cmd.exe whose parent is explorer.exe. With Windows
/// Terminal as the default terminal, its console is handed over to Terminal through COM, so cmd.exe
/// is *not* a child of WindowsTerminal.exe. The reliable link is the console itself: attaching to the
/// script's console gives its console window, and Windows Terminal makes its own window the owner of
/// that (hidden) window – one per tab.
/// </summary>
internal static class TerminalCommands
{
    private static readonly HashSet<string> TerminalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal",
        "WindowsTerminalPreview",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
    };

    private static readonly Regex QuotedScript = new(@"""([^""]+?\.(?:cmd|bat|ps1))""", RegexOptions.IgnoreCase);
    private static readonly Regex SingleQuotedScript = new(@"'([^']+?\.ps1)'", RegexOptions.IgnoreCase);
    private static readonly Regex BareScript = new(@"(?:^|\s)([^\s""']+?\.(?:cmd|bat|ps1))(?=\s|""|$)", RegexOptions.IgnoreCase);

    // Kept in a field so the delegate outlives the native registration.
    private static readonly ConsoleCtrlHandler IgnoreConsoleEvents = _ => true;

    public static bool IsWindowsTerminal(string processName) => TerminalHosts.Contains(processName);

    /// <summary>
    /// The scripts running in each top-level console window, keyed by that window: the Windows Terminal
    /// window (one script per tab) or the classic console window.
    /// </summary>
    public static Dictionary<IntPtr, List<HostedScript>> FindHostedScripts()
    {
        var result = new Dictionary<IntPtr, List<HostedScript>>();

        // Attaching to another console means leaving our own; FastFlow is a GUI app and normally has none.
        if (GetConsoleWindow() != IntPtr.Zero)
            return result;

        var scripts = new List<HostedScript>();
        foreach (var process in WindowEnumerator.GetProcessSnapshot())
        {
            if (!Shells.Contains(process.ExeFile))
                continue;
            var path = WindowEnumerator.GetProcessPath(process.ProcessId);
            var commandLine = WindowEnumerator.GetCommandLine(process.ProcessId);
            if (path != null && commandLine != null
                && TryParse(process.ProcessId, process.ParentProcessId, path, commandLine) is { } script)
                scripts.Add(script);
        }
        if (scripts.Count == 0)
            return result;

        var found = new List<(IntPtr Window, IntPtr Console, HostedScript Script)>();
        SetConsoleCtrlHandler(IgnoreConsoleEvents, true);
        try
        {
            foreach (var script in scripts)
            {
                if (!AttachConsole(script.ProcessId))
                    continue; // exited, elevated, or has no console
                try
                {
                    var console = GetConsoleWindow();
                    if (console == IntPtr.Zero)
                        continue;
                    var window = GetAncestor(console, GA_ROOTOWNER);
                    found.Add((window == IntPtr.Zero ? console : window, console, script));
                }
                finally
                {
                    FreeConsole();
                }
            }
        }
        finally
        {
            SetConsoleCtrlHandler(IgnoreConsoleEvents, false);
        }

        // A script that calls another script shares its console; keep only the outermost one per console.
        foreach (var group in found.GroupBy(f => f.Console))
        {
            var ids = group.Select(f => f.Script.ProcessId).ToHashSet();
            foreach (var (window, _, script) in group.Where(f => !ids.Contains(f.Script.ParentProcessId)))
            {
                if (!result.TryGetValue(window, out var list))
                    result[window] = list = new List<HostedScript>();
                list.Add(script);
            }
        }
        return result;
    }

    /// <summary>Arguments for wt.exe that open one new window with a tab per script.</summary>
    public static string BuildWindowsTerminalArguments(IEnumerable<HostedScript> scripts)
    {
        var tabs = scripts.Select(s =>
        {
            var directory = s.WorkingDirectory.EndsWith('\\') ? s.WorkingDirectory + "." : s.WorkingDirectory;
            // wt.exe uses ';' to separate its own commands.
            return $"-d \"{directory}\" \"{s.ShellPath}\" {s.ShellArguments}".Replace(";", "\\;");
        });
        return "-w new " + string.Join(" ; new-tab ", tabs);
    }

    /// <summary>The wt.exe app execution alias, or just "wt.exe" for the shell to resolve.</summary>
    public static string WindowsTerminalLauncher
    {
        get
        {
            var alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            return File.Exists(alias) ? alias : "wt.exe";
        }
    }

    private static HostedScript? TryParse(uint pid, uint parentPid, string shellPath, string commandLine)
    {
        var arguments = WindowEnumerator.StripExecutable(commandLine);
        var match = QuotedScript.Match(arguments);
        if (!match.Success)
            match = SingleQuotedScript.Match(arguments);
        if (!match.Success)
            match = BareScript.Match(arguments);
        if (!match.Success)
            return null;

        // Only scripts we can find again; relative paths depend on a working directory we cannot read.
        var scriptPath = match.Groups[1].Value;
        if (!Path.IsPathRooted(scriptPath) || !File.Exists(scriptPath))
            return null;

        var isCmd = Path.GetFileName(shellPath).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
        var isBatch = !scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        if (isCmd != isBatch)
            return null;

        string shellArguments;
        if (isCmd)
        {
            // Double-clicking runs: cmd.exe /c ""C:\path\script.cmd" "
            var keepOpen = Regex.IsMatch(arguments, @"(^|\s)/k(\s|$)", RegexOptions.IgnoreCase);
            var rest = arguments[(match.Index + match.Length)..].Trim().TrimEnd('"').Trim();
            // cmd strips the quotes around a path with special characters unless it follows another word.
            var call = scriptPath.IndexOfAny("&()^|<>@,;=%!".ToCharArray()) >= 0 ? "call " : "";
            shellArguments = $"{(keepOpen ? "/k" : "/c")} {call}\"{scriptPath}\"{(rest.Length > 0 ? " " + rest : "")}";
        }
        else
        {
            var noExit = arguments.Contains("-NoExit", StringComparison.OrdinalIgnoreCase);
            shellArguments = $"{(noExit ? "-NoExit " : "")}-ExecutionPolicy Bypass -File \"{scriptPath}\"";
        }

        return new HostedScript(pid, parentPid, shellPath, shellArguments, scriptPath);
    }
}
