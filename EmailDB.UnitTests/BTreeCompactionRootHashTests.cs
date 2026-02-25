using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "New IndexRoot in compacted file has correct
/// root hash". After a live-tree compaction (copying only reachable nodes with offset
/// remapping and Merkle hash recomputation), the single IndexRoot in the compacted
/// file must have a RootNodeHash that exactly matches the BLAKE3 content hash of the
/// root node block it references.
/// </summary>
public class BTreeCompactionRootHashTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionRootHashTests()
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

    // --- IndexRoot.RootNodeHash matches recomputed hash of root node ---

    [Fact]
    public async Task Compaction_IndexRootHash_MatchesRecomputedRootNodeHash()
    {
        var filePath = Path.Combine(_tempDir, "root_hash_match.emdb");
        var compactedPath = Path.Combine(_tempDir, "root_hash_match_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        var indexRoot = roots[0].Root;
        var rootBlock = await ReadBlockAtOffset(verifyManager, indexRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        byte[] recomputedHash;
        if (indexRoot.TreeHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock!.Payload);
            recomputedHash = BTreeHasher.ComputeLeafContentHash(leaf);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
            recomputedHash = BTreeHasher.ComputeInternalContentHash(internalNode);
        }

        Assert.Equal(indexRoot.RootNodeHash, recomputedHash);
    }

    // --- Root node's stored NodeContentHash matches IndexRoot.RootNodeHash ---

    [Fact]
    public async Task Compaction_RootNodeStoredHash_MatchesIndexRootHash()
    {
        var filePath = Path.Combine(_tempDir, "stored_hash_match.emdb");
        var compactedPath = Path.Combine(_tempDir, "stored_hash_match_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        var indexRoot = roots[0].Root;
        var rootBlock = await ReadBlockAtOffset(verifyManager, indexRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        // The root should be internal (height > 1 after MaxEntries + 10 inserts)
        Assert.True(indexRoot.TreeHeight >= 2, "Expected multi-level tree");
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Three-way check: IndexRoot hash == stored NodeContentHash == recomputed hash
        var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
        Assert.Equal(indexRoot.RootNodeHash, internalNode.NodeContentHash);
        Assert.Equal(indexRoot.RootNodeHash, recomputed);
    }

    // --- After inserts and deletes, compacted root hash is still correct ---

    [Fact]
    public async Task Compaction_AfterInsertsAndDeletes_RootHashIsCorrect()
    {
        var filePath = Path.Combine(_tempDir, "mutations_root_hash.emdb");
        var compactedPath = Path.Combine(_tempDir, "mutations_root_hash_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Delete some entries to create dead nodes
            for (int i = 1; i <= 10; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            // Insert more entries
            for (int i = 300; i < 310; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        var indexRoot = roots[0].Root;
        var rootBlock = await ReadBlockAtOffset(verifyManager, indexRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        byte[] recomputedHash;
        if (indexRoot.TreeHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock!.Payload);
            recomputedHash = BTreeHasher.ComputeLeafContentHash(leaf);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
            recomputedHash = BTreeHasher.ComputeInternalContentHash(internalNode);
        }

        Assert.Equal(indexRoot.RootNodeHash, recomputedHash);
    }

    // --- Three-level tree: compacted root hash is correct ---

    [Fact]
    public async Task Compaction_ThreeLevelTree_RootHashIsCorrect()
    {
        var filePath = Path.Combine(_tempDir, "3level_root_hash.emdb");
        var compactedPath = Path.Combine(_tempDir, "3level_root_hash_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            int totalInserts = 0;
            while (totalInserts < 5000)
            {
                var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
                Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
                totalInserts++;
                if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3,
                "Expected at least 3-level tree");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        var indexRoot = roots[0].Root;
        Assert.True(indexRoot.TreeHeight >= 3, "Compacted tree should preserve height");

        var rootBlock = await ReadBlockAtOffset(verifyManager, indexRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);

        Assert.Equal(indexRoot.RootNodeHash, recomputed);
        Assert.Equal(indexRoot.RootNodeHash, internalNode.NodeContentHash);
    }

    // --- Compacted root hash differs from source root hash (tree was rewritten) ---

    [Fact]
    public async Task Compaction_RootHash_DiffersFromSourceRootHash()
    {
        var filePath = Path.Combine(_tempDir, "root_hash_differs.emdb");
        var compactedPath = Path.Combine(_tempDir, "root_hash_differs_compacted.emdb");

        byte[] sourceRootHash;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            sourceRootHash = btreeIndex.CurrentRoot!.RootNodeHash;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        // The compacted root hash should differ because internal nodes have
        // new offsets and zeroed PrevChainHash, changing their content hashes
        Assert.NotEqual(sourceRootHash, roots[0].Root.RootNodeHash);

        // But it must still be valid (matches actual root node)
        var rootBlock = await ReadBlockAtOffset(verifyManager, roots[0].Root.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
        Assert.Equal(roots[0].Root.RootNodeHash, recomputed);
    }

    // --- Root hash is valid 32-byte non-zero BLAKE3 hash ---

    [Fact]
    public async Task Compaction_RootHash_Is32ByteNonZero()
    {
        var filePath = Path.Combine(_tempDir, "root_hash_format.emdb");
        var compactedPath = Path.Combine(_tempDir, "root_hash_format_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        Assert.Equal(32, roots[0].Root.RootNodeHash.Length);
        Assert.False(roots[0].Root.RootNodeHash.All(b => b == 0),
            "Compacted IndexRoot's RootNodeHash must be non-zero");
    }

    #region Compaction Helper

    private static async Task CompactLiveTreeToFile(RawBlockManager source, string destPath)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUp(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight);

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
        await dest.WriteBlockAsync(rootBlock);
    }

    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUp(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        if (remainingHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
            leaf.PrevChainHash = new byte[32];
            var payload = BTreeNodeSerializer.SerializeLeaf(leaf);

            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
                Payload = payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUp(
                    source, dest, positionToBlockId,
                    internalNode.ChildOffsets[i], remainingHeight - 1);
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
            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId(),
                Payload = payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write internal node: {writeResult.Error}");

            return (writeResult.Value.Position, remappedNode.NodeContentHash);
        }
    }

    private static async Task<(IndexRoot Root, long Position)?> FindLatestIndexRoot(RawBlockManager rawBlockManager)
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
                    latest = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                }
            }
        }

        return latest != null ? (latest, maxPosition) : null;
    }

    #endregion

    #region Verification Helpers

    private class IndexRootEntry
    {
        public IndexRoot Root { get; }
        public long BlockOffset { get; }

        public IndexRootEntry(IndexRoot root, long blockOffset)
        {
            Root = root;
            BlockOffset = blockOffset;
        }
    }

    private static async Task<List<IndexRootEntry>> CollectIndexRoots(RawBlockManager rawBlockManager)
    {
        var roots = new List<(IndexRoot Root, long Position)>();
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var root = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                roots.Add((root, kvp.Value.Position));
            }
        }

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        return roots.Select(r => new IndexRootEntry(r.Root, r.Position)).ToList();
    }

    private static async Task<Block?> ReadBlockAtOffset(RawBlockManager rawBlockManager, long offset)
    {
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            if (kvp.Value.Position == offset)
            {
                var result = await rawBlockManager.ReadBlockAsync(kvp.Key);
                return result.IsSuccess ? result.Value : null;
            }
        }
        return null;
    }

    #endregion
}
