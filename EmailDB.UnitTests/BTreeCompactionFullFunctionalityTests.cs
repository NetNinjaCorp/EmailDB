using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "Tree is fully functional after compaction
/// (all lookups still work)". After a proper compaction (live tree rewrite with
/// offset remapping), a BTreeIndex constructed from the compacted file's latest
/// IndexRoot must successfully look up every entry that was live before compaction,
/// and must correctly report not-found for entries that were deleted.
/// </summary>
public class BTreeCompactionFullFunctionalityTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionFullFunctionalityTests()
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

    // --- All point lookups succeed after compaction ---

    [Fact]
    public async Task CompactedTree_AllInsertedKeys_LookupSucceeds()
    {
        var filePath = Path.Combine(_tempDir, "all_lookups.emdb");
        var compactedPath = Path.Combine(_tempDir, "all_lookups_compacted.emdb");

        int entryCount = BTreeLeafNode.MaxEntries + 10;
        var insertedKeys = new List<(EmailHashedID Key, long BlockOffset, long BlockId)>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < entryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
                insertedKeys.Add((key, i * 100, i));
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Open compacted file and verify every key is lookupable
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        foreach (var (key, blockOffset, blockId) in insertedKeys)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess,
                $"Lookup failed for key ({key}): {lookupResult.Error}");
            Assert.Equal(blockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task CompactedTree_DeletedKeys_LookupReturnsNotFound()
    {
        var filePath = Path.Combine(_tempDir, "deleted_not_found.emdb");
        var compactedPath = Path.Combine(_tempDir, "deleted_not_found_compacted.emdb");

        int totalInserts = BTreeLeafNode.MaxEntries + 20;
        var deletedKeys = new List<EmailHashedID>();
        var survivingKeys = new List<(EmailHashedID Key, long BlockOffset, long BlockId)>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < totalInserts; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Delete the first 10 entries
            for (int i = 0; i < 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
                deletedKeys.Add(key);
            }

            // Track surviving entries
            for (int i = 10; i < totalInserts; i++)
            {
                survivingKeys.Add((
                    new EmailHashedID((ulong)(i + 1), 0, 0, 0),
                    i * 100, i));
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        // Deleted keys must not be found
        foreach (var key in deletedKeys)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsFailure,
                $"Deleted key ({key}) should not be found in compacted tree");
        }

        // Surviving keys must still be found
        foreach (var (key, blockOffset, blockId) in survivingKeys)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess,
                $"Surviving key ({key}) not found: {lookupResult.Error}");
            Assert.Equal(blockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task CompactedTree_AfterMixedMutations_AllLookupsCorrect()
    {
        var filePath = Path.Combine(_tempDir, "mixed_mutations.emdb");
        var compactedPath = Path.Combine(_tempDir, "mixed_mutations_compacted.emdb");

        var expectedEntries = new Dictionary<EmailHashedID, (long BlockOffset, long BlockId)>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Phase 1: Insert initial batch
            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
                expectedEntries[key] = (i * 100, i);
            }

            // Phase 2: Delete some entries
            for (int i = 1; i <= 5; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
                expectedEntries.Remove(key);
            }

            // Phase 3: Insert more entries (some in gaps)
            for (int i = 200; i < 215; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
                expectedEntries[key] = (i * 100, i);
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        // Verify entry count matches
        Assert.Equal(expectedEntries.Count, latestRoot.EntryCount);

        // Verify every expected entry is lookupable with correct values
        foreach (var (key, (blockOffset, blockId)) in expectedEntries)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess,
                $"Expected entry ({key}) not found: {lookupResult.Error}");
            Assert.Equal(blockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task CompactedTree_ThreeLevelTree_AllLookupsWork()
    {
        var filePath = Path.Combine(_tempDir, "3level_lookups.emdb");
        var compactedPath = Path.Combine(_tempDir, "3level_lookups_compacted.emdb");

        var insertedKeys = new List<(EmailHashedID Key, long BlockOffset, long BlockId)>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            int totalInserts = 0;
            while (totalInserts < 5000)
            {
                var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
                Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
                insertedKeys.Add((key, totalInserts * 100L, totalInserts));
                totalInserts++;
                if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3,
                "Expected at least 3-level tree");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        Assert.Equal(insertedKeys.Count, latestRoot.EntryCount);

        // Verify every single key in the 3-level tree
        foreach (var (key, blockOffset, blockId) in insertedKeys)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess,
                $"Lookup failed in 3-level compacted tree for key ({key}): {lookupResult.Error}");
            Assert.Equal(blockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task CompactedTree_NonExistentKeys_LookupFails()
    {
        var filePath = Path.Combine(_tempDir, "nonexistent_keys.emdb");
        var compactedPath = Path.Combine(_tempDir, "nonexistent_keys_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Insert keys 1-50 (using only even numbers to leave gaps)
            for (int i = 1; i <= 50; i++)
            {
                var key = new EmailHashedID((ulong)(i * 2), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        // Odd keys were never inserted - they must not be found
        for (int i = 1; i <= 50; i++)
        {
            var oddKey = new EmailHashedID((ulong)(i * 2 - 1), 0, 0, 0);
            var lookupResult = await verifyIndex.LookupAsync(oddKey);
            Assert.True(lookupResult.IsFailure,
                $"Key ({oddKey}) should not exist in compacted tree");
        }

        // Keys beyond the inserted range must not be found
        var farKey = new EmailHashedID(999999, 0, 0, 0);
        var farResult = await verifyIndex.LookupAsync(farKey);
        Assert.True(farResult.IsFailure, "Key far beyond range should not exist");
    }

    [Fact]
    public async Task CompactedTree_EntryCount_MatchesLookupableEntries()
    {
        var filePath = Path.Combine(_tempDir, "entry_count_match.emdb");
        var compactedPath = Path.Combine(_tempDir, "entry_count_match_compacted.emdb");

        int totalInserts = BTreeLeafNode.MaxEntries + 15;
        int deleteCount = 8;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < totalInserts; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            for (int i = 1; i <= deleteCount; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        int expectedCount = totalInserts - deleteCount;
        Assert.Equal(expectedCount, latestRoot.EntryCount);

        // Count successful lookups to verify they match EntryCount
        int successfulLookups = 0;
        for (int i = 0; i < totalInserts; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var lookupResult = await verifyIndex.LookupAsync(key);
            if (lookupResult.IsSuccess) successfulLookups++;
        }

        Assert.Equal(expectedCount, successfulLookups);
    }

    #region Compaction Helper

    /// <summary>
    /// Performs a proper compaction: walks the live tree from the latest
    /// IndexRoot, copies only reachable nodes to a new file (bottom-up),
    /// remapping internal node ChildOffsets and recomputing Merkle hashes.
    /// </summary>
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
            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
                Payload = readResult.Value.Payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
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

    private static async Task<IndexRoot?> GetLatestIndexRoot(RawBlockManager rawBlockManager)
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

        return latest;
    }

    private static async Task<long> GetLatestIndexRootOffset(RawBlockManager rawBlockManager)
    {
        var locations = rawBlockManager.GetBlockLocations();
        long maxPosition = -1;

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > maxPosition)
                    maxPosition = kvp.Value.Position;
            }
        }

        return maxPosition;
    }

    #endregion
}
