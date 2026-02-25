using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-46-6: Verify the compacted file's key store contains only DEKs that
/// still have blocks referencing them plus the active epoch DEK.
/// </summary>
public class BTreeCompactionReEncryptNewFileKeyStoreTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionReEncryptNewFileKeyStoreTests()
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

    private static byte[] GenerateKek()
    {
        var kek = new byte[32];
        RandomNumberGenerator.Fill(kek);
        return kek;
    }

    private static EmailHashedID CreateTestKey(int seed)
    {
        var hash = new byte[32];
        BitConverter.TryWriteBytes(hash.AsSpan(0, 4), seed);
        return new EmailHashedID(hash);
    }

    private static KeyWrappingEncryptionProvider CreateProvider(
        byte activeEpoch, params (byte epoch, byte[] dek)[] deks)
    {
        var entries = deks.Select(d => new KeyStoreEntry
        {
            Epoch = d.epoch,
            DEK = d.dek,
            Timestamp = DateTime.UtcNow,
            Retired = false
        }).ToList();

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = entries
        };

        return new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);
    }

    /// <summary>
    /// Scans all blocks in the file and returns the set of key epochs referenced
    /// by encrypted blocks.
    /// </summary>
    private static async Task<HashSet<int>> CollectReferencedEpochs(RawBlockManager manager)
    {
        var epochs = new HashSet<int>();
        foreach (var kvp in manager.GetBlockLocations())
        {
            var readResult = await manager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.IsEncrypted)
                epochs.Add(readResult.Value.KeyEpoch);
        }
        return epochs;
    }

    /// <summary>
    /// Prunes a key store by removing entries whose epoch is not referenced by any
    /// block and is not the active epoch.
    /// </summary>
    private static KeyStoreContent PruneKeyStore(KeyStoreContent original, HashSet<int> referencedEpochs)
    {
        return new KeyStoreContent
        {
            ActiveEpoch = original.ActiveEpoch,
            Entries = original.Entries
                .Where(e => referencedEpochs.Contains(e.Epoch) || e.Epoch == original.ActiveEpoch)
                .ToList()
        };
    }

    /// <summary>
    /// Writes a pruned key store block to the compacted file, encrypted with the KEK.
    /// </summary>
    private static async Task WriteKeyStoreBlock(
        RawBlockManager dest, KeyStoreContent prunedKeyStore, byte[] kek)
    {
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var encryptedPayload = ksManager.EncryptKeyStore(prunedKeyStore, kek);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = encryptedPayload
        };

        var writeResult = await dest.WriteBlockAsync(block);
        if (writeResult.IsFailure)
            throw new InvalidOperationException($"Failed to write key store block: {writeResult.Error}");
    }

    /// <summary>
    /// Reads the key store block from a file, decrypts it with the KEK, and returns the content.
    /// </summary>
    private static async Task<KeyStoreContent> ReadKeyStoreFromFile(RawBlockManager manager, byte[] kek)
    {
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);

        foreach (var kvp in manager.GetBlockLocations())
        {
            var readResult = await manager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.KeyStore)
            {
                return ksManager.DecryptKeyStore(readResult.Value.Payload, kek);
            }
        }

        throw new InvalidOperationException("No KeyStore block found in file");
    }

    // --- Two-epoch: new file key store contains only active DEK ---

    [Fact]
    public async Task CompactWithReEncrypt_TwoEpochs_NewFileKeyStoreContainsOnlyActiveDek()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var kek = GenerateKek();

        var filePath = Path.Combine(_tempDir, $"ks_two_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"ks_two_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Phase 1: Insert entries with epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Phase 2: Rotate to epoch 1, insert more
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 10; i < 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }

        // Build the original key store with both epochs
        var originalKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Prune and write key store to the compacted file
        using var compactedManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(compactedManager);
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);
        await WriteKeyStoreBlock(compactedManager, prunedKeyStore, kek);

        // Read the key store back from the compacted file
        var readBackKeyStore = await ReadKeyStoreFromFile(compactedManager, kek);

        // Verify: only the active DEK (epoch 1) is present
        Assert.Equal(1, readBackKeyStore.ActiveEpoch);
        Assert.Single(readBackKeyStore.Entries);
        Assert.Equal(1, readBackKeyStore.Entries[0].Epoch);
        Assert.DoesNotContain(readBackKeyStore.Entries, e => e.Epoch == 0);
    }

    // --- Three-epoch: new file key store contains only active DEK ---

    [Fact]
    public async Task CompactWithReEncrypt_ThreeEpochs_NewFileKeyStoreContainsOnlyActiveDek()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();
        var kek = GenerateKek();

        var filePath = Path.Combine(_tempDir, $"ks_three_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"ks_three_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 5; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 5; i < 10; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root1 = btree1.CurrentRoot;
        provider1.Dispose();

        // Epoch 2
        var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);
        for (int i = 10; i < 15; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Build original key store with all three epochs
        var originalKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Compact with re-encryption to epoch 2
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider2);
        provider2.Dispose();

        // Prune and write key store to the compacted file
        using var compactedManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(compactedManager);
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);
        await WriteKeyStoreBlock(compactedManager, prunedKeyStore, kek);

        // Read key store back
        var readBackKeyStore = await ReadKeyStoreFromFile(compactedManager, kek);

        // Verify: only the active DEK (epoch 2) is present, epochs 0 and 1 pruned
        Assert.Equal(2, readBackKeyStore.ActiveEpoch);
        Assert.Single(readBackKeyStore.Entries);
        Assert.Equal(2, readBackKeyStore.Entries[0].Epoch);
        Assert.DoesNotContain(readBackKeyStore.Entries, e => e.Epoch == 0);
        Assert.DoesNotContain(readBackKeyStore.Entries, e => e.Epoch == 1);
    }

    // --- New file key store ActiveEpoch matches re-encryption epoch ---

    [Fact]
    public async Task CompactWithReEncrypt_NewFileKeyStoreActiveEpochMatchesReEncryptionEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var kek = GenerateKek();

        var filePath = Path.Combine(_tempDir, $"ks_epoch_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"ks_epoch_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 10; i < 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        var originalKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        using var compactedManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(compactedManager);
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);
        await WriteKeyStoreBlock(compactedManager, prunedKeyStore, kek);

        var readBackKeyStore = await ReadKeyStoreFromFile(compactedManager, kek);

        // ActiveEpoch in the new file's key store must match the re-encryption epoch
        Assert.Equal(1, readBackKeyStore.ActiveEpoch);

        // Every block's epoch must match the key store's ActiveEpoch
        foreach (var kvp in compactedManager.GetBlockLocations())
        {
            var readResult = await compactedManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.IsEncrypted
                && readResult.Value.Type != BlockType.KeyStore)
            {
                Assert.Equal(readBackKeyStore.ActiveEpoch, readResult.Value.KeyEpoch);
            }
        }
    }

    // --- Active epoch DEK always retained even with no block references ---

    [Fact]
    public async Task CompactWithReEncrypt_ActiveEpochDekRetainedInNewFileKeyStoreEvenIfUnreferenced()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();
        var kek = GenerateKek();

        // Simulate scenario: rotation to epoch 2 happened but no writes occurred
        // before compaction, so no blocks reference epoch 2 directly
        var originalKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Even with no block references at all, the active epoch must be retained
        var referencedEpochs = new HashSet<int>();
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);

        // Write to a file and read back
        var filePath = Path.Combine(_tempDir, $"ks_active_retained_{Guid.NewGuid():N}.emdb");
        using var manager = new RawBlockManager(filePath);
        await WriteKeyStoreBlock(manager, prunedKeyStore, kek);
        var readBackKeyStore = await ReadKeyStoreFromFile(manager, kek);

        // Active epoch must always be retained
        Assert.Equal(2, readBackKeyStore.ActiveEpoch);
        Assert.Single(readBackKeyStore.Entries);
        Assert.Equal(2, readBackKeyStore.Entries[0].Epoch);
        Assert.DoesNotContain(readBackKeyStore.Entries, e => e.Epoch == 0);
        Assert.DoesNotContain(readBackKeyStore.Entries, e => e.Epoch == 1);
    }

    // --- Data readable using only the key store written to the new file ---

    [Fact]
    public async Task CompactWithReEncrypt_DataReadableUsingOnlyNewFileKeyStore()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var kek = GenerateKek();

        var filePath = Path.Combine(_tempDir, $"ks_readable_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"ks_readable_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 10; i < 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Build original key store
        var originalKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Prune and write key store to compacted file
        using var compactedManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(compactedManager);
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);
        await WriteKeyStoreBlock(compactedManager, prunedKeyStore, kek);

        // Read the key store from the compacted file
        var readBackKeyStore = await ReadKeyStoreFromFile(compactedManager, kek);

        // Build a provider using ONLY the key store from the compacted file
        var dekEntries = readBackKeyStore.Entries
            .Select(e => ((byte)e.Epoch, e.DEK))
            .ToArray();
        using var prunedProvider = CreateProvider((byte)readBackKeyStore.ActiveEpoch, dekEntries);

        // Get the latest index root from the compacted file
        var latestRoot = await GetLatestIndexRoot(compactedManager, prunedProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            compactedManager, latestRoot,
            await GetLatestIndexRootOffset(compactedManager, prunedProvider),
            encryptionProvider: prunedProvider);

        // All 20 entries should be readable using only the new file's key store
        for (int i = 0; i < 20; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable using only the new file's key store: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    // --- Key store DEK bytes match for retained entries ---

    [Fact]
    public async Task CompactWithReEncrypt_NewFileKeyStoreDekBytesMatchOriginal()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var kek = GenerateKek();

        var filePath = Path.Combine(_tempDir, $"ks_dek_match_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"ks_dek_match_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 10; i < 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        var originalKeyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        using var compactedManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(compactedManager);
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);
        await WriteKeyStoreBlock(compactedManager, prunedKeyStore, kek);

        var readBackKeyStore = await ReadKeyStoreFromFile(compactedManager, kek);

        // The retained active DEK bytes must match the original
        var retainedEntry = readBackKeyStore.Entries.Single(e => e.Epoch == 1);
        Assert.Equal(dek1, retainedEntry.DEK);
    }

    #region Compaction with Re-encryption Helper

    private static async Task CompactLiveTreeToFileWithReEncrypt(
        RawBlockManager source, string destPath, IBlockEncryptionProvider encryptionProvider)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source, encryptionProvider);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUpWithReEncrypt(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight,
            encryptionProvider);

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = newRootOffset,
            EntryCount = latestRoot.Value.Root.EntryCount,
            TreeHeight = latestRoot.Value.Root.TreeHeight,
            RootNodeHash = newRootHash,
            PreviousRootHash = new byte[32],
            PreviousRootOffset = -1
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };

        if (encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(BlockType.IndexRoot))
        {
            rootBlock.Payload = encryptionProvider.Encrypt(rootPayload, BlockType.IndexRoot, rootBlock.BlockId);
            rootBlock.Flags |= Block.FlagEncrypted;
            rootBlock.SetKeyEpoch((byte)encryptionProvider.ActiveEpoch);
        }

        await dest.WriteBlockAsync(rootBlock);
    }

    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUpWithReEncrypt(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight,
        IBlockEncryptionProvider encryptionProvider)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        var sourceBlock = readResult.Value;

        byte[] decryptedPayload;
        if (sourceBlock.IsEncrypted)
        {
            decryptedPayload = encryptionProvider.Decrypt(
                sourceBlock.Payload, sourceBlock.Type, sourceBlock.BlockId, sourceBlock.KeyEpoch);
        }
        else
        {
            decryptedPayload = sourceBlock.Payload;
        }

        if (remainingHeight == 1)
        {
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId();
            byte[] newPayload;
            byte newFlags = 0;

            if (encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(sourceBlock.Type))
            {
                newPayload = encryptionProvider.Encrypt(decryptedPayload, sourceBlock.Type, newBlockId);
                newFlags = (byte)(Block.FlagEncrypted | ((byte)encryptionProvider.ActiveEpoch << 1));
            }
            else
            {
                newPayload = decryptedPayload;
            }

            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = newFlags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = newPayload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            var leaf = BTreeNodeSerializer.DeserializeLeaf(decryptedPayload);
            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(decryptedPayload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUpWithReEncrypt(
                    source, dest, positionToBlockId,
                    internalNode.ChildOffsets[i], remainingHeight - 1,
                    encryptionProvider);
                newChildOffsets[i] = childOffset;
                newChildHashes[i] = childHash;
            }

            var remappedNode = new BTreeInternalNode
            {
                NodeType = internalNode.NodeType,
                Version = internalNode.Version,
                KeyCount = internalNode.KeyCount,
                PrevChainHash = new byte[32],
                Keys = internalNode.Keys,
                ChildOffsets = newChildOffsets,
                ChildHashes = newChildHashes
            };
            remappedNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(remappedNode);

            var payload = BTreeNodeSerializer.SerializeInternal(remappedNode);
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId();
            byte[] newPayload;
            byte newFlags = 0;

            if (encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(sourceBlock.Type))
            {
                newPayload = encryptionProvider.Encrypt(payload, sourceBlock.Type, newBlockId);
                newFlags = (byte)(Block.FlagEncrypted | ((byte)encryptionProvider.ActiveEpoch << 1));
            }
            else
            {
                newPayload = payload;
            }

            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = newFlags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = newPayload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write internal node: {writeResult.Error}");

            return (writeResult.Value.Position, remappedNode.NodeContentHash);
        }
    }

    #endregion

    #region Index Root Helpers

    private static async Task<(IndexRoot Root, long Position)?> FindLatestIndexRoot(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var locations = rawBlockManager.GetBlockLocations();
        IndexRoot? latest = null;
        long maxPosition = -1;

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > maxPosition)
                {
                    maxPosition = kvp.Value.Position;
                    var payload = readResult.Value.Payload;
                    if (readResult.Value.IsEncrypted && encryptionProvider != null)
                    {
                        payload = encryptionProvider.Decrypt(
                            payload, readResult.Value.Type,
                            readResult.Value.BlockId, readResult.Value.KeyEpoch);
                    }
                    latest = BTreeNodeSerializer.DeserializeIndexRoot(payload);
                }
            }
        }

        return latest != null ? (latest, maxPosition) : null;
    }

    private static async Task<IndexRoot?> GetLatestIndexRoot(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var result = await FindLatestIndexRoot(rawBlockManager, encryptionProvider);
        return result?.Root;
    }

    private static async Task<long> GetLatestIndexRootOffset(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var result = await FindLatestIndexRoot(rawBlockManager, encryptionProvider);
        return result?.Position ?? -1;
    }

    #endregion
}
