using FastFlow.UI;
using static FastFlow.Native.NativeMethods;

namespace FastFlow;

internal static class Program
{
    /// <summary>
    /// Usage:
    ///   FastFlow.exe                       open the manager window
    ///   FastFlow.exe --tray                start hidden in the tray (workflow shortcuts stay active)
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
        var startInTray = args.Any(a => a is "--tray" or "/tray");

        if (runName != null)
        {
            Application.Run(new MainForm(runName));
            return;
        }

        // Only one manager may own the global shortcuts; a second start just shows the running one.
        using var mutex = new Mutex(true, $"FastFlow.Manager.{Environment.UserName}", out var isFirst);
        if (!isFirst)
        {
            if (!startInTray)
                PostMessage(HWND_BROADCAST, MainForm.ShowManagerMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        Application.Run(new MainForm(startInTray: startInTray));
    }
}
