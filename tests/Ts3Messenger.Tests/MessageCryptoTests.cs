using Ts3Messenger.Services;
using Xunit;

namespace Ts3Messenger.Tests;

public class KeyStoreTests
{
    [Fact]
    public void ExportPublicKey_is_base64()
    {
        var keys = new KeyStore();
        var raw = Convert.FromBase64String(keys.ExportPublicKeySpkiB64());
        Assert.True(raw.Length > 0);
    }
}
