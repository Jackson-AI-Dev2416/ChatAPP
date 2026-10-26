using System.IO;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

internal static class FileTransferViewStatus
{
    public static string ForViewer(
        string transferStatus,
        IEnumerable<TransferRecipientRow>? recipients,
        string viewerUserId,
        bool isMine)
    {
        if (isMine)
            return transferStatus;
        var norm = ChatIds.NormalizeUserId(viewerUserId);
        foreach (var r in recipients ?? Array.Empty<TransferRecipientRow>())
        {
            if (ChatIds.NormalizeUserId(r.UserId) == norm)
                return r.Status;
        }

        return transferStatus;
    }

    public static string WithLocalFileOverride(string status, string? localPath)
    {
        if (status != "completed")
            return status;
        if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            return "completed";
        return status;
    }
}
