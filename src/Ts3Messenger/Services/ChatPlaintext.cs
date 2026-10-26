namespace Ts3Messenger.Services;

/// <summary>Plan A: server stores plaintext; legacy E1 rows are not recoverable.</summary>
public static class ChatPlaintext
{
    public const string LegacyClientEncrypted = "[Encrypted message from legacy client]";

    public static string Display(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        if (body.TrimStart().StartsWith("E1:", StringComparison.Ordinal))
            return LegacyClientEncrypted;
        return body;
    }
}
