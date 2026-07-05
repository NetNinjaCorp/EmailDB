using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="CheckpointWriter"/> and <see cref="CheckpointReader"/>
/// (US-EMDB-72-6, EmailDB_FileFormat_Spec.md Sections 10.1, 10.3): the Checkpoint
/// as the single commit point. Covers the write ordering (batch contents -> fsync
/// -> Checkpoint -> fsync) via an instrumented file layer, monotonic
/// CheckpointSequence, the walkable previous-checkpoint chain, FileId cross-check
/// rejection, offset-hint verification with silent re-resolution on mismatch, and
/// the fatal-fsync / torn-checkpoint failure contracts.
/// </summary>
public class CheckpointWriterReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-checkpoint-{Guid.NewGuid():N}.emdb");

    // Records the exact durable-write timeline: an "APPEND@offset" per block write
    // and an "FSYNC" per fsync, so a test can assert the contents-before-Checkpoint
    // ordering the spec (Section 10.3) mandates.
    private readonly OrderRecordingFileStream _stream;
    private readonly RuntimeBlockOffsetMap _runtimeMap = new();
    private readonly BlockManager _manager;

    private static readonly byte[] FileId = Ulid(0x01);

    public CheckpointWriterReaderTests()
    {
        _stream = new OrderRecordingFileStream(_path);
        _manager = new BlockManager(_stream, offsetMap: _runtimeMap, firstBlockOffset: 0, ownsStream: true);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ------------------------------------------------------------- Helpers

    private static byte[] Ulid(byte seed) =>
        Enumerable.Range(0, Checkpoint.FileIdSize).Select(i => (byte)(seed + i)).ToArray();

    /// <summary>A 16-byte BlockId whose lexicographic order matches the numeric order of <paramref name="i"/>.</summary>
    private static byte[] LocationBlockId(int i)
    {
        var id = new byte[BlockLocationIndex.BlockIdKeySize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), i);
        return id;
    }

    private static void Ok<T>(Result<T> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    /// <summary>Appends a real data block and returns a verified root pointer to it.</summary>
    private CheckpointRootPointer AppendRoot(byte payloadByte = 0x77, int length = 32)
    {
        var payload = new byte[length];
        Array.Fill(payload, payloadByte);
        var appended = _manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, payload);
        Ok(appended);
        return CheckpointRootPointer.Create(appended.Value.BlockId, appended.Value.Offset);
    }

    /// <summary>A minimal contents set: the folder tree points at a real block; everything else absent.</summary>
    private CheckpointContents Contents(
        CheckpointRootPointer? folder = null,
        CheckpointRootPointer? location = null,
        IReadOnlyList<CheckpointSecondaryIndex>? secondaries = null,
        long liveBlockCount = 10,
        long liveByteCount = 4096,
        long deadByteCount = 0) => new()
        {
            FolderTreeRoot = folder ?? CheckpointRootPointer.None,
            PrimaryIndexRoot = CheckpointRootPointer.None,
            LocationIndexRoot = location ?? CheckpointRootPointer.None,
            MetadataRoot = CheckpointRootPointer.None,
            KeyStoreRoot = CheckpointRootPointer.None,
            SecondaryIndexes = secondaries ?? Array.Empty<CheckpointSecondaryIndex>(),
            LiveBlockCount = liveBlockCount,
            LiveByteCount = liveByteCount,
            DeadByteCount = deadByteCount,
        };

    private CheckpointWriter NewWriter() => new(_manager, FileId);

    // The runtime map is populated by the append path for every block written this
    // session and is itself an IBlockIdResolver, so it re-resolves any stale hint.
    private CheckpointReader NewReader() => new(_manager, _runtimeMap);

    // --------------------------------------------------------------- Tests

    [Fact]
    public void WriteCheckpoint_orders_contents_fsync_then_checkpoint_fsync()
    {
        var writer = NewWriter();

        // Append batch contents (buffered, not yet fsync'd), then commit.
        var folder = AppendRoot();
        _stream.Log.Clear(); // focus on the commit-time timeline.

        var written = writer.WriteCheckpoint(Contents(folder: folder));
        Ok(written);

        // The commit-time timeline must be: FSYNC (contents durable) -> APPEND (the
        // Checkpoint block) -> FSYNC (the commit point durable). No Checkpoint append
        // may precede the first fsync.
        var log = _stream.Log;
        int firstFsync = log.IndexOf("FSYNC");
        int checkpointAppend = log.FindIndex(e => e.StartsWith("APPEND"));
        int lastFsync = log.LastIndexOf("FSYNC");

        Assert.True(firstFsync >= 0, "Expected a contents fsync before the Checkpoint.");
        Assert.True(checkpointAppend > firstFsync,
            $"Checkpoint append (index {checkpointAppend}) must follow the contents fsync (index {firstFsync}). Log: {string.Join(",", log)}");
        Assert.True(lastFsync > checkpointAppend,
            $"A final fsync (index {lastFsync}) must follow the Checkpoint append (index {checkpointAppend}). Log: {string.Join(",", log)}");
        Assert.Equal(2, log.Count(e => e == "FSYNC"));
    }

    [Fact]
    public void Written_checkpoint_round_trips_and_resolves_its_roots()
    {
        var writer = NewWriter();
        var folder = AppendRoot(0x11);
        var location = AppendRoot(0x22);

        var written = writer.WriteCheckpoint(Contents(folder: folder, location: location, liveBlockCount: 7, deadByteCount: 99));
        Ok(written);

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened);

        var resolved = opened.Value;
        Assert.Equal(CheckpointWriter.InitialSequence, resolved.Checkpoint.CheckpointSequence);
        Assert.Equal(7, resolved.Checkpoint.LiveBlockCount);
        Assert.Equal(99, resolved.Checkpoint.DeadByteCount);

        // Roots resolve to the exact offsets they were appended at, hint not stale.
        Assert.NotNull(resolved.FolderTreeRoot);
        Assert.Equal(folder.Offset, resolved.FolderTreeRoot!.Offset);
        Assert.False(resolved.FolderTreeRoot.HintWasStale);
        Assert.NotNull(resolved.LocationIndexRoot);
        Assert.Equal(location.Offset, resolved.LocationIndexRoot!.Offset);

        // Absent roots resolve to null; the first Checkpoint has no predecessor.
        Assert.Null(resolved.PrimaryIndexRoot);
        Assert.Null(resolved.KeyStoreRoot);
        Assert.Null(resolved.PreviousCheckpoint);
    }

    [Fact]
    public void Sequence_is_monotonic_and_previous_chain_is_walkable()
    {
        var writer = NewWriter();

        var offsets = new List<long>();
        var sequences = new List<ulong>();
        for (int i = 0; i < 4; i++)
        {
            var folder = AppendRoot((byte)(0x30 + i));
            var written = writer.WriteCheckpoint(Contents(folder: folder, liveBlockCount: i));
            Ok(written);
            offsets.Add(writer.LastCheckpointPointer.Offset);
            sequences.Add(written.Value.CheckpointSequence);
        }

        // Monotonic sequence 0,1,2,3.
        Assert.Equal(new ulong[] { 0, 1, 2, 3 }, sequences.ToArray());

        // The newest Checkpoint links the previous one by offset; the chain walks
        // newest-to-oldest down to the first (which links None).
        var reader = NewReader();
        var walk = reader.WalkChain(offsets[^1], FileId);
        Ok(walk);
        var chain = walk.Value;
        Assert.Equal(4, chain.Count);
        Assert.Equal(new ulong[] { 3, 2, 1, 0 }, chain.Select(c => c.CheckpointSequence).ToArray());
        Assert.True(chain[^1].PreviousCheckpoint.IsAbsent, "First Checkpoint must link None.");

        // (d) The walk returns each hop's own payload, in newest-to-oldest order:
        // batch i was written with LiveBlockCount = i, so the chain carries 3,2,1,0.
        Assert.Equal(new long[] { 3, 2, 1, 0 }, chain.Select(c => c.LiveBlockCount).ToArray());

        // Each hop's PreviousCheckpoint pointer names its predecessor's offset.
        var opened = reader.Open(offsets[^1], FileId);
        Ok(opened);
        Assert.Equal(offsets[^2], opened.Value.PreviousCheckpoint!.Offset);
    }

    [Fact]
    public void Reader_reresolves_silently_when_offset_hint_is_stale()
    {
        // A root whose recorded offset hint is wrong (points at a DIFFERENT block)
        // must not error: the reader re-resolves the BlockId through the chain.
        var writer = NewWriter();
        var realRoot = AppendRoot(0x44);      // the actual root block
        var decoy = AppendRoot(0x55);         // a different block sitting at another offset

        // Forge a pointer with the real BlockId but the decoy's offset (a stale hint,
        // as would happen after compaction moved the block).
        var staleHint = CheckpointRootPointer.Create(realRoot.BlockId, decoy.Offset);

        var written = writer.WriteCheckpoint(Contents(folder: staleHint));
        Ok(written);

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened); // NOT an error — a stale hint is re-resolved.

        var resolved = opened.Value.FolderTreeRoot;
        Assert.NotNull(resolved);
        Assert.True(resolved!.HintWasStale, "The stale hint should have been re-resolved.");
        Assert.Equal(realRoot.Offset, resolved.Offset); // resolved to the block's real offset
        Assert.Equal(realRoot.BlockId, resolved.BlockId);
    }

    [Fact]
    public void Reader_uses_the_hint_directly_when_it_matches()
    {
        var writer = NewWriter();
        var folder = AppendRoot(0x66);
        Ok(writer.WriteCheckpoint(Contents(folder: folder)));

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened);
        Assert.False(opened.Value.FolderTreeRoot!.HintWasStale,
            "A correct hint must be trusted directly, not re-resolved.");
    }

    [Fact]
    public void Reader_does_not_consult_the_resolver_for_correct_or_absent_hints()
    {
        // (c) A correct hint must be USED DIRECTLY without ever touching the
        // resolution chain, and (e) an absent (None) root must be skipped entirely.
        // Prove both with a counting resolver: with only correct hints and None
        // roots, the resolver is never consulted (zero calls).
        var writer = NewWriter();
        var folder = AppendRoot(0x66);
        var location = AppendRoot(0x67);
        // PrimaryIndexRoot / MetadataRoot / KeyStoreRoot / PreviousCheckpoint = None.
        Ok(writer.WriteCheckpoint(Contents(folder: folder, location: location)));

        var counting = new CountingResolver(_runtimeMap);
        var reader = new CheckpointReader(_manager, counting);
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened);

        Assert.False(opened.Value.FolderTreeRoot!.HintWasStale);
        Assert.False(opened.Value.LocationIndexRoot!.HintWasStale);
        Assert.Null(opened.Value.PrimaryIndexRoot);
        Assert.Null(opened.Value.MetadataRoot);
        Assert.Null(opened.Value.KeyStoreRoot);
        Assert.Null(opened.Value.PreviousCheckpoint);
        Assert.Equal(0, counting.CallCount); // never re-resolved: correct hints trusted, None roots skipped.
    }

    [Fact]
    public void Reader_reresolves_silently_when_hint_points_past_eof()
    {
        // (b) A hint pointing far past EOF is unreadable, not a valid block. The
        // reader must NOT crash: it silently re-resolves through the chain.
        var writer = NewWriter();
        var realRoot = AppendRoot(0x44);
        var pastEof = CheckpointRootPointer.Create(realRoot.BlockId, 100_000_000L);
        Ok(writer.WriteCheckpoint(Contents(folder: pastEof)));

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened); // no exception, no error surfaced.

        var resolved = opened.Value.FolderTreeRoot;
        Assert.NotNull(resolved);
        Assert.True(resolved!.HintWasStale);
        Assert.Equal(realRoot.Offset, resolved.Offset);
        Assert.Equal(realRoot.BlockId, resolved.BlockId);
    }

    [Fact]
    public void Reader_reresolves_silently_when_hint_points_at_garbage_bytes()
    {
        // (b) A hint pointing into the MIDDLE of another block (torn/garbage bytes,
        // no valid header/checksum there) is unreadable. The reader must re-resolve
        // silently rather than treat the garbage as a block or throw.
        var writer = NewWriter();
        var realRoot = AppendRoot(0x44);
        var decoy = AppendRoot(0x55);
        var garbageHint = CheckpointRootPointer.Create(realRoot.BlockId, decoy.Offset + 4);
        Ok(writer.WriteCheckpoint(Contents(folder: garbageHint)));

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened); // no exception, no error surfaced.

        var resolved = opened.Value.FolderTreeRoot;
        Assert.NotNull(resolved);
        Assert.True(resolved!.HintWasStale);
        Assert.Equal(realRoot.Offset, resolved.Offset);
    }

    [Fact]
    public void Reader_fails_with_a_distinct_error_only_when_the_root_is_unresolvable_everywhere()
    {
        // (d) Re-resolution failure is the ONLY genuine error: a root the hint
        // cannot verify AND the resolution chain has no entry for must surface as a
        // distinct, named failure — never silently.
        var writer = NewWriter();
        var decoy = AppendRoot(0x77);
        var phantomBlockId = Ulid(0xC0); // never appended -> unknown to the resolver.
        var unresolvable = CheckpointRootPointer.Create(phantomBlockId, decoy.Offset);
        Ok(writer.WriteCheckpoint(Contents(folder: unresolvable)));

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);

        Assert.True(opened.IsFailure, "An unresolvable root must be a genuine failure.");
        Assert.Contains("FolderTreeRoot", opened.Error);
        Assert.Contains("could not be resolved", opened.Error);
    }

    [Fact]
    public void Reader_reresolves_stale_hints_consistently_across_every_root_in_the_table()
    {
        // (e) The hint-verify/re-resolve behavior must hold for EVERY root pointer in
        // the Checkpoint, not just one. Forge every present root's hint to point at a
        // single decoy block (BlockId mismatch on each); all must re-resolve to their
        // own real offsets with HintWasStale set, while None roots are skipped.
        var writer = NewWriter();
        var folder = AppendRoot(0x11);
        var primary = AppendRoot(0x12);
        var location = AppendRoot(0x13);
        var metadata = AppendRoot(0x14);
        var keyStore = AppendRoot(0x15);
        var secondary = AppendRoot(0x16);
        var decoy = AppendRoot(0xFE);

        CheckpointRootPointer Stale(CheckpointRootPointer real) =>
            CheckpointRootPointer.Create(real.BlockId, decoy.Offset);

        var contents = new CheckpointContents
        {
            FolderTreeRoot = Stale(folder),
            PrimaryIndexRoot = Stale(primary),
            LocationIndexRoot = Stale(location),
            MetadataRoot = Stale(metadata),
            KeyStoreRoot = Stale(keyStore),
            SecondaryIndexes = new[]
            {
                CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, secondary.BlockId, decoy.Offset),
            },
            LiveBlockCount = 1,
            LiveByteCount = 1,
            DeadByteCount = 0,
        };
        Ok(writer.WriteCheckpoint(contents));

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened);
        var r = opened.Value;

        void AssertReresolved(ResolvedRoot? resolved, CheckpointRootPointer real, string name)
        {
            Assert.NotNull(resolved);
            Assert.True(resolved!.HintWasStale, $"{name} stale hint should have been re-resolved.");
            Assert.Equal(real.Offset, resolved.Offset);
            Assert.Equal(real.BlockId, resolved.BlockId);
        }

        AssertReresolved(r.FolderTreeRoot, folder, "FolderTreeRoot");
        AssertReresolved(r.PrimaryIndexRoot, primary, "PrimaryIndexRoot");
        AssertReresolved(r.LocationIndexRoot, location, "LocationIndexRoot");
        AssertReresolved(r.MetadataRoot, metadata, "MetadataRoot");
        AssertReresolved(r.KeyStoreRoot, keyStore, "KeyStoreRoot");

        Assert.Single(r.SecondaryIndexes);
        AssertReresolved(r.SecondaryIndexes[0].Root, secondary, "SecondaryIndexes[0]");

        // None roots in the same table are skipped (resolve to null), not errored.
        Assert.Null(r.PreviousCheckpoint);
    }

    [Fact]
    public void Reader_rejects_a_checkpoint_whose_fileid_mismatches()
    {
        var writer = NewWriter();
        var folder = AppendRoot();
        Ok(writer.WriteCheckpoint(Contents(folder: folder)));

        var reader = NewReader();
        var otherFileId = Ulid(0xEE); // a different file's identity
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, otherFileId);

        Assert.True(opened.IsFailure);
        Assert.Contains("FileId", opened.Error);
        Assert.Contains("does not belong to this file", opened.Error);
    }

    [Fact]
    public void FailedContentsFsync_is_fatal_and_commits_nothing()
    {
        var writer = NewWriter();
        var folder = AppendRoot();

        // Fail the very first fsync — the one that must make the batch contents
        // durable before the Checkpoint. Nothing is committed and the stream poisons.
        _stream.FailNextFsyncs = 1;
        var written = writer.WriteCheckpoint(Contents(folder: folder));

        Assert.True(written.IsFailure);
        Assert.Contains("fsync", written.Error);
        Assert.Contains("batch contents", written.Error);
        Assert.False(writer.HasCheckpoint, "No Checkpoint should have committed.");
        Assert.Null(writer.LastSequence);
        Assert.True(_manager.IsPoisoned, "A fatal fsync failure must poison the handle (spec 10.3).");
    }

    [Fact]
    public void FailedCheckpointFsync_is_fatal_and_does_not_advance_state()
    {
        var writer = NewWriter();

        // First checkpoint commits cleanly.
        var folder1 = AppendRoot(0x01);
        Ok(writer.WriteCheckpoint(Contents(folder: folder1)));
        var committedPointer = writer.LastCheckpointPointer;
        var committedSeq = writer.LastSequence;

        // Second: let the contents fsync succeed but fail the final fsync (the one
        // that commits the Checkpoint). State must NOT advance.
        var folder2 = AppendRoot(0x02);
        _stream.SkipFsyncFailures = 1; // the contents fsync succeeds
        _stream.FailNextFsyncs = 1;    // the Checkpoint-commit fsync fails
        var written = writer.WriteCheckpoint(Contents(folder: folder2));

        Assert.True(written.IsFailure);
        Assert.Contains("Checkpoint block", written.Error);
        Assert.Equal(committedSeq, writer.LastSequence); // unchanged
        Assert.Same(committedPointer, writer.LastCheckpointPointer);
    }

    [Fact]
    public void Load_rejects_a_torn_checkpoint_block()
    {
        var writer = NewWriter();
        var folder = AppendRoot();
        Ok(writer.WriteCheckpoint(Contents(folder: folder)));
        long checkpointOffset = writer.LastCheckpointPointer.Offset;

        // Corrupt a byte inside the Checkpoint block's payload region on disk.
        using (var raw = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            raw.Seek(checkpointOffset + 60, SeekOrigin.Begin); // into the payload
            int b = raw.ReadByte();
            raw.Seek(checkpointOffset + 60, SeekOrigin.Begin);
            raw.WriteByte((byte)(b ^ 0xFF));
            raw.Flush();
        }

        var reader = NewReader();
        var loaded = reader.Load(checkpointOffset);
        Assert.True(loaded.IsFailure, "A torn Checkpoint must never be returned as valid.");

        var opened = reader.Open(checkpointOffset, FileId);
        Assert.True(opened.IsFailure);
    }

    [Fact]
    public void Load_rejects_a_non_checkpoint_block()
    {
        // Point Load at a plain data block: it must refuse, not misinterpret it.
        var dataBlock = _manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[16]);
        Ok(dataBlock);

        var reader = NewReader();
        var loaded = reader.Load(dataBlock.Value.Offset);
        Assert.True(loaded.IsFailure);
        Assert.Contains("not a Checkpoint", loaded.Error);
    }

    [Fact]
    public void Resume_constructor_continues_sequence_and_chain_across_reopen()
    {
        // Write two checkpoints, then "reopen" with a fresh writer seeded from the
        // last durable Checkpoint: the next sequence continues and the chain links.
        var writer1 = NewWriter();
        Ok(writer1.WriteCheckpoint(Contents(folder: AppendRoot(0x01))));
        Ok(writer1.WriteCheckpoint(Contents(folder: AppendRoot(0x02))));
        var lastSeq = writer1.LastSequence!.Value;
        var lastPointer = writer1.LastCheckpointPointer;
        Assert.Equal((ulong)1, lastSeq);

        var writer2 = new CheckpointWriter(_manager, FileId, lastSeq, lastPointer);
        var resumed = writer2.WriteCheckpoint(Contents(folder: AppendRoot(0x03)));
        Ok(resumed);
        Assert.Equal((ulong)2, resumed.Value.CheckpointSequence);
        Assert.Equal(lastPointer.Offset, resumed.Value.PreviousCheckpoint.Offset);

        // The whole chain (0,1,2) is walkable from the resumed newest Checkpoint.
        var reader = NewReader();
        var walk = reader.WalkChain(writer2.LastCheckpointPointer.Offset, FileId);
        Ok(walk);
        Assert.Equal(new ulong[] { 2, 1, 0 }, walk.Value.Select(c => c.CheckpointSequence).ToArray());
    }

    [Fact]
    public void Secondary_indexes_are_carried_and_resolved()
    {
        var writer = NewWriter();
        var dateRoot = AppendRoot(0x71);
        var secondaries = new[]
        {
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, dateRoot.BlockId, dateRoot.Offset),
        };
        Ok(writer.WriteCheckpoint(Contents(secondaries: secondaries)));

        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened);
        Assert.Single(opened.Value.SecondaryIndexes);
        var entry = opened.Value.SecondaryIndexes[0];
        Assert.Equal(BTreeIndexKind.Date, entry.IndexKind);
        Assert.Equal(dateRoot.Offset, entry.Root.Offset);
    }

    [Fact]
    public void Checkpoint_is_the_last_block_of_a_multi_block_batch_after_the_contents_fsync()
    {
        // A realistic batch appends SEVERAL buffered content/root blocks, not one, so
        // the criterion "Checkpoint written last in every mutation batch" must be
        // proven against the whole batch, not a single-block happy path.
        var writer = NewWriter();
        _stream.Log.Clear(); // capture the whole batch's timeline from its first append.

        var folder = AppendRoot(0x11);
        var location = AppendRoot(0x22);
        var metadata = AppendRoot(0x33);
        var primary = AppendRoot(0x44);

        var written = writer.WriteCheckpoint(Contents(folder: folder, location: location));
        Ok(written);

        var log = _stream.Log;
        long checkpointOffset = writer.LastCheckpointPointer.Offset;
        int firstFsync = log.IndexOf("FSYNC");
        int checkpointAppend = log.IndexOf($"APPEND@{checkpointOffset}");
        int lastAppend = log.FindLastIndex(e => e.StartsWith("APPEND"));
        int lastFsync = log.LastIndexOf("FSYNC");

        Assert.True(checkpointAppend >= 0,
            $"Checkpoint append @ {checkpointOffset} not found. Log: {string.Join(",", log)}");

        // (a) Every batch-content block is appended (buffered) BEFORE the contents
        //     fsync — nothing the Checkpoint commits is still un-fsync'd at commit.
        for (int i = 0; i < log.Count; i++)
        {
            if (i != checkpointAppend && log[i].StartsWith("APPEND"))
                Assert.True(i < firstFsync,
                    $"Content append at index {i} must precede the contents fsync (index {firstFsync}). Log: {string.Join(",", log)}");
        }

        // (a) The Checkpoint block is appended strictly AFTER that contents fsync.
        Assert.True(checkpointAppend > firstFsync,
            $"Checkpoint append (index {checkpointAppend}) must follow the contents fsync (index {firstFsync}). Log: {string.Join(",", log)}");

        // (b) The Checkpoint is the LAST block of the batch — nothing from the batch
        //     is appended after it.
        Assert.Equal(checkpointAppend, lastAppend);

        // A final fsync commits the Checkpoint; exactly two fsyncs total.
        Assert.True(lastFsync > checkpointAppend,
            $"A commit fsync (index {lastFsync}) must follow the Checkpoint append (index {checkpointAppend}). Log: {string.Join(",", log)}");
        Assert.Equal(2, log.Count(e => e == "FSYNC"));
    }

    [Fact]
    public void Ordering_invariant_holds_for_every_batch_across_consecutive_checkpoints()
    {
        // (c) The contents-fsync -> Checkpoint-append -> fsync ordering must hold for
        //     EVERY batch, not just the first. Drive several consecutive batches and
        //     assert each batch's own internal timeline in isolation.
        var writer = NewWriter();

        for (int batch = 0; batch < 4; batch++)
        {
            _stream.Log.Clear(); // isolate this batch's durable-write timeline.

            var folder = AppendRoot((byte)(0x40 + batch));
            var extra = AppendRoot((byte)(0x80 + batch)); // >1 content block per batch.

            var written = writer.WriteCheckpoint(Contents(folder: folder));
            Ok(written);

            var log = _stream.Log;
            long checkpointOffset = writer.LastCheckpointPointer.Offset;
            int firstFsync = log.IndexOf("FSYNC");
            int checkpointAppend = log.IndexOf($"APPEND@{checkpointOffset}");
            int lastAppend = log.FindLastIndex(e => e.StartsWith("APPEND"));
            int lastFsync = log.LastIndexOf("FSYNC");

            Assert.True(firstFsync >= 0, $"batch {batch}: expected a contents fsync. Log: {string.Join(",", log)}");
            Assert.True(checkpointAppend > firstFsync,
                $"batch {batch}: Checkpoint (index {checkpointAppend}) appended before its contents fsync (index {firstFsync}). Log: {string.Join(",", log)}");
            Assert.Equal(checkpointAppend, lastAppend); // Checkpoint is this batch's last block.
            Assert.True(lastFsync > checkpointAppend,
                $"batch {batch}: no commit fsync after the Checkpoint append. Log: {string.Join(",", log)}");
            Assert.Equal(2, log.Count(e => e == "FSYNC"));
        }

        // The commits advanced the monotonic sequence 0..3.
        Assert.Equal((ulong)3, writer.LastSequence);
    }

    [Fact]
    public void After_a_failed_commit_fsync_the_previous_checkpoint_stays_readable_as_authoritative()
    {
        // (d) Simulate a crash between the contents fsync and the Checkpoint-commit
        //     fsync: the batch contents reach disk but the Checkpoint never commits.
        //     The previous Checkpoint must remain the authoritative one a reader sees;
        //     the torn/uncommitted batch is never surfaced as committed.
        var writer = NewWriter();

        var folderA = AppendRoot(0xA1);
        Ok(writer.WriteCheckpoint(Contents(folder: folderA)));
        var authoritativePointer = writer.LastCheckpointPointer;
        var authoritativeSeq = writer.LastSequence!.Value;

        var folderB = AppendRoot(0xB2);
        _stream.SkipFsyncFailures = 1; // B's contents fsync succeeds (contents durable)...
        _stream.FailNextFsyncs = 1;    // ...but the Checkpoint-commit fsync fails.
        var failed = writer.WriteCheckpoint(Contents(folder: folderB));
        Assert.True(failed.IsFailure);

        // Writer state did not advance: recovery's authoritative pointer is still A.
        Assert.Same(authoritativePointer, writer.LastCheckpointPointer);
        Assert.Equal(authoritativeSeq, writer.LastSequence);

        // A reader opening the authoritative pointer returns Checkpoint A — the
        // uncommitted B is never observed as the committed sequence.
        var reader = NewReader();
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, FileId);
        Ok(opened);
        Assert.Equal(authoritativeSeq, opened.Value.Checkpoint.CheckpointSequence);
    }

    [Fact]
    public void LocationIndex_nodes_land_before_the_Checkpoint_block_in_the_composed_flow()
    {
        // The composed real flow: LocationIndexCheckpoint appends its B+-tree node
        // blocks, fsyncs them durable, and emits a root that feeds CheckpointWriter.
        // The Checkpoint block that commits the batch must come AFTER every location
        // node in the file (spec Section 10.3 write order).
        var store = new BTreeNodeStore(_manager, blockIdResolver: null);
        var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
        var locationCheckpoint = new LocationIndexCheckpoint(index, _manager.Flush);

        // A batch large enough to force multiple node blocks (leaves + internal spine).
        var batch = new List<BlockLocation>();
        for (int i = 0; i < 20; i++)
            batch.Add(new BlockLocation
            {
                BlockId = LocationBlockId(i),
                Offset = 4096L * (i + 1),
                TotalBlockLength = 128,
            });

        _stream.Log.Clear(); // capture the whole batch's timeline.

        var emitted = locationCheckpoint.Checkpoint(batch);
        Ok(emitted);
        Assert.NotNull(emitted.Value);
        long locationRootOffset = emitted.Value!.RootOffset;

        // Feed the emitted location root into the Checkpoint. The offset-addressed
        // root node was minted as a real block at that offset; use its BlockId.
        byte[] rootBlockId = _runtimeMap.SnapshotOrderedByOffset()
            .First(b => b.Offset == locationRootOffset).BlockId;
        var locationPointer = CheckpointRootPointer.Create(rootBlockId, locationRootOffset);

        var writer = NewWriter();
        var written = writer.WriteCheckpoint(Contents(location: locationPointer));
        Ok(written);

        var log = _stream.Log;
        long checkpointOffset = writer.LastCheckpointPointer.Offset;
        int checkpointAppend = log.IndexOf($"APPEND@{checkpointOffset}");
        Assert.True(checkpointAppend >= 0,
            $"Checkpoint append @ {checkpointOffset} not found. Log: {string.Join(",", log)}");

        // Every location-index node block was appended before the Checkpoint block...
        var nodeAppendIndexes = new List<int>();
        for (int i = 0; i < log.Count; i++)
        {
            if (i == checkpointAppend || !log[i].StartsWith("APPEND"))
                continue;
            long off = long.Parse(log[i].AsSpan("APPEND@".Length));
            Assert.True(off < checkpointOffset,
                $"A block was appended at {off}, at/after the Checkpoint offset {checkpointOffset}. Log: {string.Join(",", log)}");
            nodeAppendIndexes.Add(i);
        }
        Assert.NotEmpty(nodeAppendIndexes); // the batch really did write node blocks.
        Assert.True(nodeAppendIndexes.Max() < checkpointAppend,
            "All location-index nodes must be appended before the Checkpoint block.");

        // ...and made durable (fsync'd) before the Checkpoint block was appended.
        int firstFsync = log.IndexOf("FSYNC");
        Assert.True(firstFsync >= 0 && firstFsync < checkpointAppend,
            $"Location nodes must be fsync'd before the Checkpoint append. Log: {string.Join(",", log)}");

        // The Checkpoint block is the last block appended in the whole composed batch.
        Assert.Equal(checkpointAppend, log.FindLastIndex(e => e.StartsWith("APPEND")));
    }

    [Fact]
    public void WalkChain_rejects_a_chain_whose_sequence_does_not_strictly_decrease()
    {
        // (c) The walk asserts the sequence strictly DECREASES newest-to-oldest. Forge
        //     a chain that violates it: a newest Checkpoint whose predecessor carries a
        //     HIGHER (non-decreasing) sequence. The resume constructor lets us stamp a
        //     new Checkpoint with a sequence that does not exceed its predecessor's by
        //     mis-seeding the last-sequence — exactly the corruption a bad chain shows.
        var writer1 = NewWriter();
        Ok(writer1.WriteCheckpoint(Contents(folder: AppendRoot(0x01)))); // seq 0
        Ok(writer1.WriteCheckpoint(Contents(folder: AppendRoot(0x02)))); // seq 1
        Ok(writer1.WriteCheckpoint(Contents(folder: AppendRoot(0x03)))); // seq 2
        var predecessor = writer1.LastCheckpointPointer;                 // points at seq 2

        // Resume mis-seeded (claim the last sequence was 0): the next Checkpoint is
        // stamped seq 1 while it links a predecessor stamped seq 2 — a chain whose
        // sequence rises going backward (2 after 1), i.e. does not strictly decrease.
        var writer2 = new CheckpointWriter(_manager, FileId, lastSequence: 0, lastCheckpoint: predecessor);
        var newest = writer2.WriteCheckpoint(Contents(folder: AppendRoot(0x04))); // seq 1
        Ok(newest);
        Assert.Equal((ulong)1, newest.Value.CheckpointSequence);

        var reader = NewReader();
        var walk = reader.WalkChain(writer2.LastCheckpointPointer.Offset, FileId);

        Assert.True(walk.IsFailure, "A non-strictly-decreasing chain must be surfaced as an error.");
        Assert.Contains("not monotonic", walk.Error);
        Assert.Contains("strictly less", walk.Error);
    }

    [Fact]
    public void WalkChain_detects_a_cycle_and_errors_instead_of_looping()
    {
        // (c) A previous-Checkpoint link that (after re-resolution) lands on an
        //     already-visited offset is a cycle. The walk must detect it and surface an
        //     error rather than loop forever. Forge it: write a Checkpoint whose
        //     PreviousCheckpoint names a phantom BlockId with a stale offset hint, then
        //     hand the reader a resolver that re-resolves that phantom back to the
        //     Checkpoint's OWN offset — closing the loop.
        var decoy = AppendRoot(0xD0);          // a real block the stale hint points at (BlockId mismatch)
        var phantomId = Ulid(0xCC);            // never appended; only the redirecting resolver knows it
        var selfLink = CheckpointRootPointer.Create(phantomId, decoy.Offset);

        // Resume constructor stamps this forged pointer as the new Checkpoint's previous.
        var writer = new CheckpointWriter(_manager, FileId, lastSequence: 5, lastCheckpoint: selfLink);
        Ok(writer.WriteCheckpoint(Contents(folder: AppendRoot(0x0A))));
        long selfOffset = writer.LastCheckpointPointer.Offset;

        // The resolver redirects the phantom BlockId back to this very Checkpoint's
        // offset, so the previous-link re-resolves to a node already on the walk.
        var looping = new RedirectingResolver(_runtimeMap, phantomId, selfOffset);
        var reader = new CheckpointReader(_manager, looping);
        var walk = reader.WalkChain(selfOffset, FileId);

        Assert.True(walk.IsFailure, "A cyclic chain must be surfaced as an error, not looped on.");
        Assert.Contains("cycle", walk.Error);
        Assert.Contains(selfOffset.ToString(), walk.Error);
    }

    [Fact]
    public void WalkChain_errors_deterministically_on_a_broken_previous_link()
    {
        // (c) A previous Checkpoint whose block is corrupt/unreadable must make the
        //     walk fail deterministically at that hop — never crash or loop. Write a
        //     two-link chain, then tear the older Checkpoint's block on disk.
        var writer = NewWriter();
        Ok(writer.WriteCheckpoint(Contents(folder: AppendRoot(0x01)))); // seq 0 (the predecessor)
        long predecessorOffset = writer.LastCheckpointPointer.Offset;
        Ok(writer.WriteCheckpoint(Contents(folder: AppendRoot(0x02)))); // seq 1 (the newest)
        long newestOffset = writer.LastCheckpointPointer.Offset;

        // Tear the predecessor's Checkpoint block on disk (flip a payload byte).
        using (var raw = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            raw.Seek(predecessorOffset + 60, SeekOrigin.Begin);
            int b = raw.ReadByte();
            raw.Seek(predecessorOffset + 60, SeekOrigin.Begin);
            raw.WriteByte((byte)(b ^ 0xFF));
            raw.Flush();
        }

        var reader = NewReader();
        var walk = reader.WalkChain(newestOffset, FileId);

        Assert.True(walk.IsFailure, "A torn predecessor must fail the walk, not crash or loop.");
        Assert.Contains("chain walk failed", walk.Error);
        Assert.Contains(predecessorOffset.ToString(), walk.Error);
    }

    [Fact]
    public void Constructor_rejects_bad_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new CheckpointWriter(null!, FileId));
        Assert.Throws<ArgumentNullException>(() => new CheckpointWriter(_manager, null!));
        Assert.Throws<ArgumentException>(() => new CheckpointWriter(_manager, new byte[15]));
        Assert.Throws<ArgumentException>(() =>
            new CheckpointWriter(_manager, FileId, 5, CheckpointRootPointer.None));

        Assert.Throws<ArgumentNullException>(() => new CheckpointReader(null!, _runtimeMap));
        Assert.Throws<ArgumentNullException>(() => new CheckpointReader(_manager, null!));
    }
}

