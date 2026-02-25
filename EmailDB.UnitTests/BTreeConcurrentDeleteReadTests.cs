using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeConcurrentDeleteReadTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeConcurrentDeleteReadTests()
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
    public async Task ConcurrentDeleteAndRead_ReadersWithPreDeleteRoot_SeeAllOriginalKeys()
    {
        // Arrange — build a tree, save the root snapshot for readers, then delete while readers read
        var filePath = Path.Combine(_tempDir, "test_concurrent_delete_read.emdb");

        const int entryCount = 30;
        var entries = new (EmailHashedID Key, long Offset, long BlockId)[entryCount];
        IndexRoot savedRoot;

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
        }

        // Act — open readers with the pre-delete snapshot, while a writer deletes keys
        const int readerCount = 10;
        var keysToDelete = new[] { 5, 10, 15, 20, 25 };
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);

            // Barrier: start readers and deleter at the same time
            using var barrier = new Barrier(readerCount + 1);

            // Reader tasks: each reader looks up ALL original keys using the pre-delete root
            var readerTasks = new Task<Result<LeafEntry>[]>[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                var idx = new BTreeIndex(readers[r], savedRoot);
                var localEntries = entries; // capture for closure
                readerTasks[r] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    var results = new Result<LeafEntry>[entryCount];
                    for (int i = 0; i < entryCount; i++)
                        results[i] = await idx.LookupAsync(localEntries[i].Key);
                    return results;
                });
            }

            // Writer task: delete keys concurrently
            var writerTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                foreach (var keyVal in keysToDelete)
                {
                    var k = new EmailHashedID((ulong)keyVal, 0, 0, 0);
                    await writerIndex.DeleteAsync(k);
                }
            });

            await Task.WhenAll(readerTasks.Append(writerTask));

            // Assert — every reader with the pre-delete snapshot sees all original keys
            for (int r = 0; r < readerCount; r++)
            {
                var results = readerTasks[r].Result;
                for (int i = 0; i < entryCount; i++)
                {
                    Assert.True(results[i].IsSuccess,
                        $"Reader {r}, key {entries[i].Key} failed: {results[i].Error}");
                    Assert.Equal(entries[i].Key, results[i].Value.Key);
                    Assert.Equal(entries[i].Offset, results[i].Value.BlockOffset);
                    Assert.Equal(entries[i].BlockId, results[i].Value.BlockId);
                }
            }

            // Assert — after deletes, a new reader with the updated root does NOT find deleted keys
            var postDeleteRoot = writerIndex.CurrentRoot!;
            using var verifyReader = new RawBlockManager(filePath, createIfNotExists: false);
            var verifyIndex = new BTreeIndex(verifyReader, postDeleteRoot);

            var deletedSet = new HashSet<int>(keysToDelete);
            for (int i = 0; i < entryCount; i++)
            {
                var lookup = await verifyIndex.LookupAsync(entries[i].Key);
                if (deletedSet.Contains(i + 1))
                {
                    Assert.True(lookup.IsFailure,
                        $"Deleted key {i + 1} should not be found with post-delete root");
                }
                else
                {
                    Assert.True(lookup.IsSuccess,
                        $"Surviving key {i + 1} should be found: {lookup.Error}");
                    Assert.Equal(entries[i].Offset, lookup.Value.BlockOffset);
                }
            }
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentDeleteAndRead_MultiLevelTree_NoCorruptionOrDeadlock()
    {
        // Arrange — build a multi-level tree for complex concurrent read paths during delete
        var filePath = Path.Combine(_tempDir, "test_concurrent_delete_read_multi.emdb");
        IndexRoot savedRoot;
        int totalInserts;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            totalInserts = BTreeLeafNode.MaxEntries + 10; // Force height >= 2
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

        // Act — concurrent readers + writer with timeout to detect deadlocks
        const int readerCount = 10;
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);

            using var barrier = new Barrier(readerCount + 1);

            // Readers: each looks up a spread of keys using snapshot root (no cancellation token)
            var readerTasks = readers.Select((r, idx) =>
            {
                var btree = new BTreeIndex(r, savedRoot);
                return Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    var results = new List<Result<LeafEntry>>();
                    for (int i = 0; i < totalInserts; i++)
                    {
                        var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                        results.Add(await btree.LookupAsync(key));
                    }
                    return results;
                });
            }).ToArray();

            // Writer: delete 10 keys spread across the tree
            var writerTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                for (int i = 1; i <= 10; i++)
                {
                    var k = new EmailHashedID((ulong)(i * (totalInserts / 10)), 0, 0, 0);
                    await writerIndex.DeleteAsync(k);
                }
            });

            // All must complete within 30s (no deadlock)
            var allTasks = Task.WhenAll(readerTasks.Append(writerTask));
            var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(completed == allTasks, "Concurrent operations deadlocked (30s timeout)");
            await allTasks; // propagate any exceptions

            // Assert — all reader lookups succeeded (snapshot isolation: old root, immutable blocks)
            foreach (var task in readerTasks)
            {
                var results = task.Result;
                Assert.All(results, r => Assert.True(r.IsSuccess, $"Reader lookup failed: {r.Error}"));
            }
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentDeleteAndRead_ReadersDuringRapidDeletes_NoCrash()
    {
        // Arrange — build a tree, then continuously read while rapidly deleting
        var filePath = Path.Combine(_tempDir, "test_rapid_delete_read.emdb");
        IndexRoot savedRoot;
        const int totalKeys = 40;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < totalKeys; i++)
            {
                var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(k, (i + 1) * 100, i + 1);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Act — readers hold the snapshot root; writer deletes half the keys rapidly
        const int readerCount = 5;
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Readers: repeatedly lookup all keys in a loop for the duration
            var readerTasks = readers.Select(r =>
            {
                var btree = new BTreeIndex(r, savedRoot);
                return Task.Run(async () =>
                {
                    int iterations = 0;
                    while (!cts.Token.IsCancellationRequested && iterations < 3)
                    {
                        for (int i = 0; i < totalKeys; i++)
                        {
                            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                            var result = await btree.LookupAsync(key);
                            // With snapshot root, all lookups should succeed
                            Assert.True(result.IsSuccess,
                                $"Snapshot reader lookup failed for key {i + 1}: {result.Error}");
                        }
                        iterations++;
                    }
                    return iterations;
                });
            }).ToArray();

            // Writer: delete every other key
            var writerTask = Task.Run(async () =>
            {
                for (int i = 1; i <= totalKeys; i += 2)
                {
                    var k = new EmailHashedID((ulong)i, 0, 0, 0);
                    var r = await writerIndex.DeleteAsync(k);
                    Assert.True(r.IsSuccess, $"Delete key {i} failed: {r.Error}");
                }
            });

            await Task.WhenAll(readerTasks.Append(writerTask));

            // Assert — all readers completed at least one full iteration without failure
            foreach (var task in readerTasks)
            {
                Assert.True(task.Result > 0, "Reader should complete at least 1 iteration");
            }

            // Assert — writer completed all deletes and tree is consistent
            Assert.Equal(totalKeys / 2, writerIndex.CurrentRoot!.EntryCount);
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }
}
