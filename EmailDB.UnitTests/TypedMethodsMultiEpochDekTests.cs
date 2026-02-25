using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: All typed methods (EmailContent, Folder, FolderTree,
/// Segment, WAL) encrypt/decrypt correctly with multi-epoch DEKs.
/// </summary>
public class TypedMethodsMultiEpochDekTests : IDisposable
{
    private readonly string _tempDir;
    private long _nextBlockId = 100_000_000;

    public TypedMethodsMultiEpochDekTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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

    private (CacheManager cacheManager, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider)
        CreateEncryptedCacheManager(
            byte activeEpoch,
            List<KeyStoreEntry> entries,
            EncryptionPolicy? policy = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        policy ??= EncryptionPolicy.Default;

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = entries
        };

        var provider = new KeyWrappingEncryptionProvider(keyStore, policy);
        var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        return (cacheManager, rawBlockManager, provider);
    }

    private (CacheManager cacheManager, KeyWrappingEncryptionProvider provider)
        CreateEncryptedCacheManagerWithRbm(
            RawBlockManager rawBlockManager,
            byte activeEpoch,
            List<KeyStoreEntry> entries,
            EncryptionPolicy? policy = null)
    {
        var serializer = new ProtobufBlockContentSerializer();
        policy ??= EncryptionPolicy.Default;

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = entries
        };

        var provider = new KeyWrappingEncryptionProvider(keyStore, policy);
        var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        return (cacheManager, provider);
    }

    /// <summary>
    /// Creates a block with a locally unique block ID to avoid collisions with
    /// the global singleton BlockIdGenerator and system block fixed IDs.
    /// </summary>
    private Block CreateBlock(BlockType type, byte[]? payload = null)
    {
        return new Block
        {
            Type = type,
            BlockId = Interlocked.Increment(ref _nextBlockId),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = payload ?? "test payload data"u8.ToArray()
        };
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public async Task WriteAndRead_EachEncryptableType_RoundTripsWithMultiEpochProvider(BlockType blockType)
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(
            activeEpoch: 0, entries: entries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = System.Text.Encoding.UTF8.GetBytes($"Payload for {blockType}");
        var block = CreateBlock(blockType, payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.True(block.IsEncrypted, $"{blockType} should be encrypted");
        Assert.Equal(0, block.KeyEpoch);

        cacheManager.InvalidateCache();

        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public async Task WriteAtEpoch0_ReadWithEpoch1Active_DecryptsCorrectly(BlockType blockType)
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"multi_epoch_{blockType}_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Write with epoch 0 active
        var entriesV0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entriesV0);

        var originalPayload = System.Text.Encoding.UTF8.GetBytes($"Epoch 0 {blockType} data");
        var block = CreateBlock(blockType, payload: (byte[])originalPayload.Clone());

        var writeResult = await cm0.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.Equal(0, block.KeyEpoch);
        long blockId = block.BlockId;

        cm0.Dispose();
        p0.Dispose();

        // Read with epoch 1 active but both DEKs available
        var entriesBoth = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cacheReader, providerBoth) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesBoth);
        using var _ = cacheReader;
        using var __ = providerBoth;

        var readResult = await cacheReader.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(originalPayload, readResult.Value.Payload);

        rbm.Dispose();
    }

    [Fact]
    public async Task KeyRotation_BlocksFromBothEpochs_AllReadableWithFullKeyStore()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"rotation_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Use only block types that have unique IDs (not system blocks with fixed IDs)
        var uniqueIdTypes = new[] { BlockType.EmailContent, BlockType.Folder, BlockType.Segment };

        // Phase 1: write one block of each type at epoch 0
        var entriesV0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entriesV0);

        var epoch0Blocks = new Dictionary<BlockType, (long BlockId, byte[] OriginalPayload)>();

        foreach (var bt in uniqueIdTypes)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes($"Epoch 0 content for {bt}");
            var block = CreateBlock(bt, payload: (byte[])payload.Clone());
            var result = await cm0.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.Equal(0, block.KeyEpoch);
            epoch0Blocks[bt] = (block.BlockId, payload);
        }

        cm0.Dispose();
        p0.Dispose();

        // Phase 2: write one block of each type at epoch 1
        var entriesV1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesV1);

        var epoch1Blocks = new Dictionary<BlockType, (long BlockId, byte[] OriginalPayload)>();

        foreach (var bt in uniqueIdTypes)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes($"Epoch 1 content for {bt}");
            var block = CreateBlock(bt, payload: (byte[])payload.Clone());
            var result = await cm1.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.Equal(1, block.KeyEpoch);
            epoch1Blocks[bt] = (block.BlockId, payload);
        }

        cm1.Dispose();
        p1.Dispose();

        // Phase 3: read all blocks with a provider that has both DEKs
        var entriesFull = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cacheReader, providerFull) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesFull);
        using var _ = cacheReader;
        using var __ = providerFull;

        // Verify epoch 0 blocks are still readable
        foreach (var bt in uniqueIdTypes)
        {
            var (blockId, expectedPayload) = epoch0Blocks[bt];
            cacheReader.InvalidateCache();
            var readResult = await cacheReader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read epoch 0 {bt} block");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        // Verify epoch 1 blocks are readable
        foreach (var bt in uniqueIdTypes)
        {
            var (blockId, expectedPayload) = epoch1Blocks[bt];
            cacheReader.InvalidateCache();
            var readResult = await cacheReader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read epoch 1 {bt} block");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        rbm.Dispose();
    }

    [Fact]
    public async Task ThreeEpochRotation_AllTypedBlocksDecryptCorrectly()
    {
        var deks = Enumerable.Range(0, 3).Select(_ => GenerateDek()).ToArray();
        var filePath = Path.Combine(_tempDir, $"three_epoch_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Use only block types with unique IDs for cross-epoch coexistence
        var uniqueIdTypes = new[] { BlockType.EmailContent, BlockType.Folder, BlockType.Segment };
        var allBlocks = new List<(BlockType Type, long BlockId, byte[] OriginalPayload, int Epoch)>();

        // Write blocks at each epoch
        for (int epoch = 0; epoch < 3; epoch++)
        {
            var entries = Enumerable.Range(0, epoch + 1).Select(e => new KeyStoreEntry
            {
                Epoch = e,
                DEK = deks[e],
                Timestamp = DateTime.UtcNow,
                Retired = e < epoch
            }).ToList();

            var (cm, p) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: (byte)epoch, entries: entries);

            foreach (var bt in uniqueIdTypes)
            {
                var payload = System.Text.Encoding.UTF8.GetBytes($"Epoch {epoch} {bt}");
                var block = CreateBlock(bt, payload: (byte[])payload.Clone());
                var result = await cm.WriteBlockAsync(block);
                Assert.True(result.IsSuccess);
                Assert.Equal(epoch, block.KeyEpoch);
                allBlocks.Add((bt, block.BlockId, payload, epoch));
            }

            cm.Dispose();
            p.Dispose();
        }

        // Read all blocks with a provider having all 3 DEKs
        var fullEntries = Enumerable.Range(0, 3).Select(e => new KeyStoreEntry
        {
            Epoch = e,
            DEK = deks[e],
            Timestamp = DateTime.UtcNow,
            Retired = e < 2
        }).ToList();
        var (cacheReader, providerFull) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 2, entries: fullEntries);
        using var _ = cacheReader;
        using var __ = providerFull;

        foreach (var (bt, blockId, expectedPayload, epoch) in allBlocks)
        {
            cacheReader.InvalidateCache();
            var readResult = await cacheReader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read {bt} block from epoch {epoch}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        rbm.Dispose();
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public async Task MissingDekForEpoch_ReadFails_ForEachBlockType(BlockType blockType)
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"missing_dek_{blockType}_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Write with epoch 0
        var entriesV0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entriesV0);

        var block = CreateBlock(blockType, payload: "sensitive data"u8.ToArray());
        var writeResult = await cm0.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        long blockId = block.BlockId;

        cm0.Dispose();
        p0.Dispose();

        // Read with only epoch 1 DEK (missing epoch 0)
        var entriesV1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cacheReader, providerV1) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesV1);
        using var _ = cacheReader;
        using var __ = providerV1;

        await Assert.ThrowsAsync<CryptographicException>(
            () => cacheReader.ReadBlockAsync(blockId));

        rbm.Dispose();
    }

    [Fact]
    public async Task MetadataBlocks_NotEncrypted_ReadableRegardlessOfEpoch()
    {
        var dek0 = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(
            activeEpoch: 0, entries: entries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = new byte[] { 0x10, 0x20, 0x30 };
        var block = CreateBlock(BlockType.Metadata, payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.False(block.IsEncrypted, "Metadata should not be encrypted under Default policy");
        Assert.Equal(0, block.KeyEpoch);

        cacheManager.InvalidateCache();

        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task MixedEncryptedAndUnencrypted_AllReadCorrectly()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var entriesFull = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(
            activeEpoch: 0, entries: entriesFull);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        // Write an encrypted EmailContent block
        var emailPayload = "encrypted email body"u8.ToArray();
        var emailBlock = CreateBlock(BlockType.EmailContent, payload: (byte[])emailPayload.Clone());
        var emailResult = await cacheManager.WriteBlockAsync(emailBlock);
        Assert.True(emailResult.IsSuccess);
        Assert.True(emailBlock.IsEncrypted);

        // Write an unencrypted Metadata block (not encrypted by Default policy)
        var metaPayload = new byte[] { 0xAA, 0xBB, 0xCC };
        var metaBlock = CreateBlock(BlockType.Metadata, payload: (byte[])metaPayload.Clone());
        var metaResult = await cacheManager.WriteBlockAsync(metaBlock);
        Assert.True(metaResult.IsSuccess);
        Assert.False(metaBlock.IsEncrypted);

        // Write an encrypted WAL block
        var walPayload = "encrypted WAL entry"u8.ToArray();
        var walBlock = CreateBlock(BlockType.WAL, payload: (byte[])walPayload.Clone());
        var walResult = await cacheManager.WriteBlockAsync(walBlock);
        Assert.True(walResult.IsSuccess);
        Assert.True(walBlock.IsEncrypted);

        cacheManager.InvalidateCache();

        // All three should read back correctly
        var readEmail = await cacheManager.ReadBlockAsync(emailBlock.BlockId);
        Assert.True(readEmail.IsSuccess);
        Assert.Equal(emailPayload, readEmail.Value.Payload);

        var readMeta = await cacheManager.ReadBlockAsync(metaBlock.BlockId);
        Assert.True(readMeta.IsSuccess);
        Assert.Equal(metaPayload, readMeta.Value.Payload);

        var readWal = await cacheManager.ReadBlockAsync(walBlock.BlockId);
        Assert.True(readWal.IsSuccess);
        Assert.Equal(walPayload, readWal.Value.Payload);
    }

    /// <summary>
    /// System blocks (WAL, FolderTree) have fixed block IDs, so rewriting at a new epoch
    /// replaces the previous version. Verify the latest version decrypts correctly.
    /// </summary>
    [Theory]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.FolderTree)]
    public async Task SystemBlocks_RewrittenAtNewEpoch_LatestVersionDecryptsCorrectly(BlockType blockType)
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"system_epoch_{blockType}_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Write block at epoch 0
        var entriesV0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entriesV0);

        var payload0 = System.Text.Encoding.UTF8.GetBytes($"Epoch 0 {blockType}");
        var block0 = CreateBlock(blockType, payload: (byte[])payload0.Clone());
        var result0 = await cm0.WriteBlockAsync(block0);
        Assert.True(result0.IsSuccess);
        Assert.Equal(0, block0.KeyEpoch);
        // EnsureBlockId() may change to a system block ID (e.g., 3 for WAL, 2 for FolderTree)
        long systemBlockId = block0.BlockId;

        cm0.Dispose();
        p0.Dispose();

        // Rewrite same system block at epoch 1 (simulating key rotation)
        var entriesV1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesV1);

        var payload1 = System.Text.Encoding.UTF8.GetBytes($"Epoch 1 {blockType}");
        var block1 = new Block
        {
            Type = blockType,
            BlockId = systemBlockId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])payload1.Clone()
        };
        var result1 = await cm1.WriteBlockAsync(block1);
        Assert.True(result1.IsSuccess);
        Assert.Equal(1, block1.KeyEpoch);

        cm1.Dispose();
        p1.Dispose();

        // Read with both DEKs — should get the latest (epoch 1) version
        var entriesFull = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cacheReader, providerFull) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesFull);
        using var _ = cacheReader;
        using var __ = providerFull;

        cacheReader.InvalidateCache();
        var read = await cacheReader.ReadBlockAsync(systemBlockId);
        Assert.True(read.IsSuccess, $"Failed to read epoch 1 {blockType}");
        Assert.Equal(payload1, read.Value.Payload);

        rbm.Dispose();
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    public async Task WriteAtEpoch0_WriteAtEpoch1_BothReadBack_ForEachType(BlockType blockType)
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"both_epochs_{blockType}_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Write block at epoch 0
        var entriesV0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entriesV0);

        var payload0 = System.Text.Encoding.UTF8.GetBytes($"Epoch 0 {blockType}");
        var block0 = CreateBlock(blockType, payload: (byte[])payload0.Clone());
        var result0 = await cm0.WriteBlockAsync(block0);
        Assert.True(result0.IsSuccess);
        long blockId0 = block0.BlockId;

        cm0.Dispose();
        p0.Dispose();

        // Write block at epoch 1
        var entriesV1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesV1);

        var payload1 = System.Text.Encoding.UTF8.GetBytes($"Epoch 1 {blockType}");
        var block1 = CreateBlock(blockType, payload: (byte[])payload1.Clone());
        var result1 = await cm1.WriteBlockAsync(block1);
        Assert.True(result1.IsSuccess);
        long blockId1 = block1.BlockId;

        cm1.Dispose();
        p1.Dispose();

        // Read both with full key store
        var entriesFull = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cacheReader, providerFull) = CreateEncryptedCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entriesFull);
        using var _ = cacheReader;
        using var __ = providerFull;

        cacheReader.InvalidateCache();
        var read0 = await cacheReader.ReadBlockAsync(blockId0);
        Assert.True(read0.IsSuccess, $"Failed to read epoch 0 {blockType}");
        Assert.Equal(payload0, read0.Value.Payload);

        cacheReader.InvalidateCache();
        var read1 = await cacheReader.ReadBlockAsync(blockId1);
        Assert.True(read1.IsSuccess, $"Failed to read epoch 1 {blockType}");
        Assert.Equal(payload1, read1.Value.Payload);

        rbm.Dispose();
    }

    [Fact]
    public async Task FullPolicy_AllBlockTypesExceptMetadata_EncryptAndDecryptAcrossEpochs()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(
            activeEpoch: 1, entries: entries, policy: EncryptionPolicy.Full);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        // Under Full policy, all types except Metadata should be encrypted
        var allTypesExceptMetadata = Enum.GetValues<BlockType>()
            .Where(bt => bt != BlockType.Metadata)
            .ToArray();

        var blocks = new List<(long BlockId, byte[] OriginalPayload, BlockType Type)>();

        foreach (var bt in allTypesExceptMetadata)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes($"Full policy {bt}");
            var block = CreateBlock(bt, payload: (byte[])payload.Clone());
            var result = await cacheManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.True(block.IsEncrypted, $"{bt} should be encrypted under Full policy");
            Assert.Equal(1, block.KeyEpoch);
            blocks.Add((block.BlockId, payload, bt));
        }

        cacheManager.InvalidateCache();

        foreach (var (blockId, expectedPayload, bt) in blocks)
        {
            var readResult = await cacheManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read {bt} under Full policy");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }
    }

    [Fact]
    public async Task LargePayloads_AllTypedMethods_RoundTripWithMultiEpochDeks()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(
            activeEpoch: 1, entries: entries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var encryptableTypes = new[] { BlockType.EmailContent, BlockType.Folder, BlockType.FolderTree, BlockType.Segment, BlockType.WAL };

        foreach (var bt in encryptableTypes)
        {
            // 32 KB payload
            var payload = new byte[32768];
            RandomNumberGenerator.Fill(payload);
            var block = CreateBlock(bt, payload: (byte[])payload.Clone());

            var writeResult = await cacheManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
            Assert.True(block.IsEncrypted);

            cacheManager.InvalidateCache();

            var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
            Assert.True(readResult.IsSuccess, $"Failed to read large {bt} block");
            Assert.Equal(payload, readResult.Value.Payload);
        }
    }
}
