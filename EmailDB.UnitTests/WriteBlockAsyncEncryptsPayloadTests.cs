using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: WriteBlockAsync encrypts payload with active DEK
/// and sets Flags bit 0 plus key epoch in bits 1-7.
/// </summary>
public class WriteBlockAsyncEncryptsPayloadTests : IDisposable
{
    private readonly string _tempDir;

    public WriteBlockAsyncEncryptsPayloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private static byte[] SamplePayload() => "Hello, encrypted world!"u8.ToArray();

    private (CacheManager cacheManager, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider) CreateEncryptedCacheManager(
        byte activeEpoch = 0,
        byte[]? dek = null,
        EncryptionPolicy? policy = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        dek ??= GenerateDek();
        policy ??= EncryptionPolicy.Default;

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = activeEpoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var provider = new KeyWrappingEncryptionProvider(keyStore, policy);
        var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        return (cacheManager, rawBlockManager, provider);
    }

    private Block CreateBlock(BlockType type = BlockType.EmailContent, byte[]? payload = null)
    {
        return new Block
        {
            Type = type,
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(type),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = payload ?? SamplePayload()
        };
    }

    [Fact]
    public async Task WriteBlockAsync_WithEncryptionProvider_SetsEncryptedFlag()
    {
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateBlock();

        var result = await cacheManager.WriteBlockAsync(block);

        Assert.True(result.IsSuccess);
        Assert.True(block.IsEncrypted, "Bit 0 (encrypted flag) should be set after WriteBlockAsync");
    }

    [Fact]
    public async Task WriteBlockAsync_WithEncryptionProvider_SetsCorrectKeyEpoch()
    {
        byte activeEpoch = 5;
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: activeEpoch);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateBlock();

        await cacheManager.WriteBlockAsync(block);

        Assert.Equal(activeEpoch, block.KeyEpoch);
    }

    [Fact]
    public async Task WriteBlockAsync_WithEncryptionProvider_FlagsBothEncryptedBitAndEpoch()
    {
        byte activeEpoch = 42;
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: activeEpoch);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateBlock();

        await cacheManager.WriteBlockAsync(block);

        byte expectedFlags = (byte)((activeEpoch << 1) | Block.FlagEncrypted);
        Assert.Equal(expectedFlags, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(activeEpoch, block.KeyEpoch);
    }

    [Fact]
    public async Task WriteBlockAsync_WithEncryptionProvider_PayloadIsEncrypted()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        // Payload should now be ciphertext, not the original plaintext
        Assert.NotEqual(originalPayload, block.Payload);
        // Ciphertext should include the AES-GCM overhead (12 nonce + 16 tag = 28 bytes)
        Assert.Equal(originalPayload.Length + 28, block.Payload.Length);
    }

    [Fact]
    public async Task WriteBlockAsync_EncryptedPayload_DecryptableWithActiveDek()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        // Decrypt with a standalone provider using the same DEK
        using var standalone = new AesGcmBlockEncryptionProvider(dek);
        var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);

        Assert.Equal(originalPayload, decrypted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(127)]
    public async Task WriteBlockAsync_VariousEpochs_StampsCorrectEpochInFlags(byte epoch)
    {
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: epoch);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateBlock();

        await cacheManager.WriteBlockAsync(block);

        Assert.True(block.IsEncrypted);
        Assert.Equal(epoch, block.KeyEpoch);
        byte expectedFlags = (byte)((epoch << 1) | Block.FlagEncrypted);
        Assert.Equal(expectedFlags, block.Flags);
    }

    [Fact]
    public async Task WriteBlockAsync_MetadataBlockType_NotEncrypted()
    {
        // Metadata is excluded from encryption by the Default policy
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = new byte[] { 0x10, 0x20, 0x30 };
        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        await cacheManager.WriteBlockAsync(block);

        // Metadata should NOT be encrypted under Default policy
        Assert.False(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
        Assert.Equal(originalPayload, block.Payload);
    }

    [Fact]
    public async Task WriteBlockAsync_WithoutEncryptionProvider_PayloadUnchanged()
    {
        // CacheManager with no encryption provider should leave blocks unmodified
        var filePath = Path.Combine(_tempDir, "no_encryption.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer);

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        Assert.False(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
        Assert.Equal(originalPayload, block.Payload);
    }

    [Fact]
    public async Task WriteBlockAsync_WithNullProvider_PayloadUnchanged()
    {
        // NullBlockEncryptionProvider (IsEnabled=false) should leave blocks unmodified
        var filePath = Path.Combine(_tempDir, "null_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer,
            encryptionProvider: NullBlockEncryptionProvider.Instance);

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        Assert.False(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
        Assert.Equal(originalPayload, block.Payload);
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public async Task WriteBlockAsync_EncryptableBlockTypes_AllGetEncrypted(BlockType blockType)
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var block = new Block
        {
            Type = blockType,
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(blockType),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])plaintext.Clone()
        };

        await cacheManager.WriteBlockAsync(block);

        Assert.True(block.IsEncrypted, $"{blockType} should be encrypted under Default policy");
        Assert.Equal(0, block.KeyEpoch); // epoch 0 is the default

        // Verify the ciphertext is decryptable
        using var standalone = new AesGcmBlockEncryptionProvider(dek);
        var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);
        Assert.Equal(plaintext, decrypted);
    }
}
