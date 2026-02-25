using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class KeyWrappingEncryptionProviderImplementsInterfaceTests
{
    private static KeyWrappingEncryptionProvider CreateProvider()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        return new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
    }

    [Fact]
    public void KeyWrappingEncryptionProvider_Implements_IBlockEncryptionProvider()
    {
        using var provider = CreateProvider();
        Assert.IsAssignableFrom<IBlockEncryptionProvider>(provider);
    }

    [Fact]
    public void KeyWrappingEncryptionProvider_Implements_IDisposable()
    {
        using var provider = CreateProvider();
        Assert.IsAssignableFrom<IDisposable>(provider);
    }

    [Fact]
    public void IsEnabled_ReturnsTrue()
    {
        using var provider = CreateProvider();
        Assert.True(provider.IsEnabled);
    }

    [Fact]
    public void OverheadBytes_Returns28()
    {
        using var provider = CreateProvider();
        Assert.Equal(28, provider.OverheadBytes);
    }

    [Fact]
    public void ShouldEncrypt_DelegatesToPolicy()
    {
        using var provider = CreateProvider();
        // Default policy encrypts EmailContent
        Assert.True(provider.ShouldEncrypt(BlockType.EmailContent));
        // Default policy does not encrypt Metadata
        Assert.False(provider.ShouldEncrypt(BlockType.Metadata));
    }

    [Fact]
    public void CanBeUsedAsInterfaceReference()
    {
        IBlockEncryptionProvider provider = CreateProvider();
        Assert.True(provider.IsEnabled);
        Assert.Equal(28, provider.OverheadBytes);
        provider.Dispose();
    }
}
