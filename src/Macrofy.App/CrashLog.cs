using System.IO;

namespace Macrofy.App;

// Appends unhandled exceptions to %AppData%/Macrofy/log.txt so field crashes leave a trace.
public static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Macrofy", "log.txt");

    public static void Write(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