/// <summary>
/// An <see cref="IBlockIdResolver"/> that wraps a real resolver and counts every
/// <see cref="TryGetLocation"/> call — the probe that proves whether the reader
/// consulted the resolution chain at all (a correct or absent hint must not).
/// </summary>
internal sealed class CountingResolver : IBlockIdResolver
{
    private readonly IBlockIdResolver _inner;

    public CountingResolver(IBlockIdResolver inner) => _inner = inner;

    /// <summary>Number of times the reader fell back to the resolution chain.</summary>
    public int CallCount { get; private set; }

    public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
    {
        CallCount++;
        return _inner.TryGetLocation(blockId, out location);
    }
}

/// <summary>
/// An <see cref="IBlockIdResolver"/> that maps ONE chosen phantom BlockId to a fixed
/// offset and delegates every other BlockId to an inner resolver. Used to forge a
/// cyclic previous-Checkpoint link: the phantom re-resolves to an offset already on
/// the walk, so the walk's cycle guard must fire.
/// </summary>
internal sealed class RedirectingResolver : IBlockIdResolver
{
    private readonly IBlockIdResolver _inner;
    private readonly byte[] _redirectId;
    private readonly long _redirectOffset;

    public RedirectingResolver(IBlockIdResolver inner, byte[] redirectId, long redirectOffset)
    {
        _inner = inner;
        _redirectId = redirectId;
        _redirectOffset = redirectOffset;
    }

