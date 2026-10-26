using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ts3Messenger.Services;

/// <summary>E1: AES-GCM room messages; keys from ECDH P-256 + room id.</summary>
public sealed class MessageCryptoService
{
    private const string Prefix = "E1:";
    private readonly KeyStore _keys;

    public MessageCryptoService(KeyStore keys) => _keys = keys;

    public string ExportPublicKeyB64() => _keys.ExportPublicKeySpkiB64();

    public byte[] DeriveDirectRoomKey(string roomId, string peerUserId, string peerPublicKeyB64)
    {
        roomId = ChatIds.NormalizeRoomId(roomId);
        using var ecdh = _keys.CreateEcdh();
        var peerBytes = Convert.FromBase64String(peerPublicKeyB64);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerBytes, out _);
        var shared = ecdh.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
        return HMACSHA256.HashData(shared, Encoding.UTF8.GetBytes($"ts3-room:{roomId}"));
    }

    public byte[] GenerateRoomKey() => RandomNumberGenerator.GetBytes(32);

    public string WrapRoomKeyForUser(byte[] roomKey, string peerPublicKeyB64)
    {
        using var ecdh = _keys.CreateEcdh();
        var peerBytes = Convert.FromBase64String(peerPublicKeyB64);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerBytes, out _);
        var wrapKey = ecdh.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[roomKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(wrapKey, 16);
        aes.Encrypt(nonce, roomKey, cipher, tag);
        var packed = new byte[1 + nonce.Length + cipher.Length + tag.Length];
        packed[0] = 1;
        Buffer.BlockCopy(nonce, 0, packed, 1, nonce.Length);
        Buffer.BlockCopy(cipher, 0, packed, 1 + nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, packed, 1 + nonce.Length + cipher.Length, tag.Length);
        return Convert.ToBase64String(packed);
    }

    public byte[] UnwrapRoomKey(string envelopeB64)
    {
        var packed = Convert.FromBase64String(envelopeB64);
        if (packed.Length < 29 || packed[0] != 1) throw new CryptographicException("Bad envelope");
        var nonce = packed.AsSpan(1, 12);
        var cipherLen = packed.Length - 1 - 12 - 16;
        var cipher = packed.AsSpan(13, cipherLen);
        var tag = packed.AsSpan(13 + cipherLen, 16);
        using var ecdh = _keys.CreateEcdh();
        // Envelope was created for us; we need sender's public key to derive - store room keys locally after unwrap via API flow.
        // For direct rooms we use DeriveDirectRoomKey instead.
        throw new NotSupportedException("Use direct derivation or cached room key");
    }

    public string Encrypt(string plain, byte[] roomKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(roomKey, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var blob = new byte[1 + nonce.Length + cipher.Length + tag.Length];
        blob[0] = 1;
        Buffer.BlockCopy(nonce, 0, blob, 1, nonce.Length);
        Buffer.BlockCopy(cipher, 0, blob, 1 + nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, 1 + nonce.Length + cipher.Length, tag.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Decrypt(string body, byte[] roomKey)
    {
        if (!body.StartsWith(Prefix, StringComparison.Ordinal)) return body;
        var packed = Convert.FromBase64String(body[Prefix.Length..]);
        if (packed[0] != 1) throw new CryptographicException("Bad version");
        var nonce = packed.AsSpan(1, 12);
        var tag = packed.AsSpan(packed.Length - 16, 16);
        var cipher = packed.AsSpan(13, packed.Length - 13 - 16);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(roomKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

public sealed class KeyStore
{
    private readonly ECDiffieHellman _ecdh;

    public KeyStore()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ProtectedKeyStorage.TryLoad(ref _ecdh);
    }

    public ECDiffieHellman CreateEcdh()
    {
        var copy = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        copy.ImportPkcs8PrivateKey(_ecdh.ExportPkcs8PrivateKey(), out _);
        return copy;
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
