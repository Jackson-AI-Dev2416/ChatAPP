using System.IO;

namespace Ts3Messenger.Services;

internal static class SnapshotTransferHelper
{
    public const string FileNamePrefix = "snapshot-";
    public const string MimeType = "image/png";

    public static bool IsSnapshotFileName(string fileName) =>
        fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsImageMime(string? mime) =>
        !string.IsNullOrEmpty(mime) && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsImageTransfer(string? fileName, string? mime, bool isSnapshot) =>
        isSnapshot || IsImageMime(mime) || (fileName != null && IsImageFileName(fileName));

    public static string NewSnapshotFileName() => $"{FileNamePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png";
}