    public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
    {
        if (blockId.SequenceEqual(_redirectId))
        {
            location = new BlockLocation
            {
                BlockId = _redirectId,
                Offset = _redirectOffset,
                TotalBlockLength = 64,
            };
            return true;
        }
        return _inner.TryGetLocation(blockId, out location);
    }
}

/// <summary>
/// A <see cref="FileStream"/> that records the durable-write timeline (block writes
/// and fsyncs, in order) and can inject fsync failures at a chosen point — the
/// instrumented file layer the write-order and fatal-fsync tests need.
/// </summary>
internal sealed class OrderRecordingFileStream : FileStream
{
    public OrderRecordingFileStream(string path)
        : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
    }

    /// <summary>Ordered timeline: "APPEND@&lt;pos&gt;" per data write, "FSYNC" per real fsync.</summary>
    public List<string> Log { get; } = new();

    /// <summary>Number of upcoming fsyncs to let succeed before <see cref="FailNextFsyncs"/> begins failing.</summary>
    public int SkipFsyncFailures { get; set; }

    /// <summary>Number of fsync attempts (after the skips) to fail with an IOException.</summary>
    public int FailNextFsyncs { get; set; }

    public override void Flush(bool flushToDisk)
    {
        if (flushToDisk)
        {
            if (SkipFsyncFailures > 0)
            {
                SkipFsyncFailures--;
            }
            else if (FailNextFsyncs > 0)
            {
                FailNextFsyncs--;
                throw new IOException("injected fsync failure");
            }
            Log.Add("FSYNC");
        }
        base.Flush(flushToDisk);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Log.Add($"APPEND@{Position}");
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Log.Add($"APPEND@{Position}");
        base.Write(buffer);
    }
}
