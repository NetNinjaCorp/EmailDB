using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Concurrent read/write stress tests: 10 reader threads + 1 writer thread
/// performing simultaneous operations on the B+-tree. Verifies no corruption,
/// no deadlocks, and snapshot isolation under sustained concurrent load.
/// </summary>
public class BTreeConcurrentReadWriteStressTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public BTreeConcurrentReadWriteStressTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Core stress test: 10 readers continuously look up keys while 1 writer
    /// performs a mix of inserts and deletes. Verifies snapshot isolation —
    /// readers with a pre-mutation root see all original data, and the writer's
    /// final state is internally consistent.
    /// </summary>
    [Fact]
    public async Task StressTest_10Readers1Writer_InsertAndDelete_NoCorruption()
    {
        var filePath = Path.Combine(_tempDir, "stress_rw.emdb");

        // Phase 1: Build initial tree with 200 entries
        const int initialEntryCount = 200;
        var entries = new (EmailHashedID Key, long Offset, long BlockId)[initialEntryCount];
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < initialEntryCount; i++)
            {
                entries[i] = (new EmailHashedID((ulong)(i + 1), 0, 0, 0), (i + 1) * 100, i + 1);
                var r = await btreeIndex.InsertAsync(entries[i].Key, entries[i].Offset, entries[i].BlockId);
                Assert.True(r.IsSuccess, $"Initial insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
            Assert.True(savedRoot.TreeHeight >= 2,
                $"Expected multi-level tree, got height {savedRoot.TreeHeight}");
        }

        _output.WriteLine($"Initial tree: {initialEntryCount} entries, height {savedRoot.TreeHeight}");

        // Phase 2: Concurrent stress — 10 readers + 1 writer
        const int readerCount = 10;
        const int readerIterations = 5; // Each reader scans all keys 5 times
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;
        var readerErrors = new List<string>[readerCount];
        var sw = Stopwatch.StartNew();

        try
        {
            for (int i = 0; i < readerCount; i++)
            {
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
                readerErrors[i] = new List<string>();
            }
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);
            using var barrier = new Barrier(readerCount + 1);

            // Reader tasks: each reader holds the pre-mutation snapshot root
            // and repeatedly looks up all original keys
            var readerTasks = new Task<int>[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                var readerIdx = r;
                var btree = new BTreeIndex(readers[r], savedRoot);
                var localEntries = entries;
                var localErrors = readerErrors[r];

                readerTasks[r] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    int completedIterations = 0;

                    for (int iter = 0; iter < readerIterations; iter++)
                    {
                        for (int i = 0; i < initialEntryCount; i++)
                        {
                            var result = await btree.LookupAsync(localEntries[i].Key);
                            if (!result.IsSuccess)
                            {
                                localErrors.Add(
                                    $"Reader {readerIdx}, iter {iter}, key {i + 1}: lookup failed: {result.Error}");
                            }
                            else if (!result.Value.Key.Equals(localEntries[i].Key))
                            {
                                localErrors.Add(
                                    $"Reader {readerIdx}, iter {iter}, key {i + 1}: key mismatch");
                            }
                            else if (result.Value.BlockOffset != localEntries[i].Offset)
                            {
                                localErrors.Add(
                                    $"Reader {readerIdx}, iter {iter}, key {i + 1}: offset mismatch " +
                                    $"(expected {localEntries[i].Offset}, got {result.Value.BlockOffset})");
                            }
                        }
                        completedIterations++;
                    }
                    return completedIterations;
                });
            }

            // Writer task: interleave inserts and deletes
            const int newInsertCount = 50;
            const int deleteCount = 30;
            var writerTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();

                // Insert new keys (beyond the initial range)
                for (int i = 0; i < newInsertCount; i++)
                {
                    var key = new EmailHashedID((ulong)(initialEntryCount + i + 1), 0, 0, 0);
                    var r = await writerIndex.InsertAsync(key, (initialEntryCount + i + 1) * 100, initialEntryCount + i + 1);
                    Assert.True(r.IsSuccess, $"Writer insert {i} failed: {r.Error}");
                }

                // Delete some keys from the initial range (every 6th key)
                int deleted = 0;
                for (int i = 6; i <= initialEntryCount && deleted < deleteCount; i += 6)
                {
                    var key = new EmailHashedID((ulong)i, 0, 0, 0);
                    var r = await writerIndex.DeleteAsync(key);
                    Assert.True(r.IsSuccess, $"Writer delete key {i} failed: {r.Error}");
                    deleted++;
                }
            });

            // Wait with a 60-second deadlock timeout
            var allTasks = Task.WhenAll(readerTasks.Cast<Task>().Append(writerTask));
            var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(completed == allTasks,
                "Concurrent read/write stress test deadlocked (60s timeout)");
            await allTasks; // propagate exceptions

            sw.Stop();
            _output.WriteLine($"Stress test completed in {sw.Elapsed.TotalMilliseconds:F0}ms");

            // Assert: all readers completed all iterations without errors
            for (int r = 0; r < readerCount; r++)
            {
                Assert.True(readerTasks[r].Result == readerIterations,
                    $"Reader {r} only completed {readerTasks[r].Result}/{readerIterations} iterations");
                Assert.Empty(readerErrors[r]);
                _output.WriteLine($"Reader {r}: {readerTasks[r].Result} iterations, 0 errors");
            }

            // Assert: writer's final tree state is consistent
            var finalRoot = writerIndex.CurrentRoot!;
            long expectedEntryCount = initialEntryCount + newInsertCount - deleteCount;
            Assert.Equal(expectedEntryCount, finalRoot.EntryCount);
            _output.WriteLine($"Final tree: {finalRoot.EntryCount} entries, height {finalRoot.TreeHeight}");

            // Verify writer's final root can be read correctly
            using var verifyReader = new RawBlockManager(filePath, createIfNotExists: false);
            var verifyIndex = new BTreeIndex(verifyReader, finalRoot);

            // Check that newly inserted keys are findable
            for (int i = 0; i < newInsertCount; i++)
            {
                var key = new EmailHashedID((ulong)(initialEntryCount + i + 1), 0, 0, 0);
                var lookup = await verifyIndex.LookupAsync(key);
                Assert.True(lookup.IsSuccess,
                    $"Post-stress verification: new key {initialEntryCount + i + 1} not found: {lookup.Error}");
            }

            // Check that deleted keys are gone
            int deletedVerify = 0;
            for (int i = 6; i <= initialEntryCount && deletedVerify < deleteCount; i += 6)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var lookup = await verifyIndex.LookupAsync(key);
                Assert.True(lookup.IsFailure,
                    $"Post-stress verification: deleted key {i} should not be found");
                deletedVerify++;
            }

            _output.WriteLine("Post-stress verification passed: all inserts found, all deletes confirmed");
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    /// <summary>
    /// Extended stress test: readers perform range queries while the writer
    /// continuously inserts, testing that range scans remain consistent
    /// under concurrent mutation.
    /// </summary>
    [Fact]
    public async Task StressTest_10Readers1Writer_RangeQueryDuringInserts_NoCorruption()
    {
        var filePath = Path.Combine(_tempDir, "stress_range_rw.emdb");

        // Build initial tree
        const int initialEntryCount = 150;
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < initialEntryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, (i + 1) * 100, i + 1);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        _output.WriteLine($"Initial tree: {initialEntryCount} entries, height {savedRoot.TreeHeight}");

        const int readerCount = 10;
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;
        var sw = Stopwatch.StartNew();

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);
            using var barrier = new Barrier(readerCount + 1);

            // Reader tasks: perform range queries over 20-key windows
            var readerTasks = new Task<int>[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                var btree = new BTreeIndex(readers[r], savedRoot);
                var readerIdx = r;

                readerTasks[r] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    int totalRangeResults = 0;

                    // Each reader scans different overlapping windows
                    for (int windowStart = 1; windowStart <= initialEntryCount - 20; windowStart += 10)
                    {
                        var startKey = new EmailHashedID((ulong)windowStart, 0, 0, 0);
                        var endKey = new EmailHashedID((ulong)(windowStart + 19), 0, 0, 0);

                        var rangeResult = await btree.RangeQueryAsync(startKey, endKey);
                        Assert.True(rangeResult.IsSuccess,
                            $"Reader {readerIdx}, range [{windowStart}, {windowStart + 19}] failed: {rangeResult.Error}");

                        // With snapshot root, range should return exactly 20 entries
                        Assert.Equal(20, rangeResult.Value.Count);

                        // Verify ordering: keys should be monotonically increasing
                        for (int i = 1; i < rangeResult.Value.Count; i++)
                        {
                            Assert.True(rangeResult.Value[i].Key.CompareTo(rangeResult.Value[i - 1].Key) > 0,
                                $"Reader {readerIdx}: range result not sorted at index {i}");
                        }

                        totalRangeResults += rangeResult.Value.Count;
                    }
                    return totalRangeResults;
                });
            }

            // Writer: insert 100 new keys during range scans
            const int newInsertCount = 100;
            var writerTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < newInsertCount; i++)
                {
                    var key = new EmailHashedID((ulong)(initialEntryCount + i + 1), 0, 0, 0);
                    var r = await writerIndex.InsertAsync(key, (initialEntryCount + i + 1) * 100, initialEntryCount + i + 1);
                    Assert.True(r.IsSuccess, $"Writer insert {i} failed: {r.Error}");
                }
            });

            var allTasks = Task.WhenAll(readerTasks.Cast<Task>().Append(writerTask));
            var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(completed == allTasks,
                "Range query stress test deadlocked (60s timeout)");
            await allTasks;

            sw.Stop();
            _output.WriteLine($"Range query stress test completed in {sw.Elapsed.TotalMilliseconds:F0}ms");

            for (int r = 0; r < readerCount; r++)
            {
                _output.WriteLine($"Reader {r}: {readerTasks[r].Result} total range results");
                Assert.True(readerTasks[r].Result > 0, $"Reader {r} produced no range results");
            }

            // Verify final state
            var finalRoot = writerIndex.CurrentRoot!;
            Assert.Equal(initialEntryCount + newInsertCount, finalRoot.EntryCount);
            _output.WriteLine($"Final tree: {finalRoot.EntryCount} entries, height {finalRoot.TreeHeight}");
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    /// <summary>
    /// Sustained load test: readers and writer run continuously for a fixed
    /// duration, verifying the system remains stable under prolonged concurrent
    /// access with mixed operations.
    /// </summary>
    [Fact]
    public async Task StressTest_10Readers1Writer_SustainedLoad_NoDeadlockOrCorruption()
    {
        var filePath = Path.Combine(_tempDir, "stress_sustained.emdb");

        // Build initial tree
        const int initialEntryCount = 100;
        var entries = new (EmailHashedID Key, long Offset, long BlockId)[initialEntryCount];
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < initialEntryCount; i++)
            {
                entries[i] = (new EmailHashedID((ulong)(i + 1), 0, 0, 0), (i + 1) * 100, i + 1);
                var r = await btreeIndex.InsertAsync(entries[i].Key, entries[i].Offset, entries[i].BlockId);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        const int readerCount = 10;
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;
        var sw = Stopwatch.StartNew();

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);

            // Run for 5 seconds or until completion
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var barrier = new Barrier(readerCount + 1);

            var readerLookupCounts = new long[readerCount];
            var readerErrorCounts = new long[readerCount];

            // Reader tasks: continuously look up random keys from snapshot root
            var readerTasks = new Task[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                var readerIdx = r;
                var btree = new BTreeIndex(readers[r], savedRoot);
                var localEntries = entries;

                readerTasks[r] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    int keyIndex = readerIdx; // Start at different positions

                    while (!cts.Token.IsCancellationRequested)
                    {
                        var result = await btree.LookupAsync(localEntries[keyIndex % initialEntryCount].Key);
                        if (result.IsSuccess)
                            Interlocked.Increment(ref readerLookupCounts[readerIdx]);
                        else
                            Interlocked.Increment(ref readerErrorCounts[readerIdx]);

                        keyIndex++;
                    }
                });
            }

            // Writer task: continuously insert new keys
            long writerInsertCount = 0;
            var writerTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                int nextKey = initialEntryCount + 1;

                while (!cts.Token.IsCancellationRequested)
                {
                    var key = new EmailHashedID((ulong)nextKey, 0, 0, 0);
                    var r = await writerIndex.InsertAsync(key, nextKey * 100L, nextKey);
                    if (r.IsSuccess)
                        Interlocked.Increment(ref writerInsertCount);
                    nextKey++;
                }
            });

            // Wait for all tasks to finish (they stop when CTS fires)
            var allTasks = readerTasks.Append(writerTask).ToArray();
            // Use a generous timeout beyond the CTS to detect true deadlocks
            var completed = await Task.WhenAny(
                Task.WhenAll(allTasks),
                Task.Delay(TimeSpan.FromSeconds(30)));

            // Cancel in case of deadlock detection
            if (!cts.IsCancellationRequested)
                cts.Cancel();

            // Give tasks time to observe cancellation
            try { await Task.WhenAll(allTasks); }
            catch (OperationCanceledException) { /* expected */ }

            sw.Stop();

            // Report results
            long totalLookups = 0;
            long totalErrors = 0;
            for (int r = 0; r < readerCount; r++)
            {
                _output.WriteLine(
                    $"Reader {r}: {readerLookupCounts[r]} lookups, {readerErrorCounts[r]} errors");
                totalLookups += readerLookupCounts[r];
                totalErrors += readerErrorCounts[r];
            }
            _output.WriteLine($"Writer: {writerInsertCount} inserts in {sw.Elapsed.TotalMilliseconds:F0}ms");
            _output.WriteLine($"Total reader lookups: {totalLookups}, errors: {totalErrors}");

            // Assert: no reader errors
            Assert.Equal(0, totalErrors);

            // Assert: meaningful work was done (not starved)
            Assert.True(totalLookups > 0, "Readers performed no lookups — possible starvation");
            Assert.True(writerInsertCount > 0, "Writer performed no inserts — possible starvation");

            // Assert: writer's tree is consistent
            var finalRoot = writerIndex.CurrentRoot!;
            _output.WriteLine($"Final tree: {finalRoot.EntryCount} entries, height {finalRoot.TreeHeight}");
            Assert.True(finalRoot.EntryCount >= initialEntryCount,
                "Final entry count should be at least the initial count");
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }
    }

    /// <summary>
    /// Hash integrity verification after concurrent stress. Builds a tree under
    /// concurrent load, then performs full BFS hash verification on the final state
    /// to confirm no corruption in the BLAKE3 hash chain.
    /// </summary>
    [Fact]
    public async Task StressTest_ConcurrentReadWrite_PostStressHashIntegrity()
    {
        var filePath = Path.Combine(_tempDir, "stress_hash_verify.emdb");

        // Build initial multi-level tree
        const int initialEntryCount = 200;
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < initialEntryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, (i + 1) * 100, i + 1);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Run concurrent stress
        const int readerCount = 10;
        var readers = new RawBlockManager[readerCount];
        RawBlockManager? writerMgr = null;
        IndexRoot finalRoot;

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);
            writerMgr = new RawBlockManager(filePath, createIfNotExists: false);

            var writerIndex = new BTreeIndex(writerMgr, savedRoot);
            using var barrier = new Barrier(readerCount + 1);

            var readerTasks = readers.Select((r, idx) =>
            {
                var btree = new BTreeIndex(r, savedRoot);
                return Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    // Each reader does 3 full scans
                    for (int iter = 0; iter < 3; iter++)
                    {
                        for (int i = 1; i <= initialEntryCount; i++)
                        {
                            var key = new EmailHashedID((ulong)i, 0, 0, 0);
                            await btree.LookupAsync(key);
                        }
                    }
                });
            }).ToArray();

            var writerTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                // Insert 100 new keys, then delete 30 keys
                for (int i = 0; i < 100; i++)
                {
                    var key = new EmailHashedID((ulong)(initialEntryCount + i + 1), 0, 0, 0);
                    await writerIndex.InsertAsync(key, (initialEntryCount + i + 1) * 100, initialEntryCount + i + 1);
                }
                for (int i = 1; i <= 30; i++)
                {
                    var key = new EmailHashedID((ulong)(i * 5), 0, 0, 0);
                    await writerIndex.DeleteAsync(key);
                }
            });

            await Task.WhenAll(readerTasks.Append(writerTask));
            finalRoot = writerIndex.CurrentRoot!;
        }
        finally
        {
            writerMgr?.Dispose();
            foreach (var reader in readers)
                reader?.Dispose();
        }

        // Post-stress: full BFS hash verification on the final root
        using var verifyMgr = new RawBlockManager(filePath, createIfNotExists: false);
        var verifyResult = await FullVerify(verifyMgr, finalRoot);

        _output.WriteLine($"Post-stress verification: valid={verifyResult.IsValid}, " +
                          $"nodes verified={verifyResult.NodesVerified}");
        Assert.True(verifyResult.IsValid,
            $"Post-stress hash verification failed: {verifyResult.Error}");
        Assert.True(verifyResult.NodesVerified > 0, "No nodes were verified");

        _output.WriteLine($"Final tree: {finalRoot.EntryCount} entries, height {finalRoot.TreeHeight}");
        _output.WriteLine("BLAKE3 hash chain integrity confirmed after concurrent stress");
    }

    #region Verification Helpers

    private record FullVerifyResult(bool IsValid, string? Error, int NodesVerified);

    private static async Task<FullVerifyResult> FullVerify(
        RawBlockManager rawBlockManager, IndexRoot indexRoot)
    {
        int nodesVerified = 0;

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, indexRoot.RootNodeBlockOffset);
        if (rootBlock == null)
            return new FullVerifyResult(false, "Failed to read root node block", nodesVerified);

        if (indexRoot.TreeHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock.Payload);
            var leafHash = BTreeHasher.ComputeLeafContentHash(leaf);
            nodesVerified++;

            if (!indexRoot.RootNodeHash.SequenceEqual(leafHash))
                return new FullVerifyResult(false,
                    "IndexRoot.RootNodeHash does not match root leaf content hash", nodesVerified);

            return new FullVerifyResult(true, null, nodesVerified);
        }

        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock.Payload);
        var rootHash = BTreeHasher.ComputeInternalContentHash(rootNode);
        nodesVerified++;

        if (!indexRoot.RootNodeHash.SequenceEqual(rootHash))
            return new FullVerifyResult(false,
                "IndexRoot.RootNodeHash does not match root internal node content hash", nodesVerified);

        var queue = new Queue<(BTreeInternalNode Parent, int ChildIndex, long ChildOffset, int Level)>();
        for (int i = 0; i < rootNode.ChildOffsets.Length; i++)
            queue.Enqueue((rootNode, i, rootNode.ChildOffsets[i], 2));

        while (queue.Count > 0)
        {
            var (parent, childIdx, childOffset, level) = queue.Dequeue();

            var childBlock = await ReadBlockAtOffset(rawBlockManager, childOffset);
            if (childBlock == null)
                return new FullVerifyResult(false,
                    $"Failed to read node at offset {childOffset} (level {level})", nodesVerified);

            if (level < indexRoot.TreeHeight)
            {
                var childNode = BTreeNodeSerializer.DeserializeInternal(childBlock.Payload);
                var childHash = BTreeHasher.ComputeInternalContentHash(childNode);
                nodesVerified++;

                if (!parent.ChildHashes[childIdx].SequenceEqual(childHash))
                    return new FullVerifyResult(false,
                        $"ChildHash mismatch at level {level}, child index {childIdx}", nodesVerified);

                for (int i = 0; i < childNode.ChildOffsets.Length; i++)
                    queue.Enqueue((childNode, i, childNode.ChildOffsets[i], level + 1));
            }
            else
            {
                var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock.Payload);
                var childHash = BTreeHasher.ComputeLeafContentHash(childLeaf);
                nodesVerified++;

                if (!parent.ChildHashes[childIdx].SequenceEqual(childHash))
                    return new FullVerifyResult(false,
                        $"ChildHash mismatch at leaf level, child index {childIdx}", nodesVerified);
            }
        }

        return new FullVerifyResult(true, null, nodesVerified);
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
