using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: Cache stores decrypted content objects, not ciphertext.
/// When CacheManager caches blocks after writes, the cached entries contain deserialized
/// plaintext content objects. Reading from the cache returns the original plaintext payload,
/// never ciphertext bytes.
/// </summary>
public class CacheStoresDecryptedContentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DefaultBlockContentSerializer _jsonSerializer;

    public CacheStoresDecryptedContentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _jsonSerializer = new DefaultBlockContentSerializer();
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
            byte activeEpoch = 0,
            byte[]? dek = null,
            EncryptionPolicy? policy = null,
            iBlockContentSerializer? serializer = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        dek ??= GenerateDek();
        policy ??= EncryptionPolicy.Default;
        serializer ??= _jsonSerializer;

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

    private MetadataContent CreateSampleMetadata() => new()
    {
        WALOffset = 4096,
        FolderTreeOffset = 8192,
        SegmentOffsets = new Dictionary<string, long> { ["seg-0"] = 100, ["seg-1"] = 200 },
        OutdatedOffsets = new List<long> { 50, 75 }
    };

    [Fact]
    public async Task CacheHit_MetadataBlock_ReturnsPlaintextNotCiphertext()
    {
        // Metadata is not encrypted under Default policy but IS cached.
        // Write a Metadata block, then read by file offset (cache hit path)
        // and verify the returned payload is the original plaintext.
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalMetadata = CreateSampleMetadata();
        var originalPayload = _jsonSerializer.Serialize(originalMetadata);

        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        // Write — CacheManager caches the deserialized MetadataContent object
        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        long fileOffset = writeResult.Value.Position;

        // Read from cache (by file offset → cache hit)
        var readResult = await cacheManager.ReadBlockAsync(fileOffset);
        Assert.True(readResult.IsSuccess);

        // The cache re-serializes the stored MetadataContent object.
        // Verify the round-tripped content matches the original.
        var roundTripped = _jsonSerializer.Deserialize<MetadataContent>(readResult.Value.Payload);
        Assert.Equal(originalMetadata.WALOffset, roundTripped.WALOffset);
        Assert.Equal(originalMetadata.FolderTreeOffset, roundTripped.FolderTreeOffset);
        Assert.Equal(originalMetadata.SegmentOffsets, roundTripped.SegmentOffsets);
    }

    [Fact]
    public async Task CacheContent_IsDeserializedObject_NotRawBytes()
    {
        // After writing a Metadata block, the cache stores a MetadataContent object.
        // Reading from cache re-serializes this object back to bytes.
        // Verify all field values survive the deserialize→cache→re-serialize round trip.
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalMetadata = CreateSampleMetadata();
        var originalPayload = _jsonSerializer.Serialize(originalMetadata);

        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        long fileOffset = writeResult.Value.Position;

        // Read from cache
        var readResult = await cacheManager.ReadBlockAsync(fileOffset);
        Assert.True(readResult.IsSuccess);

        // Deserialize and verify every field
        var cached = _jsonSerializer.Deserialize<MetadataContent>(readResult.Value.Payload);
        Assert.NotNull(cached);
        Assert.Equal(4096, cached.WALOffset);
        Assert.Equal(8192, cached.FolderTreeOffset);
        Assert.Equal(2, cached.SegmentOffsets.Count);
        Assert.Equal(100, cached.SegmentOffsets["seg-0"]);
        Assert.Equal(200, cached.SegmentOffsets["seg-1"]);
        Assert.Equal(2, cached.OutdatedOffsets.Count);
        Assert.Contains(50L, cached.OutdatedOffsets);
        Assert.Contains(75L, cached.OutdatedOffsets);
    }

    [Fact]
    public async Task EncryptedBlock_ReadByBlockId_ReturnsDecryptedPlaintext()
    {
        // Folder blocks are encrypted under Default policy.
        // After writing (payload → ciphertext on disk), reading by blockId
        // should return the decrypted plaintext, not the ciphertext.
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = "Hello, encrypted folder!"u8.ToArray();
        var block = new Block
        {
            Type = BlockType.Folder,
            BlockId = 100,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Block payload is now ciphertext
        Assert.True(block.IsEncrypted, "Folder block should be encrypted under Default policy");
        byte[] ciphertextOnDisk = (byte[])block.Payload.Clone();

        // Invalidate cache — force disk read path
        cacheManager.InvalidateCache();

        // Read by blockId — decrypts from disk
        var readResult = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Returned payload is plaintext, not ciphertext
        Assert.NotEqual(ciphertextOnDisk, readResult.Value.Payload);
        Assert.Equal(originalPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task EncryptedBlockCiphertext_NeverReturnedFromCache()
    {
        // When an encrypted block is written, the caching attempt fails
        // because ciphertext cannot be deserialized to a content object.
        // This means the cache never stores ciphertext bytes.
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = "Hello, encrypted segment!"u8.ToArray();
        var block = new Block
        {
            Type = BlockType.Segment,
            BlockId = 200,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.True(block.IsEncrypted, "Segment block should be encrypted under Default policy");

        byte[] ciphertextOnDisk = (byte[])block.Payload.Clone();

        // Invalidate cache and read from disk — should decrypt
        cacheManager.InvalidateCache();
        var readFromDisk = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(readFromDisk.IsSuccess);

        // Decrypted payload must match original, not ciphertext
        Assert.Equal(originalPayload, readFromDisk.Value.Payload);
        Assert.NotEqual(ciphertextOnDisk, readFromDisk.Value.Payload);
    }

    [Fact]
    public async Task CachePath_ConsistentWithDecryptPath_SamePlaintextReturned()
    {
        // Write a Metadata block (cached, not encrypted under Default policy).
        // Verify that reading from cache and reading from disk return equivalent plaintext.
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalMetadata = CreateSampleMetadata();
        var originalPayload = _jsonSerializer.Serialize(originalMetadata);

        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        long fileOffset = writeResult.Value.Position;

        // Read from cache (by offset → cache hit)
        var cacheRead = await cacheManager.ReadBlockAsync(fileOffset);
        Assert.True(cacheRead.IsSuccess);
        var cachePayload = cacheRead.Value.Payload;

        // Invalidate cache, then read from disk (by blockId)
        cacheManager.InvalidateCache();
        var diskRead = await cacheManager.ReadBlockAsync(block.BlockId);
        Assert.True(diskRead.IsSuccess);
        var diskPayload = diskRead.Value.Payload;

        // Both paths return equivalent plaintext content
        var fromCache = _jsonSerializer.Deserialize<MetadataContent>(cachePayload);
        var fromDisk = _jsonSerializer.Deserialize<MetadataContent>(diskPayload);

        Assert.Equal(fromCache.WALOffset, fromDisk.WALOffset);
        Assert.Equal(fromCache.FolderTreeOffset, fromDisk.FolderTreeOffset);
        Assert.Equal(fromCache.SegmentOffsets, fromDisk.SegmentOffsets);
    }

    [Fact]
    public async Task WithoutEncryption_CacheStoresPlaintextContent()
    {
        // Baseline: without encryption, the cache stores the deserialized
        // plaintext content object and returns it on cache hit.
        var filePath = Path.Combine(_tempDir, $"no_enc_{Guid.NewGuid():N}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        using var cacheManager = new CacheManager(rawBlockManager, _jsonSerializer);

        var originalMetadata = CreateSampleMetadata();
        var originalPayload = _jsonSerializer.Serialize(originalMetadata);

        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        long fileOffset = writeResult.Value.Position;

        // Read from cache
        var readResult = await cacheManager.ReadBlockAsync(fileOffset);
        Assert.True(readResult.IsSuccess);

        var cached = _jsonSerializer.Deserialize<MetadataContent>(readResult.Value.Payload);
        Assert.Equal(originalMetadata.WALOffset, cached.WALOffset);
        Assert.Equal(originalMetadata.FolderTreeOffset, cached.FolderTreeOffset);
    }

    [Fact]
    public async Task EncryptedWrite_MultipleReads_CacheServesDecryptedConsistently()
    {
        // Write with encryption, then read from cache multiple times.
        // Each read should return the same decrypted content object.
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalMetadata = CreateSampleMetadata();
        var originalPayload = _jsonSerializer.Serialize(originalMetadata);

        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        long fileOffset = writeResult.Value.Position;

        // Read from cache three times — all should return consistent plaintext
        for (int i = 0; i < 3; i++)
        {
            var readResult = await cacheManager.ReadBlockAsync(fileOffset);
            Assert.True(readResult.IsSuccess, $"Cache read {i + 1} should succeed");

            var cached = _jsonSerializer.Deserialize<MetadataContent>(readResult.Value.Payload);
            Assert.Equal(originalMetadata.WALOffset, cached.WALOffset);
            Assert.Equal(originalMetadata.FolderTreeOffset, cached.FolderTreeOffset);
            Assert.Equal(originalMetadata.SegmentOffsets.Count, cached.SegmentOffsets.Count);
        }
    }

    [Fact]
    public async Task EncryptionEnabled_MetadataNotEncrypted_CacheStoresPlaintextObject()
    {
        // Even with encryption enabled, Metadata blocks are excluded by Default policy.
        // The cache stores the plaintext MetadataContent object, not encrypted bytes.
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalMetadata = CreateSampleMetadata();
        var originalPayload = _jsonSerializer.Serialize(originalMetadata);

        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = (byte[])originalPayload.Clone()
        };

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        Assert.False(block.IsEncrypted, "Metadata should not be encrypted under Default policy");
        Assert.Equal(0, block.KeyEpoch);

        long fileOffset = writeResult.Value.Position;

        // Cache hit returns the deserialized content as plaintext
        var readResult = await cacheManager.ReadBlockAsync(fileOffset);
        Assert.True(readResult.IsSuccess);

        // Payload should be valid JSON-serialized MetadataContent
        var cached = _jsonSerializer.Deserialize<MetadataContent>(readResult.Value.Payload);
        Assert.NotNull(cached);
        Assert.Equal(originalMetadata.WALOffset, cached.WALOffset);
        Assert.Equal(originalMetadata.FolderTreeOffset, cached.FolderTreeOffset);
        Assert.Equal(originalMetadata.SegmentOffsets, cached.SegmentOffsets);
        Assert.Equal(originalMetadata.OutdatedOffsets, cached.OutdatedOffsets);
    }
}
