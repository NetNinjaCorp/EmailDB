using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-32:
/// "Compaction does not block concurrent reads during tree walk phase."
///
/// The B+-tree uses append-only writes and snapshot isolation: readers hold a
/// pre-compaction IndexRoot and traverse immutable blocks via their own
/// RawBlockManager file handles. Compaction walks the same source tree and
/// writes to a separate output file. These tests prove that concurrent reads
/// proceed without blocking or corruption while compaction is in progress.
/// </summary>
public class BTreeCompactionConcurrentReadTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionConcurrentReadTests()
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
    public async Task ConcurrentReads_DuringCompactionTreeWalk_AllSucceed()
    {
        // Arrange: build a multi-level tree with dead nodes (from copy-on-write splits)
        var filePath = Path.Combine(_tempDir, "compaction_concurrent_reads.emdb");
        var compactedPath = Path.Combine(_tempDir, "compaction_concurrent_reads_out.emdb");

        const int entryCount = BTreeLeafNode.MaxEntries + 20;
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
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
                $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");
            savedRoot = btreeIndex.CurrentRoot;
        }

        // Act: start concurrent readers and compaction at the same time
        const int readerCount = 10;
        var readers = new RawBlockManager[readerCount];

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);

            using var barrier = new Barrier(readerCount + 1);

            // Reader tasks: each reader looks up all entries using the snapshot root
            var readerTasks = new Task<Result<LeafEntry>[]>[readerCount];
            for (int r = 0; r < readerCount; r++)
            {
                var idx = new BTreeIndex(readers[r], savedRoot);
                var localEntries = entries;
                readerTasks[r] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    var results = new Result<LeafEntry>[entryCount];
                    for (int i = 0; i < entryCount; i++)
                        results[i] = await idx.LookupAsync(localEntries[i].Key);
                    return results;
                });
            }

            // Compaction task: walks the live tree and copies to a new file
            var compactionTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                using var sourceManager = new RawBlockManager(filePath, createIfNotExists: false);
                BlockIdGenerator.Instance.Reset();
                await CompactLiveTreeToFile(sourceManager, compactedPath);
            });

            // All must complete within 30s (no deadlock/blocking)
            var allTasks = Task.WhenAll(readerTasks.Append(compactionTask));
            var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(completed == allTasks,
                "Concurrent read + compaction deadlocked (30s timeout)");
            await allTasks; // propagate exceptions

            // Assert: every reader got correct results for every key
            for (int r = 0; r < readerCount; r++)
            {
                var results = readerTasks[r].Result;
                for (int i = 0; i < entryCount; i++)
                {
                    Assert.True(results[i].IsSuccess,
                        $"Reader {r}, key {entries[i].Key} failed during compaction: {results[i].Error}");
                    Assert.Equal(entries[i].Key, results[i].Value.Key);
                    Assert.Equal(entries[i].Offset, results[i].Value.BlockOffset);
                    Assert.Equal(entries[i].BlockId, results[i].Value.BlockId);
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
    public async Task ConcurrentReads_DuringCompaction_AreNotSerialized()
    {
        // Arrange: build tree, then verify that concurrent reads complete
        // quickly even while compaction is actively walking the tree.
        // We measure only reader completion time, not compaction time.
        var filePath = Path.Combine(_tempDir, "compaction_not_serialized.emdb");
        var compactedPath = Path.Combine(_tempDir, "compaction_not_serialized_out.emdb");

        const int entryCount = BTreeLeafNode.MaxEntries + 15;
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < entryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Concurrent: run lookups while compaction walks the tree,
        // measuring only how long the readers take (not compaction).
        const int readerCount = 10;
        var concurrentReaders = new RawBlockManager[readerCount];
        try
        {
            for (int i = 0; i < readerCount; i++)
                concurrentReaders[i] = new RawBlockManager(filePath, createIfNotExists: false);

            using var barrier = new Barrier(readerCount + 1);
            var readersComplete = new TaskCompletionSource<double>();

            // Reader tasks: each reader looks up multiple keys
            var lookupTasks = concurrentReaders.Select((r, i) =>
            {
                var idx = new BTreeIndex(r, savedRoot);
                return Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    var results = new Result<LeafEntry>[entryCount];
                    for (int k = 0; k < entryCount; k++)
                    {
                        var key = new EmailHashedID((ulong)(k + 1), 0, 0, 0);
                        results[k] = await idx.LookupAsync(key);
                    }
                    return results;
                });
            }).ToArray();

            // Compaction task: walks the live tree (runs alongside readers)
            var compactionTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                using var sourceManager = new RawBlockManager(filePath, createIfNotExists: false);
                BlockIdGenerator.Instance.Reset();
                await CompactLiveTreeToFile(sourceManager, compactedPath);
            });

            // Time only the readers, not the compaction
            var sw = Stopwatch.StartNew();
            var readersTask = Task.WhenAll(lookupTasks);
            await readersTask;
            sw.Stop();
            var readerMs = sw.Elapsed.TotalMilliseconds;

            // Wait for compaction to finish too (don't leave it dangling)
            await compactionTask;

            // Assert: all lookups succeeded
            foreach (var task in lookupTasks)
            {
                var results = task.Result;
                Assert.All(results, r => Assert.True(r.IsSuccess,
                    $"Lookup failed during compaction: {r.Error}"));
            }

            // Assert: readers completed quickly. If compaction blocked reads,
            // they'd wait for the full compaction duration. A 5-second ceiling
            // is generous given lookups are sub-millisecond operations.
            Assert.True(readerMs < 5000,
                $"Concurrent reads took {readerMs:F1}ms during compaction, " +
                "suggesting compaction tree walk blocked readers");
        }
        finally
        {
            foreach (var reader in concurrentReaders)
                reader?.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentReads_DuringCompaction_SnapshotIsolation()
    {
        // Verify that readers with the pre-compaction snapshot see all original
        // entries even while compaction is actively walking and copying the tree.
        var filePath = Path.Combine(_tempDir, "compaction_snapshot_isolation.emdb");
        var compactedPath = Path.Combine(_tempDir, "compaction_snapshot_isolation_out.emdb");

        const int entryCount = BTreeLeafNode.MaxEntries + 10;
        IndexRoot savedRoot;

        using (var writerManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(writerManager);
            for (int i = 0; i < entryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, (i + 1) * 100, i + 1);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            savedRoot = btreeIndex.CurrentRoot!;
        }

        // Act: readers continuously iterate all keys while compaction runs
        const int readerCount = 5;
        var readers = new RawBlockManager[readerCount];

        try
        {
            for (int i = 0; i < readerCount; i++)
                readers[i] = new RawBlockManager(filePath, createIfNotExists: false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var barrier = new Barrier(readerCount + 1);

            // Each reader does multiple full scans during compaction
            var readerTasks = readers.Select(r =>
            {
                var btree = new BTreeIndex(r, savedRoot);
                return Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    int iterations = 0;
                    while (!cts.Token.IsCancellationRequested && iterations < 3)
                    {
                        for (int i = 0; i < entryCount; i++)
                        {
                            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                            var result = await btree.LookupAsync(key);
                            Assert.True(result.IsSuccess,
                                $"Snapshot lookup failed for key {i + 1} on iteration {iterations}: {result.Error}");
                            Assert.Equal((i + 1) * 100L, result.Value.BlockOffset);
                            Assert.Equal((long)(i + 1), result.Value.BlockId);
                        }
                        iterations++;
                    }
                    return iterations;
                });
            }).ToArray();

            var compactionTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                using var sourceManager = new RawBlockManager(filePath, createIfNotExists: false);
                BlockIdGenerator.Instance.Reset();
                await CompactLiveTreeToFile(sourceManager, compactedPath);
            });

            await Task.WhenAll(readerTasks.Append(compactionTask));

            // Assert: all readers completed at least one full iteration
            foreach (var task in readerTasks)
            {
                Assert.True(task.Result > 0,
                    "Reader should complete at least 1 full scan during compaction");
            }
        }
        finally
        {
            foreach (var reader in readers)
                reader?.Dispose();
        }
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
}
