using FastFlow.UI;

namespace FastFlow;

internal static class Program
{
    /// <summary>
    /// Usage:
    ///   FastFlow.exe                       open the manager window
    ///   FastFlow.exe --run "Workflow name" restore a workflow, then exit
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? runName = null;
        var runIndex = Array.FindIndex(args, a => a is "--run" or "-r" or "/run");
        if (runIndex >= 0 && runIndex + 1 < args.Length)
            runName = args[runIndex + 1];

        Application.Run(new MainForm(runName));
    }
}
