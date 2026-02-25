using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-45-2: Verify WAL write methods encrypt content using the current active DEK
/// from KeyWrappingEncryptionProvider.
/// </summary>
public class WriteMethodsEncryptWALContentTests : IDisposable
{
    private readonly string _tempDir;

    public WriteMethodsEncryptWALContentTests()
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

    private static byte[] SampleWalPayload(int seed = 0)
    {
        return System.Text.Encoding.UTF8.GetBytes($"WAL entry payload data #{seed}");
    }

    private (CacheManager cacheManager, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider, byte[] dek)
        CreateEncryptedCacheManager(byte activeEpoch = 0, byte[]? dek = null, List<KeyStoreEntry>? extraEntries = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        dek ??= GenerateDek();

        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = activeEpoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
        };

        if (extraEntries != null)
            entries.AddRange(extraEntries);

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = entries
        };

        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        return (cacheManager, rawBlockManager, provider, dek);
    }

    private Block CreateWalBlock(byte[]? payload = null)
    {
        return new Block
        {
            Type = BlockType.WAL,
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.WAL),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = payload ?? SampleWalPayload()
        };
    }

    [Fact]
    public async Task WriteWalBlock_UsesActiveDekFromKeyWrappingProvider()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider, _) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        // Verify the active DEK can decrypt the payload
        using var standalone = new AesGcmBlockEncryptionProvider(dek);
        var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);
        Assert.Equal(originalPayload, decrypted);
    }

    [Fact]
    public async Task WriteWalBlock_WrongDekCannotDecryptPayload()
    {
        var activeDek = GenerateDek();
        var (cacheManager, rawBlockManager, provider, _) = CreateEncryptedCacheManager(dek: activeDek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock(payload: (byte[])SampleWalPayload().Clone());
        await cacheManager.WriteBlockAsync(block);

        // A different DEK should fail to decrypt
        var wrongDek = GenerateDek();
        using var wrongProvider = new AesGcmBlockEncryptionProvider(wrongDek);
        Assert.ThrowsAny<CryptographicException>(() =>
            wrongProvider.Decrypt(block.Payload, block.Type, block.BlockId));
    }

    [Fact]
    public async Task WriteMultipleWalBlocks_AllEncryptedWithSameActiveDek()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider, _) = CreateEncryptedCacheManager(activeEpoch: 3, dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        using var standalone = new AesGcmBlockEncryptionProvider(dek);
        var blocks = new List<(Block block, byte[] originalPayload)>();

        for (int i = 0; i < 5; i++)
        {
            var originalPayload = SampleWalPayload(i);
            var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());
            await cacheManager.WriteBlockAsync(block);
            blocks.Add((block, originalPayload));
        }

        // All 5 WAL blocks should be encrypted with the same active DEK
        foreach (var (block, originalPayload) in blocks)
        {
            Assert.True(block.IsEncrypted, $"WAL block {block.BlockId} should be encrypted");
            Assert.Equal(3, block.KeyEpoch);
            var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);
            Assert.Equal(originalPayload, decrypted);
        }
    }

    [Fact]
    public async Task WriteWalBlock_OnDiskPayloadIsCiphertext()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider, _) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        var writeResult = await cacheManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Read back from RawBlockManager by block ID to verify what's persisted on disk
        var readResult = await rawBlockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        var diskBlock = readResult.Value;
        // The on-disk payload should be ciphertext, not plaintext
        Assert.NotEqual(originalPayload, diskBlock.Payload);
        Assert.True(diskBlock.IsEncrypted);
        Assert.Equal(originalPayload.Length + 28, diskBlock.Payload.Length);

        // Confirm it decrypts to the original
        using var standalone = new AesGcmBlockEncryptionProvider(dek);
        var decrypted = standalone.Decrypt(diskBlock.Payload, diskBlock.Type, diskBlock.BlockId);
        Assert.Equal(originalPayload, decrypted);
    }

    [Fact]
    public async Task WriteWalBlock_KeyWrappingProvider_SelectsActiveDekAmongMultipleEpochs()
    {
        var oldDek = GenerateDek();
        var activeDek = GenerateDek();
        byte activeEpoch = 2;

        var extraEntries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = oldDek, Timestamp = DateTime.UtcNow.AddDays(-2), Retired = true },
            new() { Epoch = 1, DEK = GenerateDek(), Timestamp = DateTime.UtcNow.AddDays(-1), Retired = true }
        };

        var (cacheManager, rawBlockManager, provider, _) =
            CreateEncryptedCacheManager(activeEpoch: activeEpoch, dek: activeDek, extraEntries: extraEntries);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        // Encrypted with the active DEK (epoch 2), not old DEKs
        Assert.Equal(activeEpoch, block.KeyEpoch);

        using var activeStandalone = new AesGcmBlockEncryptionProvider(activeDek);
        var decrypted = activeStandalone.Decrypt(block.Payload, block.Type, block.BlockId);
        Assert.Equal(originalPayload, decrypted);

        // Old DEK should NOT be able to decrypt
        using var oldStandalone = new AesGcmBlockEncryptionProvider(oldDek);
        Assert.ThrowsAny<CryptographicException>(() =>
            oldStandalone.Decrypt(block.Payload, block.Type, block.BlockId));
    }

    [Fact]
    public async Task WriteWalBlock_DecryptViaKeyWrappingProvider_RoundTrips()
    {
        var dek = GenerateDek();
        byte epoch = 10;
        var (cacheManager, rawBlockManager, provider, _) = CreateEncryptedCacheManager(activeEpoch: epoch, dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        // Decrypt using the KeyWrappingEncryptionProvider itself (by epoch lookup)
        var decrypted = provider.Decrypt(block.Payload, block.Type, block.BlockId, block.KeyEpoch);
        Assert.Equal(originalPayload, decrypted);
    }

    [Fact]
    public async Task WriteWalBlock_NullEncryptionProvider_LeavesPayloadUnchanged()
    {
        var filePath = Path.Combine(_tempDir, "no_encryption.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer);

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        Assert.False(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
        Assert.Equal(originalPayload, block.Payload);
    }

    [Fact]
    public async Task WriteWalBlock_NullBlockEncryptionProvider_LeavesPayloadUnchanged()
    {
        var filePath = Path.Combine(_tempDir, "null_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer,
            encryptionProvider: NullBlockEncryptionProvider.Instance);

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        Assert.False(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
        Assert.Equal(originalPayload, block.Payload);
    }

    [Fact]
    public async Task WriteWalBlock_EachBlockGetsUniqueNonce()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider, _) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        // Write two WAL blocks with identical plaintext
        var payload = SampleWalPayload();
        var block1 = CreateWalBlock(payload: (byte[])payload.Clone());
        var block2 = CreateWalBlock(payload: (byte[])payload.Clone());

        await cacheManager.WriteBlockAsync(block1);
        await cacheManager.WriteBlockAsync(block2);

        // Both are encrypted
        Assert.True(block1.IsEncrypted);
        Assert.True(block2.IsEncrypted);

        // Ciphertexts should differ because AES-GCM uses a unique nonce per encryption
        Assert.NotEqual(block1.Payload, block2.Payload);

        // But both should decrypt to the same plaintext
        using var standalone = new AesGcmBlockEncryptionProvider(dek);
        var decrypted1 = standalone.Decrypt(block1.Payload, block1.Type, block1.BlockId);
        var decrypted2 = standalone.Decrypt(block2.Payload, block2.Type, block2.BlockId);
        Assert.Equal(payload, decrypted1);
        Assert.Equal(payload, decrypted2);
    }
}
