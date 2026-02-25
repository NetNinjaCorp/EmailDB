using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeInsertTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeInsertTests()
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

    [Fact]
    public async Task SingleInsert_EmptyTree_CreatesRootLeafNode()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        var key = new EmailHashedID(10, 20, 30, 40);
        long contentBlockOffset = 4096;
        long contentBlockId = 999;

        // Act
        var result = await btreeIndex.InsertAsync(key, contentBlockOffset, contentBlockId);

        // Assert — insert succeeded
        Assert.True(result.IsSuccess, $"Insert failed: {result.Error}");

        // Assert — IndexRoot was created
        var root = btreeIndex.CurrentRoot;
        Assert.NotNull(root);

        // Assert — tree has exactly one entry and height 1 (single leaf = root)
        Assert.Equal(1L, root.EntryCount);
        Assert.Equal((ushort)1, root.TreeHeight);

        // Assert — no previous root (first insert)
        Assert.Equal(-1L, root.PreviousRootOffset);
        Assert.All(root.PreviousRootHash, b => Assert.Equal(0, b));

        // Assert — root hash is non-zero (BLAKE3 computed)
        Assert.False(root.RootNodeHash.All(b => b == 0), "Root node hash should not be all zeros");

        // Assert — we can read back the leaf block and verify its contents
        var locations = rawBlockManager.GetBlockLocations();
        Block? leafBlock = null;
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.BTreeLeaf)
            {
                leafBlock = readResult.Value;
                break;
            }
        }

        Assert.NotNull(leafBlock);
        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Payload);

        // Assert — leaf is correctly typed
        Assert.Equal((byte)BlockType.BTreeLeaf, leaf.NodeType);
        Assert.Equal((ushort)1, leaf.Version);

        // Assert — leaf has exactly one entry
        Assert.Equal((ushort)1, leaf.EntryCount);
        Assert.Single(leaf.Entries);

        // Assert — the entry matches what was inserted
        Assert.Equal(key, leaf.Entries[0].Key);
        Assert.Equal(contentBlockOffset, leaf.Entries[0].BlockOffset);
        Assert.Equal(contentBlockId, leaf.Entries[0].BlockId);

        // Assert — content hash matches recomputed hash (integrity)
        var recomputedHash = BTreeHasher.ComputeLeafContentHash(leaf);
        Assert.Equal(recomputedHash, leaf.NodeContentHash);

        // Assert — prev chain hash is zero (first node)
        Assert.All(leaf.PrevChainHash, b => Assert.Equal(0, b));

        // Assert — IndexRoot points to this leaf block
        Assert.Equal(root.RootNodeBlockOffset, locations.First(kvp =>
        {
            var r = rawBlockManager.ReadBlockAsync(kvp.Key).GetAwaiter().GetResult();
            return r.IsSuccess && r.Value.Type == BlockType.BTreeLeaf;
        }).Value.Position);
    }

    [Fact]
    public async Task SingleInsert_EmptyTree_WritesExactlyTwoBlocks()
    {
        // A single insert into an empty tree should produce exactly 2 blocks:
        // 1 BTreeLeaf + 1 IndexRoot
        var filePath = Path.Combine(_tempDir, "test_block_count.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        var key = new EmailHashedID(1, 2, 3, 4);

        var result = await btreeIndex.InsertAsync(key, 0, 100);

        Assert.True(result.IsSuccess);

        var locations = rawBlockManager.GetBlockLocations();
        Assert.Equal(2, locations.Count);

        // Verify block types
        var types = new List<BlockType>();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);
            types.Add(readResult.Value.Type);
        }

        Assert.Contains(BlockType.BTreeLeaf, types);
        Assert.Contains(BlockType.IndexRoot, types);
    }

    [Fact]
    public async Task InsertIntoFullLeaf_TriggersSplit_ProducesTwoLeavesAndNewInternalRoot()
    {
        // Arrange — fill a leaf to MaxEntries, then insert one more to trigger split
        var filePath = Path.Combine(_tempDir, "test_leaf_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert MaxEntries keys to completely fill the root leaf
        for (int i = 0; i < BTreeLeafNode.MaxEntries; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");
        }

        // Verify the tree is still height 1 (single leaf root) with MaxEntries entries
        var rootBeforeSplit = btreeIndex.CurrentRoot;
        Assert.NotNull(rootBeforeSplit);
        Assert.Equal((ushort)1, rootBeforeSplit.TreeHeight);
        Assert.Equal((long)BTreeLeafNode.MaxEntries, rootBeforeSplit.EntryCount);

        // Act — insert one more key to trigger the split
        var splitKey = new EmailHashedID(0, 0, 0, 1); // Sorts before all existing keys
        var splitResult = await btreeIndex.InsertAsync(splitKey, 9999, 9999);

        // Assert — split succeeded
        Assert.True(splitResult.IsSuccess, $"Split insert failed: {splitResult.Error}");

        // Assert — tree height is now 2 (internal root + leaf children)
        var rootAfterSplit = btreeIndex.CurrentRoot;
        Assert.NotNull(rootAfterSplit);
        Assert.Equal((ushort)2, rootAfterSplit.TreeHeight);
        Assert.Equal((long)(BTreeLeafNode.MaxEntries + 1), rootAfterSplit.EntryCount);

        // Assert — previous root chain is maintained
        Assert.NotEqual(-1L, rootAfterSplit.PreviousRootOffset);
        Assert.False(rootAfterSplit.PreviousRootHash.All(b => b == 0),
            "Previous root hash should not be all zeros after split");

        // Find and read all blocks by type
        var leafBlocks = new List<(long blockId, Block block, long position)>();
        var internalBlocks = new List<(long blockId, Block block, long position)>();
        var indexRootBlocks = new List<(long blockId, Block block, long position)>();
        foreach (var kvp in rawBlockManager.GetBlockLocations())
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);
            switch (readResult.Value.Type)
            {
                case BlockType.BTreeLeaf:
                    leafBlocks.Add((kvp.Key, readResult.Value, kvp.Value.Position));
                    break;
                case BlockType.BTreeInternal:
                    internalBlocks.Add((kvp.Key, readResult.Value, kvp.Value.Position));
                    break;
                case BlockType.IndexRoot:
                    indexRootBlocks.Add((kvp.Key, readResult.Value, kvp.Value.Position));
                    break;
            }
        }

        // Assert — exactly 1 internal node was created (the new root)
        Assert.Single(internalBlocks);

        // Assert — the two newest leaf blocks are the split result
        // (older leaf blocks are dead space from prior inserts — append-only)
        // Find the two leaf blocks pointed to by the internal node
        var internalNode = BTreeNodeSerializer.DeserializeInternal(internalBlocks[0].block.Payload);
        Assert.Equal((ushort)1, internalNode.KeyCount); // One separator key
        Assert.Equal(2, internalNode.ChildOffsets.Length); // Two children
        Assert.Equal(2, internalNode.ChildHashes.Length);

        // Read the left and right child leaf nodes
        Block? leftLeafBlock = null;
        Block? rightLeafBlock = null;
        foreach (var (bid, block, pos) in leafBlocks)
        {
            if (pos == internalNode.ChildOffsets[0]) leftLeafBlock = block;
            if (pos == internalNode.ChildOffsets[1]) rightLeafBlock = block;
        }

        Assert.NotNull(leftLeafBlock);
        Assert.NotNull(rightLeafBlock);

        var leftLeaf = BTreeNodeSerializer.DeserializeLeaf(leftLeafBlock.Payload);
        var rightLeaf = BTreeNodeSerializer.DeserializeLeaf(rightLeafBlock.Payload);

        // Assert — total entries across both leaves equals MaxEntries + 1
        Assert.Equal(BTreeLeafNode.MaxEntries + 1, leftLeaf.EntryCount + rightLeaf.EntryCount);

        // Assert — both leaves are non-empty
        Assert.True(leftLeaf.EntryCount > 0, "Left leaf should have entries");
        Assert.True(rightLeaf.EntryCount > 0, "Right leaf should have entries");

        // Assert — left leaf's keys are all less than the separator key
        var separatorKey = internalNode.Keys[0];
        for (int i = 0; i < leftLeaf.EntryCount; i++)
        {
            Assert.True(leftLeaf.Entries[i].Key.CompareTo(separatorKey) < 0,
                $"Left leaf entry {i} should be < separator key");
        }

        // Assert — right leaf's keys are all >= the separator key
        for (int i = 0; i < rightLeaf.EntryCount; i++)
        {
            Assert.True(rightLeaf.Entries[i].Key.CompareTo(separatorKey) >= 0,
                $"Right leaf entry {i} should be >= separator key");
        }

        // Assert — keys are sorted within each leaf
        for (int i = 1; i < leftLeaf.EntryCount; i++)
        {
            Assert.True(leftLeaf.Entries[i - 1].Key.CompareTo(leftLeaf.Entries[i].Key) < 0,
                $"Left leaf entries should be sorted at position {i}");
        }
        for (int i = 1; i < rightLeaf.EntryCount; i++)
        {
            Assert.True(rightLeaf.Entries[i - 1].Key.CompareTo(rightLeaf.Entries[i].Key) < 0,
                $"Right leaf entries should be sorted at position {i}");
        }

        // Assert — separator key equals the first key of the right leaf
        Assert.Equal(rightLeaf.Entries[0].Key, separatorKey);

        // Assert — child hashes in internal node match the actual leaf content hashes
        var recomputedLeftHash = BTreeHasher.ComputeLeafContentHash(leftLeaf);
        var recomputedRightHash = BTreeHasher.ComputeLeafContentHash(rightLeaf);
        Assert.Equal(recomputedLeftHash, internalNode.ChildHashes[0]);
        Assert.Equal(recomputedRightHash, internalNode.ChildHashes[1]);

        // Assert — internal node content hash is valid
        var recomputedInternalHash = BTreeHasher.ComputeInternalContentHash(internalNode);
        Assert.Equal(recomputedInternalHash, internalNode.NodeContentHash);

        // Assert — IndexRoot hash matches the internal node hash
        Assert.Equal(internalNode.NodeContentHash, rootAfterSplit.RootNodeHash);

        // Assert — IndexRoot points to the internal node
        Assert.Equal(internalBlocks[0].position, rootAfterSplit.RootNodeBlockOffset);
    }

    [Fact]
    public async Task CascadeSplitThrough3Levels_ProducesCorrectTreeStructure()
    {
        // Arrange — insert enough sequential keys to force a cascade split to height 3.
        // With MaxEntries=82 (leaf) and MaxKeys=54 (internal):
        //   83 inserts → height 2 (leaf root splits into internal root + 2 leaves)
        //   Every ~41 additional inserts → one more leaf split → +1 key in root internal
        //   After 54 keys in root internal, next leaf split cascades → height 3
        var filePath = Path.Combine(_tempDir, "test_cascade_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        int maxInserts = 3000; // Safety limit

        while (totalInserts < maxInserts)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(result.IsSuccess, $"Insert {totalInserts} failed: {result.Error}");
            totalInserts++;

            if (btreeIndex.CurrentRoot?.TreeHeight >= 3)
                break;
        }

        // Assert — tree reached height 3
        var root = btreeIndex.CurrentRoot;
        Assert.NotNull(root);
        Assert.True(root.TreeHeight >= 3, $"Expected height >= 3, got {root.TreeHeight} after {totalInserts} inserts");
        Assert.Equal((long)totalInserts, root.EntryCount);

        // Assert — read and verify the root internal node
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, root.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        Assert.Equal(BlockType.BTreeInternal, rootBlock!.Type);
        var rootInternal = BTreeNodeSerializer.DeserializeInternal(rootBlock.Payload);

        // Root should have at least 1 key and 2 children (freshly split)
        Assert.True(rootInternal.KeyCount >= 1, $"Root should have >= 1 key, has {rootInternal.KeyCount}");
        Assert.Equal(rootInternal.KeyCount + 1, rootInternal.ChildOffsets.Length);

        // Assert — root content hash matches
        var rootContentHash = BTreeHasher.ComputeInternalContentHash(rootInternal);
        Assert.Equal(rootContentHash, root.RootNodeHash);

        // Assert — root keys are sorted
        for (int i = 1; i < rootInternal.KeyCount; i++)
        {
            Assert.True(rootInternal.Keys[i - 1].CompareTo(rootInternal.Keys[i]) < 0,
                $"Root keys not sorted at index {i}");
        }

        // Assert — root's children are internal nodes (height 3 = root internal → level-2 internals → leaves)
        long totalLeafEntries = 0;
        for (int c = 0; c < rootInternal.ChildOffsets.Length; c++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, rootInternal.ChildOffsets[c]);
            Assert.NotNull(childBlock);
            Assert.Equal(BlockType.BTreeInternal, childBlock!.Type);

            var childInternal = BTreeNodeSerializer.DeserializeInternal(childBlock.Payload);

            // Verify child hash integrity
            var recomputedChildHash = BTreeHasher.ComputeInternalContentHash(childInternal);
            Assert.Equal(recomputedChildHash, rootInternal.ChildHashes[c]);

            // Verify keys sorted within level-2 internal node
            for (int k = 1; k < childInternal.KeyCount; k++)
            {
                Assert.True(childInternal.Keys[k - 1].CompareTo(childInternal.Keys[k]) < 0,
                    $"Level-2 internal node {c} keys not sorted at index {k}");
            }

            // Assert — level-2 internal node's children are leaves
            for (int j = 0; j < childInternal.ChildOffsets.Length; j++)
            {
                var leafBlock = await ReadBlockAtOffset(rawBlockManager, childInternal.ChildOffsets[j]);
                Assert.NotNull(leafBlock);
                Assert.Equal(BlockType.BTreeLeaf, leafBlock!.Type);

                var leafNode = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Payload);

                // Verify leaf hash integrity
                var recomputedLeafHash = BTreeHasher.ComputeLeafContentHash(leafNode);
                Assert.Equal(recomputedLeafHash, childInternal.ChildHashes[j]);

                // Verify entries are sorted within the leaf
                for (int e = 1; e < leafNode.EntryCount; e++)
                {
                    Assert.True(leafNode.Entries[e - 1].Key.CompareTo(leafNode.Entries[e].Key) < 0,
                        $"Leaf entries not sorted at child {c}, leaf {j}, index {e}");
                }

                totalLeafEntries += leafNode.EntryCount;
            }
        }

        // Assert — total entries across all leaves equals the number of inserts
        Assert.Equal((long)totalInserts, totalLeafEntries);
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

    [Fact]
    public async Task DuplicateKeyInsert_UpdatesValueWithoutCreatingDuplicateEntry()
    {
        // Arrange — insert a key, then re-insert the same key with different values
        var filePath = Path.Combine(_tempDir, "test_duplicate_key.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(42, 84, 126, 168);
        long originalOffset = 1000;
        long originalBlockId = 100;

        // Act — first insert
        var firstResult = await btreeIndex.InsertAsync(key, originalOffset, originalBlockId);
        Assert.True(firstResult.IsSuccess, $"First insert failed: {firstResult.Error}");

        var rootAfterFirst = btreeIndex.CurrentRoot;
        Assert.NotNull(rootAfterFirst);
        Assert.Equal(1L, rootAfterFirst.EntryCount);
        Assert.Equal((ushort)1, rootAfterFirst.TreeHeight);

        // Read the leaf after first insert to capture its content hash
        var firstLeaf = await ReadCurrentRootLeaf(rawBlockManager, btreeIndex);
        Assert.NotNull(firstLeaf);
        var firstLeafHash = firstLeaf!.NodeContentHash;

        // Act — second insert with same key, different values (upsert)
        long updatedOffset = 2000;
        long updatedBlockId = 200;
        var secondResult = await btreeIndex.InsertAsync(key, updatedOffset, updatedBlockId);
        Assert.True(secondResult.IsSuccess, $"Upsert failed: {secondResult.Error}");

        // Assert — entry count did NOT increase (no duplicate created)
        var rootAfterUpsert = btreeIndex.CurrentRoot;
        Assert.NotNull(rootAfterUpsert);
        Assert.Equal(1L, rootAfterUpsert.EntryCount);
        Assert.Equal((ushort)1, rootAfterUpsert.TreeHeight);

        // Assert — the leaf has exactly 1 entry with the updated values
        var upsertedLeaf = await ReadCurrentRootLeaf(rawBlockManager, btreeIndex);
        Assert.NotNull(upsertedLeaf);
        Assert.Equal((ushort)1, upsertedLeaf!.EntryCount);
        Assert.Single(upsertedLeaf.Entries);
        Assert.Equal(key, upsertedLeaf.Entries[0].Key);
        Assert.Equal(updatedOffset, upsertedLeaf.Entries[0].BlockOffset);
        Assert.Equal(updatedBlockId, upsertedLeaf.Entries[0].BlockId);

        // Assert — the old values are gone from the current leaf
        Assert.NotEqual(originalOffset, upsertedLeaf.Entries[0].BlockOffset);
        Assert.NotEqual(originalBlockId, upsertedLeaf.Entries[0].BlockId);

        // Assert — PrevChainHash chains to the previous leaf version (copy-on-write)
        Assert.Equal(firstLeafHash, upsertedLeaf.PrevChainHash);

        // Assert — content hash is valid
        var recomputedHash = BTreeHasher.ComputeLeafContentHash(upsertedLeaf);
        Assert.Equal(recomputedHash, upsertedLeaf.NodeContentHash);

        // Assert — content hash changed (different values = different hash)
        Assert.NotEqual(firstLeafHash, upsertedLeaf.NodeContentHash);
    }

    [Fact]
    public async Task DuplicateKeyInsert_WithMultipleKeys_EntryCountUnchanged()
    {
        // Arrange — insert several unique keys, then upsert one of them
        var filePath = Path.Combine(_tempDir, "test_duplicate_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int keyCount = 10;
        for (int i = 0; i < keyCount; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.Equal((long)keyCount, btreeIndex.CurrentRoot!.EntryCount);

        // Act — upsert key 5 with new values
        var upsertKey = new EmailHashedID(5, 0, 0, 0);
        long newOffset = 99999;
        long newBlockId = 88888;
        var upsertResult = await btreeIndex.InsertAsync(upsertKey, newOffset, newBlockId);
        Assert.True(upsertResult.IsSuccess, $"Upsert failed: {upsertResult.Error}");

        // Assert — entry count stayed the same (no duplicate)
        Assert.Equal((long)keyCount, btreeIndex.CurrentRoot!.EntryCount);

        // Assert — tree height unchanged
        Assert.Equal((ushort)1, btreeIndex.CurrentRoot.TreeHeight);

        // Assert — the leaf contains exactly keyCount entries with the updated value
        var leaf = await ReadCurrentRootLeaf(rawBlockManager, btreeIndex);
        Assert.NotNull(leaf);
        Assert.Equal((ushort)keyCount, leaf!.EntryCount);

        // Find the upserted entry and verify its values
        var matchingEntry = leaf.Entries.FirstOrDefault(e => e.Key.Equals(upsertKey));
        Assert.NotEqual(default, matchingEntry);
        Assert.Equal(newOffset, matchingEntry.BlockOffset);
        Assert.Equal(newBlockId, matchingEntry.BlockId);

        // Assert — all keys are still sorted
        for (int i = 1; i < leaf.EntryCount; i++)
        {
            Assert.True(leaf.Entries[i - 1].Key.CompareTo(leaf.Entries[i].Key) < 0,
                $"Entries not sorted at index {i}");
        }

        // Assert — no duplicate keys exist
        var keys = leaf.Entries.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task DuplicateKeyInsert_InMultiLevelTree_UpdatesValueWithoutDuplicate()
    {
        // Arrange — build a multi-level tree then upsert a key
        var filePath = Path.Combine(_tempDir, "test_duplicate_multilevel.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert enough keys to trigger at least one split (height >= 2)
        int totalInserts = BTreeLeafNode.MaxEntries + 1;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var rootBefore = btreeIndex.CurrentRoot;
        Assert.NotNull(rootBefore);
        Assert.True(rootBefore.TreeHeight >= 2, $"Expected height >= 2, got {rootBefore.TreeHeight}");
        long entryCountBefore = rootBefore.EntryCount;

        // Act — upsert a key that exists in the tree with new values
        var upsertKey = new EmailHashedID(10, 0, 0, 0); // Key 10 was inserted earlier
        long newOffset = 777777;
        long newBlockId = 666666;
        var upsertResult = await btreeIndex.InsertAsync(upsertKey, newOffset, newBlockId);
        Assert.True(upsertResult.IsSuccess, $"Upsert failed: {upsertResult.Error}");

        // Assert — entry count did NOT increase
        var rootAfter = btreeIndex.CurrentRoot;
        Assert.NotNull(rootAfter);
        Assert.Equal(entryCountBefore, rootAfter.EntryCount);

        // Assert — tree height did NOT change
        Assert.Equal(rootBefore.TreeHeight, rootAfter.TreeHeight);

        // Assert — find the updated entry by traversing the tree
        long totalLeafEntries = 0;
        bool foundUpdatedEntry = false;
        int duplicateCount = 0;

        await TraverseLeaves(rawBlockManager, rootAfter, (leafNode) =>
        {
            totalLeafEntries += leafNode.EntryCount;
            for (int i = 0; i < leafNode.EntryCount; i++)
            {
                if (leafNode.Entries[i].Key.Equals(upsertKey))
                {
                    duplicateCount++;
                    Assert.Equal(newOffset, leafNode.Entries[i].BlockOffset);
                    Assert.Equal(newBlockId, leafNode.Entries[i].BlockId);
                    foundUpdatedEntry = true;
                }
            }
        });

        // Assert — the updated entry was found exactly once (no duplicates)
        Assert.True(foundUpdatedEntry, "Updated entry not found in tree");
        Assert.Equal(1, duplicateCount);

        // Assert — total entries across all leaves equals the original count (no new entries)
        Assert.Equal(entryCountBefore, totalLeafEntries);
    }

    /// <summary>Reads the leaf node at the current root's offset (for height-1 trees).</summary>
    private static async Task<BTreeLeafNode?> ReadCurrentRootLeaf(RawBlockManager rawBlockManager, BTreeIndex btreeIndex)
    {
        if (btreeIndex.CurrentRoot == null) return null;
        var block = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        return block != null ? BTreeNodeSerializer.DeserializeLeaf(block.Payload) : null;
    }

    /// <summary>Traverses all leaf nodes in the tree and invokes the callback for each.</summary>
    private static async Task TraverseLeaves(RawBlockManager rawBlockManager, IndexRoot root, Action<BTreeLeafNode> onLeaf)
    {
        if (root.TreeHeight == 1)
        {
            var block = await ReadBlockAtOffset(rawBlockManager, root.RootNodeBlockOffset);
            Assert.NotNull(block);
            onLeaf(BTreeNodeSerializer.DeserializeLeaf(block!.Payload));
            return;
        }

        // BFS through internal nodes
        var offsets = new Queue<(long Offset, int Level)>();
        offsets.Enqueue((root.RootNodeBlockOffset, 1));

        while (offsets.Count > 0)
        {
            var (offset, level) = offsets.Dequeue();
            var block = await ReadBlockAtOffset(rawBlockManager, offset);
            Assert.NotNull(block);

            if (level == root.TreeHeight)
            {
                // This is a leaf level
                onLeaf(BTreeNodeSerializer.DeserializeLeaf(block!.Payload));
            }
            else
            {
                // Internal node — enqueue children
                var internalNode = BTreeNodeSerializer.DeserializeInternal(block!.Payload);
                for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
                    offsets.Enqueue((internalNode.ChildOffsets[i], level + 1));
            }
        }
    }

    [Fact]
    public async Task TreeMaintainsSortedKeyOrder_After10KRandomInserts()
    {
        var filePath = Path.Combine(_tempDir, "test_10k_sorted.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Generate 10,000 unique random keys using a seeded RNG for reproducibility
        const int insertCount = 10_000;
        var rng = new Random(42);
        var insertedKeys = new HashSet<EmailHashedID>();
        var keyList = new List<EmailHashedID>();

        while (keyList.Count < insertCount)
        {
            var key = new EmailHashedID(
                (ulong)rng.NextInt64(),
                (ulong)rng.NextInt64(),
                (ulong)rng.NextInt64(),
                (ulong)rng.NextInt64());

            if (insertedKeys.Add(key))
                keyList.Add(key);
        }

        // Insert all keys in random order
        for (int i = 0; i < insertCount; i++)
        {
            var result = await btreeIndex.InsertAsync(keyList[i], i * 100L, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");
        }

        // Assert — entry count matches
        var root = btreeIndex.CurrentRoot;
        Assert.NotNull(root);
        Assert.Equal((long)insertCount, root.EntryCount);

        // Traverse all leaves and collect every key in leaf order
        var allKeysFromLeaves = new List<EmailHashedID>();
        await TraverseLeaves(rawBlockManager, root, (leafNode) =>
        {
            for (int i = 0; i < leafNode.EntryCount; i++)
                allKeysFromLeaves.Add(leafNode.Entries[i].Key);
        });

        // Assert — total keys across all leaves equals insert count
        Assert.Equal(insertCount, allKeysFromLeaves.Count);

        // Assert — keys are globally sorted across all leaves (not just within each leaf)
        for (int i = 1; i < allKeysFromLeaves.Count; i++)
        {
            Assert.True(allKeysFromLeaves[i - 1].CompareTo(allKeysFromLeaves[i]) < 0,
                $"Keys not sorted at global index {i}: " +
                $"{allKeysFromLeaves[i - 1]} should be < {allKeysFromLeaves[i]}");
        }

        // Assert — every inserted key is present in the tree
        var leafKeySet = new HashSet<EmailHashedID>(allKeysFromLeaves);
        Assert.Equal(insertCount, leafKeySet.Count);
        foreach (var key in keyList)
        {
            Assert.Contains(key, leafKeySet);
        }
    }

    [Fact]
    public async Task SingleInsert_EmptyTree_IndexRootRoundTrips()
    {
        // Verify the IndexRoot payload can be deserialized and matches
        var filePath = Path.Combine(_tempDir, "test_root_roundtrip.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        var key = new EmailHashedID(100, 200, 300, 400);

        var result = await btreeIndex.InsertAsync(key, 8192, 42);

        Assert.True(result.IsSuccess);

        // Find and read the IndexRoot block
        Block? rootBlock = null;
        foreach (var kvp in rawBlockManager.GetBlockLocations())
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                rootBlock = readResult.Value;
                break;
            }
        }

        Assert.NotNull(rootBlock);

        // Deserialize and verify
        var deserializedRoot = BTreeNodeSerializer.DeserializeIndexRoot(rootBlock.Payload);
        var inMemoryRoot = btreeIndex.CurrentRoot!;

        Assert.Equal(inMemoryRoot.EntryCount, deserializedRoot.EntryCount);
        Assert.Equal(inMemoryRoot.TreeHeight, deserializedRoot.TreeHeight);
        Assert.Equal(inMemoryRoot.RootNodeBlockOffset, deserializedRoot.RootNodeBlockOffset);
        Assert.Equal(inMemoryRoot.RootNodeHash, deserializedRoot.RootNodeHash);
        Assert.Equal(inMemoryRoot.PreviousRootOffset, deserializedRoot.PreviousRootOffset);
        Assert.Equal(inMemoryRoot.PreviousRootHash, deserializedRoot.PreviousRootHash);
    }

    [Fact]
    public async Task OldNodeBlocks_RemainUntouched_AfterInsert_AppendOnlyVerified()
    {
        // Verify that insert never overwrites existing blocks — new nodes are always appended.
        var filePath = Path.Combine(_tempDir, "test_append_only.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Phase 1: Insert several keys to build initial tree state
        int initialInserts = 10;
        for (int i = 0; i < initialInserts; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Initial insert {i} failed: {result.Error}");
        }

        // Snapshot: record byte-for-byte content of every block currently in the file
        var snapshotLocations = rawBlockManager.GetBlockLocations()
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var snapshotBytes = new Dictionary<long, byte[]>();

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            foreach (var kvp in snapshotLocations)
            {
                var buf = new byte[kvp.Value.Length];
                fs.Seek(kvp.Value.Position, SeekOrigin.Begin);
                int bytesRead = await fs.ReadAsync(buf, 0, buf.Length);
                Assert.Equal(buf.Length, bytesRead);
                snapshotBytes[kvp.Key] = buf;
            }
        }

        Assert.True(snapshotBytes.Count > 0, "Should have blocks after initial inserts");

        // Phase 2: Insert more keys — this triggers copy-on-write, appending new blocks
        int additionalInserts = 10;
        for (int i = 0; i < additionalInserts; i++)
        {
            var key = new EmailHashedID((ulong)(initialInserts + i + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, (initialInserts + i) * 100, initialInserts + i);
            Assert.True(result.IsSuccess, $"Additional insert {i} failed: {result.Error}");
        }

        // Verify: re-read the raw bytes at every original block position and compare
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            foreach (var kvp in snapshotBytes)
            {
                var buf = new byte[kvp.Value.Length];
                var loc = snapshotLocations[kvp.Key];
                fs.Seek(loc.Position, SeekOrigin.Begin);
                int bytesRead = await fs.ReadAsync(buf, 0, buf.Length);
                Assert.Equal(buf.Length, bytesRead);
                Assert.Equal(kvp.Value, buf);
            }
        }

        // Verify the file grew (new blocks were appended, not written in-place)
        var newLocations = rawBlockManager.GetBlockLocations();
        Assert.True(newLocations.Count > snapshotLocations.Count,
            $"Block count should increase: was {snapshotLocations.Count}, now {newLocations.Count}");
    }

    [Fact]
    public async Task OldNodeBlocks_RemainUntouched_AfterLeafSplit_AppendOnlyVerified()
    {
        // Verify append-only semantics across a leaf split (the most mutation-heavy operation).
        var filePath = Path.Combine(_tempDir, "test_append_only_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Fill the root leaf to capacity
        for (int i = 0; i < BTreeLeafNode.MaxEntries; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");
        }

        // Snapshot all block bytes before the split
        var preSplitLocations = rawBlockManager.GetBlockLocations()
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var preSplitBytes = new Dictionary<long, byte[]>();
        long fileSizeBeforeSplit;

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fileSizeBeforeSplit = fs.Length;
            foreach (var kvp in preSplitLocations)
            {
                var buf = new byte[kvp.Value.Length];
                fs.Seek(kvp.Value.Position, SeekOrigin.Begin);
                int bytesRead = await fs.ReadAsync(buf, 0, buf.Length);
                Assert.Equal(buf.Length, bytesRead);
                preSplitBytes[kvp.Key] = buf;
            }
        }

        // Trigger a split by inserting one more key
        var splitKey = new EmailHashedID(0, 0, 0, 1);
        var splitResult = await btreeIndex.InsertAsync(splitKey, 9999, 9999);
        Assert.True(splitResult.IsSuccess, $"Split insert failed: {splitResult.Error}");
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        // Verify: every pre-split block's raw bytes are identical
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // File must have grown (split appends new blocks)
            Assert.True(fs.Length > fileSizeBeforeSplit,
                $"File should grow after split: was {fileSizeBeforeSplit}, now {fs.Length}");

            foreach (var kvp in preSplitBytes)
            {
                var buf = new byte[kvp.Value.Length];
                var loc = preSplitLocations[kvp.Key];
                fs.Seek(loc.Position, SeekOrigin.Begin);
                int bytesRead = await fs.ReadAsync(buf, 0, buf.Length);
                Assert.Equal(buf.Length, bytesRead);
                Assert.Equal(kvp.Value, buf);
            }
        }

        // Verify new blocks were appended at positions beyond the pre-split file size
        var postSplitLocations = rawBlockManager.GetBlockLocations();
        var newBlockIds = postSplitLocations.Keys.Except(preSplitLocations.Keys).ToList();
        Assert.True(newBlockIds.Count >= 3,
            $"Split should produce at least 3 new blocks (2 leaves + 1 internal + IndexRoot), got {newBlockIds.Count}");

        foreach (var newId in newBlockIds)
        {
            Assert.True(postSplitLocations[newId].Position >= fileSizeBeforeSplit,
                $"New block {newId} at position {postSplitLocations[newId].Position} " +
                $"should be at or after pre-split file end {fileSizeBeforeSplit}");
        }
    }

    [Fact]
    public async Task AllNewBlocks_WrittenVia_RawBlockManager_WriteBlockAsync()
    {
        // Verify that every block produced by BTreeIndex.InsertAsync is written through
        // RawBlockManager.WriteBlockAsync — proven by showing that:
        //   (a) the sum of all tracked block lengths == total file size (no untracked writes)
        //   (b) every tracked block is readable and has a valid BTree-related type
        //   (c) this holds across all insert scenarios: empty tree, normal, split, upsert, multi-level
        var filePath = Path.Combine(_tempDir, "test_all_via_writeblock.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // --- Scenario 1: Insert into empty tree (creates leaf + IndexRoot) ---
        var key1 = new EmailHashedID(1, 0, 0, 0);
        var r1 = await btreeIndex.InsertAsync(key1, 100, 1);
        Assert.True(r1.IsSuccess, $"Empty tree insert failed: {r1.Error}");
        await AssertAllBlocksTrackedAndReadable(rawBlockManager, filePath, "after empty tree insert");

        // --- Scenario 2: Normal inserts into non-full leaf ---
        for (int i = 2; i <= 10; i++)
        {
            var key = new EmailHashedID((ulong)i, 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Normal insert {i} failed: {r.Error}");
        }
        await AssertAllBlocksTrackedAndReadable(rawBlockManager, filePath, "after normal inserts");

        // --- Scenario 3: Upsert (duplicate key with new values) ---
        var upsertResult = await btreeIndex.InsertAsync(new EmailHashedID(5, 0, 0, 0), 99999, 88888);
        Assert.True(upsertResult.IsSuccess, $"Upsert failed: {upsertResult.Error}");
        await AssertAllBlocksTrackedAndReadable(rawBlockManager, filePath, "after upsert");

        // --- Scenario 4: Fill leaf to capacity and trigger split (height 1 → 2) ---
        for (int i = 11; i <= BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)i, 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2, "Expected height >= 2 after split");
        await AssertAllBlocksTrackedAndReadable(rawBlockManager, filePath, "after leaf split");

        // --- Scenario 5: Continue inserting to build a multi-level tree ---
        int maxKey = BTreeLeafNode.MaxEntries + 1;
        while (btreeIndex.CurrentRoot!.TreeHeight < 3 && maxKey < 5000)
        {
            maxKey++;
            var key = new EmailHashedID((ulong)maxKey, 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, maxKey * 100L, maxKey);
            Assert.True(r.IsSuccess, $"Multi-level insert {maxKey} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3,
            $"Expected height >= 3, got {btreeIndex.CurrentRoot.TreeHeight}");
        await AssertAllBlocksTrackedAndReadable(rawBlockManager, filePath, "after multi-level tree build");

        // --- Scenario 6: Upsert in multi-level tree ---
        var multiUpsert = await btreeIndex.InsertAsync(new EmailHashedID(50, 0, 0, 0), 77777, 66666);
        Assert.True(multiUpsert.IsSuccess, $"Multi-level upsert failed: {multiUpsert.Error}");
        await AssertAllBlocksTrackedAndReadable(rawBlockManager, filePath, "after multi-level upsert");
    }

    /// <summary>
    /// Asserts that all bytes in the file are accounted for by RawBlockManager-tracked blocks,
    /// and that every tracked block is readable with a valid BTree block type.
    /// </summary>
    private static async Task AssertAllBlocksTrackedAndReadable(
        RawBlockManager rawBlockManager, string filePath, string context)
    {
        var locations = rawBlockManager.GetBlockLocations();
        Assert.True(locations.Count > 0, $"No blocks tracked {context}");

        // (a) Sum of all tracked block lengths must equal file size
        long totalTrackedBytes = locations.Values.Sum(loc => loc.Length);
        long fileSize;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fileSize = fs.Length;
        }
        Assert.Equal(fileSize, totalTrackedBytes);

        // (b) Every tracked block is readable and has a valid BTree-related type
        var validTypes = new HashSet<BlockType>
        {
            BlockType.BTreeLeaf,
            BlockType.BTreeInternal,
            BlockType.IndexRoot
        };

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess,
                $"Block {kvp.Key} at position {kvp.Value.Position} unreadable {context}: {readResult.Error}");
            Assert.Contains(readResult.Value.Type, validTypes);
        }

        // (c) Blocks are contiguous — sorted by position, each starts where the previous ends
        var sorted = locations.Values.OrderBy(loc => loc.Position).ToList();
        Assert.Equal(0L, sorted[0].Position);
        for (int i = 1; i < sorted.Count; i++)
        {
            long expectedStart = sorted[i - 1].Position + sorted[i - 1].Length;
            Assert.Equal(expectedStart, sorted[i].Position);
        }
    }

    [Fact]
    public async Task OldNodeBlocks_RemainUntouched_AfterUpsert_AppendOnlyVerified()
    {
        // Verify append-only semantics for upsert (duplicate key update).
        var filePath = Path.Combine(_tempDir, "test_append_only_upsert.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert initial keys
        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");
        }

        // Snapshot all block bytes before the upsert
        var preUpsertLocations = rawBlockManager.GetBlockLocations()
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var preUpsertBytes = new Dictionary<long, byte[]>();
        long fileSizeBeforeUpsert;

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fileSizeBeforeUpsert = fs.Length;
            foreach (var kvp in preUpsertLocations)
            {
                var buf = new byte[kvp.Value.Length];
                fs.Seek(kvp.Value.Position, SeekOrigin.Begin);
                int bytesRead = await fs.ReadAsync(buf, 0, buf.Length);
                Assert.Equal(buf.Length, bytesRead);
                preUpsertBytes[kvp.Key] = buf;
            }
        }

        // Upsert an existing key with new values
        var upsertKey = new EmailHashedID(3, 0, 0, 0);
        var upsertResult = await btreeIndex.InsertAsync(upsertKey, 77777, 88888);
        Assert.True(upsertResult.IsSuccess, $"Upsert failed: {upsertResult.Error}");

        // Verify: every pre-upsert block's raw bytes are identical
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.True(fs.Length > fileSizeBeforeUpsert,
                $"File should grow after upsert (copy-on-write): was {fileSizeBeforeUpsert}, now {fs.Length}");

            foreach (var kvp in preUpsertBytes)
            {
                var buf = new byte[kvp.Value.Length];
                var loc = preUpsertLocations[kvp.Key];
                fs.Seek(loc.Position, SeekOrigin.Begin);
                int bytesRead = await fs.ReadAsync(buf, 0, buf.Length);
                Assert.Equal(buf.Length, bytesRead);
                Assert.Equal(kvp.Value, buf);
            }
        }
    }
}
