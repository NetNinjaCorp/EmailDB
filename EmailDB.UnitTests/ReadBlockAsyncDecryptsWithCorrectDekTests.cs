using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: ReadBlockAsync reads key epoch from Flags
/// (bits 1-7) and decrypts with correct DEK via KeyWrappingEncryptionProvider.
/// </summary>
public class ReadBlockAsyncDecryptsWithCorrectDekTests : IDisposable
{
    private readonly string _tempDir;

    public ReadBlockAsyncDecryptsWithCorrectDekTests()
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
        List<KeyStoreEntry>? entries = null,
        EncryptionPolicy? policy = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        policy ??= EncryptionPolicy.Default;

        KeyStoreContent keyStore;
        if (entries != null)
        {
            keyStore = new KeyStoreContent
            {
                ActiveEpoch = activeEpoch,
                Entries = entries
            };
        }
        else
        {
            keyStore = new KeyStoreContent
            {
                ActiveEpoch = activeEpoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = activeEpoch, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
        }

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
    public async Task ReadBlockAsync_DecryptsPayloadToOriginalPlaintext()
    {
        var dek = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(entries: entries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        // Write (encrypts the payload and sets Flags)
        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Clear cache so ReadBlockAsync must re-read from disk
        cacheManager.InvalidateCache();

        // Read back using block ID (should decrypt using the key epoch from Flags)
        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ReadBlockAsync_ReadsKeyEpochFromFlagsAndDecryptsWithCorrectDek()
    {
        byte epoch = 5;
        var dek = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: epoch, entries: entries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.Equal(epoch, block.KeyEpoch);

        cacheManager.InvalidateCache();

        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Verify the decrypted payload matches the original
        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ReadBlockAsync_MultiEpoch_DecryptsWithEpochFromFlags()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        // Write a block with epoch 0 active
        var filePath = Path.Combine(_tempDir, $"multi_epoch_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        var keyStoreEpoch0 = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var providerEpoch0 = new KeyWrappingEncryptionProvider(keyStoreEpoch0, EncryptionPolicy.Default);
        var cacheManager0 = new CacheManager(rawBlockManager, serializer, encryptionProvider: providerEpoch0);

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager0.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.Equal(0, block.KeyEpoch);
        long blockId = block.BlockId;

        cacheManager0.Dispose();
        providerEpoch0.Dispose();

        // Now create a reader with both epochs loaded but epoch 1 active
        var keyStoreBoth = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerBoth = new KeyWrappingEncryptionProvider(keyStoreBoth, EncryptionPolicy.Default);
        using var cacheManagerReader = new CacheManager(rawBlockManager, serializer, encryptionProvider: providerBoth);

        var readResult = await cacheManagerReader.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);

        // Block was written with epoch 0, reader's active epoch is 1,
        // but ReadBlockAsync reads the epoch from Flags and uses the correct DEK
        Assert.Equal(originalPayload, readResult.Value.Payload);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task ReadBlockAsync_WrongEpochDek_ThrowsCryptographicException()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"wrong_epoch_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        // Write with epoch 0
        var keyStoreWrite = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerWrite = new KeyWrappingEncryptionProvider(keyStoreWrite, EncryptionPolicy.Default);
        var cacheManagerWrite = new CacheManager(rawBlockManager, serializer, encryptionProvider: providerWrite);

        var block = CreateBlock(payload: (byte[])SamplePayload().Clone());

        var writeResult = await cacheManagerWrite.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        long blockId = block.BlockId;

        cacheManagerWrite.Dispose();

        // Read with only epoch 1 (no epoch 0 DEK available)
        var keyStoreRead = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerRead = new KeyWrappingEncryptionProvider(keyStoreRead, EncryptionPolicy.Default);
        using var cacheManagerRead = new CacheManager(rawBlockManager, serializer, encryptionProvider: providerRead);

        // Should throw because the epoch 0 DEK is not in the reader's key store
        await Assert.ThrowsAsync<CryptographicException>(
            () => cacheManagerRead.ReadBlockAsync(blockId));

        rawBlockManager.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(127)]
    public async Task ReadBlockAsync_VariousEpochs_DecryptsCorrectly(byte epoch)
    {
        var dek = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: epoch, entries: entries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        cacheManager.InvalidateCache();

        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ReadBlockAsync_UnencryptedBlock_PayloadUnchanged()
    {
        // With no encryption provider, ReadBlockAsync should return raw payload
        var filePath = Path.Combine(_tempDir, $"no_enc_{Guid.NewGuid():N}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer);

        var originalPayload = SamplePayload();
        var block = CreateBlock(payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        cacheManager.InvalidateCache();

        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        Assert.False(readResult.Value.IsEncrypted);
        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ReadBlockAsync_MetadataBlock_NotDecrypted()
    {
        // Metadata is not encrypted under Default policy, so read should return as-is
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = new byte[] { 0x10, 0x20, 0x30 };
        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 999,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        cacheManager.InvalidateCache();

        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        Assert.False(readResult.Value.IsEncrypted);
        Assert.Equal(originalPayload, readResult.Value.Payload);
    }
}
