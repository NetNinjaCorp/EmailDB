using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-46-5: Verify that after compaction with re-encrypt, DEKs that no
/// longer have any blocks referencing them are removed from the key store.
/// </summary>
public class BTreeCompactionReEncryptDekPruningTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionReEncryptDekPruningTests()
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
    /// block in the file. This simulates the DEK pruning step after compaction.
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

    // --- Two-epoch: after re-encrypt, epoch 0 DEK pruned ---

    [Fact]
    public async Task CompactWithReEncrypt_TwoEpochs_RetiredEpoch0DekPrunedFromKeyStore()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"prune_two_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"prune_two_compacted_{Guid.NewGuid():N}.emdb");
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

        // Verify both epochs referenced before compaction
        var epochsBefore = await CollectReferencedEpochs(rawBlockManager);
        Assert.Contains(0, epochsBefore);
        Assert.Contains(1, epochsBefore);

        // Build the original key store (has both epoch 0 and 1)
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

        // After re-encryption, scan the compacted file for referenced epochs
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(verifyManager);

        // Only epoch 1 should be referenced
        Assert.DoesNotContain(0, referencedEpochs);
        Assert.Contains(1, referencedEpochs);

        // Prune the key store
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);

        // Verify: epoch 0 DEK was pruned
        Assert.DoesNotContain(prunedKeyStore.Entries, e => e.Epoch == 0);
        Assert.Single(prunedKeyStore.Entries);
        Assert.Equal(1, prunedKeyStore.Entries[0].Epoch);
        Assert.Equal(1, prunedKeyStore.ActiveEpoch);
    }

    // --- Three-epoch: after re-encrypt to epoch 2, epochs 0 and 1 pruned ---

    [Fact]
    public async Task CompactWithReEncrypt_ThreeEpochs_RetiredEpochs0And1DeksPruned()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"prune_three_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"prune_three_compacted_{Guid.NewGuid():N}.emdb");
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

        // Verify multiple epochs before compaction
        var epochsBefore = await CollectReferencedEpochs(rawBlockManager);
        Assert.True(epochsBefore.Count >= 2, "Should have blocks from at least 2 epochs");

        // Build full key store
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

        // Scan compacted file
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(verifyManager);

        // Only epoch 2 should remain referenced
        Assert.DoesNotContain(0, referencedEpochs);
        Assert.DoesNotContain(1, referencedEpochs);
        Assert.Contains(2, referencedEpochs);

        // Prune the key store
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);

        // Both old DEKs pruned, only active epoch remains
        Assert.Single(prunedKeyStore.Entries);
        Assert.Equal(2, prunedKeyStore.Entries[0].Epoch);
        Assert.Equal(2, prunedKeyStore.ActiveEpoch);
    }

    // --- Active epoch always retained even if no encrypted blocks exist ---

    [Fact]
    public async Task PruneKeyStore_ActiveEpochAlwaysRetainedEvenIfNoBlocksReferenceIt()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        // Simulate a scenario where the active epoch has no encrypted blocks yet
        // (e.g., rotation happened but no new writes before compaction)
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

        // After re-encryption all blocks reference epoch 2, but test the edge case
        // where referenced epochs is empty (hypothetical empty file)
        var referencedEpochs = new HashSet<int>(); // no blocks at all

        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);

        // Active epoch must always be retained
        Assert.Single(prunedKeyStore.Entries);
        Assert.Equal(2, prunedKeyStore.Entries[0].Epoch);
        Assert.Equal(2, prunedKeyStore.ActiveEpoch);
    }

    // --- Pruned key store still allows decryption of all compacted blocks ---

    [Fact]
    public async Task CompactWithReEncrypt_PrunedKeyStoreStillAllowsFullDecryption()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"prune_decrypt_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"prune_decrypt_compacted_{Guid.NewGuid():N}.emdb");
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

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Build pruned key store with ONLY the active DEK
        using var prunedProvider = CreateProvider(1, (1, dek1));
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        var latestRoot = await GetLatestIndexRoot(verifyManager, prunedProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, prunedProvider),
            encryptionProvider: prunedProvider);

        // All 20 entries should be readable with the pruned (active-only) key store
        for (int i = 0; i < 20; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable with pruned key store: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    // --- Pruned key store entry count matches exactly the number of referenced epochs ---

    [Fact]
    public async Task CompactWithReEncrypt_PrunedKeyStoreEntryCountMatchesReferencedEpochs()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"prune_count_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"prune_count_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert across 3 epochs
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 8; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 8; i < 16; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root1 = btree1.CurrentRoot;
        provider1.Dispose();

        var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);
        for (int i = 16; i < 24; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Original key store has 3 entries
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
        Assert.Equal(3, originalKeyStore.Entries.Count);

        // Compact with re-encryption to epoch 2
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider2);
        provider2.Dispose();

        // Scan and prune
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(verifyManager);
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);

        // After re-encryption, only 1 epoch is referenced (the active one)
        Assert.Equal(1, referencedEpochs.Count);
        Assert.Equal(1, prunedKeyStore.Entries.Count);
        Assert.Equal(2, prunedKeyStore.Entries[0].Epoch);

        // Verify the reduction: from 3 entries down to 1
        Assert.True(prunedKeyStore.Entries.Count < originalKeyStore.Entries.Count,
            "Pruned key store should have fewer entries than the original");
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
