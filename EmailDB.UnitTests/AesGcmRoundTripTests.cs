using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that AesGcmBlockEncryptionProvider round-trip encrypt/decrypt
/// produces identical plaintext for all payload sizes and block types.
/// </summary>
public class AesGcmRoundTripTests
{
    private static byte[] GenerateKey()
    {
        var key = new byte[AesGcmBlockEncryptionProvider.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void RoundTrip_SmallPayload_ProducesIdenticalPlaintext()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 1);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_EmptyPayload_ProducesIdenticalPlaintext()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = Array.Empty<byte>();

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 0);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 0);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void RoundTrip_VariousPayloadSizes_ProducesIdenticalPlaintext(int size)
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[size];
        RandomNumberGenerator.Fill(plaintext);

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 42);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 42);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.Cleanup)]
    [InlineData(BlockType.BTreeLeaf)]
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    [InlineData(BlockType.EmailContent)]
    public void RoundTrip_AllBlockTypes_ProducesIdenticalPlaintext(BlockType blockType)
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"

        var encrypted = provider.Encrypt(plaintext, blockType, blockId: 7);
        var decrypted = provider.Decrypt(encrypted, blockType, blockId: 7);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_MultipleEncryptions_SameKey_AllProduceIdenticalPlaintext()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        for (int i = 0; i < 100; i++)
        {
            var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: i);
            var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: i);
            Assert.Equal(plaintext, decrypted);
        }
    }

    [Fact]
    public void RoundTrip_SameBlockId_DifferentEncryptions_AllDecryptCorrectly()
    {
        // Even though nonce randomness differs, each ciphertext decrypts to the same plaintext
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        long blockId = 99;

        var encrypted1 = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);
        var encrypted2 = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

        // Ciphertexts should differ (due to random nonce bytes)
        Assert.NotEqual(encrypted1, encrypted2);

        // But both decrypt to the same plaintext
        var decrypted1 = provider.Decrypt(encrypted1, BlockType.EmailContent, blockId);
        var decrypted2 = provider.Decrypt(encrypted2, BlockType.EmailContent, blockId);

        Assert.Equal(plaintext, decrypted1);
        Assert.Equal(plaintext, decrypted2);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void RoundTrip_VariousBlockIds_ProducesIdenticalPlaintext(long blockId)
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_LargePayload_ProducesIdenticalPlaintext()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[1_000_000]; // 1 MB
        RandomNumberGenerator.Fill(plaintext);

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 1);

        Assert.Equal(plaintext, decrypted);
    }
}
