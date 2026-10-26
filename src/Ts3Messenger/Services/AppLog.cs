using System.IO;

namespace Ts3Messenger.Services;

/// <summary>File log under %AppData%\Ts3Messenger\logs (for support; not shown in UI).</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ts3Messenger", "logs");

    private static string LogFilePath =>
        Path.Combine(LogDirectory, $"messenger-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string context, Exception ex) =>
        Write("ERROR", $"{context}{Environment.NewLine}{ex}");

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Never break the app because logging failed.
        }
    }
}
