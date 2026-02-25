using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class KeyWrappingEncryptionProviderPolicyTests
{
    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private static KeyStoreContent CreateSingleEpochKeyStore()
    {
        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };
    }

    [Fact]
    public void ShouldEncrypt_DelegatesToDefaultPolicy()
    {
        using var provider = new KeyWrappingEncryptionProvider(CreateSingleEpochKeyStore(), EncryptionPolicy.Default);

        Assert.True(provider.ShouldEncrypt(BlockType.EmailContent));
        Assert.True(provider.ShouldEncrypt(BlockType.Folder));
        Assert.True(provider.ShouldEncrypt(BlockType.FolderTree));
        Assert.True(provider.ShouldEncrypt(BlockType.Segment));
        Assert.True(provider.ShouldEncrypt(BlockType.WAL));

        Assert.False(provider.ShouldEncrypt(BlockType.Metadata));
        Assert.False(provider.ShouldEncrypt(BlockType.Cleanup));
        Assert.False(provider.ShouldEncrypt(BlockType.BTreeLeaf));
        Assert.False(provider.ShouldEncrypt(BlockType.BTreeInternal));
        Assert.False(provider.ShouldEncrypt(BlockType.IndexRoot));
    }

    [Fact]
    public void ShouldEncrypt_DelegatesToFullPolicy()
    {
        using var provider = new KeyWrappingEncryptionProvider(CreateSingleEpochKeyStore(), EncryptionPolicy.Full);

        foreach (var bt in Enum.GetValues<BlockType>().Where(bt => bt != BlockType.Metadata))
        {
            Assert.True(provider.ShouldEncrypt(bt), $"Full policy should encrypt {bt}");
        }

        Assert.False(provider.ShouldEncrypt(BlockType.Metadata));
    }

    [Fact]
    public void ShouldEncrypt_DelegatesToCustomPolicy()
    {
        var customPolicy = new EncryptionPolicy(new[] { BlockType.WAL, BlockType.Cleanup });
        using var provider = new KeyWrappingEncryptionProvider(CreateSingleEpochKeyStore(), customPolicy);

        Assert.True(provider.ShouldEncrypt(BlockType.WAL));
        Assert.True(provider.ShouldEncrypt(BlockType.Cleanup));

        Assert.False(provider.ShouldEncrypt(BlockType.EmailContent));
        Assert.False(provider.ShouldEncrypt(BlockType.Metadata));
        Assert.False(provider.ShouldEncrypt(BlockType.Folder));
    }

    [Fact]
    public void ShouldEncrypt_EmptyPolicy_ReturnsFalseForAll()
    {
        var emptyPolicy = new EncryptionPolicy(Array.Empty<BlockType>());
        using var provider = new KeyWrappingEncryptionProvider(CreateSingleEpochKeyStore(), emptyPolicy);

        foreach (var bt in Enum.GetValues<BlockType>())
        {
            Assert.False(provider.ShouldEncrypt(bt), $"Empty policy should not encrypt {bt}");
        }
    }

    [Fact]
    public void ShouldEncrypt_MatchesPolicyForAllBlockTypes()
    {
        var policy = EncryptionPolicy.Default;
        using var provider = new KeyWrappingEncryptionProvider(CreateSingleEpochKeyStore(), policy);

        foreach (var bt in Enum.GetValues<BlockType>())
        {
            Assert.Equal(policy.ShouldEncrypt(bt), provider.ShouldEncrypt(bt));
        }
    }

    [Fact]
    public void ShouldEncrypt_SameKeyStoreDifferentPolicies_DifferentResults()
    {
        var keyStore = CreateSingleEpochKeyStore();
        using var defaultProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        using var fullProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);

        // Cleanup is encrypted by Full but not Default
        Assert.False(defaultProvider.ShouldEncrypt(BlockType.Cleanup));
        Assert.True(fullProvider.ShouldEncrypt(BlockType.Cleanup));

        // BTreeLeaf is encrypted by Full but not Default
        Assert.False(defaultProvider.ShouldEncrypt(BlockType.BTreeLeaf));
        Assert.True(fullProvider.ShouldEncrypt(BlockType.BTreeLeaf));

        // Both agree on EmailContent (true) and Metadata (false)
        Assert.True(defaultProvider.ShouldEncrypt(BlockType.EmailContent));
        Assert.True(fullProvider.ShouldEncrypt(BlockType.EmailContent));
        Assert.False(defaultProvider.ShouldEncrypt(BlockType.Metadata));
        Assert.False(fullProvider.ShouldEncrypt(BlockType.Metadata));
    }

    [Fact]
    public void ShouldEncrypt_ConsistentAcrossMultipleCalls()
    {
        using var provider = new KeyWrappingEncryptionProvider(CreateSingleEpochKeyStore(), EncryptionPolicy.Default);

        for (int i = 0; i < 100; i++)
        {
            Assert.True(provider.ShouldEncrypt(BlockType.EmailContent));
            Assert.False(provider.ShouldEncrypt(BlockType.Metadata));
        }
    }

    [Fact]
    public void Encrypt_RespectsPolicy_EncryptsOnlyWhenPolicySaysYes()
    {
        var keyStore = CreateSingleEpochKeyStore();
        // Custom policy that only encrypts WAL
        var policy = new EncryptionPolicy(new[] { BlockType.WAL });
        using var provider = new KeyWrappingEncryptionProvider(keyStore, policy);

        var plaintext = "test data"u8.ToArray();

        // WAL should be marked for encryption
        Assert.True(provider.ShouldEncrypt(BlockType.WAL));
        var ciphertext = provider.Encrypt(plaintext, BlockType.WAL, blockId: 1);
        Assert.NotEqual(plaintext, ciphertext);

        // EmailContent should NOT be marked for encryption
        Assert.False(provider.ShouldEncrypt(BlockType.EmailContent));
    }

    [Fact]
    public void ShouldEncrypt_WithMultipleEpochs_PolicyStillApplies()
    {
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 2, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Policy decisions are independent of key epochs
        Assert.True(provider.ShouldEncrypt(BlockType.EmailContent));
        Assert.False(provider.ShouldEncrypt(BlockType.Metadata));
    }
}
