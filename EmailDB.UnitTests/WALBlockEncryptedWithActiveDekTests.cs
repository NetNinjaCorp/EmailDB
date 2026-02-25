using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: WAL blocks are encrypted with the active DEK
/// from the key store and the active key epoch is stamped into Flags bits 1-7.
/// </summary>
public class WALBlockEncryptedWithActiveDekTests : IDisposable
{
    private readonly string _tempDir;

    public WALBlockEncryptedWithActiveDekTests()
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

    private static byte[] SampleWalPayload() => "WAL entry payload data"u8.ToArray();

    private (CacheManager cacheManager, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider)
        CreateEncryptedCacheManager(byte activeEpoch = 0, byte[]? dek = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        dek ??= GenerateDek();

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = activeEpoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        return (cacheManager, rawBlockManager, provider);
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
    public async Task WriteWalBlock_SetsEncryptedFlag()
    {
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock();

        var result = await cacheManager.WriteBlockAsync(block);

        Assert.True(result.IsSuccess);
        Assert.True(block.IsEncrypted, "WAL block should have encrypted flag (bit 0) set");
    }

    [Fact]
    public async Task WriteWalBlock_StampsActiveKeyEpochInFlags()
    {
        byte activeEpoch = 5;
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: activeEpoch);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock();

        await cacheManager.WriteBlockAsync(block);

        Assert.Equal(activeEpoch, block.KeyEpoch);
    }

    [Fact]
    public async Task WriteWalBlock_FlagsBitsMatchEncryptedBitAndEpoch()
    {
        byte activeEpoch = 42;
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: activeEpoch);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock();

        await cacheManager.WriteBlockAsync(block);

        byte expectedFlags = (byte)((activeEpoch << 1) | Block.FlagEncrypted);
        Assert.Equal(expectedFlags, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(activeEpoch, block.KeyEpoch);
    }

    [Fact]
    public async Task WriteWalBlock_PayloadIsEncrypted_DiffersFromPlaintext()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

        await cacheManager.WriteBlockAsync(block);

        Assert.NotEqual(originalPayload, block.Payload);
        // Ciphertext includes AES-GCM overhead: 12-byte nonce + 16-byte auth tag = 28 bytes
        Assert.Equal(originalPayload.Length + 28, block.Payload.Length);
    }

    [Fact]
    public async Task WriteWalBlock_EncryptedPayload_DecryptableWithActiveDek()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var originalPayload = SampleWalPayload();
        var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());

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
    [InlineData(63)]
    [InlineData(127)]
    public async Task WriteWalBlock_VariousEpochs_StampsCorrectEpochInFlags(byte epoch)
    {
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(activeEpoch: epoch);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock();

        await cacheManager.WriteBlockAsync(block);

        Assert.True(block.IsEncrypted);
        Assert.Equal(epoch, block.KeyEpoch);
        byte expectedFlags = (byte)((epoch << 1) | Block.FlagEncrypted);
        Assert.Equal(expectedFlags, block.Flags);
    }

    [Fact]
    public async Task WriteWalBlock_DefaultPolicy_WalIsEncrypted()
    {
        // Confirm WAL is in the Default encryption policy's encrypted set
        Assert.True(EncryptionPolicy.Default.ShouldEncrypt(BlockType.WAL),
            "Default encryption policy should include WAL blocks");

        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager();
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock();

        await cacheManager.WriteBlockAsync(block);

        Assert.True(block.IsEncrypted);
    }

    [Fact]
    public async Task WriteWalBlock_EncryptedPayload_WrongDekCannotDecrypt()
    {
        var dek = GenerateDek();
        var (cacheManager, rawBlockManager, provider) = CreateEncryptedCacheManager(dek: dek);
        using var _ = cacheManager;
        using var __ = rawBlockManager;
        using var ___ = provider;

        var block = CreateWalBlock(payload: (byte[])SampleWalPayload().Clone());

        await cacheManager.WriteBlockAsync(block);

        // Attempting decryption with a different DEK should fail
        var wrongDek = GenerateDek();
        using var wrongProvider = new AesGcmBlockEncryptionProvider(wrongDek);
        Assert.ThrowsAny<CryptographicException>(() =>
            wrongProvider.Decrypt(block.Payload, block.Type, block.BlockId));
    }
}
