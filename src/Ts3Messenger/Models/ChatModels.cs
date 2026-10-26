using Ts3Messenger.Services;

namespace Ts3Messenger.Models;

public sealed class UserMe
{
    public string id { get; set; } = "";
    public string login_id { get; set; } = "";
    public string display_name { get; set; } = "";
    public string role { get; set; } = "";
}

public sealed class RoomItem
{
    public string Id { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string? Name { get; set; }
    public int UnreadCount { get; set; }
    public string? LastMessagePreview { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public bool LastMessageMine { get; set; }
    public string? CreatedByUserId { get; set; }
}

public sealed class RoomMemberItem
{
    public string UserId { get; set; } = "";
    public string LoginId { get; set; } = "";
}

public sealed class ContactItem
{
    public string Id { get; set; } = "";
    public string LoginId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Online { get; set; }
    public string? LanIp { get; set; }
    public string? DirectRoomId { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LastMessagePreview { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public bool LastMessageMine { get; set; }
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string SenderUserId { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime SentAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public string PlainText { get; set; } = "";
    public bool IsMine { get; set; }
    public MessageDeliveryState DeliveryState { get; set; } = MessageDeliveryState.Pending;
    public bool IsFileTransfer { get; set; }
    public string? FileTransferId { get; set; }
    public string FileName { get; set; } = "";
    public string? MimeType { get; set; }
    public bool IsSnapshot { get; set; }
    public string? LocalImagePath { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileStatus { get; set; } = "";
    public double FilePercent { get; set; }
    public long BytesDone { get; set; }
    public string? Caption { get; set; }
    public int ItemCount { get; set; } = 1;
    public bool IsFolder { get; set; }
    public List<TransferItem> Items { get; set; } = new();
    public List<string> RecipientUserIds { get; set; } = new();
    public bool PeerDelivered { get; set; }
    public string? FailureReason { get; set; }

    public bool IsImageTransfer =>
        IsFileTransfer
        && ItemCount <= 1
        && !IsFolder
        && SnapshotTransferHelper.IsImageTransfer(FileName, MimeType, IsSnapshot);
}

public sealed class TransferItem
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
}

public enum MessageDeliveryState
{
    Pending,
    Sent,
    Delivered,
    Read,
}

public sealed class TransferRecipientRow
{
    public string UserId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FailureReason { get; set; }
    public bool Delivered { get; set; }
    public long BytesDone { get; set; }
}

public sealed class RoomFileTransfer
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string SenderUserId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? MimeType { get; set; }
    public string Status { get; set; } = "";
    public DateTime? CreatedAt { get; set; }
    public string? Caption { get; set; }
    public int ItemCount { get; set; } = 1;
    public bool IsFolder { get; set; }
    public List<TransferItem> Items { get; set; } = new();
    public List<TransferRecipientRow> Recipients { get; set; } = new();
}

public sealed class FileTransferInfo
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = "";
}

public sealed class FileProgress
{
    public string TransferId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long TotalBytes { get; set; }
    public long SentBytes { get; set; }
    public double Percent => TotalBytes <= 0 ? 0 : (double)SentBytes / TotalBytes * 100.0;
    public string Label => $"{FileSizeFormatter.Format(SentBytes)} / {FileSizeFormatter.Format(TotalBytes)}";
}
