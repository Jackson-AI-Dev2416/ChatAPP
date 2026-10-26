namespace Ts3Messenger.Services;

/// <summary>Local file queued until the user presses Send.</summary>
public sealed class PendingOutgoingAttachment : IDisposable
{
    public required FileSendPayload Payload { get; init; }
    public bool IsSnapshot { get; init; }
    public string? MimeType { get; init; }

    public string DisplayName => Payload.TransferFileName;

    public void Dispose() => Payload.Dispose();
}
