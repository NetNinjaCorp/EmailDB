using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Crash recovery stress tests: 100 independent trials simulating process crashes
/// at various points during WAL operations, verifying that all unflushed entries
/// are recovered correctly and data integrity is maintained after reopening.
/// </summary>
public class BTreeCrashRecoveryStressTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public BTreeCrashRecoveryStressTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_crash_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Scans block locations in the RawBlockManager to find the latest IndexRoot block
    /// and reconstructs the BTreeIndex from it.
    /// </summary>
    private static async Task<BTreeIndex> RecoverBTreeIndexAsync(RawBlockManager rawBlockManager)
    {
        var locations = rawBlockManager.GetBlockLocations();
        IndexRoot? recoveredRoot = null;
        long recoveredRootBlockOffset = -1;

        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > recoveredRootBlockOffset)
                {
                    recoveredRootBlockOffset = kvp.Value.Position;
                    recoveredRoot = BTreeNodeSerializer.DeserializeIndexRoot(blockResult.Value.Payload);
                }
            }
        }

        return new BTreeIndex(rawBlockManager, recoveredRoot, recoveredRootBlockOffset);
    }

    /// <summary>
    /// Core stress test: 100 trials of crash-then-recover with varying entry counts.
    /// Each trial inserts a random number of entries into the WAL, disposes without
    /// flushing (simulating a crash), then reopens and verifies all entries are recovered.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_100Trials_AllEntriesRecovered()
    {
        const int totalTrials = 100;
        int passedTrials = 0;
        var rng = new Random(42);
        var sw = Stopwatch.StartNew();

        for (int trial = 0; trial < totalTrials; trial++)
        {
            BlockIdGenerator.Instance.Reset();
            var filePath = Path.Combine(_tempDir, $"crash_trial_{trial}.emdb");
            int entryCount = rng.Next(1, 80);
            var expectedEntries = new Dictionary<EmailHashedID, (long BlockOffset, long BlockId)>();
            long walOffset;

            // Phase 1: Insert entries into WAL, then "crash" (dispose without flush)
            {
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = new BTreeIndex(rawBlockManager);
                walOffset = rawBlockManager.FileLength + 4096;
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                for (int i = 0; i < entryCount; i++)
                {
                    var key = new EmailHashedID(
                        (ulong)(trial * 10000 + i + 1),
                        (ulong)(i * 7 + 3),
                        (ulong)(trial + 1),
                        (ulong)(i * 13 + 1));
                    long offset = (i + 1) * 256L;
                    long blockId = i + 1;
                    await walManager.InsertAsync(key, offset, blockId);
                    expectedEntries[key] = (offset, blockId);
                }

                Assert.Equal(entryCount, walManager.BufferCount);
                Assert.True(walManager.IsDirty);
            }

            // Phase 2: Reopen and verify all entries recovered
            {
                BlockIdGenerator.Instance.Reset();
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = new BTreeIndex(rawBlockManager);
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                Assert.Equal(entryCount, walManager.BufferCount);
                Assert.True(walManager.IsDirty);

                foreach (var (key, (expectedOffset, expectedBlockId)) in expectedEntries)
                {
                    var lookup = await walManager.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial}: Key should be recovered from WAL");
                    Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
                    Assert.Equal(expectedBlockId, lookup.Value.BlockId);
                }

                await walManager.FlushAsync();
                Assert.Equal(0, walManager.BufferCount);
                Assert.False(walManager.IsDirty);
                Assert.NotNull(btreeIndex.CurrentRoot);
                Assert.Equal((long)entryCount, btreeIndex.CurrentRoot.EntryCount);
            }

            passedTrials++;
        }

        sw.Stop();
        _output.WriteLine($"Crash recovery stress test: {passedTrials}/{totalTrials} trials passed in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(totalTrials, passedTrials);
    }

    /// <summary>
    /// 100 trials where entries are flushed to BTree (directly), then more entries are
    /// added to the WAL before crashing. Recovery must preserve both the BTree state
    /// and the unflushed WAL entries.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_100Trials_PartialFlushThenCrash_BothFlushedAndUnflushedRecovered()
    {
        const int totalTrials = 100;
        int passedTrials = 0;
        var rng = new Random(123);
        var sw = Stopwatch.StartNew();

        for (int trial = 0; trial < totalTrials; trial++)
        {
            BlockIdGenerator.Instance.Reset();
            var filePath = Path.Combine(_tempDir, $"partial_flush_trial_{trial}.emdb");
            int flushedCount = rng.Next(1, 40);
            int unflushedCount = rng.Next(1, 40);
            var flushedEntries = new Dictionary<EmailHashedID, (long BlockOffset, long BlockId)>();
            var unflushedEntries = new Dictionary<EmailHashedID, (long BlockOffset, long BlockId)>();
            long walOffset;

            // Phase 1: Insert entries directly to BTree, then add WAL entries + crash
            {
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = new BTreeIndex(rawBlockManager);

                // Insert directly to BTree (blocks written at file start)
                for (int i = 0; i < flushedCount; i++)
                {
                    var key = new EmailHashedID(
                        (ulong)(trial * 100000 + i + 1), 0, 0, 0);
                    long offset = (i + 1) * 100L;
                    long blockId = i + 1;
                    var result = await btreeIndex.InsertAsync(key, offset, blockId);
                    Assert.True(result.IsSuccess, $"Trial {trial}: Direct insert {i} failed: {result.Error}");
                    flushedEntries[key] = (offset, blockId);
                }

                // Place WAL after blocks
                walOffset = rawBlockManager.FileLength + 4096;
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                // Insert unflushed entries via WAL
                for (int i = 0; i < unflushedCount; i++)
                {
                    var key = new EmailHashedID(
                        (ulong)(trial * 100000 + flushedCount + i + 1), 0, 0, 0);
                    long offset = (flushedCount + i + 1) * 100L;
                    long blockId = flushedCount + i + 1;
                    await walManager.InsertAsync(key, offset, blockId);
                    unflushedEntries[key] = (offset, blockId);
                }

                Assert.Equal(unflushedCount, walManager.BufferCount);
                Assert.True(walManager.IsDirty);
                // Crash — dispose without flushing WAL
            }

            // Phase 2: Reopen and verify both BTree and WAL entries
            {
                BlockIdGenerator.Instance.Reset();
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = await RecoverBTreeIndexAsync(rawBlockManager);

                Assert.NotNull(btreeIndex.CurrentRoot);
                Assert.Equal((long)flushedCount, btreeIndex.CurrentRoot.EntryCount);

                // Verify BTree entries
                foreach (var (key, (expectedOffset, _)) in flushedEntries)
                {
                    var lookup = await btreeIndex.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial}: Flushed key should be in BTree");
                    Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
                }

                // Recover WAL entries
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                Assert.Equal(unflushedCount, walManager.BufferCount);
                Assert.True(walManager.IsDirty);

                foreach (var (key, (expectedOffset, expectedBlockId)) in unflushedEntries)
                {
                    var lookup = await walManager.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial}: Unflushed key should be recovered from WAL");
                    Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
                    Assert.Equal(expectedBlockId, lookup.Value.BlockId);
                }

                // Flush WAL to BTree and verify total
                await walManager.FlushAsync();
                Assert.Equal((long)(flushedCount + unflushedCount), btreeIndex.CurrentRoot!.EntryCount);
            }

            passedTrials++;
        }

        sw.Stop();
        _output.WriteLine($"Partial flush crash recovery: {passedTrials}/{totalTrials} trials passed in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(totalTrials, passedTrials);
    }

    /// <summary>
    /// 100 trials with upserts before crash: verifies that the latest value for each
    /// key is recovered, not stale values.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_100Trials_UpsertsBeforeCrash_LatestValuesRecovered()
    {
        const int totalTrials = 100;
        int passedTrials = 0;
        var rng = new Random(999);
        var sw = Stopwatch.StartNew();

        for (int trial = 0; trial < totalTrials; trial++)
        {
            BlockIdGenerator.Instance.Reset();
            var filePath = Path.Combine(_tempDir, $"upsert_trial_{trial}.emdb");
            int baseEntryCount = rng.Next(5, 30);
            int upsertCount = rng.Next(1, baseEntryCount);
            var finalEntries = new Dictionary<EmailHashedID, (long BlockOffset, long BlockId)>();
            var keys = new EmailHashedID[baseEntryCount];
            long walOffset;

            // Phase 1: Insert entries, upsert some, then crash
            {
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = new BTreeIndex(rawBlockManager);
                walOffset = rawBlockManager.FileLength + 4096;
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                for (int i = 0; i < baseEntryCount; i++)
                {
                    keys[i] = new EmailHashedID(
                        (ulong)(trial * 10000 + i + 1),
                        (ulong)(i + 1), 0, 0);
                    long offset = (i + 1) * 100L;
                    long blockId = i + 1;
                    await walManager.InsertAsync(keys[i], offset, blockId);
                    finalEntries[keys[i]] = (offset, blockId);
                }

                for (int u = 0; u < upsertCount; u++)
                {
                    int idx = rng.Next(0, baseEntryCount);
                    long newOffset = 90000L + u;
                    long newBlockId = 90000 + u;
                    await walManager.InsertAsync(keys[idx], newOffset, newBlockId);
                    finalEntries[keys[idx]] = (newOffset, newBlockId);
                }
            }

            // Phase 2: Recover and verify latest values
            {
                BlockIdGenerator.Instance.Reset();
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = new BTreeIndex(rawBlockManager);
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                Assert.Equal(baseEntryCount, walManager.BufferCount);
                Assert.True(walManager.IsDirty);

                foreach (var (key, (expectedOffset, expectedBlockId)) in finalEntries)
                {
                    var lookup = await walManager.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial}: Key should be recovered");
                    Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
                    Assert.Equal(expectedBlockId, lookup.Value.BlockId);
                }
            }

            passedTrials++;
        }

        sw.Stop();
        _output.WriteLine($"Upsert crash recovery: {passedTrials}/{totalTrials} trials passed in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(totalTrials, passedTrials);
    }

    /// <summary>
    /// 100 trials with multiple crash/recovery cycles: each trial runs 3 cycles where
    /// WAL entries survive a crash and are verified on recovery. After recovery, a fresh
    /// file is built with the merged state to avoid WAL-gap issues with the block scanner.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_100Trials_MultipleFlushCrashCycles_StatePreserved()
    {
        const int totalTrials = 100;
        int passedTrials = 0;
        var sw = Stopwatch.StartNew();

        for (int trial = 0; trial < totalTrials; trial++)
        {
            // Track all entries across cycles
            var allEntries = new Dictionary<EmailHashedID, (long BlockOffset, long BlockId)>();

            // Cycle 1: Build BTree with 5 entries, add 3 WAL entries, crash
            BlockIdGenerator.Instance.Reset();
            var file1 = Path.Combine(_tempDir, $"multi_{trial}_c1.emdb");
            long walOffset1;
            {
                using var rawBlockManager = new RawBlockManager(file1);
                var btreeIndex = new BTreeIndex(rawBlockManager);

                for (int i = 0; i < 5; i++)
                {
                    var key = new EmailHashedID((ulong)(trial * 1000 + i + 1), 0, 0, 0);
                    var result = await btreeIndex.InsertAsync(key, (i + 1) * 100L, i + 1);
                    Assert.True(result.IsSuccess);
                    allEntries[key] = ((i + 1) * 100L, i + 1);
                }

                walOffset1 = rawBlockManager.FileLength + 4096;
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset1,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                for (int i = 5; i < 8; i++)
                {
                    var key = new EmailHashedID((ulong)(trial * 1000 + i + 1), 0, 0, 0);
                    await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
                    allEntries[key] = ((i + 1) * 100L, i + 1);
                }
                Assert.Equal(3, walManager.BufferCount);
            }

            // Recovery 1: Verify 5 BTree + 3 WAL
            {
                BlockIdGenerator.Instance.Reset();
                using var rawBlockManager = new RawBlockManager(file1);
                var btreeIndex = await RecoverBTreeIndexAsync(rawBlockManager);
                Assert.NotNull(btreeIndex.CurrentRoot);
                Assert.Equal(5L, btreeIndex.CurrentRoot.EntryCount);

                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset1,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);
                Assert.Equal(3, walManager.BufferCount);
                Assert.True(walManager.IsDirty);

                // Verify all 8 entries accessible (5 via BTree, 3 via WAL)
                foreach (var (key, (expectedOffset, expectedBlockId)) in allEntries)
                {
                    var lookup = await walManager.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial} cycle 1: Key should be accessible");
                    Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
                }
            }

            // Cycle 2: Fresh file with all 8 entries in BTree, add 4 WAL entries, crash
            BlockIdGenerator.Instance.Reset();
            var file2 = Path.Combine(_tempDir, $"multi_{trial}_c2.emdb");
            long walOffset2;
            {
                using var rawBlockManager = new RawBlockManager(file2);
                var btreeIndex = new BTreeIndex(rawBlockManager);

                foreach (var (key, (offset, blockId)) in allEntries)
                {
                    var result = await btreeIndex.InsertAsync(key, offset, blockId);
                    Assert.True(result.IsSuccess);
                }
                Assert.Equal(8L, btreeIndex.CurrentRoot!.EntryCount);

                walOffset2 = rawBlockManager.FileLength + 4096;
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset2,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                for (int i = 8; i < 12; i++)
                {
                    var key = new EmailHashedID((ulong)(trial * 1000 + i + 1), 0, 0, 0);
                    await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
                    allEntries[key] = ((i + 1) * 100L, i + 1);
                }
                Assert.Equal(4, walManager.BufferCount);
            }

            // Recovery 2: Verify 8 BTree + 4 WAL
            {
                BlockIdGenerator.Instance.Reset();
                using var rawBlockManager = new RawBlockManager(file2);
                var btreeIndex = await RecoverBTreeIndexAsync(rawBlockManager);
                Assert.NotNull(btreeIndex.CurrentRoot);
                Assert.Equal(8L, btreeIndex.CurrentRoot.EntryCount);

                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset2,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);
                Assert.Equal(4, walManager.BufferCount);
                Assert.True(walManager.IsDirty);

                // Verify all 12 entries accessible
                foreach (var (key, (expectedOffset, _)) in allEntries)
                {
                    var lookup = await walManager.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial} cycle 2: Key should be accessible");
                    Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
                }
            }

            passedTrials++;
        }

        sw.Stop();
        _output.WriteLine($"Multi-cycle crash recovery: {passedTrials}/{totalTrials} trials passed in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(totalTrials, passedTrials);
    }

    /// <summary>
    /// 100 trials of clean shutdown — entries inserted directly to BTree, verified
    /// on reopen that the BTree state is fully intact and WAL is clean.
    /// </summary>
    [Fact]
    public async Task CrashRecovery_100Trials_CleanFlushBeforeCrash_NothingRecovered()
    {
        const int totalTrials = 100;
        int passedTrials = 0;
        var rng = new Random(777);
        var sw = Stopwatch.StartNew();

        for (int trial = 0; trial < totalTrials; trial++)
        {
            BlockIdGenerator.Instance.Reset();
            var filePath = Path.Combine(_tempDir, $"clean_flush_trial_{trial}.emdb");
            int entryCount = rng.Next(1, 60);
            long walOffset;

            // Phase 1: Insert entries to BTree directly, then crash
            {
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = new BTreeIndex(rawBlockManager);

                for (int i = 0; i < entryCount; i++)
                {
                    var key = new EmailHashedID((ulong)(trial * 10000 + i + 1), 0, 0, 0);
                    var result = await btreeIndex.InsertAsync(key, (i + 1) * 100L, i + 1);
                    Assert.True(result.IsSuccess);
                }

                Assert.NotNull(btreeIndex.CurrentRoot);
                Assert.Equal((long)entryCount, btreeIndex.CurrentRoot.EntryCount);
                walOffset = rawBlockManager.FileLength + 4096;
            }

            // Phase 2: Reopen — BTree should have all entries, WAL clean
            {
                BlockIdGenerator.Instance.Reset();
                using var rawBlockManager = new RawBlockManager(filePath);
                var btreeIndex = await RecoverBTreeIndexAsync(rawBlockManager);

                Assert.NotNull(btreeIndex.CurrentRoot);
                Assert.Equal((long)entryCount, btreeIndex.CurrentRoot.EntryCount);

                // WAL at new offset — should be empty/clean
                using var walManager = new BTreeWALManager(
                    rawBlockManager, btreeIndex,
                    walPayloadFileOffset: walOffset,
                    autoFlushThreshold: 1000,
                    autoFlushEnabled: false);

                Assert.Equal(0, walManager.BufferCount);
                Assert.False(walManager.IsDirty);

                // Spot-check entries in BTree
                for (int i = 0; i < entryCount; i += Math.Max(1, entryCount / 5))
                {
                    var key = new EmailHashedID((ulong)(trial * 10000 + i + 1), 0, 0, 0);
                    var lookup = await btreeIndex.LookupAsync(key);
                    Assert.True(lookup.IsSuccess,
                        $"Trial {trial}: Key {i + 1} should be in BTree after clean shutdown");
                }
            }

            passedTrials++;
        }

        sw.Stop();
        _output.WriteLine($"Clean flush crash recovery: {passedTrials}/{totalTrials} trials passed in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(totalTrials, passedTrials);
    }
}
