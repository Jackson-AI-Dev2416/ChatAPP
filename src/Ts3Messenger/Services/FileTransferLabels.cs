using System.IO;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

internal static class FileTransferLabels
{
    public const int MaxTitleNameLength = 28;
    public const string FileNotFoundReason = "file_not_found";
    public const string FileChangedReason = "file_changed";

    public static string Title(ChatMessage m) =>
        Title(m.FileName, m.ItemCount, m.IsFolder);

    public static string Title(string name, int itemCount, bool isFolder) =>
        $"{(isFolder ? "📁" : "📎")} {TitleText(name, itemCount, isFolder)}";

    public static string TitleText(ChatMessage m) =>
        TitleText(m.FileName, m.ItemCount, m.IsFolder);

    public static string TitleText(string name, int itemCount, bool isFolder)
    {
        var shortName = TruncateName(name, MaxTitleNameLength);
        if (isFolder)
            return $"{shortName} ({itemCount} {(itemCount == 1 ? "file" : "files")})";
        if (itemCount > 1)
            return $"{shortName} and {itemCount - 1} more";
        return shortName;
    }

    /// <summary>Shortens long names in the middle so the extension stays visible.</summary>
    public static string TruncateName(string name, int max)
    {
        if (string.IsNullOrEmpty(name) || name.Length <= max)
            return name;
        var ext = Path.GetExtension(name);
        if (ext.Length == 0 || ext.Length > 8)
            return name[..(max - 1)] + "…";
        var keep = max - ext.Length - 1;
        return name[..keep] + "…" + ext;
    }

    public static string Status(ChatMessage m, bool senderOnline)
    {
        var pct = Percent(m);
        return m.FileStatus switch
        {
            "pending" => "waiting...",
            "accepted" or "in_progress" when !m.IsMine && !senderOnline => "Sender offline",
            "accepted" or "in_progress" when m.IsMine => $"sending... {pct:0}%",
            "accepted" or "in_progress" => $"receiving... {pct:0}%",
            "paused" => $"paused {pct:0}%",
            "completed" => "received",
            "cancelled" or "rejected" or "expired" => "canceled",
            "failed" when m.FailureReason == FileNotFoundReason => "File not found",
            "failed" when m.FailureReason == FileChangedReason => "File changed",
            "failed" => "failed",
            _ => "",
        };
    }

    public static double Percent(ChatMessage m)
    {
        if (m.FilePercent > 0)
            return Math.Min(100, m.FilePercent);
        if (m.FileSizeBytes <= 0)
            return 0;
        return Math.Min(100, m.BytesDone * 100.0 / m.FileSizeBytes);
    }

    public static string ItemsTooltip(ChatMessage m, int maxLines = 20)
    {
        if (m.Items.Count <= 1)
            return m.FileName;
        var lines = m.Items.Take(maxLines).Select(i => i.Path).ToList();
        if (m.Items.Count > maxLines)
            lines.Add($"and {m.Items.Count - maxLines} more");
        return string.Join("\n", lines);
    }
}
