using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ts3Messenger.Services;

public sealed class SavedSessionCookies
{
    public string ServerBaseUrl { get; set; } = "";
    public string SessionCookie { get; set; } = "";
    public string CsrfCookie { get; set; } = "";
}

public static class SessionCookieStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ts3Messenger",
            "session.bin");

    public static void Save(string serverBaseUrl, string sessionValue, string csrfValue)
    {
        if (string.IsNullOrWhiteSpace(sessionValue)) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SavedSessionCookies
            {
                ServerBaseUrl = serverBaseUrl.TrimEnd('/'),
                SessionCookie = sessionValue,
                CsrfCookie = csrfValue,
            });
        var protectedBytes = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(FilePath, protectedBytes);
    }

    public static SavedSessionCookies? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(FilePath);
            var json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SavedSessionCookies>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

public static class CredentialProtector
{
    public static string? Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plain),
            null,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedB64)
    {
        if (string.IsNullOrWhiteSpace(protectedB64)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedB64), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
