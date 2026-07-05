using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="LocationIndexCheckpoint"/> — the checkpoint-time batch
/// insert for the BlockLocationIndex (US-EMDB-71-7, EmailDB_FileFormat_Spec.md
/// Section 7, docs/BTree_Index.md Section 3): collect the blocks appended since
/// the last Checkpoint (the runtime map), sort by BlockId, batch-insert into the
/// offset-addressed location index in ONE COW pass, fsync the new nodes, and emit
/// a fresh <see cref="LocationIndexRoot"/> (Sequence + 1) for the Checkpoint block
/// to reference. The node store is built WITHOUT an <see cref="IBlockIdResolver"/>
/// so every read resolves purely by ChildOffset — the index cannot depend on
/// itself (spec Section 7).
/// </summary>
public class LocationIndexCheckpointTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-loc-checkpoint-{Guid.NewGuid():N}.emdb");

    // The BlockManager's own append bookkeeping. It records EVERY appended block —
    // including the location index's own (offset-addressed) node blocks — so it is
    // deliberately kept separate from the "blocks appended since last Checkpoint"
    // runtime map a test feeds to the checkpointer, which models only DATA blocks.
    private readonly RuntimeBlockOffsetMap _nodeMap = new();
    private readonly FaultInjectingFileStream _stream;
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public LocationIndexCheckpointTests()
    {
        _stream = new FaultInjectingFileStream(_path);
        _manager = new BlockManager(_stream, offsetMap: _nodeMap, firstBlockOffset: 0, ownsStream: true);
        // No IBlockIdResolver: an offset-addressed tree must resolve every node
        // purely by ChildOffset — it is the offset map and cannot depend on one.
        _store = new BTreeNodeStore(_manager, blockIdResolver: null);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ------------------------------------------------------------- Helpers

    /// <summary>Tiny fan-out so a handful of blocks forces internal levels.</summary>
    private BlockLocationIndex CreateIndex(int maxLeafEntries = 4, int maxInternalKeys = 3) =>
        new(_store, maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

    private LocationIndexCheckpoint CreateCheckpoint(BlockLocationIndex index, LocationIndexRoot? initialRoot = null) =>
        new(index, _manager.Flush, initialRoot);

    /// <summary>
    /// Runs <paramref name="mutate"/> against a fresh, isolated location index over
    /// its own block stream and returns how many node blocks it appended. Every
    /// <see cref="BlockManager.Append"/> mints a unique BlockId and notifies the
    /// offset map, and the store only ever appends B+-tree nodes, so the map's
    /// entry count equals the number of node blocks written — the write-amplification
    /// signal that separates one batch COW pass from N individual inserts.
    /// </summary>
    private static long CountNodeWrites(Action<BlockLocationIndex> mutate, int maxLeafEntries = 4, int maxInternalKeys = 3)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-loc-cow-{Guid.NewGuid():N}.emdb");
        var nodeMap = new RuntimeBlockOffsetMap();
        var stream = new FaultInjectingFileStream(path);
        var manager = new BlockManager(stream, offsetMap: nodeMap, firstBlockOffset: 0, ownsStream: true);
        try
        {
            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(store, maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);
            mutate(index);
            return nodeMap.Count;
        }
        finally
        {
            manager.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>A 16-byte BlockId whose lexicographic order equals the numeric order of <paramref name="i"/>.</summary>
    private static byte[] BlockId(int i)
    {
        var id = new byte[BlockLocationIndex.BlockIdKeySize];
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), i);
        return id;
    }

    private static BlockLocation Loc(int i, long offset, long length) =>
        new() { BlockId = BlockId(i), Offset = offset, TotalBlockLength = length };

    private static BlockHeader Header(byte[] blockId) => new()
    {
        Type = BlockType.EmailContent,
        Encoding = PayloadEncoding.RawBytes,
        BlockId = blockId,
        PayloadLength = 10,
    };

    private static void Ok(Result result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
    private static void Ok<T>(Result<T> result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    private static void AssertLocation(BlockLocationIndex index, int i, long offset, long length)
    {
        var lookup = index.Lookup(BlockId(i));
        Ok(lookup);
        Assert.True(lookup.Value.Found, $"Block {i} should be present.");
        Assert.Equal(offset, lookup.Value.Offset);
        Assert.Equal(length, lookup.Value.Length);
    }

    // --------------------------------------------------------------- Tests

    [Fact]
    public void Checkpoint_batch_inserts_all_entries_and_emits_matching_root()
    {
        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        var batch = new List<BlockLocation>();
        for (int i = 0; i < 30; i++)
            batch.Add(Loc(i, offset: 4096L * (i + 1), length: 128 + i));

        var result = checkpointer.Checkpoint(batch);
        Ok(result);

        // Every block is now resolvable in the durable index...
        Assert.Equal(30, index.Count);
        for (int i = 0; i < 30; i++)
            AssertLocation(index, i, 4096L * (i + 1), 128 + i);

        // ...and the emitted location root describes exactly that tree version:
        // the offset-addressed root pointer, shape, hash, and the initial Sequence.
        var emitted = result.Value!;
        Assert.NotNull(index.Root);
        Assert.True(index.Root!.Height > 1, "Expected a multi-level tree.");
        long expectedOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root.RootRef.Reference);
        Assert.Equal(expectedOffset, emitted.RootOffset);
        Assert.Equal(index.Root.EntryCount, emitted.EntryCount);
        Assert.Equal(index.Root.Height, emitted.TreeHeight);
        Assert.Equal(index.Root.RootHash, emitted.RootHash);
        Assert.Equal(LocationIndexRoot.InitialSequence, emitted.Sequence);
        Assert.Same(emitted, checkpointer.CommittedRoot);
        Assert.Equal(LocationIndexRoot.InitialSequence, checkpointer.Sequence);
    }

    [Fact]
    public void Checkpoint_collects_entries_from_the_runtime_map()
    {
        // Populate a runtime map exactly as the append path would, then check that
        // a checkpoint folds every data block appended this session into the index.
        var pending = new RuntimeBlockOffsetMap();
        const int n = 40;
        for (int i = 0; i < n; i++)
            pending.OnBlockAppended(Header(BlockId(i)), offset: 8192L * (i + 1), totalBlockLength: 256);
        Assert.Equal(n, pending.Count);

        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        var result = checkpointer.Checkpoint(pending);
        Ok(result);
        Assert.NotNull(result.Value);
        Assert.Equal(n, index.Count);
        for (int i = 0; i < n; i++)
            AssertLocation(index, i, 8192L * (i + 1), 256);

        // The checkpoint does NOT clear the runtime map — that happens only once
        // the Checkpoint block is durable (the Checkpoint layer's job, US-EMDB-72).
        Assert.Equal(n, pending.Count);
    }

    [Fact]
    public void Checkpoint_sorts_by_blockid_even_when_runtime_order_is_by_offset()
    {
        // The runtime map snapshot is ordered by file offset; here offset order is
        // the REVERSE of BlockId order. The batch insert must still land every
        // entry at its BlockId key, so all lookups resolve and a scan is ordered.
        const int n = 25;
        var pending = new RuntimeBlockOffsetMap();
        for (int i = 0; i < n; i++)
            pending.OnBlockAppended(Header(BlockId(i)), offset: 8192L * (n - i), totalBlockLength: 200);

        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);
        Ok(checkpointer.Checkpoint(pending));

        Assert.Equal(n, index.Count);
        for (int i = 0; i < n; i++)
            AssertLocation(index, i, 8192L * (n - i), 200);

        // A range scan of the location tree yields ascending BlockId order.
        var scan = index.Tree.Scan(index.Root);
        var keys = new List<int>();
        while (true)
        {
            var step = scan.MoveNext();
            Ok(step);
            if (!step.Value)
                break;
            keys.Add(BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(12)));
        }
        Assert.Equal(Enumerable.Range(0, n).ToList(), keys);
    }

    [Fact]
    public void Successive_checkpoints_increment_sequence_and_accumulate_entries()
    {
        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        // First checkpoint: blocks 0..9, Sequence 0.
        var first = new List<BlockLocation>();
        for (int i = 0; i < 10; i++)
            first.Add(Loc(i, offset: 1000L + i, length: 64));
        var r1 = checkpointer.Checkpoint(first);
        Ok(r1);
        Assert.Equal(LocationIndexRoot.InitialSequence, r1.Value!.Sequence);
        Assert.Equal(10, index.Count);

        // Second checkpoint: only the NEW blocks 10..19 (the runtime map is cleared
        // between checkpoints in real usage); Sequence bumps to 1 and the earlier
        // entries persist in the COW tree.
        var second = new List<BlockLocation>();
        for (int i = 10; i < 20; i++)
            second.Add(Loc(i, offset: 2000L + i, length: 64));
        var r2 = checkpointer.Checkpoint(second);
        Ok(r2);
        Assert.Equal((ulong)1, r2.Value!.Sequence);
        Assert.Equal((ulong)1, checkpointer.Sequence);
        Assert.Equal(20, index.Count);
        for (int i = 0; i < 20; i++)
            AssertLocation(index, i, (i < 10 ? 1000L : 2000L) + i, 64);
    }

    [Fact]
    public void Second_checkpoint_batch_inserts_only_the_blocks_since_the_last_checkpoint()
    {
        // The full delta cycle the criterion names: populate the runtime map, take a
        // Checkpoint, clear the map once the Checkpoint block is durable, append the
        // NEXT session's blocks, and Checkpoint again. Only the delta may be folded
        // in — the pre-checkpoint entries must not be re-inserted.
        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        var pending = new RuntimeBlockOffsetMap();
        for (int i = 0; i < 10; i++)
            pending.OnBlockAppended(Header(BlockId(i)), offset: 8192L * (i + 1), totalBlockLength: 256);
        var r1 = checkpointer.Checkpoint(pending);
        Ok(r1);
        Assert.Equal(LocationIndexRoot.InitialSequence, r1.Value!.Sequence);
        Assert.Equal(10, index.Count);

        // Hold the first durable version to prove COW leaves it untouched, and record
        // how many node blocks the whole store has written so far.
        var rootAfterFirst = index.Root!;
        long writesAfterFirst = _nodeMap.Count;

        // Checkpoint block durable -> the runtime map is cleared, so ONLY the delta
        // (blocks 10..19) is what the next Checkpoint sees.
        pending.Clear();
        for (int i = 10; i < 20; i++)
            pending.OnBlockAppended(Header(BlockId(i)), offset: 8192L * (i + 1), totalBlockLength: 256);
        Assert.Equal(10, pending.Count);

        var r2 = checkpointer.Checkpoint(pending);
        Ok(r2);
        Assert.Equal((ulong)1, r2.Value!.Sequence);
        long deltaWrites = _nodeMap.Count - writesAfterFirst;

        // All twenty blocks resolve through the accumulated COW index.
        Assert.Equal(20, index.Count);
        for (int i = 0; i < 20; i++)
            AssertLocation(index, i, 8192L * (i + 1), 256);

        // The second Checkpoint folded in only the 10-block delta: it wrote strictly
        // fewer node blocks than building the full 20-entry tree from scratch. Had it
        // re-inserted the pre-checkpoint blocks 0..9, PutBatch would have rewritten
        // every leaf's root-to-leaf path, matching (or exceeding) a from-scratch build.
        long fullBuildWrites = CountNodeWrites(idx =>
        {
            var all = new List<BlockLocation>();
            for (int i = 0; i < 20; i++)
                all.Add(Loc(i, offset: 8192L * (i + 1), length: 256));
            Ok(idx.PutBatch(all));
        });
        Assert.True(deltaWrites > 0, "The delta checkpoint should have written the new nodes.");
        Assert.True(deltaWrites < fullBuildWrites,
            $"Delta checkpoint wrote {deltaWrites} node blocks; a full 20-entry rebuild writes {fullBuildWrites}. " +
            "A larger count would mean the pre-checkpoint entries were re-inserted.");

        // The first durable version is an untouched COW snapshot: it still resolves
        // exactly blocks 0..9 and none of the delta — mutation never happened in place.
        for (int i = 0; i < 10; i++)
        {
            var hit = index.Tree.TryGet(rootAfterFirst, BlockId(i));
            Ok(hit);
            Assert.True(hit.Value.Found, $"Old snapshot should still see block {i}.");
        }
        var absent = index.Tree.TryGet(rootAfterFirst, BlockId(15));
        Ok(absent);
        Assert.False(absent.Value.Found, "Old snapshot must not see a delta block.");
    }

    // docs/BTree_Index.md Section 4 defines the checkpoint batch-insert as a single
    // amortized COW pass — "sort buffered entries by key -> apply to the tree in one
    // pass" so "a batch of 100 inserts touching ~10 leaves writes ~15 nodes instead of
    // 400". BlockLocationIndex.PutBatch delegates to CowBTree.InsertBatch, which descends
    // the tree once and rewrites each shared leaf and internal path a single time, so a
    // batch writes strictly — and substantially — fewer node blocks than N individual
    // inserts (US-EMDB-104).
    [Fact]
    public void Checkpoint_batch_insert_is_one_cow_pass_not_n_individual_inserts()
    {
        // "batch-insert ... in ONE COW pass": inserting the same sorted entries as a
        // single batch should rewrite each shared leaf once plus the internal spine,
        // whereas N individual upserts each rewrite a full root-to-leaf path. The batch
        // must therefore write strictly — and substantially — fewer node blocks.
        const int n = 30;
        var batch = new List<BlockLocation>();
        for (int i = 0; i < n; i++)
            batch.Add(Loc(i, offset: 4096L * (i + 1), length: 128 + i));

        long batchWrites = CountNodeWrites(idx => Ok(idx.PutBatch(batch)));
        long individualWrites = CountNodeWrites(idx =>
        {
            foreach (var location in batch)
                Ok(idx.Put(location));
        });

        Assert.True(batchWrites < individualWrites,
            $"Batch wrote {batchWrites} node blocks but {n} individual inserts wrote {individualWrites}; " +
            "the batch must amortize shared-leaf rewrites into one COW pass.");
        // Even stronger: the single pass writes fewer node blocks than there are
        // entries, which N individual path rewrites can never do.
        Assert.True(batchWrites < n,
            $"A single COW pass over {n} entries wrote {batchWrites} node blocks; expected fewer than {n}.");
    }

    [Fact]
    public void Checkpoint_with_no_pending_entries_is_a_noop()
    {
        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        // Empty on a brand-new index: no root emitted (index still empty), no bump.
        var empty = checkpointer.Checkpoint(new List<BlockLocation>());
        Ok(empty);
        Assert.Null(empty.Value);
        Assert.Null(checkpointer.CommittedRoot);
        Assert.Null(checkpointer.Sequence);

        // After a real checkpoint, an empty one re-returns the SAME root unchanged
        // (the Checkpoint keeps referencing the same location root; no Sequence bump).
        Ok(checkpointer.Checkpoint(new List<BlockLocation> { Loc(1, 100, 50) }));
        var committed = checkpointer.CommittedRoot;
        Assert.NotNull(committed);

        var noop = checkpointer.Checkpoint(new RuntimeBlockOffsetMap()); // nothing pending
        Ok(noop);
        Assert.Same(committed, noop.Value);
        Assert.Same(committed, checkpointer.CommittedRoot);
        Assert.Equal(LocationIndexRoot.InitialSequence, checkpointer.Sequence);
    }

    [Fact]
    public void FailedNodeWrite_LeavesPreviousRootAuthoritative()
    {
        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        // Commit a first checkpoint successfully.
        var first = new List<BlockLocation>();
        for (int i = 0; i < 6; i++)
            first.Add(Loc(i, offset: 1000L + i, length: 64));
        Ok(checkpointer.Checkpoint(first));
        var authoritative = checkpointer.CommittedRoot;
        var authoritativeIndexRoot = index.Root;

        // Poison the block stream so applying the next batch fails on a node append.
        var second = new List<BlockLocation>();
        for (int i = 6; i < 12; i++)
            second.Add(Loc(i, offset: 2000L + i, length: 64));
        _stream.FailWrites = true;

        var failed = checkpointer.Checkpoint(second);
        Assert.True(failed.IsFailure);
        Assert.Contains("authoritative", failed.Error);

        // The previous location root and index root are untouched and readable;
        // PutBatch left the index Root at its pre-batch version on the node failure.
        Assert.Same(authoritative, checkpointer.CommittedRoot);
        Assert.Same(authoritativeIndexRoot, index.Root);
        Assert.Equal(LocationIndexRoot.InitialSequence, checkpointer.Sequence);
        Assert.Equal(6, index.Count);
        for (int i = 0; i < 6; i++)
            AssertLocation(index, i, 1000L + i, 64);
    }

    [Fact]
    public void FailedFsync_LeavesPreviousLocationRootUnchanged()
    {
        // The write order is nodes -> fsync -> emit root. An fsync failure is FATAL
        // (spec Section 10.3), so no new location root is emitted and the previous
        // one stays authoritative (crash recovery reloads the durable root).
        var index = CreateIndex();
        var checkpointer = CreateCheckpoint(index);

        var first = new List<BlockLocation>();
        for (int i = 0; i < 6; i++)
            first.Add(Loc(i, offset: 1000L + i, length: 64));
        Ok(checkpointer.Checkpoint(first));
        var authoritative = checkpointer.CommittedRoot;

        // Let the node writes succeed but fail the fsync that must precede the root.
        var second = new List<BlockLocation>();
        for (int i = 6; i < 12; i++)
            second.Add(Loc(i, offset: 2000L + i, length: 64));
        _stream.FailNextFsyncs = 1;

        var failed = checkpointer.Checkpoint(second);
        Assert.True(failed.IsFailure);
        Assert.Contains("fsync", failed.Error);
        Assert.Contains("authoritative", failed.Error);

        // No new location root was emitted; the previous one is unchanged.
        Assert.Same(authoritative, checkpointer.CommittedRoot);
        Assert.Equal(LocationIndexRoot.InitialSequence, checkpointer.Sequence);
    }

    [Fact]
    public void Reopen_from_committed_root_continues_the_sequence()
    {
        // A checkpointer opened with the last Checkpoint's location root derives the
        // NEXT version from it, so Sequence keeps climbing across a reopen.
        var index = CreateIndex();
        var first = CreateCheckpoint(index);
        Ok(first.Checkpoint(new List<BlockLocation> { Loc(1, 100, 50), Loc(2, 200, 60) }));
        var committed = first.CommittedRoot!;
        Assert.Equal(LocationIndexRoot.InitialSequence, committed.Sequence);

        // "Reopen": a fresh checkpointer over the same index, seeded with the root.
        var reopened = CreateCheckpoint(index, committed);
        var next = reopened.Checkpoint(new List<BlockLocation> { Loc(3, 300, 70) });
        Ok(next);
        Assert.Equal((ulong)1, next.Value!.Sequence);
        Assert.Equal(3, index.Count);
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        var index = CreateIndex();
        Assert.Throws<ArgumentNullException>(() => new LocationIndexCheckpoint(null!, _manager.Flush));
        Assert.Throws<ArgumentNullException>(() => new LocationIndexCheckpoint(index, null!));
        var checkpointer = CreateCheckpoint(index);
        Assert.Throws<ArgumentNullException>(() => checkpointer.Checkpoint((RuntimeBlockOffsetMap)null!));
        Assert.Throws<ArgumentNullException>(() => checkpointer.Checkpoint((IReadOnlyList<BlockLocation>)null!));
    }

    // --------------------------------------------- LocationIndexRoot descriptor

    [Fact]
    public void LocationIndexRoot_CreateInitial_and_NextVersion_track_fields_and_sequence()
    {
        var hashA = new byte[IndexRootSerializer.RootHashSize];
        Array.Fill(hashA, (byte)0xAB);
        var initial = LocationIndexRoot.CreateInitial(rootOffset: 4096, entryCount: 5, treeHeight: 2, rootHash: hashA);
        Assert.Equal(4096, initial.RootOffset);
        Assert.Equal(5, initial.EntryCount);
        Assert.Equal(2, initial.TreeHeight);
        Assert.Equal(hashA, initial.RootHash);
        Assert.Equal(LocationIndexRoot.InitialSequence, initial.Sequence);

        var hashB = new byte[IndexRootSerializer.RootHashSize];
        Array.Fill(hashB, (byte)0xCD);
        var next = initial.NextVersion(rootOffset: 8192, entryCount: 9, treeHeight: 3, rootHash: hashB);
        Assert.Equal((ulong)1, next.Sequence);
        Assert.Equal(8192, next.RootOffset);
        Assert.Equal(9, next.EntryCount);
        Assert.Equal(3, next.TreeHeight);
        Assert.Equal(hashB, next.RootHash);
        // The original version is an untouched snapshot.
        Assert.Equal(LocationIndexRoot.InitialSequence, initial.Sequence);
    }

    [Fact]
    public void LocationIndexRoot_rejects_malformed_fields()
    {
        var hash = new byte[IndexRootSerializer.RootHashSize];
        Assert.Throws<ArgumentOutOfRangeException>(() => LocationIndexRoot.CreateInitial(-1, 0, 1, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocationIndexRoot.CreateInitial(0, -1, 1, hash));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocationIndexRoot.CreateInitial(0, 0, 0, hash));
        Assert.Throws<ArgumentException>(() => LocationIndexRoot.CreateInitial(0, 0, 1, new byte[31]));
    }
}
