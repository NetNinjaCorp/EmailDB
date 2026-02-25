using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-45-4: Verify WAL recovery correctly handles WAL blocks written before
/// and after a key rotation — blocks from different key epochs should all be recoverable.
/// </summary>
public class RecoverFromDiskDecryptsWALBlocksAcrossKeyRotationTests : IDisposable
{
    private readonly string _tempDir;

    public RecoverFromDiskDecryptsWALBlocksAcrossKeyRotationTests()
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
        => System.Text.Encoding.UTF8.GetBytes($"WAL recovery rotation payload #{seed}");

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
    public async Task Recovery_BlocksBeforeAndAfterKeyRotation_BothDecryptCorrectly()
    {
        // Write a WAL block at epoch 0 (pre-rotation) and another at epoch 1
        // (post-rotation) in separate files, then recover both with a provider
        // that has both DEKs loaded.
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var serializer = new ProtobufBlockContentSerializer();

        var preRotationPayload = SampleWalPayload(0);
        var postRotationPayload = SampleWalPayload(1);
        var preFile = Path.Combine(_tempDir, $"pre_rotation_{Guid.NewGuid():N}.emdb");
        var postFile = Path.Combine(_tempDir, $"post_rotation_{Guid.NewGuid():N}.emdb");
        long preBlockId, postBlockId;

        // Phase 1: Write WAL block at epoch 0 (pre-rotation)
        {
            var rawBlockManager = new RawBlockManager(preFile);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 0,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])preRotationPayload.Clone());
            var result = await cacheManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.Equal(0, block.KeyEpoch);
            Assert.True(block.IsEncrypted);
            preBlockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Phase 2: Write WAL block at epoch 1 (post-rotation)
        {
            var rawBlockManager = new RawBlockManager(postFile);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 1,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                    new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])postRotationPayload.Clone());
            var result = await cacheManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.Equal(1, block.KeyEpoch);
            Assert.True(block.IsEncrypted);
            postBlockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Phase 3: Recover both files with a provider that has both DEKs
        var recoveryKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Read pre-rotation block (epoch 0)
        {
            var rawBlockManager = new RawBlockManager(preFile);
            using var provider = new KeyWrappingEncryptionProvider(recoveryKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var preResult = await cacheManager.ReadBlockAsync(preBlockId);
            Assert.True(preResult.IsSuccess, $"Failed to read pre-rotation WAL block: {preResult.Error}");
            Assert.Equal(preRotationPayload, preResult.Value.Payload);

            rawBlockManager.Dispose();
        }

        // Read post-rotation block (epoch 1)
        {
            var rawBlockManager = new RawBlockManager(postFile);
            using var provider = new KeyWrappingEncryptionProvider(recoveryKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var postResult = await cacheManager.ReadBlockAsync(postBlockId);
            Assert.True(postResult.IsSuccess, $"Failed to read post-rotation WAL block: {postResult.Error}");
            Assert.Equal(postRotationPayload, postResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task Recovery_ThreeKeyRotations_AllEpochBlocksRecoverable()
    {
        // Simulate three key rotations: epochs 0 → 1 → 2 → 3.
        // Write one WAL block at each epoch in separate files, then recover all
        // with a provider that has every DEK loaded.
        var deks = new[] { GenerateDek(), GenerateDek(), GenerateDek(), GenerateDek() };
        var payloads = new[] { SampleWalPayload(10), SampleWalPayload(20), SampleWalPayload(30), SampleWalPayload(40) };
        var serializer = new ProtobufBlockContentSerializer();
        var filePaths = new string[4];
        var blockIds = new long[4];

        // Write WAL blocks at each epoch
        for (int i = 0; i < 4; i++)
        {
            filePaths[i] = Path.Combine(_tempDir, $"rotation_e{i}_{Guid.NewGuid():N}.emdb");
            var rawBlockManager = new RawBlockManager(filePaths[i]);

            // Build key store with all DEKs up to and including current epoch
            var entries = new List<KeyStoreEntry>();
            for (int j = 0; j <= i; j++)
            {
                entries.Add(new KeyStoreEntry
                {
                    Epoch = j,
                    DEK = deks[j],
                    Timestamp = DateTime.UtcNow.AddMinutes(-10 + j),
                    Retired = j < i
                });
            }

            var keyStore = new KeyStoreContent { ActiveEpoch = i, Entries = entries };
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

        // Recover all with a single provider that has all 4 DEKs
        var allEntries = new List<KeyStoreEntry>();
        for (int i = 0; i < 4; i++)
        {
            allEntries.Add(new KeyStoreEntry
            {
                Epoch = i,
                DEK = deks[i],
                Timestamp = DateTime.UtcNow.AddMinutes(-10 + i),
                Retired = i < 3
            });
        }
        var fullKeyStore = new KeyStoreContent { ActiveEpoch = 3, Entries = allEntries };

        for (int i = 0; i < 4; i++)
        {
            var rawBlockManager = new RawBlockManager(filePaths[i]);
            using var provider = new KeyWrappingEncryptionProvider(fullKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var readResult = await cacheManager.ReadBlockAsync(blockIds[i]);
            Assert.True(readResult.IsSuccess, $"Failed to read epoch-{i} WAL block: {readResult.Error}");
            Assert.Equal(payloads[i], readResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task Recovery_PreRotationBlockOnly_StillDecryptableAfterRotation()
    {
        // After key rotation, blocks written with the old DEK (now retired)
        // should still be decryptable since the key store retains old DEKs.
        var oldDek = GenerateDek();
        var newDek = GenerateDek();
        var originalPayload = SampleWalPayload(99);
        var filePath = Path.Combine(_tempDir, $"pre_rotation_only_{Guid.NewGuid():N}.emdb");
        var serializer = new ProtobufBlockContentSerializer();
        long blockId;

        // Write block at epoch 0 (before rotation)
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 0,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = oldDek, Timestamp = DateTime.UtcNow, Retired = false }
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

        // Recover AFTER rotation: active epoch is now 1, but old DEK retained (retired)
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 1,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = oldDek, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                    new() { Epoch = 1, DEK = newDek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var readResult = await cacheManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Pre-rotation block should still be readable: {readResult.Error}");
            Assert.Equal(originalPayload, readResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task Recovery_MixedEpochBlocks_AllRecoverableWithFullKeyStore()
    {
        // Write WAL blocks at three different epochs (separate files), then recover
        // each with a single provider that has all DEKs — simulating a full key store
        // after multiple rotations.
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();
        var serializer = new ProtobufBlockContentSerializer();

        var payloads = new byte[5][];
        var blockIds = new long[5];
        var filePaths = new string[5];
        var epochsForBlocks = new int[] { 0, 0, 1, 1, 2 };

        // Write blocks — each block in its own file, epoch determines the active DEK
        for (int i = 0; i < 5; i++)
        {
            filePaths[i] = Path.Combine(_tempDir, $"mixed_e{epochsForBlocks[i]}_b{i}_{Guid.NewGuid():N}.emdb");
            payloads[i] = SampleWalPayload(i);

            var entries = new List<KeyStoreEntry>();
            if (epochsForBlocks[i] >= 0)
                entries.Add(new KeyStoreEntry { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddMinutes(-10), Retired = epochsForBlocks[i] > 0 });
            if (epochsForBlocks[i] >= 1)
                entries.Add(new KeyStoreEntry { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = epochsForBlocks[i] > 1 });
            if (epochsForBlocks[i] >= 2)
                entries.Add(new KeyStoreEntry { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false });

            var rawBlockManager = new RawBlockManager(filePaths[i]);
            var keyStore = new KeyStoreContent { ActiveEpoch = epochsForBlocks[i], Entries = entries };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])payloads[i].Clone());
            await cacheManager.WriteBlockAsync(block);
            Assert.Equal(epochsForBlocks[i], block.KeyEpoch);
            blockIds[i] = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Recover all 5 blocks with a provider that has all 3 DEKs
        var fullKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddMinutes(-10), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        for (int i = 0; i < 5; i++)
        {
            var rawBlockManager = new RawBlockManager(filePaths[i]);
            using var provider = new KeyWrappingEncryptionProvider(fullKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var readResult = await cacheManager.ReadBlockAsync(blockIds[i]);
            Assert.True(readResult.IsSuccess,
                $"Failed to read WAL block {i} (epoch {epochsForBlocks[i]}): {readResult.Error}");
            Assert.Equal(payloads[i], readResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }

    [Fact]
    public async Task Recovery_RawBlockEpochsPreservedAcrossRotation_ManualDecrypt()
    {
        // Write blocks at different epochs in separate files, then read raw blocks
        // and manually decrypt using KeyWrappingEncryptionProvider.Decrypt with
        // each block's KeyEpoch extracted from Flags.
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var serializer = new ProtobufBlockContentSerializer();

        var payload0 = SampleWalPayload(100);
        var payload1 = SampleWalPayload(200);
        var file0 = Path.Combine(_tempDir, $"raw_epoch0_{Guid.NewGuid():N}.emdb");
        var file1 = Path.Combine(_tempDir, $"raw_epoch1_{Guid.NewGuid():N}.emdb");
        long blockId0, blockId1;

        // Write at epoch 0
        {
            var rawBlockManager = new RawBlockManager(file0);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 0,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])payload0.Clone());
            await cacheManager.WriteBlockAsync(block);
            blockId0 = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Write at epoch 1
        {
            var rawBlockManager = new RawBlockManager(file1);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 1,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                    new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])payload1.Clone());
            await cacheManager.WriteBlockAsync(block);
            blockId1 = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Read raw blocks and manually decrypt using epoch from Flags
        var recoveryKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };
        using var recoveryProvider = new KeyWrappingEncryptionProvider(recoveryKeyStore, EncryptionPolicy.Default);

        // Verify epoch 0 block
        {
            using var rawBlockManager = new RawBlockManager(file0);
            var raw0 = await rawBlockManager.ReadBlockAsync(blockId0);
            Assert.True(raw0.IsSuccess);
            Assert.True(raw0.Value.IsEncrypted);
            Assert.Equal(0, raw0.Value.KeyEpoch);
            var decrypted0 = recoveryProvider.Decrypt(raw0.Value.Payload, raw0.Value.Type, raw0.Value.BlockId, raw0.Value.KeyEpoch);
            Assert.Equal(payload0, decrypted0);
        }

        // Verify epoch 1 block
        {
            using var rawBlockManager = new RawBlockManager(file1);
            var raw1 = await rawBlockManager.ReadBlockAsync(blockId1);
            Assert.True(raw1.IsSuccess);
            Assert.True(raw1.Value.IsEncrypted);
            Assert.Equal(1, raw1.Value.KeyEpoch);
            var decrypted1 = recoveryProvider.Decrypt(raw1.Value.Payload, raw1.Value.Type, raw1.Value.BlockId, raw1.Value.KeyEpoch);
            Assert.Equal(payload1, decrypted1);
        }
    }

    [Fact]
    public async Task Recovery_PostRotationProviderMissingOldDek_FailsForOldBlock()
    {
        // After rotation, if the old DEK is NOT in the key store, reading a
        // pre-rotation block should fail with CryptographicException.
        var oldDek = GenerateDek();
        var newDek = GenerateDek();
        var filePath = Path.Combine(_tempDir, $"missing_old_dek_{Guid.NewGuid():N}.emdb");
        var serializer = new ProtobufBlockContentSerializer();
        long preBlockId;

        // Write at epoch 0
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 0,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 0, DEK = oldDek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])SampleWalPayload().Clone());
            await cacheManager.WriteBlockAsync(block);
            preBlockId = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Recover with provider that ONLY has epoch 1 (old DEK removed)
        {
            var rawBlockManager = new RawBlockManager(filePath);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = 1,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = 1, DEK = newDek, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            await Assert.ThrowsAsync<CryptographicException>(
                () => cacheManager.ReadBlockAsync(preBlockId));

            rawBlockManager.Dispose();
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 42)]
    [InlineData(5, 127)]
    [InlineData(10, 11)]
    public async Task Recovery_VariousEpochPairs_BothRecoverableAfterRotation(byte epochBefore, byte epochAfter)
    {
        // Parameterized test: write at epochBefore and epochAfter in separate files,
        // then recover both with a single provider containing both DEKs.
        var dekBefore = GenerateDek();
        var dekAfter = GenerateDek();
        var serializer = new ProtobufBlockContentSerializer();

        var payloadBefore = SampleWalPayload(epochBefore);
        var payloadAfter = SampleWalPayload(epochAfter);
        var fileBefore = Path.Combine(_tempDir, $"pair_before_{epochBefore}_{Guid.NewGuid():N}.emdb");
        var fileAfter = Path.Combine(_tempDir, $"pair_after_{epochAfter}_{Guid.NewGuid():N}.emdb");
        long blockIdBefore, blockIdAfter;

        // Write at epochBefore
        {
            var rawBlockManager = new RawBlockManager(fileBefore);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epochBefore,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epochBefore, DEK = dekBefore, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])payloadBefore.Clone());
            await cacheManager.WriteBlockAsync(block);
            Assert.Equal(epochBefore, block.KeyEpoch);
            blockIdBefore = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Write at epochAfter
        {
            var rawBlockManager = new RawBlockManager(fileAfter);
            var keyStore = new KeyStoreContent
            {
                ActiveEpoch = epochAfter,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = epochBefore, DEK = dekBefore, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                    new() { Epoch = epochAfter, DEK = dekAfter, Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
            var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var block = CreateWalBlock(payload: (byte[])payloadAfter.Clone());
            await cacheManager.WriteBlockAsync(block);
            Assert.Equal(epochAfter, block.KeyEpoch);
            blockIdAfter = block.BlockId;

            cacheManager.Dispose();
            provider.Dispose();
            rawBlockManager.Dispose();
        }

        // Recover both with a provider that has both DEKs
        var recoveryKeyStore = new KeyStoreContent
        {
            ActiveEpoch = epochAfter,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = epochBefore, DEK = dekBefore, Timestamp = DateTime.UtcNow.AddMinutes(-5), Retired = true },
                new() { Epoch = epochAfter, DEK = dekAfter, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Read pre-rotation block
        {
            var rawBlockManager = new RawBlockManager(fileBefore);
            using var provider = new KeyWrappingEncryptionProvider(recoveryKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var beforeResult = await cacheManager.ReadBlockAsync(blockIdBefore);
            Assert.True(beforeResult.IsSuccess,
                $"Failed to read pre-rotation block (epoch {epochBefore}): {beforeResult.Error}");
            Assert.Equal(payloadBefore, beforeResult.Value.Payload);

            rawBlockManager.Dispose();
        }

        // Read post-rotation block
        {
            var rawBlockManager = new RawBlockManager(fileAfter);
            using var provider = new KeyWrappingEncryptionProvider(recoveryKeyStore, EncryptionPolicy.Default);
            using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

            var afterResult = await cacheManager.ReadBlockAsync(blockIdAfter);
            Assert.True(afterResult.IsSuccess,
                $"Failed to read post-rotation block (epoch {epochAfter}): {afterResult.Error}");
            Assert.Equal(payloadAfter, afterResult.Value.Payload);

            rawBlockManager.Dispose();
        }
    }
}
