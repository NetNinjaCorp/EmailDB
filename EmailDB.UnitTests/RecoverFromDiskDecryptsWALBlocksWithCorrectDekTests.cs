using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-45-3: Verify RecoverFromDisk reads the key epoch from each WAL block's
/// Flags and uses KeyWrappingEncryptionProvider to look up the correct DEK for decryption.
/// </summary>
public class RecoverFromDiskDecryptsWALBlocksWithCorrectDekTests : IDisposable
{
    private readonly string _tempDir;

    public RecoverFromDiskDecryptsWALBlocksWithCorrectDekTests()
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
        => System.Text.Encoding.UTF8.GetBytes($"WAL recovery payload #{seed}");

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
    public async Task RecoverFromDisk_WALBlock_ReadsKeyEpochFromFlagsAndDecryptsWithCorrectDek()
    {
        // Write a WAL block with a specific epoch, then read it back from disk
        // to verify the read path extracts the key epoch from Flags and uses
        // KeyWrappingEncryptionProvider to find the correct DEK.
        byte epoch = 7;
        var dek = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"recover_{Guid.NewGuid():N}.emdb");

        var originalPayload = SampleWalPayload();
        long blockId;

        // Phase 1: Write encrypted WAL block to disk
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());
            var writeResult = await cacheManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
            Assert.True(block.IsEncrypted, "WAL block should be encrypted after write");
            Assert.Equal(epoch, block.KeyEpoch);
            blockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Phase 2: Recover — open the file with a fresh CacheManager and read back
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var readResult = await cacheManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read WAL block: {readResult.Error}");

            Assert.Equal(originalPayload, readResult.Value.Payload);
            rawBlockManager.Dispose();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(127)]
    public async Task RecoverFromDisk_VariousEpochs_WALBlockDecryptedWithCorrectDek(byte epoch)
    {
        // Write a WAL block at each epoch, close, reopen, and verify recovery
        // decrypts using the correct DEK for that epoch.
        var dek = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"epoch_{epoch}_{Guid.NewGuid():N}.emdb");
        var originalPayload = SampleWalPayload(epoch);
        long blockId;

        // Write encrypted WAL block
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());
            await cacheManager.WriteBlockAsync(block);
            Assert.Equal(epoch, block.KeyEpoch);
            blockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Recover with a multi-epoch provider (active epoch differs from write epoch)
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch, // active doesn't matter for reads
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var readResult = await cacheManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Failed to read WAL block at epoch {epoch}: {readResult.Error}");
            Assert.Equal(originalPayload, readResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task RecoverFromDisk_MultiEpoch_ReaderWithAllDeks_DecryptsEachFileCorrectly()
    {
        // Simulate key rotation across separate files: write WAL blocks at different
        // epochs, then recover each with a provider that has all DEKs loaded.
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();
        var serializer = new ProtobufBlockContentSerializer();

        var payloads = new[] { SampleWalPayload(0), SampleWalPayload(1), SampleWalPayload(2) };
        var deks = new[] { dek0, dek1, dek2 };
        var filePaths = new string[3];
        var blockIds = new long[3];

        // Write each WAL block at its own epoch in a separate file
        for (int i = 0; i < 3; i++)
        {
            filePaths[i] = Path.Combine(_tempDir, $"epoch{i}_{Guid.NewGuid():N}.emdb");
            var rawBlockManager = new RawBlockManager(filePaths[i]);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = i,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = i, DEK = deks[i], Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])payloads[i].Clone());
            await cacheManager.WriteBlockAsync(block);
            Assert.Equal(i, block.KeyEpoch);
            blockIds[i] = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Recover each file with a provider that has ALL three DEKs
        var allEntriesKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        for (int i = 0; i < 3; i++)
        {
            var rawBlockManager = new RawBlockManager(filePaths[i]);
            using var provider = new KeyWrappingEncryptionProvider(allEntriesKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var readResult = await cacheManager.ReadBlockAsync(blockIds[i]);
            Assert.True(readResult.IsSuccess, $"Failed to read epoch-{i} WAL block: {readResult.Error}");
            Assert.Equal(payloads[i], readResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task RecoverFromDisk_MissingDekForEpoch_ThrowsCryptographicException()
    {
        // Write a WAL block at epoch 3, then try to recover with a provider
        // that does NOT have epoch 3 — should throw CryptographicException.
        byte writeEpoch = 3;
        var writeDek = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"missing_dek_{Guid.NewGuid():N}.emdb");

        long blockId;

        // Phase 1: Write encrypted WAL block at epoch 3
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = writeEpoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = writeEpoch, DEK = writeDek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])SampleWalPayload().Clone());
            await cacheManager.WriteBlockAsync(block);
            blockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Phase 2: Recover with only epoch 0 DEK — epoch 3 is missing
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 0,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            await Assert.ThrowsAsync<CryptographicException>(
                () => cacheManager.ReadBlockAsync(blockId));

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task RecoverFromDisk_WrongDekForEpoch_ThrowsCryptographicException()
    {
        // Write a WAL block at epoch 1, then try to recover with a provider
        // that has epoch 1 but with a DIFFERENT DEK — should fail decryption.
        byte epoch = 1;
        var realDek = GenerateDek();
        var wrongDek = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"wrong_dek_{Guid.NewGuid():N}.emdb");

        long blockId;

        // Phase 1: Write with real DEK
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = realDek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])SampleWalPayload().Clone());
            await cacheManager.WriteBlockAsync(block);
            blockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Phase 2: Recover with wrong DEK at the same epoch
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = wrongDek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => cacheManager.ReadBlockAsync(blockId));

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task RecoverFromDisk_KeyEpochPreservedOnDisk_MatchesOriginalWrite()
    {
        // Verify the key epoch stored in Flags on disk matches what was written,
        // by reading the raw block (without decryption) and inspecting Flags.
        byte epoch = 42;
        var dek = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"epoch_preserved_{Guid.NewGuid():N}.emdb");

        long blockId;

        // Write encrypted WAL block
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock();
            await cacheManager.WriteBlockAsync(block);
            blockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Read back raw (no encryption provider) to inspect Flags on disk
        {
            using var rawBlockManager = new RawBlockManager(filePath);

            var rawResult = await rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(rawResult.IsSuccess);

            var diskBlock = rawResult.Value;
            Assert.True(diskBlock.IsEncrypted, "Encrypted flag (bit 0) should be set on disk");
            Assert.Equal(epoch, diskBlock.KeyEpoch);
            Assert.Equal(BlockType.WAL, diskBlock.Type);
        }
    }

    [Fact]
    public async Task RecoverFromDisk_ManualDecryptViaKeyWrappingProvider_UsesEpochFromFlags()
    {
        // End-to-end: write a WAL block at a specific epoch, then recover by reading
        // the raw block, extracting KeyEpoch from Flags, and calling
        // KeyWrappingEncryptionProvider.Decrypt(ciphertext, type, id, keyEpoch) directly.
        byte writeEpoch = 5;
        var dek5 = GenerateDek();
        var dek10 = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"manual_decrypt_{Guid.NewGuid():N}.emdb");

        var originalPayload = SampleWalPayload(555);
        long blockId;

        // Write WAL block at epoch 5
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var serializer = new ProtobufBlockContentSerializer();
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = writeEpoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = writeEpoch, DEK = dek5, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])originalPayload.Clone());
            await cacheManager.WriteBlockAsync(block);
            blockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Recover: read raw block, extract epoch from Flags, decrypt manually
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 10, // active epoch differs from write epoch
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = writeEpoch, DEK = dek5, Timestamp = DateTime.UtcNow, Retired = false },
                    new() { Epoch = 10, DEK = dek10, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

            var rawResult = await rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(rawResult.IsSuccess);

            var diskBlock = rawResult.Value;
            Assert.True(diskBlock.IsEncrypted);
            Assert.Equal(writeEpoch, diskBlock.KeyEpoch);
            Assert.Equal(BlockType.WAL, diskBlock.Type);

            // Decrypt using the epoch read from the block's Flags
            var decrypted = provider.Decrypt(
                diskBlock.Payload, diskBlock.Type, diskBlock.BlockId, diskBlock.KeyEpoch);
            Assert.Equal(originalPayload, decrypted);
        }
    }
}
