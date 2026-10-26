using System.Text.Json;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

/// <summary>Parses the server's file-transfer dict (REST list and WebSocket events share one shape).</summary>
internal static class TransferJson
{
    public static RoomFileTransfer Parse(JsonElement t, string? fallbackRoomId = null)
    {
        var size = t.TryGetProperty("file_size_bytes", out var sz) ? FileSizeFormatter.ReadBytesFromJson(sz) : 0L;
        var fileName = Str(t, "file_name") ?? "file";
        var items = new List<TransferItem>();
        if (t.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in itemsEl.EnumerateArray())
            {
                var path = Str(it, "path");
                if (string.IsNullOrEmpty(path))
                    continue;
                var itemSize = it.TryGetProperty("size", out var isz) ? FileSizeFormatter.ReadBytesFromJson(isz) : 0L;
                items.Add(new TransferItem { Path = path, Size = itemSize });
            }
        }

        if (items.Count == 0)
            items.Add(new TransferItem { Path = fileName, Size = size });

        var recipients = new List<TransferRecipientRow>();
        if (t.TryGetProperty("recipients", out var recips) && recips.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in recips.EnumerateArray())
            {
                recipients.Add(new TransferRecipientRow
                {
                    UserId = ChatIds.NormalizeUserId(Str(r, "user_id") ?? ""),
                    Status = Str(r, "status") ?? "",
                    FailureReason = Str(r, "failure_reason"),
                    Delivered = !string.IsNullOrEmpty(Str(r, "delivered_at")),
                    BytesDone = r.TryGetProperty("bytes_done", out var bd) ? FileSizeFormatter.ReadBytesFromJson(bd) : 0L,
                });
            }
        }

        DateTime? createdAt = null;
        if (t.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String)
            createdAt = ChatTimestamps.ParseJsonInstant(ca);

        return new RoomFileTransfer
        {
            Id = Str(t, "id") ?? "",
            RoomId = ChatIds.NormalizeRoomId(Str(t, "room_id") ?? fallbackRoomId ?? ""),
            SenderUserId = ChatIds.NormalizeUserId(Str(t, "sender_user_id") ?? ""),
            FileName = fileName,
            FileSizeBytes = size,
            MimeType = Str(t, "mime_type"),
            Status = Str(t, "status") ?? "",
            CreatedAt = createdAt,
            Caption = Str(t, "caption"),
            ItemCount = t.TryGetProperty("item_count", out var ic) && ic.ValueKind == JsonValueKind.Number
                ? Math.Max(1, ic.GetInt32())
                : items.Count,
            IsFolder = t.TryGetProperty("is_folder", out var f) && f.ValueKind == JsonValueKind.True,
            Items = items,
            Recipients = recipients,
        };
    }

    /// <summary>
    /// Direct chats have one recipient; its row carries the real per-person state
    /// (paused, failure reason, delivery) for both the sender and the recipient view.
    /// </summary>
    public static TransferRecipientRow? RowForViewer(RoomFileTransfer t, string viewerUserId, bool isMine)
    {
        if (isMine)
            return t.Recipients.FirstOrDefault();
        var me = ChatIds.NormalizeUserId(viewerUserId);
        return t.Recipients.FirstOrDefault(r => r.UserId == me);
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
