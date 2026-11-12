using System.Text;

namespace FastFlow.Core;

/// <summary>
/// Appends the activity log, plus diagnostic detail that is too noisy for the window, to
/// %APPDATA%\FastFlow\fastflow.log so a run can be looked at afterwards. Rolls over to fastflow.old.log at 2 MB.
/// </summary>
internal static class FileLog
{
    private const long MaxSize = 2 * 1024 * 1024;
    private static readonly object Lock = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastFlow", "fastflow.log");

    /// <summary>A line that is also shown in the Activity panel.</summary>
    public static void Info(string message) => Write("INFO ", message);

    /// <summary>Diagnostic detail written only to the file.</summary>
    public static void Debug(string message) => Write("DEBUG", message);

    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break a run
        }
    }
}
