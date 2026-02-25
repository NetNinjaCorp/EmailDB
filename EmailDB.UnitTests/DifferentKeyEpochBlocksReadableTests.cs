using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: Blocks written with different key epochs are all readable.
/// Exercises end-to-end CacheManager write→rotate→write→read-all scenarios to ensure
/// the KeyWrappingEncryptionProvider's DEK lookup decrypts every epoch correctly.
/// </summary>
public class DifferentKeyEpochBlocksReadableTests : IDisposable
{
    private readonly string _tempDir;
    private long _nextBlockId = 200_000_000;

    public DifferentKeyEpochBlocksReadableTests()
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

    private (CacheManager cacheManager, KeyWrappingEncryptionProvider provider)
        CreateCacheManagerWithRbm(
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

    [Fact]
    public async Task FourEpochRotation_AllBlocksReadableWithFullKeyStore()
    {
        var deks = Enumerable.Range(0, 4).Select(_ => GenerateDek()).ToArray();
        var filePath = Path.Combine(_tempDir, $"four_epoch_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        var written = new List<(long BlockId, byte[] OriginalPayload, int Epoch)>();

        // Write blocks at epochs 0 through 3, rotating keys between each
        for (int epoch = 0; epoch < 4; epoch++)
        {
            var entries = Enumerable.Range(0, epoch + 1).Select(e => new KeyStoreEntry
            {
                Epoch = e,
                DEK = deks[e],
                Timestamp = DateTime.UtcNow,
                Retired = e < epoch
            }).ToList();

            var (cm, p) = CreateCacheManagerWithRbm(rbm, activeEpoch: (byte)epoch, entries: entries);

            var payload = System.Text.Encoding.UTF8.GetBytes($"Block written at epoch {epoch}");
            var block = CreateBlock(BlockType.EmailContent, payload: (byte[])payload.Clone());
            var result = await cm.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.Equal(epoch, block.KeyEpoch);
            written.Add((block.BlockId, payload, epoch));

            cm.Dispose();
            p.Dispose();
        }

        // Read all 4 blocks with a single CacheManager that has all DEKs
        var fullEntries = Enumerable.Range(0, 4).Select(e => new KeyStoreEntry
        {
            Epoch = e,
            DEK = deks[e],
            Timestamp = DateTime.UtcNow,
            Retired = e < 3
        }).ToList();
        var (reader, providerFull) = CreateCacheManagerWithRbm(rbm, activeEpoch: 3, entries: fullEntries);
        using var _ = reader;
        using var __ = providerFull;

        foreach (var (blockId, expectedPayload, epoch) in written)
        {
            reader.InvalidateCache();
            var readResult = await reader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read block from epoch {epoch}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        rbm.Dispose();
    }

    [Fact]
    public async Task MultipleBlocksPerEpoch_AllReadableAfterRotation()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"multi_blocks_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        var written = new List<(long BlockId, byte[] OriginalPayload, int Epoch)>();

        // Write 3 blocks at epoch 0
        var entries0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entries0);

        for (int i = 0; i < 3; i++)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes($"Epoch 0 block {i}");
            var block = CreateBlock(BlockType.EmailContent, payload: (byte[])payload.Clone());
            var result = await cm0.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            written.Add((block.BlockId, payload, 0));
        }

        cm0.Dispose();
        p0.Dispose();

        // Write 3 blocks at epoch 1
        var entries1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entries1);

