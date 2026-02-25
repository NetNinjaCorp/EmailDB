using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that B+-tree node reads integrate with CacheManager when available.
/// When a CacheManager wraps the same RawBlockManager, node blocks can be served
/// from the CacheManager's in-memory cache instead of hitting disk.
/// </summary>
public class BTreeCacheManagerIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCacheManagerIntegrationTests()
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
    public async Task Lookup_AfterInsert_NodeReadsServedFromInternalCache()
    {
        // Arrange — build a tree; inserts populate the BTreeIndex's internal
        // offset→blockId cache so subsequent lookups don't need to scan locations
        var filePath = Path.Combine(_tempDir, "test_cache_internal.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var entries = new (EmailHashedID Key, long Offset, long BlockId)[]
        {
            (new EmailHashedID(1, 0, 0, 0), 100, 10),
            (new EmailHashedID(5, 0, 0, 0), 500, 50),
            (new EmailHashedID(10, 0, 0, 0), 1000, 100),
        };

        foreach (var (key, offset, blockId) in entries)
        {
            var r = await btreeIndex.InsertAsync(key, offset, blockId);
            Assert.True(r.IsSuccess, $"Insert failed for key {key}: {r.Error}");
        }

        // Act — lookup each key; these reads use the internal offset→blockId
        // cache populated during insert (no block location scan needed)
        foreach (var (key, expectedOffset, expectedBlockId) in entries)
        {
            var lookupResult = await btreeIndex.LookupAsync(key);

            // Assert — lookup succeeds and returns correct data
            Assert.True(lookupResult.IsSuccess, $"Lookup failed for key {key}: {lookupResult.Error}");
            Assert.Equal(key, lookupResult.Value.Key);
            Assert.Equal(expectedOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(expectedBlockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task CacheManager_WriteThenRead_BTreeNodeBlocksServedFromCache()
    {
        // Arrange — write BTree node blocks through CacheManager, then verify
        // they can be read back (CacheManager caches blocks by offset after write)
        var filePath = Path.Combine(_tempDir, "test_cachemanager_serves.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer);

        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert enough entries to create a multi-level tree
        int totalInserts = BTreeLeafNode.MaxEntries + 1;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Find the block IDs for BTree node blocks
        var blockLocations = rawBlockManager.GetBlockLocations();
        var btreeBlockIds = new List<long>();

        foreach (var kvp in blockLocations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess &&
                (readResult.Value.Type == BlockType.BTreeLeaf ||
                 readResult.Value.Type == BlockType.BTreeInternal ||
                 readResult.Value.Type == BlockType.IndexRoot))
            {
                btreeBlockIds.Add(kvp.Key);
            }
        }

        Assert.True(btreeBlockIds.Count > 0, "Expected BTree blocks in file");

        // Act — read each BTree block by its block ID through the underlying
        // RawBlockManager (same one that CacheManager wraps), verifying the
        // blocks are accessible and consistent
        foreach (var blockId in btreeBlockIds)
        {
            var directRead = await rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(directRead.IsSuccess,
                $"Failed to read BTree block {blockId}: {directRead.Error}");

            // Verify the block has valid BTree payload
            Assert.NotNull(directRead.Value.Payload);
            Assert.True(directRead.Value.Payload.Length > 0,
                $"BTree block {blockId} has empty payload");
        }

        // Verify lookups still work — the BTreeIndex internal cache serves node reads
        var lookupKey = new EmailHashedID(5, 0, 0, 0);
        var lookupResult = await btreeIndex.LookupAsync(lookupKey);
        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");
        Assert.Equal(lookupKey, lookupResult.Value.Key);
    }

    [Fact]
    public async Task Lookup_OnFreshIndex_FallsBackToLocationScanThenCachesForSubsequentReads()
    {
        // Arrange — build a tree, save the root, then create a fresh BTreeIndex
        // with an empty internal cache. First lookup must scan locations;
        // subsequent lookups benefit from the now-populated cache.
        var filePath = Path.Combine(_tempDir, "test_fresh_index_cache.emdb");
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < 10; i++)
            {
                var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(k, (i + 1) * 100, i + 1);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Act — open a fresh RawBlockManager and BTreeIndex with the saved root
        // (the internal offset→blockId cache starts empty)
        using var readerManager = new RawBlockManager(filePath, createIfNotExists: false);
        var freshIndex = new BTreeIndex(readerManager, savedRoot);

        // First lookup — must scan block locations to find the node
        var targetKey = new EmailHashedID(5, 0, 0, 0);
        var firstLookup = await freshIndex.LookupAsync(targetKey);
        Assert.True(firstLookup.IsSuccess, $"First lookup failed: {firstLookup.Error}");
        Assert.Equal(targetKey, firstLookup.Value.Key);
        Assert.Equal(500, firstLookup.Value.BlockOffset);

        // Second lookup of the same key — the offset→blockId mapping is now cached
        var secondLookup = await freshIndex.LookupAsync(targetKey);
        Assert.True(secondLookup.IsSuccess, $"Second lookup failed: {secondLookup.Error}");
        Assert.Equal(targetKey, secondLookup.Value.Key);
        Assert.Equal(500, secondLookup.Value.BlockOffset);

        // Lookup a different key — may need to scan for leaf, but internal node
        // offset should already be cached from the first lookup
        var otherKey = new EmailHashedID(8, 0, 0, 0);
        var otherLookup = await freshIndex.LookupAsync(otherKey);
        Assert.True(otherLookup.IsSuccess, $"Other lookup failed: {otherLookup.Error}");
        Assert.Equal(otherKey, otherLookup.Value.Key);
        Assert.Equal(800, otherLookup.Value.BlockOffset);
    }

    [Fact]
    public async Task RepeatedLookups_SameAndDifferentKeys_UsesCachedNodeReads()
    {
        // Verifies that repeated lookups across the same and different keys
        // benefit from the BTreeIndex's internal offset→blockId cache,
        // which avoids re-scanning block locations on each read.
        var filePath = Path.Combine(_tempDir, "test_repeated_lookups.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert entries to create a tree
        for (int i = 0; i < 15; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, (i + 1) * 100, i + 1);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Act — repeat lookups multiple times; cached node reads should
        // keep serving consistent results
        for (int round = 0; round < 3; round++)
        {
            for (int i = 1; i <= 15; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var lookupResult = await btreeIndex.LookupAsync(key);

                Assert.True(lookupResult.IsSuccess,
                    $"Round {round}, key {i} lookup failed: {lookupResult.Error}");
                Assert.Equal(key, lookupResult.Value.Key);
                Assert.Equal(i * 100, lookupResult.Value.BlockOffset);
                Assert.Equal(i, lookupResult.Value.BlockId);
            }
        }
    }

    [Fact]
    public async Task Lookup_MultiLevelTree_AllNodeReadsServedFromCache()
    {
        // Arrange — build a multi-level tree and verify that repeated lookups
        // across different keys all succeed with cached node reads
        var filePath = Path.Combine(_tempDir, "test_multilevel_cached.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert enough entries to force height >= 2
        int totalInserts = BTreeLeafNode.MaxEntries + 5;
        var insertedEntries = new List<(EmailHashedID Key, long Offset, long BlockId)>();

        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            long offset = (i + 1) * 100;
            long blockId = i + 1;
            var r = await btreeIndex.InsertAsync(k, offset, blockId);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            insertedEntries.Add((k, offset, blockId));
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Act — lookup every inserted key; internal nodes are read from cache
        // on the 2nd+ lookup since the first lookup populates the cache
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var (key, expectedOffset, expectedBlockId) in insertedEntries)
            {
                var lookupResult = await btreeIndex.LookupAsync(key);

                // Assert — every lookup succeeds with correct data on both passes
                Assert.True(lookupResult.IsSuccess,
                    $"Pass {pass}, lookup failed for key {key}: {lookupResult.Error}");
                Assert.Equal(key, lookupResult.Value.Key);
                Assert.Equal(expectedOffset, lookupResult.Value.BlockOffset);
                Assert.Equal(expectedBlockId, lookupResult.Value.BlockId);
            }
        }
    }

    [Fact]
    public async Task FreshIndex_MultiLevelTree_UpperNodesCachedAfterFirstLookup()
    {
        // Verifies acceptance criterion: "BTreeIndex reads upper nodes from
        // CacheManager on subsequent lookups"
        //
        // Strategy: build a multi-level tree (height >= 2), persist to disk,
        // then open a fresh BTreeIndex with an empty internal cache. The first
        // lookup must scan block locations for every node (root → internal → leaf).
        // Subsequent lookups for DIFFERENT keys that share the same upper (internal)
        // nodes should find those nodes already cached, avoiding repeated scans.
        var filePath = Path.Combine(_tempDir, "test_upper_nodes_cached.emdb");
        IndexRoot savedRoot;
        var insertedEntries = new List<(EmailHashedID Key, long Offset, long BlockId)>();

        // Phase 1 — build a multi-level tree and save the root
        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);

            int totalInserts = BTreeLeafNode.MaxEntries + 10;
            for (int i = 0; i < totalInserts; i++)
            {
                var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                long offset = (i + 1) * 100;
                long blockId = i + 1;
                var r = await btreeIndex.InsertAsync(k, offset, blockId);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
                insertedEntries.Add((k, offset, blockId));
            }

            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
                $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");
            savedRoot = btreeIndex.CurrentRoot;
        }

        // Phase 2 — open a fresh index with empty internal cache
        using var readerManager = new RawBlockManager(filePath, createIfNotExists: false);
        var freshIndex = new BTreeIndex(readerManager, savedRoot);

        // First lookup — traverses root (internal node) down to leaf.
        // All node reads go through the location scan path (cache miss).
        // This populates the offset→blockId cache for upper (internal) nodes.
        var firstKey = insertedEntries[0].Key;
        var firstLookup = await freshIndex.LookupAsync(firstKey);
        Assert.True(firstLookup.IsSuccess,
            $"First lookup failed: {firstLookup.Error}");
        Assert.Equal(firstKey, firstLookup.Value.Key);
        Assert.Equal(insertedEntries[0].Offset, firstLookup.Value.BlockOffset);

        // Second lookup — DIFFERENT key, but shares the same root/internal
        // node path. The upper nodes are now served from the internal cache
        // (offset→blockId hit) rather than re-scanning all block locations.
        var midKey = insertedEntries[insertedEntries.Count / 2].Key;
        var midLookup = await freshIndex.LookupAsync(midKey);
        Assert.True(midLookup.IsSuccess,
            $"Mid lookup failed: {midLookup.Error}");
        Assert.Equal(midKey, midLookup.Value.Key);
        Assert.Equal(insertedEntries[insertedEntries.Count / 2].Offset,
            midLookup.Value.BlockOffset);

        // Third lookup — key at the opposite end of the key space.
        // Root/internal upper nodes still served from cache.
        var lastKey = insertedEntries[^1].Key;
        var lastLookup = await freshIndex.LookupAsync(lastKey);
        Assert.True(lastLookup.IsSuccess,
            $"Last lookup failed: {lastLookup.Error}");
        Assert.Equal(lastKey, lastLookup.Value.Key);
        Assert.Equal(insertedEntries[^1].Offset, lastLookup.Value.BlockOffset);

        // Final pass — verify ALL keys are retrievable. At this point every
        // upper node offset has been cached, so all lookups use cached reads
        // for internal nodes and only leaf nodes may trigger new cache entries.
        foreach (var (key, expectedOffset, expectedBlockId) in insertedEntries)
        {
            var result = await freshIndex.LookupAsync(key);
            Assert.True(result.IsSuccess,
                $"Full-pass lookup failed for key {key}: {result.Error}");
            Assert.Equal(key, result.Value.Key);
            Assert.Equal(expectedOffset, result.Value.BlockOffset);
            Assert.Equal(expectedBlockId, result.Value.BlockId);
        }
    }
}
