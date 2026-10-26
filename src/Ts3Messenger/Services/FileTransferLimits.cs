namespace Ts3Messenger.Services;

internal static class FileTransferLimits
{
    /// <summary>100 GiB (1024-based), aligned with backend <c>chat_max_file_size_bytes</c>.</summary>
    public const long MaxFileSizeBytes = 107_374_182_400L;

    public const string MaxFileSizeLabel = "100GB";
}