        for (int i = 0; i < 3; i++)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes($"Epoch 1 block {i}");
            var block = CreateBlock(BlockType.EmailContent, payload: (byte[])payload.Clone());
            var result = await cm1.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            written.Add((block.BlockId, payload, 1));
        }

        cm1.Dispose();
        p1.Dispose();

        // Write 3 blocks at epoch 2
        var entries2 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm2, p2) = CreateCacheManagerWithRbm(rbm, activeEpoch: 2, entries: entries2);

        for (int i = 0; i < 3; i++)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes($"Epoch 2 block {i}");
            var block = CreateBlock(BlockType.EmailContent, payload: (byte[])payload.Clone());
            var result = await cm2.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            written.Add((block.BlockId, payload, 2));
        }

        cm2.Dispose();
        p2.Dispose();

        // Read all 9 blocks with full key store
        var fullEntries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (reader, providerFull) = CreateCacheManagerWithRbm(rbm, activeEpoch: 2, entries: fullEntries);
        using var _ = reader;
        using var __ = providerFull;

        foreach (var (blockId, expectedPayload, epoch) in written)
        {
            reader.InvalidateCache();
            var readResult = await reader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read epoch {epoch} block {blockId}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        rbm.Dispose();
    }

    [Fact]
    public async Task DifferentBlockTypesAcrossEpochs_AllReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"mixed_types_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        var written = new List<(long BlockId, byte[] OriginalPayload, BlockType Type, int Epoch)>();

        // Epoch 0: EmailContent and Folder
        var entries0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entries0);

        var emailPayload = "Email at epoch 0"u8.ToArray();
        var emailBlock = CreateBlock(BlockType.EmailContent, payload: (byte[])emailPayload.Clone());
        Assert.True((await cm0.WriteBlockAsync(emailBlock)).IsSuccess);
        written.Add((emailBlock.BlockId, emailPayload, BlockType.EmailContent, 0));

        var folderPayload = "Folder at epoch 0"u8.ToArray();
        var folderBlock = CreateBlock(BlockType.Folder, payload: (byte[])folderPayload.Clone());
        Assert.True((await cm0.WriteBlockAsync(folderBlock)).IsSuccess);
        written.Add((folderBlock.BlockId, folderPayload, BlockType.Folder, 0));

        cm0.Dispose();
        p0.Dispose();

        // Epoch 1: Segment and EmailContent
        var entries1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entries1);

        var segPayload = "Segment at epoch 1"u8.ToArray();
        var segBlock = CreateBlock(BlockType.Segment, payload: (byte[])segPayload.Clone());
        Assert.True((await cm1.WriteBlockAsync(segBlock)).IsSuccess);
        written.Add((segBlock.BlockId, segPayload, BlockType.Segment, 1));

        var email1Payload = "Email at epoch 1"u8.ToArray();
        var email1Block = CreateBlock(BlockType.EmailContent, payload: (byte[])email1Payload.Clone());
        Assert.True((await cm1.WriteBlockAsync(email1Block)).IsSuccess);
        written.Add((email1Block.BlockId, email1Payload, BlockType.EmailContent, 1));

        cm1.Dispose();
        p1.Dispose();

        // Read all with full key store
        var fullEntries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (reader, providerFull) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: fullEntries);
        using var _ = reader;
        using var __ = providerFull;

        foreach (var (blockId, expectedPayload, blockType, epoch) in written)
        {
            reader.InvalidateCache();
            var readResult = await reader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read {blockType} from epoch {epoch}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        rbm.Dispose();
    }

    [Fact]
    public async Task InterleavedWriteAndRead_EachEpochBlockReadableImmediately()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"interleaved_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Write at epoch 0
        var entries0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entries0);

        var payload0 = "Epoch 0 data"u8.ToArray();
        var block0 = CreateBlock(BlockType.EmailContent, payload: (byte[])payload0.Clone());
        Assert.True((await cm0.WriteBlockAsync(block0)).IsSuccess);
        long blockId0 = block0.BlockId;

        // Read epoch 0 block back immediately
        cm0.InvalidateCache();
        var read0 = await cm0.ReadBlockAsync(blockId0);
        Assert.True(read0.IsSuccess);
        Assert.Equal(payload0, read0.Value.Payload);

        cm0.Dispose();
        p0.Dispose();

        // Rotate to epoch 1 and write
        var entries1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entries1);

        var payload1 = "Epoch 1 data"u8.ToArray();
        var block1 = CreateBlock(BlockType.EmailContent, payload: (byte[])payload1.Clone());
        Assert.True((await cm1.WriteBlockAsync(block1)).IsSuccess);
        long blockId1 = block1.BlockId;

        // Read both blocks: epoch 0 block is still readable with new provider
        cm1.InvalidateCache();
        var readOld = await cm1.ReadBlockAsync(blockId0);
        Assert.True(readOld.IsSuccess, "Epoch 0 block should be readable after rotation to epoch 1");
        Assert.Equal(payload0, readOld.Value.Payload);

        cm1.InvalidateCache();
        var readNew = await cm1.ReadBlockAsync(blockId1);
        Assert.True(readNew.IsSuccess, "Epoch 1 block should be readable at epoch 1");
        Assert.Equal(payload1, readNew.Value.Payload);

        cm1.Dispose();
        p1.Dispose();
        rbm.Dispose();
    }

    [Fact]
    public async Task LargePayloads_AcrossEpochs_AllDecryptCorrectly()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"large_payloads_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        var written = new List<(long BlockId, byte[] OriginalPayload, int Epoch)>();

        // Write a 64 KB block at epoch 0
        var entries0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entries0);

        var largePayload0 = new byte[65536];
        RandomNumberGenerator.Fill(largePayload0);
        var block0 = CreateBlock(BlockType.EmailContent, payload: (byte[])largePayload0.Clone());
        Assert.True((await cm0.WriteBlockAsync(block0)).IsSuccess);
        written.Add((block0.BlockId, largePayload0, 0));

        cm0.Dispose();
        p0.Dispose();

        // Write a 64 KB block at epoch 1
        var entries1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entries1);

        var largePayload1 = new byte[65536];
        RandomNumberGenerator.Fill(largePayload1);
        var block1 = CreateBlock(BlockType.EmailContent, payload: (byte[])largePayload1.Clone());
        Assert.True((await cm1.WriteBlockAsync(block1)).IsSuccess);
        written.Add((block1.BlockId, largePayload1, 1));

        cm1.Dispose();
        p1.Dispose();

        // Read all with full key store
        var fullEntries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (reader, providerFull) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: fullEntries);
        using var _ = reader;
        using var __ = providerFull;

        foreach (var (blockId, expectedPayload, epoch) in written)
        {
            reader.InvalidateCache();
            var readResult = await reader.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read 64KB block from epoch {epoch}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        rbm.Dispose();
    }

    [Fact]
    public async Task MixedEncryptedAndUnencrypted_AcrossEpochs_AllReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"mixed_enc_{Guid.NewGuid():N}.emdb");
        var rbm = new RawBlockManager(filePath);

        // Epoch 0: one encrypted EmailContent + one unencrypted Metadata
        var entries0 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm0, p0) = CreateCacheManagerWithRbm(rbm, activeEpoch: 0, entries: entries0);

        var encPayload0 = "Encrypted email epoch 0"u8.ToArray();
        var encBlock0 = CreateBlock(BlockType.EmailContent, payload: (byte[])encPayload0.Clone());
        Assert.True((await cm0.WriteBlockAsync(encBlock0)).IsSuccess);
        Assert.True(encBlock0.IsEncrypted);

        var metaPayload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var metaBlock = CreateBlock(BlockType.Metadata, payload: (byte[])metaPayload.Clone());
        Assert.True((await cm0.WriteBlockAsync(metaBlock)).IsSuccess);
        Assert.False(metaBlock.IsEncrypted);

        cm0.Dispose();
        p0.Dispose();

        // Epoch 1: another encrypted EmailContent
        var entries1 = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (cm1, p1) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: entries1);

        var encPayload1 = "Encrypted email epoch 1"u8.ToArray();
        var encBlock1 = CreateBlock(BlockType.EmailContent, payload: (byte[])encPayload1.Clone());
        Assert.True((await cm1.WriteBlockAsync(encBlock1)).IsSuccess);
        Assert.True(encBlock1.IsEncrypted);

        cm1.Dispose();
        p1.Dispose();

        // Read all with full key store
        var fullEntries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
            new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
        };
        var (reader, providerFull) = CreateCacheManagerWithRbm(rbm, activeEpoch: 1, entries: fullEntries);
        using var _ = reader;
        using var __ = providerFull;

        reader.InvalidateCache();
        var readEnc0 = await reader.ReadBlockAsync(encBlock0.BlockId);
        Assert.True(readEnc0.IsSuccess, "Epoch 0 encrypted block should be readable");
        Assert.Equal(encPayload0, readEnc0.Value.Payload);

        reader.InvalidateCache();
        var readMeta = await reader.ReadBlockAsync(metaBlock.BlockId);
        Assert.True(readMeta.IsSuccess, "Unencrypted metadata block should be readable");
        Assert.Equal(metaPayload, readMeta.Value.Payload);

        reader.InvalidateCache();
        var readEnc1 = await reader.ReadBlockAsync(encBlock1.BlockId);
        Assert.True(readEnc1.IsSuccess, "Epoch 1 encrypted block should be readable");
        Assert.Equal(encPayload1, readEnc1.Value.Payload);

        rbm.Dispose();
    }
}
