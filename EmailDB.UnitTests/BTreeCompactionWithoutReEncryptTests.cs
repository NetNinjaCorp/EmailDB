using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-46-7: Verify that when re-encrypt is false (default), compaction does
/// normal space reclamation without touching encryption — blocks retain their
/// original key epochs and all DEKs remain in the key store.
/// </summary>
public class BTreeCompactionWithoutReEncryptTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionWithoutReEncryptTests()
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
    /// Collects epochs only from leaf blocks (which are copied as-is during compaction
    /// without re-encrypt, preserving their original epoch).
    /// </summary>
    private static async Task<HashSet<int>> CollectLeafEpochs(RawBlockManager manager)
    {
        var epochs = new HashSet<int>();
        foreach (var kvp in manager.GetBlockLocations())
        {
            var readResult = await manager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.IsEncrypted &&
                readResult.Value.Type == BlockType.BTreeLeaf)
                epochs.Add(readResult.Value.KeyEpoch);
        }
        return epochs;
    }

    // --- Compaction without re-encrypt preserves leaf block epochs ---

    [Fact]
    public async Task CompactWithoutReEncrypt_LeafBlocksRetainOriginalEpochs()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"preserve_epochs_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"preserve_epochs_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert many entries at epoch 0 to create multiple leaf blocks
        int epoch0Count = BTreeLeafNode.MaxEntries * 5;
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < epoch0Count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert only a few entries (touches only a few leaves)
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = epoch0Count; i < epoch0Count + 3; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }
        int totalEntries = epoch0Count + 3;

        // Compact WITHOUT re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // After compaction: untouched leaves should still have epoch 0
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var leafEpochs = await CollectLeafEpochs(verifyManager);

        Assert.Contains(0, leafEpochs);
        Assert.True(leafEpochs.Count >= 1, "Should have leaf blocks with preserved epochs");
    }

    // --- All data readable after compaction without re-encrypt with all DEKs ---

    [Fact]
    public async Task CompactWithoutReEncrypt_AllDataReadableWithAllDeks()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"readable_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"readable_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert many entries at epoch 0
        int epoch0Count = BTreeLeafNode.MaxEntries * 5;
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < epoch0Count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert a few more
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = epoch0Count; i < epoch0Count + 3; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }
        int totalEntries = epoch0Count + 3;

        // Compact WITHOUT re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify all entries are readable with both DEKs
        using var bothDeksProvider = CreateProvider(1, (0, dek0), (1, dek1));
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        var latestRoot = await GetLatestIndexRoot(verifyManager, bothDeksProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, bothDeksProvider),
            encryptionProvider: bothDeksProvider);

        for (int i = 0; i < totalEntries; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable with both DEKs after compaction: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    // --- Key store cannot be pruned: all DEKs remain needed ---

    [Fact]
    public async Task CompactWithoutReEncrypt_AllDeksRetainedInKeyStore()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"no_prune_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"no_prune_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert many entries at epoch 0 (creates multiple leaf blocks)
        int epoch0Count = BTreeLeafNode.MaxEntries * 5;
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < epoch0Count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert a few
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = epoch0Count; i < epoch0Count + 3; i++)
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

        // Compact WITHOUT re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Scan referenced epochs in compacted file
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var referencedEpochs = await CollectReferencedEpochs(verifyManager);

        // Epoch 0 still referenced (leaf blocks preserved as-is)
        Assert.Contains(0, referencedEpochs);
        Assert.Contains(1, referencedEpochs);

        // Pruning should retain all entries — no DEKs can be safely removed
        var prunedKeyStore = PruneKeyStore(originalKeyStore, referencedEpochs);
        Assert.Equal(2, prunedKeyStore.Entries.Count);
        Assert.Contains(prunedKeyStore.Entries, e => e.Epoch == 0);
        Assert.Contains(prunedKeyStore.Entries, e => e.Epoch == 1);
    }

    // --- Epoch-0 blocks fail decryption without the epoch-0 DEK ---

    [Fact]
    public async Task CompactWithoutReEncrypt_ActiveOnlyDekCannotDecryptOldEpochBlocks()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"active_only_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"active_only_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert many at epoch 0
        int epoch0Count = BTreeLeafNode.MaxEntries * 5;
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < epoch0Count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert a few
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = epoch0Count; i < epoch0Count + 3; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact WITHOUT re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify epoch 0 blocks still exist
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var epochs = await CollectReferencedEpochs(verifyManager);
        Assert.Contains(0, epochs);

        // Attempting to decrypt epoch-0 blocks with only the active DEK (epoch 1) must fail
        using var activeOnlyProvider = CreateProvider(1, (1, dek1));
        bool decryptionFailed = false;

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.IsEncrypted &&
                readResult.Value.KeyEpoch == 0)
            {
                try
                {
                    activeOnlyProvider.Decrypt(
                        readResult.Value.Payload, readResult.Value.Type,
                        readResult.Value.BlockId, readResult.Value.KeyEpoch);
                }
                catch (Exception ex) when (ex is CryptographicException or KeyNotFoundException)
                {
                    decryptionFailed = true;
                    break;
                }
            }
        }

        Assert.True(decryptionFailed,
            "Decrypting epoch-0 blocks with only the active DEK should fail, " +
            "proving that without re-encrypt, old DEKs are still required");
    }

    // --- Leaf Flags bytes are copied byte-for-byte ---

    [Fact]
    public async Task CompactWithoutReEncrypt_LeafFlagsBytesPreservedExactly()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"flags_exact_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"flags_exact_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert many at epoch 0
        int epoch0Count = BTreeLeafNode.MaxEntries * 5;
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < epoch0Count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert a few
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = epoch0Count; i < epoch0Count + 3; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact WITHOUT re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify: after compaction, leaf blocks with epoch 0 have exact epoch-0 Flags byte
        byte epoch0Flags = (byte)(Block.FlagEncrypted | (0 << 1)); // 0x01
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        bool foundEpoch0Leaf = false;

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.BTreeLeaf &&
                readResult.Value.IsEncrypted && readResult.Value.KeyEpoch == 0)
            {
                Assert.Equal(epoch0Flags, readResult.Value.Flags);
                foundEpoch0Leaf = true;
            }
        }

        Assert.True(foundEpoch0Leaf,
            "Should find at least one leaf block with preserved epoch-0 Flags");
    }

    // --- Three-epoch: untouched leaves preserve all original epochs ---

    [Fact]
    public async Task CompactWithoutReEncrypt_ThreeEpochs_UntouchedLeavesPreserved()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"three_epoch_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"three_epoch_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: many entries
        int epoch0Count = BTreeLeafNode.MaxEntries * 5;
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < epoch0Count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: a few entries
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = epoch0Count; i < epoch0Count + 2; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root1 = btree1.CurrentRoot;
        provider1.Dispose();

        // Epoch 2: a few entries
        var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);
        for (int i = epoch0Count + 2; i < epoch0Count + 4; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        int totalEntries = epoch0Count + 4;

        // Compact WITHOUT re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider2);
        provider2.Dispose();

        // Verify: leaf blocks still have epoch 0 (untouched during epoch 1/2 inserts)
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var leafEpochs = await CollectLeafEpochs(verifyManager);

        Assert.Contains(0, leafEpochs);

        // Verify data is readable with all DEKs
        using var allDeksProvider = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var latestRoot = await GetLatestIndexRoot(verifyManager, allDeksProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, allDeksProvider),
            encryptionProvider: allDeksProvider);

        for (int i = 0; i < totalEntries; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable with all DEKs after 3-epoch compaction: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    #region Key Store Pruning Helper

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

    #endregion

    #region Compaction Helpers

    private static async Task CompactLiveTreeToFilePreservingEncryption(
        RawBlockManager source, string destPath, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source, encryptionProvider);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUpPreservingEncryption(
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

        if (encryptionProvider != null && encryptionProvider.IsEnabled &&
            encryptionProvider.ShouldEncrypt(BlockType.IndexRoot))
        {
            rootBlock.Payload = encryptionProvider.Encrypt(rootPayload, BlockType.IndexRoot, rootBlock.BlockId);
            rootBlock.Flags |= Block.FlagEncrypted;
            rootBlock.SetKeyEpoch((byte)encryptionProvider.ActiveEpoch);
        }

        await dest.WriteBlockAsync(rootBlock);
    }

    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUpPreservingEncryption(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight,
        IBlockEncryptionProvider? encryptionProvider = null)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        var sourceBlock = readResult.Value;

        if (remainingHeight == 1)
        {
            // Leaf: copy as-is (payload stays encrypted with original epoch)
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId();
            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = sourceBlock.Flags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = sourceBlock.Payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            byte[] decryptedPayload = sourceBlock.IsEncrypted && encryptionProvider != null
                ? encryptionProvider.Decrypt(sourceBlock.Payload, sourceBlock.Type,
                    sourceBlock.BlockId, sourceBlock.KeyEpoch)
                : sourceBlock.Payload;

            var leaf = BTreeNodeSerializer.DeserializeLeaf(decryptedPayload);
            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            byte[] decryptedPayload = sourceBlock.IsEncrypted && encryptionProvider != null
                ? encryptionProvider.Decrypt(sourceBlock.Payload, sourceBlock.Type,
                    sourceBlock.BlockId, sourceBlock.KeyEpoch)
                : sourceBlock.Payload;

            var internalNode = BTreeNodeSerializer.DeserializeInternal(decryptedPayload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUpPreservingEncryption(
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
            byte newFlags = 0;
            byte[] newPayload;

            if (sourceBlock.IsEncrypted && encryptionProvider != null &&
                encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(sourceBlock.Type))
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
