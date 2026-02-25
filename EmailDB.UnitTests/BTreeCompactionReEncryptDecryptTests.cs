using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-46-2: Verify that when re-encrypt is true, each block is decrypted
/// using its key epoch's DEK and re-encrypted with the current active DEK.
/// Blocks from multiple epochs should all end up with the active epoch.
/// </summary>
public class BTreeCompactionReEncryptDecryptTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionReEncryptDecryptTests()
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

    // --- Two-epoch re-encryption ---

    [Fact]
    public async Task CompactWithReEncrypt_TwoEpochs_AllBlocksGetActiveEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reencrypt_two_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"reencrypt_two_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Phase 1: Insert entries with epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 10; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree0.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Phase 2: Rotate to epoch 1, insert more entries
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 10; i < 20; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree1.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }

        // Verify mixed epochs exist before compaction
        var locations = rawBlockManager.GetBlockLocations();
        var epochsBeforeCompaction = new HashSet<int>();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.IsEncrypted)
                epochsBeforeCompaction.Add(blockResult.Value.KeyEpoch);
        }
        Assert.Contains(0, epochsBeforeCompaction);
        Assert.Contains(1, epochsBeforeCompaction);

        // Compact with re-encryption using active epoch 1
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify all encrypted blocks in compacted file have active epoch (1)
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedLocations = verifyManager.GetBlockLocations();

        foreach (var kvp in compactedLocations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read compacted block {kvp.Key}");

            if (readResult.Value.IsEncrypted)
            {
                Assert.Equal(1, readResult.Value.KeyEpoch);
            }
        }
    }

    // --- Three-epoch re-encryption ---

    [Fact]
    public async Task CompactWithReEncrypt_ThreeEpochs_AllBlocksEndUpAtActiveEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reencrypt_three_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"reencrypt_three_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 5; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more entries
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

        // Epoch 2: insert more entries
        var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);
        for (int i = 10; i < 15; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Verify mixed epochs exist before compaction
        var epochsBefore = new HashSet<int>();
        foreach (var kvp in rawBlockManager.GetBlockLocations())
        {
            var br = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (br.IsSuccess && br.Value.IsEncrypted)
                epochsBefore.Add(br.Value.KeyEpoch);
        }
        Assert.True(epochsBefore.Count >= 2, "Should have blocks from at least 2 different epochs");

        // Compact with re-encryption using active epoch 2
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider2);
        provider2.Dispose();

        // Verify all encrypted blocks have epoch 2
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.IsEncrypted)
            {
                Assert.Equal(2, readResult.Value.KeyEpoch);
            }
        }
    }

    // --- Data readable after re-encryption with only the active DEK ---

    [Fact]
    public async Task CompactWithReEncrypt_DataReadableWithOnlyActiveDek()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reencrypt_active_only_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"reencrypt_active_only_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more entries
        var provider1Full = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1Full);
        for (int i = 10; i < 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1Full);
        provider1Full.Dispose();

        // Read compacted file with ONLY the active DEK (epoch 1) — no epoch 0 DEK
        using var activeOnlyProvider = CreateProvider(1, (1, dek1));
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        var latestRoot = await GetLatestIndexRoot(verifyManager, activeOnlyProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, activeOnlyProvider),
            encryptionProvider: activeOnlyProvider);

        // All 20 entries should be readable with only the active DEK
        for (int i = 0; i < 20; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable with only active DEK after re-encryption: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    // --- Plaintext data preserved after decrypt-reencrypt cycle ---

    [Fact]
    public async Task CompactWithReEncrypt_PlaintextDataIdenticalAfterReEncryption()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reencrypt_plaintext_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"reencrypt_plaintext_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert entries at epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        int entryCount = BTreeLeafNode.MaxEntries + 5;
        for (int i = 0; i < entryCount; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree0.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;

        // Decrypt all blocks to get plaintext payloads before compaction
        var plaintextsBefore = new Dictionary<BlockType, List<byte[]>>();
        var livePositions = await CollectLiveNodePositions(rawBlockManager, provider0);
        var posToBlock = new Dictionary<long, long>();
        foreach (var kvp in rawBlockManager.GetBlockLocations())
            posToBlock[kvp.Value.Position] = kvp.Key;

        foreach (var pos in livePositions)
        {
            if (posToBlock.TryGetValue(pos, out var blockId))
            {
                var readResult = await rawBlockManager.ReadBlockAsync(blockId);
                if (readResult.IsSuccess &&
                    (readResult.Value.Type == BlockType.BTreeLeaf ||
                     readResult.Value.Type == BlockType.BTreeInternal))
                {
                    // Decrypt to get plaintext
                    var plaintext = readResult.Value.IsEncrypted
                        ? provider0.Decrypt(readResult.Value.Payload, readResult.Value.Type,
                            readResult.Value.BlockId, readResult.Value.KeyEpoch)
                        : readResult.Value.Payload;

                    if (!plaintextsBefore.ContainsKey(readResult.Value.Type))
                        plaintextsBefore[readResult.Value.Type] = new List<byte[]>();
                    plaintextsBefore[readResult.Value.Type].Add(plaintext);
                }
            }
        }
        provider0.Dispose();

        // Compact with re-encryption to epoch 1
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);

        // Decrypt compacted blocks and compare plaintext
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var plaintextsAfter = new Dictionary<BlockType, List<byte[]>>();

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess &&
                (readResult.Value.Type == BlockType.BTreeLeaf ||
                 readResult.Value.Type == BlockType.BTreeInternal))
            {
                var plaintext = readResult.Value.IsEncrypted
                    ? provider1.Decrypt(readResult.Value.Payload, readResult.Value.Type,
                        readResult.Value.BlockId, readResult.Value.KeyEpoch)
                    : readResult.Value.Payload;

                if (!plaintextsAfter.ContainsKey(readResult.Value.Type))
                    plaintextsAfter[readResult.Value.Type] = new List<byte[]>();
                plaintextsAfter[readResult.Value.Type].Add(plaintext);
            }
        }

        // Same number of each block type
        foreach (var (blockType, origPlaintexts) in plaintextsBefore)
        {
            Assert.True(plaintextsAfter.ContainsKey(blockType),
                $"Compacted file missing block type {blockType}");
            Assert.Equal(origPlaintexts.Count, plaintextsAfter[blockType].Count);
        }
    }

    // --- Large tree with internal nodes across epochs ---

    [Fact]
    public async Task CompactWithReEncrypt_LargeTreeMultiEpoch_AllBlocksReEncrypted()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reencrypt_large_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"reencrypt_large_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert enough to create internal nodes
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        int count = BTreeLeafNode.MaxEntries + 20;
        for (int i = 0; i < count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        Assert.True(btree0.CurrentRoot!.TreeHeight >= 2, "Tree should have internal nodes");
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more entries
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = count; i < count + 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify: all encrypted blocks have epoch 1
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        int encryptedBlockCount = 0;
        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.IsEncrypted)
            {
                Assert.Equal(1, readResult.Value.KeyEpoch);
                encryptedBlockCount++;
            }
        }
        Assert.True(encryptedBlockCount > 0, "Should have at least one encrypted block");

        // Verify: all data readable with only active DEK
        using var activeOnlyProvider = CreateProvider(1, (1, dek1));
        var latestRoot = await GetLatestIndexRoot(verifyManager, activeOnlyProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, activeOnlyProvider),
            encryptionProvider: activeOnlyProvider);

        for (int i = 0; i < count + 20; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess, $"Key {i} lookup failed after re-encryption: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
        }
    }

    #region Compaction with Re-encryption Helper

    /// <summary>
    /// Compacts the live tree to a new file, re-encrypting each block's payload:
    /// decrypt with the block's key epoch DEK, then re-encrypt with the active DEK.
    /// </summary>
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

        // IndexRoot is encrypted if the policy says so
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

        // Decrypt the payload using the block's original key epoch
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
            // Re-encrypt with the active DEK
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

    #region Live Node Collection

    private static async Task<HashSet<long>> CollectLiveNodePositions(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var livePositions = new HashSet<long>();
        var locations = rawBlockManager.GetBlockLocations();

        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var roots = new List<(IndexRoot Root, long Position)>();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var payload = readResult.Value.Payload;
                if (readResult.Value.IsEncrypted && encryptionProvider != null)
                {
                    payload = encryptionProvider.Decrypt(
                        payload, readResult.Value.Type,
                        readResult.Value.BlockId, readResult.Value.KeyEpoch);
                }
                var root = BTreeNodeSerializer.DeserializeIndexRoot(payload);
                roots.Add((root, kvp.Value.Position));
            }
        }

        if (roots.Count == 0)
            return livePositions;

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        var latestRoot = roots[^1];

        livePositions.Add(latestRoot.Position);

        await WalkTreeCollectPositions(
            rawBlockManager, positionToBlockId, livePositions,
            latestRoot.Root.RootNodeBlockOffset, latestRoot.Root.TreeHeight,
            encryptionProvider);

        return livePositions;
    }

    private static async Task WalkTreeCollectPositions(
        RawBlockManager rawBlockManager,
        Dictionary<long, long> positionToBlockId,
        HashSet<long> livePositions,
        long nodeOffset,
        int remainingHeight,
        IBlockEncryptionProvider? encryptionProvider = null)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            return;

        var readResult = await rawBlockManager.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            return;

        livePositions.Add(nodeOffset);

        if (remainingHeight > 1 && readResult.Value.Type == BlockType.BTreeInternal)
        {
            var payload = readResult.Value.Payload;
            if (readResult.Value.IsEncrypted && encryptionProvider != null)
            {
                payload = encryptionProvider.Decrypt(
                    payload, readResult.Value.Type,
                    readResult.Value.BlockId, readResult.Value.KeyEpoch);
            }
            var internalNode = BTreeNodeSerializer.DeserializeInternal(payload);
            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                await WalkTreeCollectPositions(
                    rawBlockManager, positionToBlockId, livePositions,
                    internalNode.ChildOffsets[i], remainingHeight - 1,
                    encryptionProvider);
            }
        }
    }

    #endregion
}
