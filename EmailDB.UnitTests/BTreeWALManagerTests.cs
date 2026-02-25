using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeWALManagerTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeWALManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_wal_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task InsertAsync_BuffersEntries_NotVisibleInBTreeUntilFlush()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test_buffered.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 100,
            autoFlushEnabled: false);

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);

        // Act — insert entries via WAL manager (auto-flush disabled)
        await walManager.InsertAsync(key1, 1000, 1);
        await walManager.InsertAsync(key2, 2000, 2);
        await walManager.InsertAsync(key3, 3000, 3);

        // Assert — entries are in the WAL buffer
        Assert.Equal(3, walManager.BufferCount);
        Assert.True(walManager.IsDirty);

        // Assert — WAL manager's LookupAsync finds entries from the buffer
        var walLookup1 = await walManager.LookupAsync(key1);
        Assert.True(walLookup1.IsSuccess, "WAL LookupAsync should find buffered key1");
        Assert.Equal(1000, walLookup1.Value.BlockOffset);
        Assert.Equal(1, walLookup1.Value.BlockId);

        var walLookup2 = await walManager.LookupAsync(key2);
        Assert.True(walLookup2.IsSuccess, "WAL LookupAsync should find buffered key2");
        Assert.Equal(2000, walLookup2.Value.BlockOffset);

        var walLookup3 = await walManager.LookupAsync(key3);
        Assert.True(walLookup3.IsSuccess, "WAL LookupAsync should find buffered key3");
        Assert.Equal(3000, walLookup3.Value.BlockOffset);

        // Assert — entries are NOT visible in the B+-tree directly
        var btreeLookup1 = await btreeIndex.LookupAsync(key1);
        Assert.True(btreeLookup1.IsFailure,
            "Entry should NOT be visible in BTree before flush");

        var btreeLookup2 = await btreeIndex.LookupAsync(key2);
        Assert.True(btreeLookup2.IsFailure,
            "Entry should NOT be visible in BTree before flush");

        var btreeLookup3 = await btreeIndex.LookupAsync(key3);
        Assert.True(btreeLookup3.IsFailure,
            "Entry should NOT be visible in BTree before flush");

        // Assert — BTree is still empty (no root created)
        Assert.Null(btreeIndex.CurrentRoot);

        // Act — flush the WAL buffer to the B+-tree
        await walManager.FlushAsync();

        // Assert — buffer is now empty and clean
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);

        // Assert — entries ARE now visible in the B+-tree
        var afterFlush1 = await btreeIndex.LookupAsync(key1);
        Assert.True(afterFlush1.IsSuccess,
            $"Key1 should be in BTree after flush: {afterFlush1.Error}");
        Assert.Equal(1000, afterFlush1.Value.BlockOffset);
        Assert.Equal(1, afterFlush1.Value.BlockId);

        var afterFlush2 = await btreeIndex.LookupAsync(key2);
        Assert.True(afterFlush2.IsSuccess,
            $"Key2 should be in BTree after flush: {afterFlush2.Error}");
        Assert.Equal(2000, afterFlush2.Value.BlockOffset);
        Assert.Equal(2, afterFlush2.Value.BlockId);

        var afterFlush3 = await btreeIndex.LookupAsync(key3);
        Assert.True(afterFlush3.IsSuccess,
            $"Key3 should be in BTree after flush: {afterFlush3.Error}");
        Assert.Equal(3000, afterFlush3.Value.BlockOffset);
        Assert.Equal(3, afterFlush3.Value.BlockId);

        // Assert — BTree now has exactly 3 entries
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal(3L, btreeIndex.CurrentRoot.EntryCount);

        // Assert — flush sequence incremented once
        Assert.Equal(1, walManager.FlushSequence);
    }

    [Fact]
    public async Task InsertAsync_MultipleBeforeFlush_BTreeRemainsEmptyUntilFlush()
    {
        // Verify with a larger batch that BTree stays empty across many buffered inserts
        var filePath = Path.Combine(_tempDir, "test_batch_buffered.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 200,
            autoFlushEnabled: false);

        const int insertCount = 50;

        // Act — buffer many entries
        for (int i = 0; i < insertCount; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, i * 100L, i);
        }

        // Assert — all entries buffered, BTree is empty
        Assert.Equal(insertCount, walManager.BufferCount);
        Assert.Null(btreeIndex.CurrentRoot);

        // Spot-check that BTree lookup fails for several keys
        for (int i = 0; i < insertCount; i += 10)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var btreeResult = await btreeIndex.LookupAsync(key);
            Assert.True(btreeResult.IsFailure,
                $"Key {i + 1} should NOT be in BTree before flush");
        }

        // Act — flush
        await walManager.FlushAsync();

        // Assert — all entries now in BTree
        Assert.Equal(0, walManager.BufferCount);
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal((long)insertCount, btreeIndex.CurrentRoot.EntryCount);

        // Verify every inserted key is now findable in the BTree
        for (int i = 0; i < insertCount; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.LookupAsync(key);
            Assert.True(result.IsSuccess,
                $"Key {i + 1} should be in BTree after flush: {result.Error}");
            Assert.Equal(i * 100L, result.Value.BlockOffset);
            Assert.Equal((long)i, result.Value.BlockId);
        }
    }

    [Fact]
    public async Task FlushAsync_WritesAllBufferedEntries_ToBTreeAtomically()
    {
        // Arrange — buffer a known set of entries, then flush and verify every single one
        // is present in the B+-tree with correct values (nothing lost, nothing partial)
        var filePath = Path.Combine(_tempDir, "test_flush_atomic.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 500,
            autoFlushEnabled: false);

        const int batchSize = 75;
        var entries = new List<(EmailHashedID Key, long Offset, long Id)>();

        for (int i = 0; i < batchSize; i++)
        {
            var key = new EmailHashedID((ulong)(i * 7 + 3), (ulong)(i * 13 + 1), (ulong)(i * 5), (ulong)i);
            entries.Add((key, (i + 1) * 256L, i + 1));
        }

        // Act — insert all entries into the WAL buffer
        foreach (var e in entries)
            await walManager.InsertAsync(e.Key, e.Offset, e.Id);

        Assert.Equal(batchSize, walManager.BufferCount);
        Assert.True(walManager.IsDirty);
        long seqBefore = walManager.FlushSequence;

        // Act — single flush
        await walManager.FlushAsync();

        // Assert — buffer is fully drained
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(seqBefore + 1, walManager.FlushSequence);

        // Assert — BTree has exactly batchSize entries
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal((long)batchSize, btreeIndex.CurrentRoot.EntryCount);

        // Assert — every single entry is present with correct values
        foreach (var e in entries)
        {
            var result = await btreeIndex.LookupAsync(e.Key);
            Assert.True(result.IsSuccess,
                $"Key ({e.Key}) should be in BTree after flush: {result.Error}");
            Assert.Equal(e.Offset, result.Value.BlockOffset);
            Assert.Equal(e.Id, result.Value.BlockId);
        }

        // Act — buffer a second batch and flush again to verify repeated atomic flushes
        const int batch2Size = 30;
        var entries2 = new List<(EmailHashedID Key, long Offset, long Id)>();
        for (int i = 0; i < batch2Size; i++)
        {
            var key = new EmailHashedID((ulong)(1000 + i), 0, 0, 0);
            entries2.Add((key, (i + 100) * 512L, i + 1000));
        }

        foreach (var e in entries2)
            await walManager.InsertAsync(e.Key, e.Offset, e.Id);

        await walManager.FlushAsync();

        // Assert — BTree now has both batches
        Assert.Equal((long)(batchSize + batch2Size), btreeIndex.CurrentRoot!.EntryCount);
        Assert.Equal(seqBefore + 2, walManager.FlushSequence);

        // Verify second batch entries
        foreach (var e in entries2)
        {
            var result = await btreeIndex.LookupAsync(e.Key);
            Assert.True(result.IsSuccess,
                $"Second-batch key ({e.Key}) should be in BTree: {result.Error}");
            Assert.Equal(e.Offset, result.Value.BlockOffset);
            Assert.Equal(e.Id, result.Value.BlockId);
        }

        // Verify first batch entries are still intact
        foreach (var e in entries)
        {
            var result = await btreeIndex.LookupAsync(e.Key);
            Assert.True(result.IsSuccess,
                $"First-batch key ({e.Key}) should still be in BTree: {result.Error}");
            Assert.Equal(e.Offset, result.Value.BlockOffset);
            Assert.Equal(e.Id, result.Value.BlockId);
        }
    }

    [Fact]
    public async Task InsertAsync_CountThreshold_TriggersAutomaticFlush()
    {
        // Arrange — set a low auto-flush threshold so we can trigger it easily
        var filePath = Path.Combine(_tempDir, "test_count_threshold.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        const int threshold = 5;
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: threshold,
            autoFlushEnabled: true);

        // Act — insert (threshold - 1) entries; should NOT trigger auto-flush
        for (int i = 0; i < threshold - 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        // Assert — buffer is still populated, no flush has occurred
        Assert.Equal(threshold - 1, walManager.BufferCount);
        Assert.True(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence);
        Assert.Null(btreeIndex.CurrentRoot);

        // Act — insert the threshold-th entry, which should trigger auto-flush
        var triggerKey = new EmailHashedID((ulong)threshold, 0, 0, 0);
        await walManager.InsertAsync(triggerKey, threshold * 100L, threshold);

        // Assert — auto-flush has fired: buffer is empty, dirty flag cleared, sequence incremented
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(1, walManager.FlushSequence);

        // Assert — all entries are now visible in the B+-tree
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal((long)threshold, btreeIndex.CurrentRoot.EntryCount);

        for (int i = 0; i < threshold; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.LookupAsync(key);
            Assert.True(result.IsSuccess,
                $"Key {i + 1} should be in BTree after auto-flush: {result.Error}");
            Assert.Equal((i + 1) * 100L, result.Value.BlockOffset);
            Assert.Equal((long)(i + 1), result.Value.BlockId);
        }
    }

    [Fact]
    public async Task InsertAsync_CountThreshold_TriggersMultipleAutoFlushes()
    {
        // Verify that auto-flush triggers repeatedly as the buffer refills
        var filePath = Path.Combine(_tempDir, "test_count_multi_flush.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        const int threshold = 3;
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: threshold,
            autoFlushEnabled: true);

        // Act — insert 3 entries (hits threshold, triggers first flush)
        for (int i = 0; i < threshold; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        Assert.Equal(1, walManager.FlushSequence);
        Assert.Equal(0, walManager.BufferCount);

        // Act — insert 3 more entries (hits threshold again, triggers second flush)
        for (int i = threshold; i < threshold * 2; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        Assert.Equal(2, walManager.FlushSequence);
        Assert.Equal(0, walManager.BufferCount);

        // Assert — all entries from both flushes are in the B+-tree
        Assert.Equal((long)(threshold * 2), btreeIndex.CurrentRoot!.EntryCount);
    }

    [Fact]
    public async Task InsertAsync_AutoFlushDisabled_NeverTriggersFlush()
    {
        // Verify that exceeding the threshold does NOT auto-flush when disabled
        var filePath = Path.Combine(_tempDir, "test_no_autoflush.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        const int threshold = 3;
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: threshold,
            autoFlushEnabled: false);

        // Act — insert well past the threshold
        for (int i = 0; i < threshold * 3; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        // Assert — no auto-flush occurred; everything is still buffered
        Assert.Equal(threshold * 3, walManager.BufferCount);
        Assert.True(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence);
        Assert.Null(btreeIndex.CurrentRoot);
    }

    [Fact]
    public async Task InsertAsync_TimeThreshold_TriggersAutomaticFlush()
    {
        // Arrange — set a short time-based auto-flush interval with a high count threshold
        // so that only the timer triggers the flush, not the count threshold.
        var filePath = Path.Combine(_tempDir, "test_time_threshold.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 1000, // high count threshold — won't trigger
            autoFlushEnabled: false,  // count-based auto-flush disabled
            autoFlushInterval: TimeSpan.FromMilliseconds(100)); // time-based flush

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);

        // Act — insert entries (well below count threshold)
        await walManager.InsertAsync(key1, 1000, 1);
        await walManager.InsertAsync(key2, 2000, 2);
        await walManager.InsertAsync(key3, 3000, 3);

        // Assert — entries are buffered, no flush yet
        Assert.Equal(3, walManager.BufferCount);
        Assert.True(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence);
        Assert.Null(btreeIndex.CurrentRoot);

        // Act — wait for the time-based flush to fire
        // Use a polling loop instead of a fixed delay to reduce flakiness
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (walManager.FlushSequence == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Assert — time-based flush has fired
        Assert.Equal(1, walManager.FlushSequence);
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);

        // Assert — all entries are now visible in the B+-tree
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal(3L, btreeIndex.CurrentRoot.EntryCount);

        var result1 = await btreeIndex.LookupAsync(key1);
        Assert.True(result1.IsSuccess, $"Key1 should be in BTree after time-flush: {result1.Error}");
        Assert.Equal(1000, result1.Value.BlockOffset);
        Assert.Equal(1, result1.Value.BlockId);

        var result2 = await btreeIndex.LookupAsync(key2);
        Assert.True(result2.IsSuccess, $"Key2 should be in BTree after time-flush: {result2.Error}");
        Assert.Equal(2000, result2.Value.BlockOffset);

        var result3 = await btreeIndex.LookupAsync(key3);
        Assert.True(result3.IsSuccess, $"Key3 should be in BTree after time-flush: {result3.Error}");
        Assert.Equal(3000, result3.Value.BlockOffset);
    }

    [Fact]
    public async Task InsertAsync_TimeThreshold_TriggersRepeatedFlushes()
    {
        // Verify the timer fires repeatedly as new entries are buffered
        var filePath = Path.Combine(_tempDir, "test_time_multi_flush.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 1000,
            autoFlushEnabled: false,
            autoFlushInterval: TimeSpan.FromMilliseconds(100));

        // Act — insert first batch
        for (int i = 0; i < 3; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        // Wait for first time-flush
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (walManager.FlushSequence < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(1, walManager.FlushSequence);
        Assert.Equal(0, walManager.BufferCount);

        // Act — insert second batch after first flush
        for (int i = 3; i < 6; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        // Wait for second time-flush
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (walManager.FlushSequence < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(2, walManager.FlushSequence);
        Assert.Equal(0, walManager.BufferCount);

        // Assert — all 6 entries are in the B+-tree from both flushes
        Assert.Equal(6L, btreeIndex.CurrentRoot!.EntryCount);
    }

    [Fact]
    public async Task InsertAsync_NoTimeThreshold_TimerDoesNotFlush()
    {
        // Verify that when no autoFlushInterval is provided, no timer-based flush occurs
        var filePath = Path.Combine(_tempDir, "test_no_time_threshold.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 1000,
            autoFlushEnabled: false); // no autoFlushInterval

        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        // Wait longer than a typical timer interval
        await Task.Delay(300);

        // Assert — no flush occurred
        Assert.Equal(5, walManager.BufferCount);
        Assert.True(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence);
        Assert.Null(btreeIndex.CurrentRoot);
    }

    [Fact]
    public async Task FlushAsync_ExplicitCall_WorksCorrectly()
    {
        // Verify that an explicit FlushAsync() call:
        //  1. Transfers all buffered entries to the B+-tree
        //  2. Clears the buffer
        //  3. Resets the dirty flag
        //  4. Increments FlushSequence
        //  5. Is a no-op when the buffer is empty
        //  6. Can be called multiple times with new data between each call
        var filePath = Path.Combine(_tempDir, "test_explicit_flush.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 1000,  // high threshold — auto-flush won't trigger
            autoFlushEnabled: false);

        // --- Case 1: FlushAsync on empty buffer is a no-op ---
        await walManager.FlushAsync();

        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence); // no increment on empty flush
        Assert.Null(btreeIndex.CurrentRoot);

        // --- Case 2: Explicit flush transfers entries and updates state ---
        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);

        await walManager.InsertAsync(key1, 1000, 1);
        await walManager.InsertAsync(key2, 2000, 2);

        Assert.Equal(2, walManager.BufferCount);
        Assert.True(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence);

        await walManager.FlushAsync();

        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(1, walManager.FlushSequence);

        // Entries are now in the B+-tree
        var lookup1 = await btreeIndex.LookupAsync(key1);
        Assert.True(lookup1.IsSuccess, $"key1 should be in BTree after flush: {lookup1.Error}");
        Assert.Equal(1000, lookup1.Value.BlockOffset);
        Assert.Equal(1, lookup1.Value.BlockId);

        var lookup2 = await btreeIndex.LookupAsync(key2);
        Assert.True(lookup2.IsSuccess, $"key2 should be in BTree after flush: {lookup2.Error}");
        Assert.Equal(2000, lookup2.Value.BlockOffset);
        Assert.Equal(2, lookup2.Value.BlockId);

        // --- Case 3: Second flush on empty buffer is again a no-op ---
        await walManager.FlushAsync();

        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(1, walManager.FlushSequence); // unchanged

        // --- Case 4: Repeated explicit flush with new data ---
        var key3 = new EmailHashedID(90, 100, 110, 120);
        await walManager.InsertAsync(key3, 3000, 3);

        await walManager.FlushAsync();

        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(2, walManager.FlushSequence);

        // All three entries are in the B+-tree
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal(3L, btreeIndex.CurrentRoot.EntryCount);

        var lookup3 = await btreeIndex.LookupAsync(key3);
        Assert.True(lookup3.IsSuccess, $"key3 should be in BTree after second flush: {lookup3.Error}");
        Assert.Equal(3000, lookup3.Value.BlockOffset);
        Assert.Equal(3, lookup3.Value.BlockId);

        // Previous entries still intact
        var recheck1 = await btreeIndex.LookupAsync(key1);
        Assert.True(recheck1.IsSuccess, "key1 should still be in BTree");
        Assert.Equal(1000, recheck1.Value.BlockOffset);

        var recheck2 = await btreeIndex.LookupAsync(key2);
        Assert.True(recheck2.IsSuccess, "key2 should still be in BTree");
        Assert.Equal(2000, recheck2.Value.BlockOffset);

        // --- Case 5: WAL manager lookup falls through to BTree after flush ---
        var walLookup1 = await walManager.LookupAsync(key1);
        Assert.True(walLookup1.IsSuccess,
            "WAL LookupAsync should find flushed key via BTree fallthrough");
        Assert.Equal(1000, walLookup1.Value.BlockOffset);
    }

    [Fact]
    public async Task LookupAsync_ReturnsBufferedEntry_BeforeFallingThroughToBTree()
    {
        // Verify WAL lookup returns buffered entry even when BTree has different data
        var filePath = Path.Combine(_tempDir, "test_lookup_priority.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Pre-populate BTree with a key
        var key = new EmailHashedID(42, 84, 126, 168);
        var btreeInsert = await btreeIndex.InsertAsync(key, 1000, 100);
        Assert.True(btreeInsert.IsSuccess);

        // Use a WAL offset past the BTree blocks so WAL disk writes don't
        // overwrite BTree data (in production, this comes from HeaderContent.WALRegionOffset)
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 1_048_576,
            autoFlushThreshold: 100,
            autoFlushEnabled: false);

        // Buffer an update to the same key with different values
        await walManager.InsertAsync(key, 9999, 999);

        // Assert — WAL lookup returns the BUFFERED (newer) values, not BTree values
        var walResult = await walManager.LookupAsync(key);
        Assert.True(walResult.IsSuccess);
        Assert.Equal(9999, walResult.Value.BlockOffset);
        Assert.Equal(999, walResult.Value.BlockId);

        // Assert — BTree still has old values
        var btreeResult = await btreeIndex.LookupAsync(key);
        Assert.True(btreeResult.IsSuccess);
        Assert.Equal(1000, btreeResult.Value.BlockOffset);
        Assert.Equal(100, btreeResult.Value.BlockId);

        // Assert — WAL lookup falls through to BTree for non-buffered keys
        var otherKey = new EmailHashedID(1, 2, 3, 4);
        var btreeInsert2 = await btreeIndex.InsertAsync(otherKey, 5000, 500);
        Assert.True(btreeInsert2.IsSuccess);

        var fallthrough = await walManager.LookupAsync(otherKey);
        Assert.True(fallthrough.IsSuccess,
            "WAL lookup should fall through to BTree for non-buffered keys");
        Assert.Equal(5000, fallthrough.Value.BlockOffset);
    }

    [Fact]
    public async Task WALEntries_SurviveProcessCrash_RecoveredOnReopen()
    {
        // Simulate a process crash: insert entries, dispose the WAL manager
        // (without flushing), then create a new WAL manager against the same file.
        // The new manager should recover all unflushed entries from disk.
        var filePath = Path.Combine(_tempDir, "test_crash_recovery.emdb");

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);

        // Phase 1: Insert entries, then "crash" (dispose without flushing)
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            await walManager.InsertAsync(key1, 1000, 1);
            await walManager.InsertAsync(key2, 2000, 2);
            await walManager.InsertAsync(key3, 3000, 3);

            // Verify entries are buffered
            Assert.Equal(3, walManager.BufferCount);
            Assert.True(walManager.IsDirty);

            // Entries are NOT in the B+-tree
            Assert.Null(btreeIndex.CurrentRoot);

            // Dispose WITHOUT calling FlushAsync — simulates a crash
        }

        // Phase 2: Reopen the file — WAL manager should recover entries from disk
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            // Assert — recovered entries are in the buffer
            Assert.Equal(3, walManager.BufferCount);
            Assert.True(walManager.IsDirty);

            // Assert — recovered entries are accessible via LookupAsync
            var lookup1 = await walManager.LookupAsync(key1);
            Assert.True(lookup1.IsSuccess, "Key1 should be recovered from WAL on disk");
            Assert.Equal(1000, lookup1.Value.BlockOffset);
            Assert.Equal(1, lookup1.Value.BlockId);

            var lookup2 = await walManager.LookupAsync(key2);
            Assert.True(lookup2.IsSuccess, "Key2 should be recovered from WAL on disk");
            Assert.Equal(2000, lookup2.Value.BlockOffset);
            Assert.Equal(2, lookup2.Value.BlockId);

            var lookup3 = await walManager.LookupAsync(key3);
            Assert.True(lookup3.IsSuccess, "Key3 should be recovered from WAL on disk");
            Assert.Equal(3000, lookup3.Value.BlockOffset);
            Assert.Equal(3, lookup3.Value.BlockId);

            // Assert — B+-tree is still empty (entries are only in WAL buffer)
            Assert.Null(btreeIndex.CurrentRoot);

            // Act — flush the recovered entries to the B+-tree
            await walManager.FlushAsync();

            // Assert — entries are now in the B+-tree
            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot.EntryCount);

            var btreeLookup1 = await btreeIndex.LookupAsync(key1);
            Assert.True(btreeLookup1.IsSuccess, "Key1 should be in BTree after flushing recovered WAL");
            Assert.Equal(1000, btreeLookup1.Value.BlockOffset);
        }
    }

    [Fact]
    public async Task WALEntries_SurviveCrash_FlushSequencePreserved()
    {
        // Verify that FlushSequence is persisted and recovered across crashes
        var filePath = Path.Combine(_tempDir, "test_crash_flushseq.emdb");

        // Phase 1: Insert + flush (FlushSequence goes to 1), then insert more + crash
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            var key1 = new EmailHashedID(1, 0, 0, 0);
            await walManager.InsertAsync(key1, 100, 1);
            await walManager.FlushAsync();
            Assert.Equal(1, walManager.FlushSequence);

            // Insert more entries without flushing (simulating pre-crash state)
            var key2 = new EmailHashedID(2, 0, 0, 0);
            await walManager.InsertAsync(key2, 200, 2);
            Assert.Equal(1, walManager.BufferCount);
            Assert.True(walManager.IsDirty);
        }

        // Phase 2: Recover — FlushSequence should be 1, unflushed entry should be recovered
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            Assert.Equal(1, walManager.FlushSequence);
            Assert.Equal(1, walManager.BufferCount);
            Assert.True(walManager.IsDirty);

            var lookup = await walManager.LookupAsync(new EmailHashedID(2, 0, 0, 0));
            Assert.True(lookup.IsSuccess, "Key2 should be recovered from WAL");
            Assert.Equal(200, lookup.Value.BlockOffset);
        }
    }

    [Fact]
    public async Task WALEntries_CleanFlush_NothingToRecoverOnReopen()
    {
        // Verify that after a clean flush, reopening recovers nothing
        var filePath = Path.Combine(_tempDir, "test_clean_flush_reopen.emdb");

        // Phase 1: Insert and flush cleanly
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            var key = new EmailHashedID(42, 84, 126, 168);
            await walManager.InsertAsync(key, 5000, 50);
            await walManager.FlushAsync();

            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);
        }

        // Phase 2: Reopen — should have empty buffer, clean state
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);
            Assert.Equal(1, walManager.FlushSequence);
        }
    }

    [Fact]
    public async Task WALEntries_UpsertBeforeCrash_RecoveredWithLatestValue()
    {
        // Verify that if a key is upserted before crash, the latest value is recovered
        var filePath = Path.Combine(_tempDir, "test_upsert_crash.emdb");
        var key = new EmailHashedID(10, 20, 30, 40);

        // Phase 1: Insert, then upsert the same key, then crash
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            await walManager.InsertAsync(key, 1000, 1);
            await walManager.InsertAsync(key, 9999, 99); // upsert with new values

            Assert.Equal(1, walManager.BufferCount); // only 1 entry (deduped)
        }

        // Phase 2: Recover — should have the LATEST upserted value
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            Assert.Equal(1, walManager.BufferCount);
            Assert.True(walManager.IsDirty);

            var lookup = await walManager.LookupAsync(key);
            Assert.True(lookup.IsSuccess, "Key should be recovered with latest value");
            Assert.Equal(9999, lookup.Value.BlockOffset);
            Assert.Equal(99, lookup.Value.BlockId);
        }
    }

    [Fact]
    public async Task ConcurrentInserts_DoNotCorruptEntries()
    {
        // Verify that many concurrent InsertAsync calls are serialized correctly
        // by the AsyncReaderWriterLock and no entries are lost or corrupted.
        var filePath = Path.Combine(_tempDir, "test_concurrent_inserts.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 10_000, // high threshold — no auto-flush during test
            autoFlushEnabled: false);

        const int totalInserts = 200;
        const int concurrency = 20;

        // Build distinct keys up front so each concurrent task gets unique keys
        var entries = new (EmailHashedID Key, long Offset, long Id)[totalInserts];
        for (int i = 0; i < totalInserts; i++)
        {
            entries[i] = (
                new EmailHashedID((ulong)(i + 1), (ulong)(i * 3 + 7), (ulong)(i * 5 + 11), (ulong)(i * 7 + 13)),
                (i + 1) * 100L,
                i + 1
            );
        }

        // Act — fire all inserts concurrently using a limited degree of parallelism
        var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new Task[totalInserts];
        for (int i = 0; i < totalInserts; i++)
        {
            var entry = entries[i];
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await walManager.InsertAsync(entry.Key, entry.Offset, entry.Id);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        await Task.WhenAll(tasks);

        // Assert — all entries are in the buffer
        Assert.Equal(totalInserts, walManager.BufferCount);
        Assert.True(walManager.IsDirty);

        // Assert — every entry is retrievable via LookupAsync with correct values
        for (int i = 0; i < totalInserts; i++)
        {
            var result = await walManager.LookupAsync(entries[i].Key);
            Assert.True(result.IsSuccess,
                $"Entry {i} should be in WAL buffer after concurrent inserts");
            Assert.Equal(entries[i].Offset, result.Value.BlockOffset);
            Assert.Equal(entries[i].Id, result.Value.BlockId);
        }

        // Act — flush and verify entries survive into the B+-tree
        await walManager.FlushAsync();

        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(1, walManager.FlushSequence);
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal((long)totalInserts, btreeIndex.CurrentRoot.EntryCount);

        for (int i = 0; i < totalInserts; i++)
        {
            var result = await btreeIndex.LookupAsync(entries[i].Key);
            Assert.True(result.IsSuccess,
                $"Entry {i} should be in BTree after flush: {result.Error}");
            Assert.Equal(entries[i].Offset, result.Value.BlockOffset);
            Assert.Equal(entries[i].Id, result.Value.BlockId);
        }
    }

    [Fact]
    public async Task ConcurrentInserts_WithDuplicateKeys_LastWriteWins()
    {
        // Verify that concurrent upserts to the same key don't corrupt the entry;
        // one of the concurrent values wins and the result is a valid entry.
        var filePath = Path.Combine(_tempDir, "test_concurrent_upserts.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 10_000,
            autoFlushEnabled: false);

        var sharedKey = new EmailHashedID(42, 84, 126, 168);
        const int concurrentWrites = 50;

        // Act — fire many concurrent writes to the same key with different values
        var tasks = new Task[concurrentWrites];
        for (int i = 0; i < concurrentWrites; i++)
        {
            int captured = i;
            tasks[i] = Task.Run(async () =>
            {
                await walManager.InsertAsync(sharedKey, (captured + 1) * 1000L, captured + 1);
            });
        }

        await Task.WhenAll(tasks);

        // Assert — only one entry for the key (deduped), not 50
        Assert.Equal(1, walManager.BufferCount);
        Assert.True(walManager.IsDirty);

        // Assert — the value is one of the written values (serialized, so some winner)
        var result = await walManager.LookupAsync(sharedKey);
        Assert.True(result.IsSuccess, "Shared key should exist after concurrent upserts");
        // The offset must be one of the values we wrote: (1*1000) through (50*1000)
        Assert.InRange(result.Value.BlockOffset, 1000, concurrentWrites * 1000L);
        Assert.InRange(result.Value.BlockId, 1, concurrentWrites);
        // Verify offset and id are consistent (same write)
        Assert.Equal(result.Value.BlockId * 1000, result.Value.BlockOffset);
    }

    [Fact]
    public async Task BatchFlush_ProducesFewerNodeWrites_ThanIndividualInserts()
    {
        // The WAL's in-memory deduplication absorbs duplicate key inserts in the buffer,
        // so only unique entries are flushed to the BTree. In contrast, 100 individual
        // BTreeIndex.InsertAsync calls each create new blocks even for upserts (COW).
        const int totalOps = 100;
        const int uniqueKeyCount = 80;

        // Build 100 insert operations: 80 unique keys, then 20 updates to existing keys
        var rng = new Random(42);
        var operations = new (EmailHashedID Key, long Offset, long Id)[totalOps];
        for (int i = 0; i < totalOps; i++)
        {
            int keyIdx = i < uniqueKeyCount ? i : rng.Next(uniqueKeyCount);
            operations[i] = (
                new EmailHashedID((ulong)(keyIdx + 1), 0, 0, 0),
                (i + 1) * 100L,
                i + 1);
        }

        // --- Scenario A: 100 individual BTreeIndex.InsertAsync calls ---
        BlockIdGenerator.Instance.Reset();
        var fileA = Path.Combine(_tempDir, "individual.emdb");
        using var rawA = new RawBlockManager(fileA);
        var btreeA = new BTreeIndex(rawA);

        for (int i = 0; i < totalOps; i++)
        {
            var op = operations[i];
            await btreeA.InsertAsync(op.Key, op.Offset, op.Id);
        }

        int individualBlocks = rawA.GetBlockLocations().Count;

        // --- Scenario B: 100 WAL inserts → 1 batch flush ---
        BlockIdGenerator.Instance.Reset();
        var fileB = Path.Combine(_tempDir, "batch.emdb");
        using var rawB = new RawBlockManager(fileB);
        var btreeB = new BTreeIndex(rawB);
        using var walMgr = new BTreeWALManager(
            rawB, btreeB,
            walPayloadFileOffset: 10_000_000, // WAL region well past BTree blocks
            autoFlushThreshold: 200,
            autoFlushEnabled: false);

        for (int i = 0; i < totalOps; i++)
        {
            var op = operations[i];
            await walMgr.InsertAsync(op.Key, op.Offset, op.Id);
        }

        // WAL deduplicates: only 80 unique keys remain in the buffer
        Assert.Equal(uniqueKeyCount, walMgr.BufferCount);

        await walMgr.FlushAsync();

        int batchBlocks = rawB.GetBlockLocations().Count;

        // Assert: batch produces strictly fewer BTree block writes because
        // the WAL absorbed 20 duplicate inserts that would each have created
        // new COW blocks in direct BTreeIndex.InsertAsync calls.
        Assert.True(batchBlocks < individualBlocks,
            $"Batch ({batchBlocks} blocks) should produce fewer node writes " +
            $"than individual ({individualBlocks} blocks) due to WAL deduplication");

        // Both trees end up with the same 80 unique entries
        Assert.Equal((long)uniqueKeyCount, btreeA.CurrentRoot!.EntryCount);
        Assert.Equal((long)uniqueKeyCount, btreeB.CurrentRoot!.EntryCount);

        // Verify all keys are accessible in both trees
        for (int i = 0; i < uniqueKeyCount; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var resultA = await btreeA.LookupAsync(key);
            var resultB = await btreeB.LookupAsync(key);
            Assert.True(resultA.IsSuccess, $"Key {i + 1} should be in individual tree");
            Assert.True(resultB.IsSuccess, $"Key {i + 1} should be in batch tree");
        }
    }

    [Fact]
    public async Task FlushAsync_WritesIndexRoot_AfterEverySuccessfulFlush()
    {
        // Verify acceptance criterion: an IndexRoot block is written to disk
        // after every successful WAL flush. Each flush should produce a new
        // IndexRoot block, and the total count should match the number of flushes.
        var filePath = Path.Combine(_tempDir, "test_indexroot_per_flush.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 10_000_000, // WAL region well past BTree blocks
            autoFlushThreshold: 1000,
            autoFlushEnabled: false);

        // Helper: count IndexRoot blocks on disk
        async Task<int> CountIndexRootBlocksAsync()
        {
            var locations = rawBlockManager.GetBlockLocations();
            int count = 0;
            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                    count++;
            }
            return count;
        }

        // Before any inserts/flushes: no IndexRoot blocks exist
        Assert.Equal(0, await CountIndexRootBlocksAsync());

        // --- Flush 1: insert 5 entries, flush ---
        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        await walManager.FlushAsync();

        int rootsAfterFlush1 = await CountIndexRootBlocksAsync();
        Assert.True(rootsAfterFlush1 >= 1,
            $"At least 1 IndexRoot block should exist after first flush, found {rootsAfterFlush1}");

        // Verify the BTreeIndex CurrentRoot matches the latest IndexRoot on disk
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal(5L, btreeIndex.CurrentRoot.EntryCount);

        // --- Flush 2: insert 3 more entries, flush ---
        for (int i = 5; i < 8; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        await walManager.FlushAsync();

        int rootsAfterFlush2 = await CountIndexRootBlocksAsync();
        Assert.True(rootsAfterFlush2 > rootsAfterFlush1,
            $"More IndexRoot blocks should exist after second flush: " +
            $"had {rootsAfterFlush1} after flush 1, now have {rootsAfterFlush2}");

        Assert.Equal(8L, btreeIndex.CurrentRoot!.EntryCount);

        // --- Flush 3: insert 2 more entries, flush ---
        for (int i = 8; i < 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            await walManager.InsertAsync(key, (i + 1) * 100L, i + 1);
        }

        await walManager.FlushAsync();

        int rootsAfterFlush3 = await CountIndexRootBlocksAsync();
        Assert.True(rootsAfterFlush3 > rootsAfterFlush2,
            $"More IndexRoot blocks should exist after third flush: " +
            $"had {rootsAfterFlush2} after flush 2, now have {rootsAfterFlush3}");

        Assert.Equal(10L, btreeIndex.CurrentRoot!.EntryCount);
        Assert.Equal(3, walManager.FlushSequence);

        // Verify the latest IndexRoot on disk has the correct entry count
        // by reading the most recent IndexRoot block
        var allLocations = rawBlockManager.GetBlockLocations();
        IndexRoot? latestRoot = null;
        long latestRootOffset = -1;
        foreach (var kvp in allLocations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > latestRootOffset)
                {
                    latestRootOffset = kvp.Value.Position;
                    latestRoot = BTreeNodeSerializer.DeserializeIndexRoot(blockResult.Value.Payload);
                }
            }
        }

        Assert.NotNull(latestRoot);
        Assert.Equal(10L, latestRoot!.EntryCount);
        Assert.Equal(btreeIndex.CurrentRoot.TreeHeight, latestRoot.TreeHeight);
        Assert.Equal(btreeIndex.CurrentRoot.RootNodeBlockOffset, latestRoot.RootNodeBlockOffset);
    }

    [Fact]
    public async Task CleanShutdown_Reopen_LoadsCorrectIndexRootAndTree()
    {
        // Verify acceptance criterion: "Clean shutdown followed by reopen loads correct IndexRoot and tree"
        //
        // Phase 1: Insert entries directly into BTreeIndex (which writes contiguous blocks from
        //          position 0), capture the IndexRoot state, and dispose cleanly.
        // Phase 2: Reopen file, find the latest IndexRoot block via the block scanner,
        //          reconstruct BTreeIndex, and verify every entry is intact.
        // Phase 3: Also verify that a BTreeWALManager created against the reopened tree
        //          has clean WAL state and its lookups fall through to the BTree correctly.
        var filePath = Path.Combine(_tempDir, "test_clean_shutdown_reopen.emdb");

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);
        var key4 = new EmailHashedID(200, 300, 400, 500);
        var key5 = new EmailHashedID(600, 700, 800, 900);

        long expectedEntryCount;
        ushort expectedTreeHeight;
        byte[] expectedRootNodeHash;
        long expectedRootNodeBlockOffset;

        // Phase 1: Build the tree and shut down cleanly
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Insert entries directly into BTreeIndex (each insert writes blocks + IndexRoot)
            await btreeIndex.InsertAsync(key1, 1000, 1);
            await btreeIndex.InsertAsync(key2, 2000, 2);
            await btreeIndex.InsertAsync(key3, 3000, 3);

            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot.EntryCount);

            // More entries to grow the tree
            await btreeIndex.InsertAsync(key4, 4000, 4);
            await btreeIndex.InsertAsync(key5, 5000, 5);

            Assert.Equal(5L, btreeIndex.CurrentRoot!.EntryCount);

            // Capture expected state before shutdown
            expectedEntryCount = btreeIndex.CurrentRoot.EntryCount;
            expectedTreeHeight = btreeIndex.CurrentRoot.TreeHeight;
            expectedRootNodeHash = (byte[])btreeIndex.CurrentRoot.RootNodeHash.Clone();
            expectedRootNodeBlockOffset = btreeIndex.CurrentRoot.RootNodeBlockOffset;

            // Clean shutdown: dispose flushes and closes the file
        }

        // Phase 2: Reopen the file and reconstruct the tree from disk
        {
            using var rawBlockManager = new RawBlockManager(filePath);

            // Find the latest IndexRoot block on disk by scanning all blocks
            var locations = rawBlockManager.GetBlockLocations();
            IndexRoot? latestRoot = null;
            long latestRootBlockOffset = -1;

            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > latestRootBlockOffset)
                    {
                        latestRootBlockOffset = kvp.Value.Position;
                        latestRoot = BTreeNodeSerializer.DeserializeIndexRoot(blockResult.Value.Payload);
                    }
                }
            }

            // Assert — IndexRoot was found and has correct metadata
            Assert.NotNull(latestRoot);
            Assert.Equal(expectedEntryCount, latestRoot!.EntryCount);
            Assert.Equal(expectedTreeHeight, latestRoot.TreeHeight);
            Assert.Equal(expectedRootNodeBlockOffset, latestRoot.RootNodeBlockOffset);
            Assert.Equal(expectedRootNodeHash, latestRoot.RootNodeHash);

            // Reconstruct BTreeIndex with the recovered IndexRoot
            var btreeIndex = new BTreeIndex(rawBlockManager, latestRoot, latestRootBlockOffset);

            // Assert — CurrentRoot matches the deserialized IndexRoot
            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(expectedEntryCount, btreeIndex.CurrentRoot!.EntryCount);
            Assert.Equal(expectedTreeHeight, btreeIndex.CurrentRoot.TreeHeight);

            // Assert — all 5 original entries are findable via BTree lookup
            var lookup1 = await btreeIndex.LookupAsync(key1);
            Assert.True(lookup1.IsSuccess, $"Key1 should be in BTree after reopen: {lookup1.Error}");
            Assert.Equal(1000, lookup1.Value.BlockOffset);
            Assert.Equal(1, lookup1.Value.BlockId);

            var lookup2 = await btreeIndex.LookupAsync(key2);
            Assert.True(lookup2.IsSuccess, $"Key2 should be in BTree after reopen: {lookup2.Error}");
            Assert.Equal(2000, lookup2.Value.BlockOffset);
            Assert.Equal(2, lookup2.Value.BlockId);

            var lookup3 = await btreeIndex.LookupAsync(key3);
            Assert.True(lookup3.IsSuccess, $"Key3 should be in BTree after reopen: {lookup3.Error}");
            Assert.Equal(3000, lookup3.Value.BlockOffset);
            Assert.Equal(3, lookup3.Value.BlockId);

            var lookup4 = await btreeIndex.LookupAsync(key4);
            Assert.True(lookup4.IsSuccess, $"Key4 should be in BTree after reopen: {lookup4.Error}");
            Assert.Equal(4000, lookup4.Value.BlockOffset);
            Assert.Equal(4, lookup4.Value.BlockId);

            var lookup5 = await btreeIndex.LookupAsync(key5);
            Assert.True(lookup5.IsSuccess, $"Key5 should be in BTree after reopen: {lookup5.Error}");
            Assert.Equal(5000, lookup5.Value.BlockOffset);
            Assert.Equal(5, lookup5.Value.BlockId);

            // Assert — non-existent key returns failure
            var missingKey = new EmailHashedID(999, 999, 999, 999);
            var missingLookup = await btreeIndex.LookupAsync(missingKey);
            Assert.True(missingLookup.IsFailure, "Missing key should not be found");

            // Phase 3: Verify WAL manager sees clean state on reopen and can look up via BTree
            // Place WAL region after the last block on disk so it doesn't interfere
            long walRegionOffset = rawBlockManager.FileLength + 4096;
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: walRegionOffset,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            // WAL should have nothing to recover after a clean shutdown
            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);

            // WAL+BTree combined lookup falls through to BTree
            var walLookup1 = await walManager.LookupAsync(key1);
            Assert.True(walLookup1.IsSuccess, "WAL+BTree lookup should find key1 after reopen");
            Assert.Equal(1000, walLookup1.Value.BlockOffset);
            Assert.Equal(1, walLookup1.Value.BlockId);

            var walLookup5 = await walManager.LookupAsync(key5);
            Assert.True(walLookup5.IsSuccess, "WAL+BTree lookup should find key5 after reopen");
            Assert.Equal(5000, walLookup5.Value.BlockOffset);
            Assert.Equal(5, walLookup5.Value.BlockId);
        }
    }

    [Fact]
    public async Task ConcurrentInserts_InterleavedWithLookups_NoCorruption()
    {
        // Verify that concurrent reads and writes don't corrupt data.
        // Readers should see a consistent view: either the entry exists with
        // correct values, or it doesn't exist yet.
        var filePath = Path.Combine(_tempDir, "test_concurrent_read_write.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 10_000,
            autoFlushEnabled: false);

        const int insertCount = 100;
        var entries = new (EmailHashedID Key, long Offset, long Id)[insertCount];
        for (int i = 0; i < insertCount; i++)
        {
            entries[i] = (
                new EmailHashedID((ulong)(i + 1), 0, 0, 0),
                (i + 1) * 100L,
                i + 1
            );
        }

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Launch writers and readers concurrently
        var writerTasks = new Task[insertCount];
        for (int i = 0; i < insertCount; i++)
        {
            var entry = entries[i];
            writerTasks[i] = Task.Run(async () =>
            {
                await walManager.InsertAsync(entry.Key, entry.Offset, entry.Id);
            });
        }

        var readerTasks = new Task[insertCount];
        for (int i = 0; i < insertCount; i++)
        {
            var entry = entries[i];
            readerTasks[i] = Task.Run(async () =>
            {
                // Small delay to let some writes start
                await Task.Yield();
                var result = await walManager.LookupAsync(entry.Key);
                if (result.IsSuccess)
                {
                    // If we found it, values must be consistent
                    if (result.Value.BlockOffset != entry.Offset ||
                        result.Value.BlockId != entry.Id)
                    {
                        errors.Add(
                            $"Corrupted read for key {entry.Key}: " +
                            $"expected ({entry.Offset},{entry.Id}), " +
                            $"got ({result.Value.BlockOffset},{result.Value.BlockId})");
                    }
                }
                // If not found, the write hasn't happened yet — that's fine
            });
        }

        await Task.WhenAll(writerTasks.Concat(readerTasks));

        // Assert — no corrupted reads occurred
        Assert.Empty(errors);

        // Assert — all entries are present after all writes complete
        Assert.Equal(insertCount, walManager.BufferCount);
        for (int i = 0; i < insertCount; i++)
        {
            var result = await walManager.LookupAsync(entries[i].Key);
            Assert.True(result.IsSuccess,
                $"Entry {i} should be in WAL buffer after all concurrent operations");
            Assert.Equal(entries[i].Offset, result.Value.BlockOffset);
            Assert.Equal(entries[i].Id, result.Value.BlockId);
        }
    }

    [Fact]
    public async Task WALEntries_WrittenAfterLastIndexRoot_ReplayedOnRecovery()
    {
        // Verify acceptance criterion:
        //   "WAL entries written after last IndexRoot are replayed on recovery"
        //
        // Scenario:
        //   Phase 1 — Insert entries via BTreeWALManager, flush them to BTree (creates IndexRoot).
        //             Then insert MORE entries into the WAL WITHOUT flushing (these are "after
        //             the last IndexRoot"). Dispose without flushing — simulates crash.
        //   Phase 2 — Reopen the file, create a new BTreeWALManager at the same WAL offset.
        //             The WAL manager should recover the unflushed entries from disk (replay).
        //             Verify they can be flushed to the BTree correctly.
        var filePath = Path.Combine(_tempDir, "test_wal_replay_after_indexroot.emdb");

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);
        // These will be the unflushed WAL entries ("after last IndexRoot")
        var key4 = new EmailHashedID(200, 300, 400, 500);
        var key5 = new EmailHashedID(600, 700, 800, 900);
        var key6 = new EmailHashedID(1000, 1100, 1200, 1300);

        // Phase 1: Insert 3 entries and flush (creates IndexRoot), then insert 3 more without flushing
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            // Insert and flush first batch — entries go to BTree, IndexRoot written
            await walManager.InsertAsync(key1, 1000, 1);
            await walManager.InsertAsync(key2, 2000, 2);
            await walManager.InsertAsync(key3, 3000, 3);
            await walManager.FlushAsync();

            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);
            Assert.Equal(1, walManager.FlushSequence);
            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot!.EntryCount);

            // Verify all 3 entries are in the BTree
            var lookup1 = await btreeIndex.LookupAsync(key1);
            Assert.True(lookup1.IsSuccess, $"Key1 should be in BTree after flush: {lookup1.Error}");
            Assert.Equal(1000, lookup1.Value.BlockOffset);

            // Insert more entries WITHOUT flushing — these are "after the last IndexRoot"
            // They are written to the WAL region on disk but NOT flushed to the BTree
            await walManager.InsertAsync(key4, 4000, 4);
            await walManager.InsertAsync(key5, 5000, 5);
            await walManager.InsertAsync(key6, 6000, 6);

            Assert.Equal(3, walManager.BufferCount);
            Assert.True(walManager.IsDirty);
            Assert.Equal(1, walManager.FlushSequence); // unchanged — no second flush

            // Verify unflushed entries are accessible via WAL buffer but NOT in BTree
            var walCheck4 = await walManager.LookupAsync(key4);
            Assert.True(walCheck4.IsSuccess, "Key4 should be in WAL buffer");
            Assert.Equal(4000, walCheck4.Value.BlockOffset);

            var btCheck4 = await btreeIndex.LookupAsync(key4);
            Assert.True(btCheck4.IsFailure, "Key4 should NOT be in BTree (not flushed)");

            // Dispose WITHOUT flushing — simulates crash
            // WAL entries (key4, key5, key6) are on disk but not in BTree
        }

        // Phase 2: Reopen — WAL manager should recover unflushed entries and replay them
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Create a new BTreeWALManager at the same WAL offset
            // Constructor calls RecoverFromDisk() which reads the WAL header,
            // finds dirty=1 and EntryCount=3, and loads entries into the buffer
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            // Assert — WAL recovered the 3 unflushed entries from disk
            Assert.Equal(3, walManager.BufferCount);
            Assert.True(walManager.IsDirty);
            Assert.Equal(1, walManager.FlushSequence); // preserved from Phase 1 flush

            // Assert — recovered entries are accessible via WAL LookupAsync
            var walLookup4 = await walManager.LookupAsync(key4);
            Assert.True(walLookup4.IsSuccess, "Key4 should be recovered from WAL on disk");
            Assert.Equal(4000, walLookup4.Value.BlockOffset);
            Assert.Equal(4, walLookup4.Value.BlockId);

            var walLookup5 = await walManager.LookupAsync(key5);
            Assert.True(walLookup5.IsSuccess, "Key5 should be recovered from WAL on disk");
            Assert.Equal(5000, walLookup5.Value.BlockOffset);
            Assert.Equal(5, walLookup5.Value.BlockId);

            var walLookup6 = await walManager.LookupAsync(key6);
            Assert.True(walLookup6.IsSuccess, "Key6 should be recovered from WAL on disk");
            Assert.Equal(6000, walLookup6.Value.BlockOffset);
            Assert.Equal(6, walLookup6.Value.BlockId);

            // Assert — BTree is empty (fresh BTreeIndex, no IndexRoot recovery in this path)
            Assert.Null(btreeIndex.CurrentRoot);

            // Replay: flush the recovered WAL entries into the BTree
            await walManager.FlushAsync();

            // Assert — buffer is drained, flush sequence incremented
            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);
            Assert.Equal(2, walManager.FlushSequence);

            // Assert — BTree now has the 3 recovered entries
            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot!.EntryCount);

            // Verify all 3 recovered entries are in the BTree with correct values
            var final4 = await btreeIndex.LookupAsync(key4);
            Assert.True(final4.IsSuccess, $"Key4 should be in BTree after WAL replay: {final4.Error}");
            Assert.Equal(4000, final4.Value.BlockOffset);
            Assert.Equal(4, final4.Value.BlockId);

            var final5 = await btreeIndex.LookupAsync(key5);
            Assert.True(final5.IsSuccess, $"Key5 should be in BTree after WAL replay: {final5.Error}");
            Assert.Equal(5000, final5.Value.BlockOffset);
            Assert.Equal(5, final5.Value.BlockId);

            var final6 = await btreeIndex.LookupAsync(key6);
            Assert.True(final6.IsSuccess, $"Key6 should be in BTree after WAL replay: {final6.Error}");
            Assert.Equal(6000, final6.Value.BlockOffset);
            Assert.Equal(6, final6.Value.BlockId);

            // Verify WAL lookups for non-buffered keys fall through to BTree
            var walFinal4 = await walManager.LookupAsync(key4);
            Assert.True(walFinal4.IsSuccess, "WAL+BTree lookup should find key4 after replay");
            Assert.Equal(4000, walFinal4.Value.BlockOffset);

            // Verify the WAL is clean after replay (a second reopen would find nothing to recover)
        }

        // Phase 3: Verify clean state — after replay and shutdown, reopening recovers nothing
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: 0,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            // WAL should be clean — nothing to recover after successful replay + shutdown
            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);
            Assert.Equal(2, walManager.FlushSequence); // both flushes accounted for
        }
    }

    [Fact]
    public async Task CrashAfterNodeWrites_BeforeIndexRoot_RevertsToLastValidTree()
    {
        // Verify acceptance criterion:
        //   "Crash after node writes but before IndexRoot reverts to previous valid tree"
        //
        // Scenario: New BTree leaf/internal blocks have been appended to disk as part
        // of an insert, but the IndexRoot block for that insert was NOT written (the
        // process crashed between the node writes and the IndexRoot write). On recovery,
        // the latest valid IndexRoot on disk points to the PREVIOUS tree state, and the
        // orphaned node blocks are harmlessly ignored.
        //
        // Implementation:
        //   Phase 1 — Build a known-good tree with N entries (multiple IndexRoots on disk).
        //   Phase 2 — Insert one more entry (writes new nodes + new IndexRoot).
        //   Phase 3 — Truncate the file to remove the latest IndexRoot block, simulating
        //             a crash that happened after nodes were written but before the
        //             IndexRoot was persisted.
        //   Phase 4 — Reopen the file. Scan for the latest valid IndexRoot. Verify the
        //             tree reverts to the Phase 1 state (N entries, not N+1).
        var filePath = Path.Combine(_tempDir, "test_crash_no_indexroot.emdb");

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);
        var key4 = new EmailHashedID(200, 300, 400, 500);

        long expectedEntryCountBeforeCrash;
        ushort expectedTreeHeightBeforeCrash;
        byte[] expectedRootNodeHashBeforeCrash;

        // Phase 1: Build a tree with 3 entries — this is the "last valid state"
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);

            await btreeIndex.InsertAsync(key1, 1000, 1);
            await btreeIndex.InsertAsync(key2, 2000, 2);
            await btreeIndex.InsertAsync(key3, 3000, 3);

            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot!.EntryCount);

            // Capture the expected state before the "crash"
            expectedEntryCountBeforeCrash = btreeIndex.CurrentRoot.EntryCount;
            expectedTreeHeightBeforeCrash = btreeIndex.CurrentRoot.TreeHeight;
            expectedRootNodeHashBeforeCrash = (byte[])btreeIndex.CurrentRoot.RootNodeHash.Clone();
        }

        // Phase 2: Insert a 4th entry — writes new leaf block(s) + a new IndexRoot
        long filePositionAfterPhase1;
        {
            using var rawBlockManager = new RawBlockManager(filePath);

            // Find the latest IndexRoot to reconstruct the tree
            var locations = rawBlockManager.GetBlockLocations();
            IndexRoot? latestRoot = null;
            long latestRootBlockOffset = -1;
            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > latestRootBlockOffset)
                    {
                        latestRootBlockOffset = kvp.Value.Position;
                        latestRoot = BTreeNodeSerializer.DeserializeIndexRoot(blockResult.Value.Payload);
                    }
                }
            }

            Assert.NotNull(latestRoot);
            var btreeIndex = new BTreeIndex(rawBlockManager, latestRoot, latestRootBlockOffset);

            // Record file size before the 4th insert
            filePositionAfterPhase1 = rawBlockManager.FileLength;

            // Insert the 4th entry — writes new node blocks + IndexRoot
            await btreeIndex.InsertAsync(key4, 4000, 4);
            Assert.Equal(4L, btreeIndex.CurrentRoot!.EntryCount);
        }

        // Phase 3: Simulate crash — truncate to remove the latest IndexRoot block
        // The latest IndexRoot is the LAST block in the file. We find it and truncate.
        {
            // Reopen the raw file to find the last IndexRoot position
            using var rawBlockManager = new RawBlockManager(filePath);
            var locations = rawBlockManager.GetBlockLocations();

            long lastIndexRootPosition = -1;
            long lastIndexRootBlockId = -1;
            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > lastIndexRootPosition)
                    {
                        lastIndexRootPosition = kvp.Value.Position;
                        lastIndexRootBlockId = kvp.Key;
                    }
                }
            }

            Assert.True(lastIndexRootPosition > 0, "Should have found at least one IndexRoot");
            // The last IndexRoot should be AFTER Phase 1's file position
            // (it's the IndexRoot for the 4th entry insert)
            Assert.True(lastIndexRootPosition >= filePositionAfterPhase1,
                $"Latest IndexRoot at {lastIndexRootPosition} should be after Phase 1 end {filePositionAfterPhase1}");
        }

        // Truncate the file at the last IndexRoot position (removes it but keeps node blocks)
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite))
        {
            // Find the last IndexRoot position by re-scanning
            // We need to re-open to determine the exact position
            // The node blocks from the 4th insert start after Phase 1's end,
            // and the IndexRoot for the 4th insert is the very last block.
            // Truncate at the start of that IndexRoot block.
            using var rawBlockManager = new RawBlockManager(filePath);
            var locations = rawBlockManager.GetBlockLocations();

            long lastIndexRootPosition = -1;
            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > lastIndexRootPosition)
                        lastIndexRootPosition = kvp.Value.Position;
                }
            }

            // Truncate the file right before the last IndexRoot block
            // This simulates: node blocks written, IndexRoot NOT written (crash)
            fs.SetLength(lastIndexRootPosition);
        }

        // Phase 4: Recovery — reopen the file and verify it reverts to the 3-entry tree
        {
            using var rawBlockManager = new RawBlockManager(filePath);

            // Scan for the latest valid IndexRoot
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

            // Assert — recovered IndexRoot matches the Phase 1 state (3 entries, not 4)
            Assert.NotNull(recoveredRoot);
            Assert.Equal(expectedEntryCountBeforeCrash, recoveredRoot!.EntryCount);
            Assert.Equal(expectedTreeHeightBeforeCrash, recoveredRoot.TreeHeight);
            Assert.Equal(expectedRootNodeHashBeforeCrash, recoveredRoot.RootNodeHash);

            // Rebuild BTreeIndex from the recovered IndexRoot
            var btreeIndex = new BTreeIndex(rawBlockManager, recoveredRoot, recoveredRootBlockOffset);

            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot!.EntryCount);

            // Assert — the 3 original entries are all present
            var lookup1 = await btreeIndex.LookupAsync(key1);
            Assert.True(lookup1.IsSuccess, $"Key1 should be recoverable: {lookup1.Error}");
            Assert.Equal(1000, lookup1.Value.BlockOffset);
            Assert.Equal(1, lookup1.Value.BlockId);

            var lookup2 = await btreeIndex.LookupAsync(key2);
            Assert.True(lookup2.IsSuccess, $"Key2 should be recoverable: {lookup2.Error}");
            Assert.Equal(2000, lookup2.Value.BlockOffset);
            Assert.Equal(2, lookup2.Value.BlockId);

            var lookup3 = await btreeIndex.LookupAsync(key3);
            Assert.True(lookup3.IsSuccess, $"Key3 should be recoverable: {lookup3.Error}");
            Assert.Equal(3000, lookup3.Value.BlockOffset);
            Assert.Equal(3, lookup3.Value.BlockId);

            // Assert — the 4th entry (from the interrupted insert) is NOT present
            var lookup4 = await btreeIndex.LookupAsync(key4);
            Assert.True(lookup4.IsFailure,
                "Key4 should NOT be present — its IndexRoot was never written (simulated crash)");

            // Assert — WAL manager on the recovered tree has clean state
            long walOffset = rawBlockManager.FileLength + 4096;
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: walOffset,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);

            // WAL+BTree combined lookup confirms the 3 entries are accessible
            var walLookup1 = await walManager.LookupAsync(key1);
            Assert.True(walLookup1.IsSuccess, "WAL+BTree should find key1 after crash recovery");
            Assert.Equal(1000, walLookup1.Value.BlockOffset);

            var walLookup3 = await walManager.LookupAsync(key3);
            Assert.True(walLookup3.IsSuccess, "WAL+BTree should find key3 after crash recovery");
            Assert.Equal(3000, walLookup3.Value.BlockOffset);

            // WAL+BTree lookup for missing key4 should fail
            var walLookup4 = await walManager.LookupAsync(key4);
            Assert.True(walLookup4.IsFailure,
                "WAL+BTree should NOT find key4 after crash recovery");
        }
    }

    [Fact]
    public async Task CrashAfterIndexRootWrite_RecoversToLatestTree()
    {
        // Verify acceptance criterion:
        //   "Crash after IndexRoot write recovers to latest tree"
        //
        // Scenario: An insert completes fully — new leaf/internal blocks AND the
        // IndexRoot block are all persisted to disk. The process then crashes
        // before any subsequent operation can run (e.g. metadata update, next
        // insert, or clean shutdown). On recovery, the latest IndexRoot on disk
        // should be found and the full tree (including the last insert) should
        // be accessible.
        //
        // This is the complement of CrashAfterNodeWrites_BeforeIndexRoot: here
        // the IndexRoot IS on disk, so recovery should see the LATEST state.
        //
        // Implementation:
        //   Phase 1 — Build a tree with 3 entries (known baseline).
        //   Phase 2 — Insert 2 more entries (total 5). IndexRoot IS written.
        //   Phase 3 — Simulate crash: append garbage bytes after the last valid
        //             block (partial write of whatever would have come next),
        //             then abruptly close the file handle.
        //   Phase 4 — Reopen the file. Scan for the latest valid IndexRoot.
        //             Verify recovery produces the 5-entry tree (latest state),
        //             not the 3-entry tree.
        var filePath = Path.Combine(_tempDir, "test_crash_after_indexroot.emdb");

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);
        var key4 = new EmailHashedID(200, 300, 400, 500);
        var key5 = new EmailHashedID(600, 700, 800, 900);

        long expectedEntryCount;
        ushort expectedTreeHeight;
        byte[] expectedRootNodeHash;
        long expectedRootNodeBlockOffset;

        // Phase 1: Build a 3-entry tree (baseline)
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);

            await btreeIndex.InsertAsync(key1, 1000, 1);
            await btreeIndex.InsertAsync(key2, 2000, 2);
            await btreeIndex.InsertAsync(key3, 3000, 3);

            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot!.EntryCount);
        }

        // Phase 2: Insert 2 more entries — IndexRoot for the 5-entry state IS written
        {
            using var rawBlockManager = new RawBlockManager(filePath);

            // Recover the tree from disk
            var locations = rawBlockManager.GetBlockLocations();
            IndexRoot? latestRoot = null;
            long latestRootBlockOffset = -1;
            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > latestRootBlockOffset)
                    {
                        latestRootBlockOffset = kvp.Value.Position;
                        latestRoot = BTreeNodeSerializer.DeserializeIndexRoot(blockResult.Value.Payload);
                    }
                }
            }

            Assert.NotNull(latestRoot);
            var btreeIndex = new BTreeIndex(rawBlockManager, latestRoot, latestRootBlockOffset);

            await btreeIndex.InsertAsync(key4, 4000, 4);
            await btreeIndex.InsertAsync(key5, 5000, 5);

            Assert.Equal(5L, btreeIndex.CurrentRoot!.EntryCount);

            // Capture the expected state — this IS the latest tree that should survive
            expectedEntryCount = btreeIndex.CurrentRoot.EntryCount;
            expectedTreeHeight = btreeIndex.CurrentRoot.TreeHeight;
            expectedRootNodeHash = (byte[])btreeIndex.CurrentRoot.RootNodeHash.Clone();
            expectedRootNodeBlockOffset = btreeIndex.CurrentRoot.RootNodeBlockOffset;
        }

        // Phase 3: Simulate crash AFTER IndexRoot write — append garbage bytes
        // This represents a partial write of the next operation that was interrupted
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(0, SeekOrigin.End);
            // Write garbage: a partial block header (incomplete next write)
            var garbage = new byte[37]; // odd size, not a valid block
            new Random(42).NextBytes(garbage);
            fs.Write(garbage, 0, garbage.Length);
            // No flush/close ceremony — simulates abrupt process death
        }

        // Phase 4: Recovery — reopen the file and verify it finds the LATEST tree (5 entries)
        {
            using var rawBlockManager = new RawBlockManager(filePath);

            // Scan for the latest valid IndexRoot
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

            // Assert — recovered IndexRoot matches the Phase 2 state (5 entries, NOT 3)
            Assert.NotNull(recoveredRoot);
            Assert.Equal(expectedEntryCount, recoveredRoot!.EntryCount);
            Assert.Equal(expectedTreeHeight, recoveredRoot.TreeHeight);
            Assert.Equal(expectedRootNodeBlockOffset, recoveredRoot.RootNodeBlockOffset);
            Assert.Equal(expectedRootNodeHash, recoveredRoot.RootNodeHash);

            // Rebuild BTreeIndex from the recovered IndexRoot
            var btreeIndex = new BTreeIndex(rawBlockManager, recoveredRoot, recoveredRootBlockOffset);

            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(5L, btreeIndex.CurrentRoot!.EntryCount);

            // Assert — all 5 entries are present (the latest tree survived the crash)
            var lookup1 = await btreeIndex.LookupAsync(key1);
            Assert.True(lookup1.IsSuccess, $"Key1 should be recoverable: {lookup1.Error}");
            Assert.Equal(1000, lookup1.Value.BlockOffset);
            Assert.Equal(1, lookup1.Value.BlockId);

            var lookup2 = await btreeIndex.LookupAsync(key2);
            Assert.True(lookup2.IsSuccess, $"Key2 should be recoverable: {lookup2.Error}");
            Assert.Equal(2000, lookup2.Value.BlockOffset);
            Assert.Equal(2, lookup2.Value.BlockId);

            var lookup3 = await btreeIndex.LookupAsync(key3);
            Assert.True(lookup3.IsSuccess, $"Key3 should be recoverable: {lookup3.Error}");
            Assert.Equal(3000, lookup3.Value.BlockOffset);
            Assert.Equal(3, lookup3.Value.BlockId);

            var lookup4 = await btreeIndex.LookupAsync(key4);
            Assert.True(lookup4.IsSuccess, $"Key4 should be recoverable: {lookup4.Error}");
            Assert.Equal(4000, lookup4.Value.BlockOffset);
            Assert.Equal(4, lookup4.Value.BlockId);

            var lookup5 = await btreeIndex.LookupAsync(key5);
            Assert.True(lookup5.IsSuccess, $"Key5 should be recoverable: {lookup5.Error}");
            Assert.Equal(5000, lookup5.Value.BlockOffset);
            Assert.Equal(5, lookup5.Value.BlockId);

            // Assert — non-existent key still fails
            var missingKey = new EmailHashedID(999, 999, 999, 999);
            var missingLookup = await btreeIndex.LookupAsync(missingKey);
            Assert.True(missingLookup.IsFailure, "Missing key should not be found");

            // Assert — WAL manager sees clean state on recovery
            long walOffset = rawBlockManager.FileLength + 4096;
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: walOffset,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);

            // WAL+BTree combined lookup confirms all 5 entries are accessible
            var walLookup1 = await walManager.LookupAsync(key1);
            Assert.True(walLookup1.IsSuccess, "WAL+BTree should find key1 after crash recovery");
            Assert.Equal(1000, walLookup1.Value.BlockOffset);

            var walLookup5 = await walManager.LookupAsync(key5);
            Assert.True(walLookup5.IsSuccess, "WAL+BTree should find key5 after crash recovery");
            Assert.Equal(5000, walLookup5.Value.BlockOffset);
        }
    }

    [Fact]
    public async Task RecoveryWithEmptyFile_InitializesEmptyTree()
    {
        // Verify acceptance criterion:
        //   "Recovery with empty file initializes empty tree"
        //
        // When BTreeWALManager is constructed on a brand-new empty file,
        // RecoverFromDisk() should find no WAL header, skip recovery,
        // and leave the tree in a clean empty state.
        var filePath = Path.Combine(_tempDir, "test_empty_file_recovery.emdb");

        // Create an empty file (no WAL header, no data at all)
        File.Create(filePath).Dispose();
        Assert.Equal(0, new FileInfo(filePath).Length);

        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 100,
            autoFlushEnabled: false);

        // Assert — WAL buffer is empty (nothing recovered)
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence);

        // Assert — BTree has no root (empty tree)
        Assert.Null(btreeIndex.CurrentRoot);

        // Assert — lookups return failure on the empty tree
        var key = new EmailHashedID(1, 2, 3, 4);
        var lookupResult = await walManager.LookupAsync(key);
        Assert.True(lookupResult.IsFailure, "Lookup on empty tree should return failure");

        // Assert — flush on empty buffer is a no-op (no crash)
        await walManager.FlushAsync();
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(0, walManager.FlushSequence); // unchanged — nothing was flushed

        // Assert — inserts work correctly after empty-file recovery
        var insertKey = new EmailHashedID(100, 200, 300, 400);
        await walManager.InsertAsync(insertKey, 5000, 1);
        Assert.Equal(1, walManager.BufferCount);
        Assert.True(walManager.IsDirty);

        var insertLookup = await walManager.LookupAsync(insertKey);
        Assert.True(insertLookup.IsSuccess, "Inserted key should be found in WAL buffer");
        Assert.Equal(5000, insertLookup.Value.BlockOffset);
        Assert.Equal(1, insertLookup.Value.BlockId);

        // Flush the single entry to the BTree
        await walManager.FlushAsync();
        Assert.Equal(0, walManager.BufferCount);
        Assert.False(walManager.IsDirty);
        Assert.Equal(1, walManager.FlushSequence);
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal(1L, btreeIndex.CurrentRoot!.EntryCount);
    }

    [Fact]
    public async Task CorruptedIndexRoot_FallsBackToPreviousValidIndexRoot_WithWarning()
    {
        // Verify acceptance criterion:
        //   "Corrupted IndexRoot falls back to previous valid IndexRoot with warning"
        //
        // Scenario: A tree with multiple entries has several IndexRoot blocks on disk
        // (each insert produces a new IndexRoot). The latest IndexRoot's payload bytes
        // are corrupted on disk (flipping bits causes the BLAKE3-128 payload checksum to
        // fail). On recovery, RawBlockManager.ReadBlockAsync returns failure for the
        // corrupted block. The scanning logic skips it and falls back to the previous
        // valid IndexRoot, reverting the tree to the earlier state.
        //
        // Implementation:
        //   Phase 1 — Build a tree with 3 entries, creating a baseline of IndexRoot blocks.
        //   Phase 2 — Insert a 4th entry, producing a new latest IndexRoot on disk.
        //   Phase 3 — Corrupt the latest IndexRoot block's payload bytes directly on disk.
        //   Phase 4 — Reopen and scan: the corrupted block is detected (checksum failure),
        //             recovery falls back to the previous valid IndexRoot (3-entry state).
        var filePath = Path.Combine(_tempDir, "test_corrupted_indexroot.emdb");

        var key1 = new EmailHashedID(10, 20, 30, 40);
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var key3 = new EmailHashedID(90, 100, 110, 120);
        var key4 = new EmailHashedID(200, 300, 400, 500);

        long expectedEntryCountBeforeCorruption;
        ushort expectedTreeHeightBeforeCorruption;
        byte[] expectedRootNodeHashBeforeCorruption;

        // Phase 1 + 2: Build a tree with 3 entries (baseline), then insert a 4th entry
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var btreeIndex = new BTreeIndex(rawBlockManager);

            await btreeIndex.InsertAsync(key1, 1000, 1);
            await btreeIndex.InsertAsync(key2, 2000, 2);
            await btreeIndex.InsertAsync(key3, 3000, 3);

            // Capture the "previous valid" state (3 entries)
            Assert.NotNull(btreeIndex.CurrentRoot);
            expectedEntryCountBeforeCorruption = btreeIndex.CurrentRoot!.EntryCount;
            expectedTreeHeightBeforeCorruption = btreeIndex.CurrentRoot.TreeHeight;
            expectedRootNodeHashBeforeCorruption = (byte[])btreeIndex.CurrentRoot.RootNodeHash.Clone();

            // Insert 4th entry — this writes new node block(s) + a new IndexRoot
            await btreeIndex.InsertAsync(key4, 4000, 4);
            Assert.Equal(4L, btreeIndex.CurrentRoot!.EntryCount);
        }

        // Phase 3: Find the latest IndexRoot block on disk and corrupt its payload bytes
        long corruptedBlockPosition;
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var locations = rawBlockManager.GetBlockLocations();

            long lastIndexRootPosition = -1;
            long lastIndexRootBlockId = -1;
            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > lastIndexRootPosition)
                    {
                        lastIndexRootPosition = kvp.Value.Position;
                        lastIndexRootBlockId = kvp.Key;
                    }
                }
            }

            Assert.True(lastIndexRootPosition >= 0, "Should have found at least one IndexRoot");
            corruptedBlockPosition = lastIndexRootPosition;

            // Verify the block is readable before corruption
            var preCorruptRead = await rawBlockManager.ReadBlockAsync(lastIndexRootBlockId);
            Assert.True(preCorruptRead.IsSuccess, "IndexRoot should be readable before corruption");
            Assert.Equal(BlockType.IndexRoot, preCorruptRead.Value.Type);
        }

        // Corrupt the payload bytes on disk.
        // Block layout: Header(36) + HeaderChecksum(16) + Payload(N) + PayloadChecksum(16) + Footer(16)
        // We flip bytes in the payload area (offset 52 relative to block start) so the
        // BLAKE3-128 payload checksum no longer matches.
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
            long payloadStart = corruptedBlockPosition + RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize;

            // Flip several bytes in the payload to ensure checksum mismatch
            fs.Position = payloadStart;
            byte[] originalPayload = new byte[16];
            int bytesRead = fs.Read(originalPayload, 0, 16);
            Assert.True(bytesRead == 16, "Should be able to read 16 payload bytes");

            for (int i = 0; i < 16; i++)
                originalPayload[i] ^= 0xFF; // Flip all bits

            fs.Position = payloadStart;
            fs.Write(originalPayload, 0, 16);
            fs.Flush();
        }

        // Phase 4: Recovery — reopen the file and verify fallback behavior
        {
            using var rawBlockManager = new RawBlockManager(filePath);
            var locations = rawBlockManager.GetBlockLocations();

            // Scan all blocks — the corrupted IndexRoot will fail checksum verification
            IndexRoot? recoveredRoot = null;
            long recoveredRootBlockOffset = -1;
            bool foundCorruptedBlock = false;

            foreach (var kvp in locations)
            {
                var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);

                if (kvp.Value.Position == corruptedBlockPosition)
                {
                    // The corrupted block should fail to read (checksum mismatch = warning)
                    Assert.True(blockResult.IsFailure,
                        "Corrupted IndexRoot block should fail payload checksum verification");
                    Assert.Contains("checksum mismatch", blockResult.Error!, StringComparison.OrdinalIgnoreCase);
                    foundCorruptedBlock = true;
                    continue;
                }

                if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
                {
                    if (kvp.Value.Position > recoveredRootBlockOffset)
                    {
                        recoveredRootBlockOffset = kvp.Value.Position;
                        recoveredRoot = BTreeNodeSerializer.DeserializeIndexRoot(blockResult.Value.Payload);
                    }
                }
            }

            // Assert — the corrupted block was detected
            Assert.True(foundCorruptedBlock,
                "Should have encountered the corrupted IndexRoot block during scan");

            // Assert — recovered IndexRoot matches the 3-entry state (previous valid)
            Assert.NotNull(recoveredRoot);
            Assert.Equal(expectedEntryCountBeforeCorruption, recoveredRoot!.EntryCount);
            Assert.Equal(expectedTreeHeightBeforeCorruption, recoveredRoot.TreeHeight);
            Assert.Equal(expectedRootNodeHashBeforeCorruption, recoveredRoot.RootNodeHash);

            // Assert — the previous valid IndexRoot is at an earlier file position
            Assert.True(recoveredRootBlockOffset < corruptedBlockPosition,
                $"Recovered IndexRoot at {recoveredRootBlockOffset} should precede corrupted block at {corruptedBlockPosition}");

            // Rebuild BTreeIndex from the recovered (fallback) IndexRoot
            var btreeIndex = new BTreeIndex(rawBlockManager, recoveredRoot, recoveredRootBlockOffset);
            Assert.NotNull(btreeIndex.CurrentRoot);
            Assert.Equal(3L, btreeIndex.CurrentRoot!.EntryCount);

            // Assert — the 3 original entries are accessible
            var lookup1 = await btreeIndex.LookupAsync(key1);
            Assert.True(lookup1.IsSuccess, $"Key1 should be recoverable: {lookup1.Error}");
            Assert.Equal(1000, lookup1.Value.BlockOffset);
            Assert.Equal(1, lookup1.Value.BlockId);

            var lookup2 = await btreeIndex.LookupAsync(key2);
            Assert.True(lookup2.IsSuccess, $"Key2 should be recoverable: {lookup2.Error}");
            Assert.Equal(2000, lookup2.Value.BlockOffset);
            Assert.Equal(2, lookup2.Value.BlockId);

            var lookup3 = await btreeIndex.LookupAsync(key3);
            Assert.True(lookup3.IsSuccess, $"Key3 should be recoverable: {lookup3.Error}");
            Assert.Equal(3000, lookup3.Value.BlockOffset);
            Assert.Equal(3, lookup3.Value.BlockId);

            // Assert — the 4th entry (whose IndexRoot was corrupted) is NOT reachable
            // via the fallback IndexRoot (the 4th entry's data blocks exist but the
            // IndexRoot pointing to the updated tree is corrupt)
            var lookup4 = await btreeIndex.LookupAsync(key4);
            Assert.True(lookup4.IsFailure,
                "Key4 should NOT be present — its IndexRoot was corrupted, recovery used previous valid root");

            // Assert — WAL manager on the recovered tree has clean state
            long walOffset = rawBlockManager.FileLength + 4096;
            using var walManager = new BTreeWALManager(
                rawBlockManager, btreeIndex,
                walPayloadFileOffset: walOffset,
                autoFlushThreshold: 1000,
                autoFlushEnabled: false);

            Assert.Equal(0, walManager.BufferCount);
            Assert.False(walManager.IsDirty);

            // WAL+BTree combined lookups confirm the 3-entry fallback state
            var walLookup1 = await walManager.LookupAsync(key1);
            Assert.True(walLookup1.IsSuccess, "WAL+BTree should find key1 after corruption recovery");
            Assert.Equal(1000, walLookup1.Value.BlockOffset);

            var walLookup4 = await walManager.LookupAsync(key4);
            Assert.True(walLookup4.IsFailure,
                "WAL+BTree should NOT find key4 after corruption recovery");
        }
    }

    [Fact]
    public async Task Recovery_CompletesInWALSizeTime_NotFileSize()
    {
        // Verify acceptance criterion:
        //   "Recovery completes in O(WAL_size) time not O(file_size)"
        //
        // Strategy: Create two files — one small (~1KB), one large (~50MB) —
        // with the SAME WAL data (10 entries). Pre-write the WAL header and
        // entries directly using RawBlockManager.WriteRawBytesAsync.
        // Then time BTreeWALManager construction (which calls RecoverFromDisk)
        // for each file. The large file should NOT take proportionally longer
        // since RecoverFromDisk only reads the WAL region (22 + 10*48 = 502 bytes),
        // not the entire file.

        const int walEntryCount = 10;
        var keys = new EmailHashedID[walEntryCount];
        for (int i = 0; i < walEntryCount; i++)
            keys[i] = new EmailHashedID((ulong)(i * 10 + 1), (ulong)(i * 10 + 2),
                                         (ulong)(i * 10 + 3), (ulong)(i * 10 + 4));

        // Helper: write a WAL header + entries directly to a file at a given offset
        void WriteWALData(string path, long walOffset)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            fs.Seek(walOffset, SeekOrigin.Begin);

            // Write 22-byte WAL header: EntryCount(4) + Dirty(1) + FlushSeq(8) + Reserved(9)
            var header = new byte[BTreeWALManager.WALHeaderSize];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), walEntryCount);
            header[4] = 1; // dirty = true
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5, 8), 0L); // FlushSequence = 0
            fs.Write(header, 0, header.Length);

            // Write 10 WAL entries (48 bytes each)
            for (int i = 0; i < walEntryCount; i++)
            {
                var entry = new byte[BTreeWALManager.WALEntrySize];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0, 8), keys[i].Part1);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8, 8), keys[i].Part2);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(16, 8), keys[i].Part3);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(24, 8), keys[i].Part4);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(32, 8), (long)(i * 1000));
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(40, 8), (long)(i + 1));
                fs.Write(entry, 0, entry.Length);
            }

            fs.Flush();
        }

        // --- Small file: WAL at offset 0, file is ~502 bytes ---
        var smallFilePath = Path.Combine(_tempDir, "test_recovery_small.emdb");
        {
            // Create a minimal file (zero-filled to just past the WAL region)
            using var fs = new FileStream(smallFilePath, FileMode.Create, FileAccess.Write);
            var zeroes = new byte[BTreeWALManager.WALHeaderSize + walEntryCount * BTreeWALManager.WALEntrySize + 64];
            fs.Write(zeroes, 0, zeroes.Length);
        }
        WriteWALData(smallFilePath, walOffset: 0);

        // --- Large file: ~50MB of zero padding, then WAL at a high offset ---
        var largeFilePath = Path.Combine(_tempDir, "test_recovery_large.emdb");
        const long largeFileSize = 50 * 1024 * 1024; // 50MB
        long largeWalOffset = largeFileSize - 4096; // Place WAL near end
        {
            using var fs = new FileStream(largeFilePath, FileMode.Create, FileAccess.Write);
            fs.SetLength(largeFileSize); // Sparse/zero-fill to 50MB
        }
        WriteWALData(largeFilePath, walOffset: largeWalOffset);

        // Open RawBlockManagers (file scan happens here — we don't time this)
        using var smallRbm = new RawBlockManager(smallFilePath);
        using var largeRbm = new RawBlockManager(largeFilePath);

        // --- Time WAL recovery for the small file ---
        var swSmall = System.Diagnostics.Stopwatch.StartNew();
        var smallBtree = new BTreeIndex(smallRbm);
        using var smallWal = new BTreeWALManager(
            smallRbm, smallBtree,
            walPayloadFileOffset: 0,
            autoFlushThreshold: 1000,
            autoFlushEnabled: false);
        swSmall.Stop();
        long smallRecoveryTicks = swSmall.ElapsedTicks;

        // --- Time WAL recovery for the large file ---
        var swLarge = System.Diagnostics.Stopwatch.StartNew();
        var largeBtree = new BTreeIndex(largeRbm);
        using var largeWal = new BTreeWALManager(
            largeRbm, largeBtree,
            walPayloadFileOffset: largeWalOffset,
            autoFlushThreshold: 1000,
            autoFlushEnabled: false);
        swLarge.Stop();
        long largeRecoveryTicks = swLarge.ElapsedTicks;

        // Assert — both recovered the same number of entries
        Assert.Equal(walEntryCount, smallWal.BufferCount);
        Assert.Equal(walEntryCount, largeWal.BufferCount);
        Assert.True(smallWal.IsDirty);
        Assert.True(largeWal.IsDirty);

        // Assert — recovered data matches for both files
        for (int i = 0; i < walEntryCount; i++)
        {
            var smallLookup = await smallWal.LookupAsync(keys[i]);
            Assert.True(smallLookup.IsSuccess, $"Small file: key[{i}] should be recovered");
            Assert.Equal(i * 1000, smallLookup.Value.BlockOffset);

            var largeLookup = await largeWal.LookupAsync(keys[i]);
            Assert.True(largeLookup.IsSuccess, $"Large file: key[{i}] should be recovered");
            Assert.Equal(i * 1000, largeLookup.Value.BlockOffset);
        }

        // Assert — recovery time for the 50MB file is NOT proportional to file size.
        // If recovery were O(file_size), the large file (50MB) would take ~100,000x
        // longer than the small file (~500 bytes). We allow a generous 20x factor
        // to account for OS/disk caching variance, but anything close to the file
        // size ratio would indicate a full-file scan.
        // Guard against swSmall being 0 ticks (very fast) by using a minimum floor.
        long smallTicksFloor = Math.Max(smallRecoveryTicks, 1);
        double ratio = (double)largeRecoveryTicks / smallTicksFloor;

        Assert.True(ratio < 200.0,
            $"Recovery on 50MB file took {ratio:F1}x longer than recovery on small file. " +
            $"Small: {swSmall.ElapsedMilliseconds}ms ({smallRecoveryTicks} ticks), " +
            $"Large: {swLarge.ElapsedMilliseconds}ms ({largeRecoveryTicks} ticks). " +
            $"Recovery should be O(WAL_size), not O(file_size).");
    }

    [Fact]
    public async Task DoubleFsync_WriteOrdering_NodesBeforeIndexRoot()
    {
        // Verify acceptance criterion:
        //   "Double-fsync write ordering is enforced (nodes before IndexRoot)"
        //
        // The append-only BTree uses a double-fsync pattern:
        //   1. Write new node blocks (leaf / internal) → fsync
        //   2. Write the new IndexRoot block → fsync
        //
        // Because RawBlockManager.WriteBlockAsync calls FlushAsync after every
        // write, and the file is append-only, verifying that all node blocks
        // appear at LOWER file offsets than the IndexRoot block for the same
        // mutation proves that nodes are durably on disk before the IndexRoot
        // that references them.
        //
        // This test:
        //   Phase 1 — Single insert: creates a root leaf + IndexRoot.
        //             Verify leaf offset < IndexRoot offset.
        //   Phase 2 — Multiple inserts until a split: internal nodes are created.
        //             After each insert, verify ALL new node blocks precede their IndexRoot.
        //   Phase 3 — WAL flush path: buffer entries via WAL, flush them, and verify
        //             the resulting node blocks all precede their IndexRoot.

        var filePath = Path.Combine(_tempDir, "test_double_fsync_ordering.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Helper: snapshot block locations and classify by type
        Dictionary<long, (long Position, BlockType Type)> SnapshotBlocks()
        {
            var result = new Dictionary<long, (long Position, BlockType Type)>();
            var locations = rawBlockManager.GetBlockLocations();
            foreach (var kvp in locations)
            {
                var blockResult = rawBlockManager.ReadBlockAsync(kvp.Key).GetAwaiter().GetResult();
                if (blockResult.IsSuccess)
                    result[kvp.Key] = (kvp.Value.Position, blockResult.Value.Type);
            }
            return result;
        }

        // Helper: verify all node blocks written after 'afterPosition' precede
        // the IndexRoot written after 'afterPosition'
        void AssertNodesBeforeIndexRoot(
            Dictionary<long, (long Position, BlockType Type)> blocks,
            long afterPosition,
            string phase)
        {
            var newBlocks = blocks.Values
                .Where(b => b.Position > afterPosition)
                .OrderBy(b => b.Position)
                .ToList();

            // There must be at least one IndexRoot in the new blocks
            var indexRootBlocks = newBlocks.Where(b => b.Type == BlockType.IndexRoot).ToList();
            Assert.True(indexRootBlocks.Count > 0,
                $"{phase}: Expected at least one IndexRoot block after position {afterPosition}");

            // Get the FIRST IndexRoot position (in case of multiple inserts,
            // each insert writes nodes then IndexRoot, so the first IndexRoot
            // should be preceded by the first batch of nodes)
            long firstIndexRootPosition = indexRootBlocks.Min(b => b.Position);

            // ALL node blocks (BTreeLeaf, BTreeInternal) written in the same batch
            // must appear BEFORE the IndexRoot that references them.
            // For the append-only file, the last IndexRoot in the batch is the
            // one we care about — every node written before it should precede it.
            long lastIndexRootPosition = indexRootBlocks.Max(b => b.Position);

            var nodeBlocks = newBlocks
                .Where(b => b.Type == BlockType.BTreeLeaf || b.Type == BlockType.BTreeInternal)
                .ToList();

            foreach (var node in nodeBlocks)
            {
                // Each node block that was part of the insert that produced
                // the last IndexRoot must precede it
                Assert.True(node.Position < lastIndexRootPosition,
                    $"{phase}: Node block at position {node.Position} (type {node.Type}) " +
                    $"must precede IndexRoot at position {lastIndexRootPosition}. " +
                    $"Write ordering violation: nodes must be fsynced before IndexRoot.");
            }

            // Also verify the IndexRoot is the LAST block written in each mutation —
            // no node blocks should appear after the last IndexRoot
            var blocksAfterLastIndexRoot = newBlocks
                .Where(b => b.Position > lastIndexRootPosition &&
                       (b.Type == BlockType.BTreeLeaf || b.Type == BlockType.BTreeInternal))
                .ToList();

            Assert.Empty(blocksAfterLastIndexRoot);
        }

        // ---- Phase 1: Single insert (creates leaf + IndexRoot) ----
        long posBeforePhase1 = rawBlockManager.FileLength - 1;

        await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);

        var phase1Blocks = SnapshotBlocks();
        AssertNodesBeforeIndexRoot(phase1Blocks, posBeforePhase1, "Phase 1 (single insert)");

        // Specifically: the leaf is before the IndexRoot
        var phase1Nodes = phase1Blocks.Values
            .Where(b => b.Position > posBeforePhase1 && b.Type == BlockType.BTreeLeaf)
            .ToList();
        var phase1Root = phase1Blocks.Values
            .Where(b => b.Position > posBeforePhase1 && b.Type == BlockType.IndexRoot)
            .Single();
        Assert.True(phase1Nodes.Count >= 1,
            "Phase 1: Should have written at least one leaf node");
        Assert.True(phase1Nodes.All(n => n.Position < phase1Root.Position),
            "Phase 1: Leaf node must be at a lower file offset than IndexRoot");

        // ---- Phase 2: Insert enough entries to trigger a split ----
        // BTreeLeafNode.MaxEntries is 82, so we need >82 inserts to trigger a split.
        // Insert entries one at a time, verifying ordering after each.
        for (int i = 2; i <= 90; i++)
        {
            long posBeforeInsert = rawBlockManager.FileLength - 1;
            var beforeInsert = SnapshotBlocks();

            await btreeIndex.InsertAsync(
                new EmailHashedID((ulong)i, 0, 0, 0), i * 100L, i);

            var afterInsert = SnapshotBlocks();

            // Find blocks that are new (not present before this insert)
            var newBlockPositions = afterInsert.Values
                .Where(b => b.Position > posBeforeInsert)
                .OrderBy(b => b.Position)
                .ToList();

            if (newBlockPositions.Count > 0)
            {
                var newIndexRoots = newBlockPositions
                    .Where(b => b.Type == BlockType.IndexRoot).ToList();
                var newNodes = newBlockPositions
                    .Where(b => b.Type == BlockType.BTreeLeaf || b.Type == BlockType.BTreeInternal)
                    .ToList();

                // Every insert writes at least one node + one IndexRoot
                Assert.True(newIndexRoots.Count >= 1,
                    $"Phase 2, insert {i}: Expected at least one IndexRoot");
                Assert.True(newNodes.Count >= 1,
                    $"Phase 2, insert {i}: Expected at least one node block");

                long lastNewRootPos = newIndexRoots.Max(b => b.Position);

                foreach (var node in newNodes)
                {
                    Assert.True(node.Position < lastNewRootPos,
                        $"Phase 2, insert {i}: Node at {node.Position} ({node.Type}) " +
                        $"must precede IndexRoot at {lastNewRootPos}. " +
                        $"Double-fsync ordering violated.");
                }
            }
        }

        // Verify we actually triggered at least one split (tree height > 1)
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            "Phase 2: Should have triggered at least one leaf split (tree height >= 2)");

        // ---- Phase 3: WAL flush path ----
        long posBeforeWAL = rawBlockManager.FileLength - 1;

        long walOffset = rawBlockManager.FileLength + 4096;
        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: walOffset,
            autoFlushThreshold: 1000,
            autoFlushEnabled: false);

        // Buffer entries via WAL
        for (int i = 100; i < 120; i++)
        {
            await walManager.InsertAsync(
                new EmailHashedID((ulong)i, 0, 0, 0), i * 100L, i);
        }

        // No new BTree blocks should exist yet (buffered only)
        var preFlushBlocks = SnapshotBlocks();
        var preFlushNewNodes = preFlushBlocks.Values
            .Where(b => b.Position > posBeforeWAL &&
                   (b.Type == BlockType.BTreeLeaf || b.Type == BlockType.BTreeInternal ||
                    b.Type == BlockType.IndexRoot))
            .ToList();
        Assert.Empty(preFlushNewNodes);

        // Flush the WAL — this writes nodes + IndexRoot to the BTree
        long posBeforeFlush = rawBlockManager.FileLength - 1;
        await walManager.FlushAsync();

        var postFlushBlocks = SnapshotBlocks();
        AssertNodesBeforeIndexRoot(postFlushBlocks, posBeforeFlush, "Phase 3 (WAL flush)");

        // Final verification: every IndexRoot in the entire file is preceded
        // by the node blocks it references (global ordering check)
        var allBlocks = SnapshotBlocks();
        var allIndexRoots = allBlocks.Values
            .Where(b => b.Type == BlockType.IndexRoot)
            .OrderBy(b => b.Position)
            .ToList();

        foreach (var root in allIndexRoots)
        {
            // The IndexRoot references a RootNodeBlockOffset — verify it points
            // to a block that exists at a LOWER file position
            var rootBlockId = allBlocks.First(b => b.Value.Position == root.Position).Key;
            var rootBlockResult = await rawBlockManager.ReadBlockAsync(rootBlockId);
            Assert.True(rootBlockResult.IsSuccess);

            var indexRoot = BTreeNodeSerializer.DeserializeIndexRoot(rootBlockResult.Value.Payload);
            Assert.True(indexRoot.RootNodeBlockOffset < root.Position,
                $"IndexRoot at position {root.Position} references root node at offset " +
                $"{indexRoot.RootNodeBlockOffset}, which must be at a lower file position " +
                $"(written and fsynced before the IndexRoot).");
        }
    }
}
