using System.IO;

namespace Ts3Messenger.Services;

internal static class SnapshotPaths
{
    public static string ReceivedDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ts3Messenger",
                "snapshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ReceivedFilePath(string transferId, string fileName)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "snapshot.png";
        return Path.Combine(ReceivedDirectory, $"{transferId}_{safe}");
    }

    public static string? TryFindPersistedPath(string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            return null;
        var prefix = transferId + "_";
        try
        {
            return Directory
                .EnumerateFiles(ReceivedDirectory)
                .FirstOrDefault(p =>
                    Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Default filename for recipient "Save as" on received snapshots (local time).</summary>
    public static string ReceivedSaveAsFileName(DateTime receivedAtUtc)
    {
        var local = receivedAtUtc.Kind == DateTimeKind.Utc
            ? receivedAtUtc.ToLocalTime()
            : receivedAtUtc;
        return $"snapshot_{local:yyyyMMdd_HHmmss}.png";
    }

    public static string CreateTempCapturePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Ts3Messenger", "captures");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, SnapshotTransferHelper.NewSnapshotFileName());
    }

    /// <summary>
    /// Copies a sent/received snapshot into LocalAppData so previews survive temp-file cleanup and app restarts.
    /// </summary>
    public static string? TryPersistTransferFile(string transferId, string fileName, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return null;
        var dest = ReceivedFilePath(transferId, fileName);
        try
        {
            if (!File.Exists(dest))
                File.Copy(sourcePath, dest);
            return dest;
        }
        catch
        {
            return File.Exists(dest) ? dest : null;
        }
    }
}
