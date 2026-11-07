using System.IO;
using System.Security.Cryptography;

namespace Ts3Messenger.Services;

/// <summary>Device key used only to register a public key so this user appears in chat directory.</summary>
public sealed class KeyStore
{
    private readonly ECDiffieHellman _ecdh;

    public KeyStore()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ProtectedKeyStorage.TryLoad(ref _ecdh);
    }

    public string ExportPublicKeySpkiB64() =>
        Convert.ToBase64String(_ecdh.ExportSubjectPublicKeyInfo());

    public void Persist() => ProtectedKeyStorage.Save(_ecdh);
}

internal static class ProtectedKeyStorage
{
    private static string KeyFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ts3Messenger",
            "device.key");

    public static void TryLoad(ref ECDiffieHellman ecdh)
    {
        if (!File.Exists(KeyFilePath)) return;
        try
        {
            var protectedBytes = File.ReadAllBytes(KeyFilePath);
            var pkcs8 = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        catch
        {
            // new key
        }
    }

    public static void Save(ECDiffieHellman ecdh)
    {
        var dir = Path.GetDirectoryName(KeyFilePath)!;
        Directory.CreateDirectory(dir);
        var pkcs8 = ecdh.ExportPkcs8PrivateKey();
        var protectedBytes = ProtectedData.Protect(pkcs8, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFilePath, protectedBytes);
    }
}
