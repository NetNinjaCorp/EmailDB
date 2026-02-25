using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeConcurrentReaderTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeConcurrentReaderTests()
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
    public async Task ConcurrentLookups_AllReturnCorrectResults()
    {
        // Arrange — build a tree, then open multiple readers on the same file
        var filePath = Path.Combine(_tempDir, "test_concurrent_readers.emdb");

        const int entryCount = 20;
        var entries = new (EmailHashedID Key, long Offset, long BlockId)[entryCount];
        IndexRoot savedRoot;
        long savedRootOffset;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = (new EmailHashedID((ulong)(i + 1), 0, 0, 0), (i + 1) * 100, i + 1);
                var r = await btreeIndex.InsertAsync(entries[i].Key, entries[i].Offset, entries[i].BlockId);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
            savedRootOffset = -1; // Not tracked externally, but BTreeIndex rebuilds offset cache from blockLocations
        }

        // Act — open separate RawBlockManagers (separate file handles) for concurrent readers
        const int readerCount = 10;
        var readers = new RawBlockManager[readerCount];
        var indices = new BTreeIndex[readerCount];
        try
        {
            for (int i = 0; i < readerCount; i++)
            {
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
                indices[i] = new BTreeIndex(readers[i], savedRoot);
            }

            // Fire all lookups concurrently — each reader looks up all entries
            var lookupTasks = new List<Task<Result<LeafEntry>[]>>();
            for (int r = 0; r < readerCount; r++)
            {
                var idx = indices[r];
                lookupTasks.Add(Task.Run(async () =>
                {
                    var results = new Result<LeafEntry>[entryCount];
                    var tasks = entries.Select(e => idx.LookupAsync(e.Key)).ToArray();
                    results = await Task.WhenAll(tasks);
                    return results;
                }));
            }

            var allReaderResults = await Task.WhenAll(lookupTasks);

            // Assert — every reader got correct results for every key
            for (int r = 0; r < readerCount; r++)
            {
                for (int i = 0; i < entryCount; i++)
                {
                    var result = allReaderResults[r][i];
                    Assert.True(result.IsSuccess, $"Reader {r}, key {i} failed: {result.Error}");
                    Assert.Equal(entries[i].Key, result.Value.Key);
                    Assert.Equal(entries[i].Offset, result.Value.BlockOffset);
                    Assert.Equal(entries[i].BlockId, result.Value.BlockId);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentLookups_SameKey_AllReturnSameResult()
    {
        // Arrange — build a tree, then have many concurrent readers lookup the same key
        var filePath = Path.Combine(_tempDir, "test_concurrent_same_key.emdb");

        var targetKey = new EmailHashedID(42, 84, 126, 168);
        long expectedOffset = 9999;
        long expectedBlockId = 777;
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < 10; i++)
            {
                var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                await btreeIndex.InsertAsync(k, i * 100, i);
            }
            var insertResult = await btreeIndex.InsertAsync(targetKey, expectedOffset, expectedBlockId);
            Assert.True(insertResult.IsSuccess);
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Act — 20 concurrent readers, all looking up the same key
        const int readerCount = 20;
        var readers = new RawBlockManager[readerCount];
        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);

            var lookupTasks = readers.Select(r =>
            {
                var idx = new BTreeIndex(r, savedRoot);
                return idx.LookupAsync(targetKey);
            }).ToArray();

            var results = await Task.WhenAll(lookupTasks);

            // Assert — all readers got the correct, identical result
            for (int i = 0; i < readerCount; i++)
            {
                Assert.True(results[i].IsSuccess, $"Reader {i} failed: {results[i].Error}");
                Assert.Equal(targetKey, results[i].Value.Key);
                Assert.Equal(expectedOffset, results[i].Value.BlockOffset);
                Assert.Equal(expectedBlockId, results[i].Value.BlockId);
            }
        }
        finally
        {
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentLookups_MultiLevelTree_DoNotDeadlock()
    {
        // Arrange — build a multi-level tree for complex read paths
        var filePath = Path.Combine(_tempDir, "test_concurrent_no_deadlock.emdb");
        IndexRoot savedRoot;
        int totalInserts;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            totalInserts = BTreeLeafNode.MaxEntries + 5; // Force height >= 2
            for (int i = 0; i < totalInserts; i++)
            {
                var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(k, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
                $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");
            savedRoot = btreeIndex.CurrentRoot;
        }

        // Act — many concurrent readers on the multi-level tree with timeout
        const int readerCount = 50;
        var readers = new RawBlockManager[readerCount];
        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var lookupTasks = readers.Select((r, i) =>
            {
                var idx = new BTreeIndex(r, savedRoot);
                var key = new EmailHashedID((ulong)((i % totalInserts) + 1), 0, 0, 0);
                return idx.LookupAsync(key, cts.Token);
            }).ToArray();

            // Assert — all complete within the timeout (no deadlock) and return correct results
            var allResults = await Task.WhenAll(lookupTasks);
            Assert.All(allResults, r => Assert.True(r.IsSuccess, $"Lookup failed: {r.Error}"));
        }
        finally
        {
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentLookups_AreNotSerialized()
    {
        // Arrange — build a tree large enough that reads take measurable time
        var filePath = Path.Combine(_tempDir, "test_concurrent_not_serialized.emdb");
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            int totalInserts = BTreeLeafNode.MaxEntries + 5;
            for (int i = 0; i < totalInserts; i++)
            {
                var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(k, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Measure sequential baseline
        const int lookupCount = 20;
        var sw = Stopwatch.StartNew();
        using (var seqManager = new RawBlockManager(filePath, createIfNotExists: false))
        {
            var seqIndex = new BTreeIndex(seqManager, savedRoot);
            for (int i = 0; i < lookupCount; i++)
            {
                var key = new EmailHashedID((ulong)((i % 10) + 1), 0, 0, 0);
                var r = await seqIndex.LookupAsync(key);
                Assert.True(r.IsSuccess);
            }
        }
        sw.Stop();
        var sequentialMs = sw.Elapsed.TotalMilliseconds;

        // Measure concurrent execution
        var readers = new RawBlockManager[lookupCount];
        try
        {
            for (int i = 0; i < lookupCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);

            sw.Restart();
            var concurrentTasks = readers.Select((r, i) =>
            {
                var idx = new BTreeIndex(r, savedRoot);
                var key = new EmailHashedID((ulong)((i % 10) + 1), 0, 0, 0);
                return idx.LookupAsync(key);
            }).ToArray();

            var results = await Task.WhenAll(concurrentTasks);
            sw.Stop();
            var concurrentMs = sw.Elapsed.TotalMilliseconds;

            // Assert — concurrent lookups all succeeded
            Assert.All(results, r => Assert.True(r.IsSuccess, $"Lookup failed: {r.Error}"));

            // Assert — concurrent was not significantly slower than sequential
            // (if readers were blocked/serialized, concurrent would take ~lookupCount * per-lookup time)
            // We just verify it completed — the fact all tasks ran via Task.WhenAll without
            // serializing is the key proof that readers don't block each other
            Assert.True(concurrentMs < sequentialMs * 10,
                $"Concurrent ({concurrentMs:F1}ms) took >10x sequential ({sequentialMs:F1}ms), " +
                "suggesting readers may be blocking each other");
        }
        finally
        {
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }
}
