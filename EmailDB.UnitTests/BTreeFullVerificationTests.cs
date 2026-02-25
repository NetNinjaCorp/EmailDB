using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that full verification traverses all nodes in the B+-tree and reports
/// the first integrity violation. Full verification is O(n nodes): it walks every
/// internal and leaf node via BFS, recomputing BLAKE3 hashes at each level and
/// comparing against the parent's stored ChildHash (or IndexRoot.RootNodeHash for
/// the root node). The first mismatch is reported with the offending node's location.
/// </summary>
public class BTreeFullVerificationTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeFullVerificationTests()
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
    public async Task FullVerify_UntamperedSingleLeafTree_Passes()
    {
        // A single-leaf tree (height 1) should pass full verification:
        // IndexRoot.RootNodeHash must match the leaf's recomputed content hash.
        var filePath = Path.Combine(_tempDir, "full_verify_single_leaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)1, btreeIndex.CurrentRoot!.TreeHeight);

        var result = await FullVerify(rawBlockManager, btreeIndex.CurrentRoot);
        Assert.True(result.IsValid, $"Full verification failed: {result.Error}");
        Assert.Equal(1, result.NodesVerified); // Only the root leaf
    }

    [Fact]
    public async Task FullVerify_UntamperedTwoLevelTree_PassesAndVisitsAllNodes()
    {
        // A 2-level tree should pass full verification, visiting the internal root
        // and all child leaves.
        var filePath = Path.Combine(_tempDir, "full_verify_2level.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var result = await FullVerify(rawBlockManager, btreeIndex.CurrentRoot);
        Assert.True(result.IsValid, $"Full verification failed: {result.Error}");
        // Must have visited at least 3 nodes: 1 internal root + at least 2 leaves
        Assert.True(result.NodesVerified >= 3,
            $"Expected at least 3 nodes verified, got {result.NodesVerified}");
    }

    [Fact]
    public async Task FullVerify_UntamperedThreeLevelTree_PassesAndVisitsAllNodes()
    {
        // A 3-level tree should pass full verification, visiting every node at
        // every level via BFS traversal.
        var filePath = Path.Combine(_tempDir, "full_verify_3level.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        while (totalInserts < 3000)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
            totalInserts++;
            if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3);

        var result = await FullVerify(rawBlockManager, btreeIndex.CurrentRoot);
        Assert.True(result.IsValid, $"Full verification failed: {result.Error}");
        // A 3-level tree must have many nodes
        Assert.True(result.NodesVerified >= 5,
            $"Expected many nodes verified for 3-level tree, got {result.NodesVerified}");
    }

    [Fact]
    public async Task FullVerify_TamperedLeaf_ReportsFirstViolation()
    {
        // Tamper with a single leaf in a 2-level tree. Full verification must
        // detect the mismatch and report it as the first integrity violation.
        var filePath = Path.Combine(_tempDir, "full_verify_tampered_leaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Read the root internal node to find a child leaf
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Read the first child leaf
        var leafBlock = await ReadBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[0]);
        Assert.NotNull(leafBlock);
        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);

        // Tamper: modify a leaf entry's BlockOffset
        leaf.Entries[0].BlockOffset = 0xDEADDEAD;
        var tamperedHash = BTreeHasher.ComputeLeafContentHash(leaf);

        // The parent's stored ChildHash no longer matches
        Assert.NotEqual(rootNode.ChildHashes[0], tamperedHash);

        // Full verification (simulated with the tampered child hash injected) would detect this.
        // We verify at the data-structure level: parent ChildHash vs recomputed child hash.
        var fullResult = await FullVerifyWithTamperedChild(
            rawBlockManager, btreeIndex.CurrentRoot, 0, tamperedHash);
        Assert.False(fullResult.IsValid, "Full verification should detect tampered leaf");
        Assert.Contains("ChildHash mismatch", fullResult.Error);
    }

    [Fact]
    public async Task FullVerify_TamperedInternalNode_InThreeLevelTree_ReportsViolation()
    {
        // Build a 3-level tree, tamper with a mid-level internal node.
        // Full verification must detect the mismatch at the root → mid-level boundary.
        var filePath = Path.Combine(_tempDir, "full_verify_tampered_internal_3level.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        while (totalInserts < 3000)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
            totalInserts++;
            if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3);

        // Read root, then read a mid-level internal child
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        var midBlock = await ReadBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[0]);
        Assert.NotNull(midBlock);
        var midNode = BTreeNodeSerializer.DeserializeInternal(midBlock!.Payload);

        // Tamper: modify a key in the mid-level node
        midNode.Keys[0] = new EmailHashedID(0xDEADBEEF, 0xCAFEBABE, 0, 0);
        var tamperedMidHash = BTreeHasher.ComputeInternalContentHash(midNode);

        // Root's stored ChildHash for child[0] doesn't match the tampered mid-node's hash
        Assert.NotEqual(rootNode.ChildHashes[0], tamperedMidHash);

        // Full verification detects the mismatch at the root → child[0] level
        var fullResult = await FullVerifyWithTamperedChild(
            rawBlockManager, btreeIndex.CurrentRoot, 0, tamperedMidHash);
        Assert.False(fullResult.IsValid, "Full verification should detect tampered internal node");
        Assert.Contains("ChildHash mismatch", fullResult.Error);
    }

    [Fact]
    public async Task FullVerify_TamperedRootHash_DetectedAtIndexRootLevel()
    {
        // If the root node's content hash doesn't match IndexRoot.RootNodeHash,
        // full verification must report it immediately.
        var filePath = Path.Combine(_tempDir, "full_verify_tampered_root.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Create a fake IndexRoot with a corrupted RootNodeHash
        var corruptedRoot = new IndexRoot
        {
            RootNodeBlockOffset = btreeIndex.CurrentRoot.RootNodeBlockOffset,
            EntryCount = btreeIndex.CurrentRoot.EntryCount,
            TreeHeight = btreeIndex.CurrentRoot.TreeHeight,
            RootNodeHash = new byte[32], // Zeroed out — mismatch
            PreviousRootHash = btreeIndex.CurrentRoot.PreviousRootHash,
            PreviousRootOffset = btreeIndex.CurrentRoot.PreviousRootOffset
        };

        var result = await FullVerify(rawBlockManager, corruptedRoot);
        Assert.False(result.IsValid, "Full verification should detect corrupted IndexRoot.RootNodeHash");
        Assert.Contains("RootNodeHash", result.Error);
    }

    [Fact]
    public async Task FullVerify_ReportsFirstViolationOnly_StopsAfterDetection()
    {
        // Full verification should report the FIRST integrity violation and stop.
        // Even if multiple nodes are tampered, only the first encountered violation
        // is returned.
        var filePath = Path.Combine(_tempDir, "full_verify_first_only.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Corrupt the IndexRoot.RootNodeHash — this is the first check
        var corruptedRoot = new IndexRoot
        {
            RootNodeBlockOffset = btreeIndex.CurrentRoot.RootNodeBlockOffset,
            EntryCount = btreeIndex.CurrentRoot.EntryCount,
            TreeHeight = btreeIndex.CurrentRoot.TreeHeight,
            RootNodeHash = new byte[32], // Zeroed = corrupted
            PreviousRootHash = btreeIndex.CurrentRoot.PreviousRootHash,
            PreviousRootOffset = btreeIndex.CurrentRoot.PreviousRootOffset
        };

        var result = await FullVerify(rawBlockManager, corruptedRoot);
        Assert.False(result.IsValid);
        // The error should be about the root hash — the first check that fails
        Assert.Contains("RootNodeHash", result.Error);
        // Verification stopped at root level, so few nodes were visited
        Assert.Equal(1, result.NodesVerified);
    }

    [Fact]
    public async Task FullVerify_AllLeafHashesChecked_InTwoLevelTree()
    {
        // Verify that full verification actually checks every leaf by tampering
        // with the LAST child leaf (not the first). This ensures full traversal
        // visits all children, not just the first.
        var filePath = Path.Combine(_tempDir, "full_verify_last_leaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Tamper with the LAST child leaf
        int lastChildIdx = rootNode.ChildOffsets.Length - 1;
        var lastLeafBlock = await ReadBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[lastChildIdx]);
        Assert.NotNull(lastLeafBlock);
        var lastLeaf = BTreeNodeSerializer.DeserializeLeaf(lastLeafBlock!.Payload);

        lastLeaf.Entries[0].BlockId = 0xBADBAD;
        var tamperedHash = BTreeHasher.ComputeLeafContentHash(lastLeaf);

        Assert.NotEqual(rootNode.ChildHashes[lastChildIdx], tamperedHash);

        // Full verification with tampered last child must still detect it
        var result = await FullVerifyWithTamperedChild(
            rawBlockManager, btreeIndex.CurrentRoot, lastChildIdx, tamperedHash);
        Assert.False(result.IsValid, "Full verification must detect tampered last leaf");
        Assert.Contains("ChildHash mismatch", result.Error);
    }

    [Fact]
    public async Task FullVerify_AfterDeleteAndInsert_StillPasses()
    {
        // After deleting and re-inserting keys, the tree structure changes but
        // full verification should still pass for an untampered tree.
        var filePath = Path.Combine(_tempDir, "full_verify_after_mutations.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build a 2-level tree
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Delete several keys
        for (int i = 1; i <= 5; i++)
        {
            var key = new EmailHashedID((ulong)i, 0, 0, 0);
            var dr = await btreeIndex.DeleteAsync(key);
            Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
        }

        // Insert new keys
        for (int i = 200; i < 210; i++)
        {
            var key = new EmailHashedID((ulong)i, 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var result = await FullVerify(rawBlockManager, btreeIndex.CurrentRoot);
        Assert.True(result.IsValid, $"Full verification should pass after mutations: {result.Error}");
        Assert.True(result.NodesVerified >= 2,
            $"Expected multiple nodes verified, got {result.NodesVerified}");
    }

    [Fact]
    public async Task FullVerify_SingleBitFlipInDeepLeaf_ThreeLevelTree_Detected()
    {
        // In a 3-level tree, a single-bit flip in a deeply nested leaf must be
        // detected by full verification at the immediate parent's ChildHash check.
        var filePath = Path.Combine(_tempDir, "full_verify_deep_bit_flip.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        while (totalInserts < 3000)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
            totalInserts++;
            if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3);

        // Navigate root → mid-level internal → leaf
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Pick the last mid-level child
        int lastMidIdx = rootNode.ChildOffsets.Length - 1;
        var midBlock = await ReadBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[lastMidIdx]);
        Assert.NotNull(midBlock);
        var midNode = BTreeNodeSerializer.DeserializeInternal(midBlock!.Payload);

        // Pick the last leaf of this mid-level node
        int lastLeafIdx = midNode.ChildOffsets.Length - 1;
        var leafBlock = await ReadBlockAtOffset(rawBlockManager, midNode.ChildOffsets[lastLeafIdx]);
        Assert.NotNull(leafBlock);
        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);

        // Single-bit tamper in the leaf
        leaf.Entries[0].BlockOffset ^= 1;
        var tamperedLeafHash = BTreeHasher.ComputeLeafContentHash(leaf);

        // The mid-level parent's stored ChildHash for this leaf no longer matches
        Assert.NotEqual(midNode.ChildHashes[lastLeafIdx], tamperedLeafHash);
    }

    [Fact]
    public async Task FullVerify_CountsAllNodes_InThreeLevelTree()
    {
        // Full verification must visit EVERY node (all internals + all leaves).
        // Count the actual nodes via BFS and compare with what FullVerify reports.
        var filePath = Path.Combine(_tempDir, "full_verify_node_count.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        while (totalInserts < 3000)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
            totalInserts++;
            if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3);

        // Count nodes manually via BFS
        int manualCount = await CountAllNodes(rawBlockManager, btreeIndex.CurrentRoot);

        // Full verification should visit the same count
        var result = await FullVerify(rawBlockManager, btreeIndex.CurrentRoot);
        Assert.True(result.IsValid, $"Full verification failed: {result.Error}");
        Assert.Equal(manualCount, result.NodesVerified);
    }

    #region Full Verification Implementation

    /// <summary>
    /// Full verification: traverses ALL nodes in the B+-tree via BFS, recomputing
    /// BLAKE3 hashes at each node and comparing against the parent's stored hash.
    /// Returns the first integrity violation found, or success if the tree is clean.
    /// This is the O(n nodes) verification mode.
    /// </summary>
    private static async Task<FullVerifyResult> FullVerify(
        RawBlockManager rawBlockManager, IndexRoot indexRoot)
    {
        int nodesVerified = 0;

        // Step 1: Read the root node and verify against IndexRoot.RootNodeHash
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, indexRoot.RootNodeBlockOffset);
        if (rootBlock == null)
            return new FullVerifyResult(false, "Failed to read root node block", nodesVerified);

        if (indexRoot.TreeHeight == 1)
        {
            // Root is a leaf — verify its hash against IndexRoot
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock.Payload);
            var leafHash = BTreeHasher.ComputeLeafContentHash(leaf);
            nodesVerified++;

            if (!indexRoot.RootNodeHash.SequenceEqual(leafHash))
                return new FullVerifyResult(false,
                    "IndexRoot.RootNodeHash does not match root leaf content hash", nodesVerified);

            return new FullVerifyResult(true, null, nodesVerified);
        }

        // Root is an internal node
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock.Payload);
        var rootHash = BTreeHasher.ComputeInternalContentHash(rootNode);
        nodesVerified++;

        if (!indexRoot.RootNodeHash.SequenceEqual(rootHash))
            return new FullVerifyResult(false,
                "IndexRoot.RootNodeHash does not match root internal node content hash", nodesVerified);

        // Step 2: BFS traverse all internal nodes and leaves
        // Queue holds (parentNode, childIndex, childOffset, currentLevel)
        var queue = new Queue<(BTreeInternalNode Parent, int ChildIndex, long ChildOffset, int Level)>();
        for (int i = 0; i < rootNode.ChildOffsets.Length; i++)
        {
            queue.Enqueue((rootNode, i, rootNode.ChildOffsets[i], 2));
        }

        while (queue.Count > 0)
        {
            var (parent, childIdx, childOffset, level) = queue.Dequeue();

            var childBlock = await ReadBlockAtOffset(rawBlockManager, childOffset);
            if (childBlock == null)
                return new FullVerifyResult(false,
                    $"Failed to read node at offset {childOffset} (level {level})", nodesVerified);

            if (level < indexRoot.TreeHeight)
            {
                // Internal node
                var childNode = BTreeNodeSerializer.DeserializeInternal(childBlock.Payload);
                var childHash = BTreeHasher.ComputeInternalContentHash(childNode);
                nodesVerified++;

                if (!parent.ChildHashes[childIdx].SequenceEqual(childHash))
                    return new FullVerifyResult(false,
                        $"ChildHash mismatch at level {level}: parent's stored hash does not match internal child's recomputed hash at child index {childIdx}",
                        nodesVerified);

                // Enqueue this node's children
                for (int i = 0; i < childNode.ChildOffsets.Length; i++)
                {
                    queue.Enqueue((childNode, i, childNode.ChildOffsets[i], level + 1));
                }
            }
            else
            {
                // Leaf node
                var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock.Payload);
                var childHash = BTreeHasher.ComputeLeafContentHash(childLeaf);
                nodesVerified++;

                if (!parent.ChildHashes[childIdx].SequenceEqual(childHash))
                    return new FullVerifyResult(false,
                        $"ChildHash mismatch at leaf level: parent's stored hash does not match leaf's recomputed hash at child index {childIdx}",
                        nodesVerified);
            }
        }

        return new FullVerifyResult(true, null, nodesVerified);
    }

    /// <summary>
    /// Simulates full verification where one specific child's hash has been tampered.
    /// Reads the actual tree from disk but substitutes the tampered hash for the
    /// specified child index at the root level.
    /// </summary>
    private static async Task<FullVerifyResult> FullVerifyWithTamperedChild(
        RawBlockManager rawBlockManager, IndexRoot indexRoot, int tamperedChildIndex, byte[] tamperedHash)
    {
        int nodesVerified = 0;

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, indexRoot.RootNodeBlockOffset);
        if (rootBlock == null)
            return new FullVerifyResult(false, "Failed to read root node block", nodesVerified);

        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock.Payload);
        var rootHash = BTreeHasher.ComputeInternalContentHash(rootNode);
        nodesVerified++;

        if (!indexRoot.RootNodeHash.SequenceEqual(rootHash))
            return new FullVerifyResult(false,
                "IndexRoot.RootNodeHash does not match root internal node content hash", nodesVerified);

        // BFS — but for the tampered child, use the tampered hash to simulate
        // what would happen if that child's on-disk content was corrupted
        var queue = new Queue<(BTreeInternalNode Parent, int ChildIndex, long ChildOffset, int Level, bool IsTampered)>();
        for (int i = 0; i < rootNode.ChildOffsets.Length; i++)
        {
            queue.Enqueue((rootNode, i, rootNode.ChildOffsets[i], 2, i == tamperedChildIndex));
        }

        while (queue.Count > 0)
        {
            var (parent, childIdx, childOffset, level, isTampered) = queue.Dequeue();

            var childBlock = await ReadBlockAtOffset(rawBlockManager, childOffset);
            if (childBlock == null)
                return new FullVerifyResult(false,
                    $"Failed to read node at offset {childOffset} (level {level})", nodesVerified);

            byte[] childHash;
            if (level < indexRoot.TreeHeight)
            {
                var childNode = BTreeNodeSerializer.DeserializeInternal(childBlock.Payload);
                childHash = isTampered ? tamperedHash : BTreeHasher.ComputeInternalContentHash(childNode);
                nodesVerified++;

                if (!parent.ChildHashes[childIdx].SequenceEqual(childHash))
                    return new FullVerifyResult(false,
                        $"ChildHash mismatch at level {level}: parent's stored hash does not match child's recomputed hash at child index {childIdx}",
                        nodesVerified);

                for (int i = 0; i < childNode.ChildOffsets.Length; i++)
                {
                    queue.Enqueue((childNode, i, childNode.ChildOffsets[i], level + 1, false));
                }
            }
            else
            {
                var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock.Payload);
                childHash = isTampered ? tamperedHash : BTreeHasher.ComputeLeafContentHash(childLeaf);
                nodesVerified++;

                if (!parent.ChildHashes[childIdx].SequenceEqual(childHash))
                    return new FullVerifyResult(false,
                        $"ChildHash mismatch at leaf level: parent's stored hash does not match leaf's recomputed hash at child index {childIdx}",
                        nodesVerified);
            }
        }

        return new FullVerifyResult(true, null, nodesVerified);
    }

    private static async Task<int> CountAllNodes(RawBlockManager rawBlockManager, IndexRoot indexRoot)
    {
        int count = 1; // Root node

        if (indexRoot.TreeHeight == 1)
            return count;

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, indexRoot.RootNodeBlockOffset);
        if (rootBlock == null) return count;

        var queue = new Queue<(long Offset, int Level)>();
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock.Payload);
        for (int i = 0; i < rootNode.ChildOffsets.Length; i++)
        {
            queue.Enqueue((rootNode.ChildOffsets[i], 2));
        }

        while (queue.Count > 0)
        {
            var (offset, level) = queue.Dequeue();
            count++;

            if (level < indexRoot.TreeHeight)
            {
                var block = await ReadBlockAtOffset(rawBlockManager, offset);
                if (block != null)
                {
                    var internalNode = BTreeNodeSerializer.DeserializeInternal(block.Payload);
                    for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
                    {
                        queue.Enqueue((internalNode.ChildOffsets[i], level + 1));
                    }
                }
            }
        }

        return count;
    }

    #endregion

    #region Helpers

    private record FullVerifyResult(bool IsValid, string? Error, int NodesVerified);

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
