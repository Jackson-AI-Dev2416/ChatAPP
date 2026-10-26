using System.Text;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

internal static class ChatHistoryClipboard
{
    public static string FormatLine(ChatMessage message, string myDisplayName, string peerDisplayName)
    {
        var who = message.IsMine ? myDisplayName : peerDisplayName;
        var when = ChatTimestamps.Format(message.SentAt);
        if (message.IsFileTransfer)
            return $"[{when}] {who}: [file] {message.FileName} ({message.FileSizeBytes} bytes)";

        var body = (message.PlainText ?? "").Replace("\r\n", "\n").Trim();
        return $"[{when}] {who}: {body}";
    }

    public static string FormatMany(
        IEnumerable<ChatMessage> messages,
        string myDisplayName,
        string peerDisplayName)
    {
        var sb = new StringBuilder();
        foreach (var m in messages.OrderBy(x => x.SentAt))
        {
            sb.AppendLine(FormatLine(m, myDisplayName, peerDisplayName));
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatReplyQuote(ChatMessage message, string myDisplayName, string peerDisplayName)
    {
        var who = message.IsMine ? myDisplayName : peerDisplayName;
        var when = ChatTimestamps.Format(message.SentAt)[11..]; // HH:mm:ss in company TZ
        var body = (message.PlainText ?? "").Replace("\r\n", " ").Trim();
        if (body.Length > 120)
            body = body[..120] + "…";
        return $"↩ {who} ({when})\n{body}";
    }

    /// <summary>Message text only, without the "[time] name:" prefix used for a full transcript.</summary>
    public static string FormatBodies(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages.OrderBy(x => x.SentAt))
        {
            var body = m.IsFileTransfer
                ? (string.IsNullOrWhiteSpace(m.Caption) ? m.FileName : m.Caption.Trim())
                : (m.PlainText ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (body.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(body);
        }

        return sb.ToString();
    }

    /// <summary>
    /// A reply is stored as "↩ name (time)\nquoted text\n\nnew text".
    /// </summary>
    public static bool TrySplitReply(string? plain, out string quote, out string reply)
    {
        quote = "";
        reply = "";
        var text = (plain ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (!text.StartsWith("↩ ", StringComparison.Ordinal))
            return false;
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (split <= 0 || !text[..split].Contains('\n'))
            return false;
        quote = text[..split].TrimEnd();
        reply = text[(split + 2)..].TrimStart('\n');
        return true;
    }
}
