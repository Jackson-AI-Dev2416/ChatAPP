using System.Security.Cryptography;
using Ts3Messenger.Services;
using Xunit;

namespace Ts3Messenger.Tests;

public class MessageCryptoTests
{
    [Fact]
    public void Encrypt_decrypt_roundtrip_direct_room_key()
    {
        var aliceKeys = new KeyStore();
        var bobKeys = new KeyStore();
        var alice = new MessageCryptoService(aliceKeys);
        var bob = new MessageCryptoService(bobKeys);
        const string roomId = "11111111-1111-1111-1111-111111111111";
        var bobPub = bob.ExportPublicKeyB64();
        var alicePub = alice.ExportPublicKeyB64();
        var aliceRoomKey = alice.DeriveDirectRoomKey(roomId, "peer", bobPub);
        var bobRoomKey = bob.DeriveDirectRoomKey(roomId, "peer", alicePub);
        const string plain = "안녕 TS3 🔒";
        var cipher = alice.Encrypt(plain, aliceRoomKey);
        Assert.StartsWith("E1:", cipher);
        var back = bob.Decrypt(cipher, bobRoomKey);
        Assert.Equal(plain, back);
    }
}
