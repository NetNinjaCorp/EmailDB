using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that AesGcmBlockEncryptionProvider encrypts payloads to the format:
/// [Nonce (12 bytes)] [Ciphertext (N bytes)] [Auth Tag (16 bytes)]
/// </summary>
public class AesGcmEncryptPayloadFormatTests
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int OverheadSize = NonceSize + TagSize; // 28

    private static byte[] GenerateKey()
    {
        var key = new byte[AesGcmBlockEncryptionProvider.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Encrypt_OutputLength_IsPlaintextLengthPlusOverhead()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        Assert.Equal(plaintext.Length + OverheadSize, encrypted.Length);
    }

    [Fact]
    public void Encrypt_OutputFormat_StartsWithNonce12Bytes()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        long blockId = 42;

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

        // The first 12 bytes are the nonce
        var nonce = encrypted[..NonceSize];
        Assert.Equal(NonceSize, nonce.Length);

        // First 8 bytes of nonce should be the blockId in little-endian
        var extractedBlockId = BitConverter.ToInt64(nonce, 0);
        Assert.Equal(blockId, extractedBlockId);
    }

    [Fact]
    public void Encrypt_OutputFormat_CiphertextRegionMatchesPlaintextLength()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 10);

        // Ciphertext region sits between nonce and tag
        var ciphertextRegion = encrypted[NonceSize..^TagSize];
        Assert.Equal(plaintext.Length, ciphertextRegion.Length);
    }

    [Fact]
    public void Encrypt_OutputFormat_EndsWithAuthTag16Bytes()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 99);

        // Last 16 bytes are the auth tag
        var tag = encrypted[^TagSize..];
        Assert.Equal(TagSize, tag.Length);
    }

    [Fact]
    public void Encrypt_OutputFormat_ThreeRegionsAccountForEntireOutput()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[100];
        RandomNumberGenerator.Fill(plaintext);

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 7);

        // Verify the three regions:
        // Nonce = bytes [0..12)
        // Ciphertext = bytes [12..12+N) where N = plaintext.Length
        // AuthTag = bytes [12+N..12+N+16)
        Assert.Equal(NonceSize + plaintext.Length + TagSize, encrypted.Length);

        var nonce = encrypted[..NonceSize];
        var ciphertext = encrypted[NonceSize..(NonceSize + plaintext.Length)];
        var authTag = encrypted[(NonceSize + plaintext.Length)..];

        Assert.Equal(NonceSize, nonce.Length);
        Assert.Equal(plaintext.Length, ciphertext.Length);
        Assert.Equal(TagSize, authTag.Length);
    }

    [Fact]
    public void Encrypt_CiphertextRegion_IsDifferentFromPlaintext()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        var ciphertextRegion = encrypted[NonceSize..^TagSize];
        // Ciphertext should be different from plaintext (with overwhelming probability)
        Assert.NotEqual(plaintext, ciphertextRegion);
    }

    [Fact]
    public void Encrypt_EmptyPlaintext_ProducesOnlyNonceAndTag()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = Array.Empty<byte>();

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 0);

        // Empty plaintext → output should be exactly Nonce(12) + AuthTag(16) = 28 bytes
        Assert.Equal(OverheadSize, encrypted.Length);
    }

    [Fact]
    public void Encrypt_OutputFormat_DecryptableByExtractingRegions()
    {
        // This test validates the format by manually extracting nonce, ciphertext,
        // and tag from the output, then decrypting with raw AesGcm to prove correctness.
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 5);

        // Extract the three regions according to the format spec
        var nonce = encrypted[..NonceSize];
        var ciphertext = encrypted[NonceSize..^TagSize];
        var tag = encrypted[^TagSize..];

        // Decrypt using raw AesGcm to verify format correctness
        using var aes = new AesGcm(key, TagSize);
        var decrypted = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, decrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void OverheadBytes_Is28()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        Assert.Equal(OverheadSize, provider.OverheadBytes);
        Assert.Equal(28, provider.OverheadBytes);
    }

    [Fact]
    public void Nonce_RandomBytes_DifferAcrossEncryptionsWithSameBlockId()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x01, 0x02, 0x03 };
        long blockId = 100;

        var encrypted1 = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);
        var encrypted2 = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

        // First 8 bytes (blockId) should be identical
        var blockIdBytes1 = encrypted1[..8];
        var blockIdBytes2 = encrypted2[..8];
        Assert.Equal(blockIdBytes1, blockIdBytes2);

        // Last 4 bytes of nonce (random portion) should differ between calls
        var randomBytes1 = encrypted1[8..NonceSize];
        var randomBytes2 = encrypted2[8..NonceSize];
        Assert.NotEqual(randomBytes1, randomBytes2);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void Nonce_BlockIdBytes_MatchBlockIdLittleEndian(long blockId)
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0xAA, 0xBB };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

        // Extract first 8 bytes of nonce
        var nonceBlockIdBytes = encrypted[..8];
        var expectedBytes = BitConverter.GetBytes(blockId);
        Assert.Equal(expectedBytes, nonceBlockIdBytes);

        // Verify round-trip via BitConverter
        var extractedBlockId = BitConverter.ToInt64(encrypted, 0);
        Assert.Equal(blockId, extractedBlockId);
    }

    [Fact]
    public void Nonce_Structure_Is8ByteBlockIdPlus4ByteRandom()
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[] { 0x01 };
        long blockId = 0x0102030405060708;

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

        // Total nonce is 12 bytes
        var nonce = encrypted[..NonceSize];
        Assert.Equal(12, nonce.Length);

        // Bytes 0-7: blockId in little-endian
        Assert.Equal(0x08, nonce[0]); // least significant byte first
        Assert.Equal(0x07, nonce[1]);
        Assert.Equal(0x06, nonce[2]);
        Assert.Equal(0x05, nonce[3]);
        Assert.Equal(0x04, nonce[4]);
        Assert.Equal(0x03, nonce[5]);
        Assert.Equal(0x02, nonce[6]);
        Assert.Equal(0x01, nonce[7]);

        // Bytes 8-11: random (4 bytes exist)
        var randomPart = nonce[8..12];
        Assert.Equal(4, randomPart.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Encrypt_VariousPayloadSizes_MaintainsCorrectFormat(int size)
    {
        using var provider = new AesGcmBlockEncryptionProvider(GenerateKey());
        var plaintext = new byte[size];
        RandomNumberGenerator.Fill(plaintext);

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Verify total length
        Assert.Equal(NonceSize + size + TagSize, encrypted.Length);

        // Verify nonce region
        Assert.Equal(NonceSize, encrypted[..NonceSize].Length);
        // Verify ciphertext region
        Assert.Equal(size, encrypted[NonceSize..^TagSize].Length);
        // Verify tag region
        Assert.Equal(TagSize, encrypted[^TagSize..].Length);
    }
}
