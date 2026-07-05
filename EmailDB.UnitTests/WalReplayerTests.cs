using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="WalReplayer"/> (US-EMDB-73-6, EmailDB_FileFormat_Spec.md
/// Sections 10.2, 10.4): crash-recovery WAL replay. Covers the checkpoint fence
/// (only WAL matching the last valid Checkpoint is replayed; WAL fencing an older
/// Checkpoint is committed history and skipped), WalSequence replay ordering,
/// the fresh-Checkpoint commit after replay, and the edge cases — no matching WAL
/// (no-op), a torn WAL block mid-scan (stop at the last valid block), duplicate
/// sequences (deterministic), non-WAL post-Checkpoint blocks, a sink failure, and
/// an absent Checkpoint fence.
/// </summary>
public class WalReplayerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-walreplay-{Guid.NewGuid():N}.emdb");
    private readonly FileStream _stream;
    private readonly RuntimeBlockOffsetMap _runtimeMap = new();
    private readonly BlockManager _manager;

    private static readonly byte[] FileId = Ulid(0x01);

    public WalReplayerTests()
    {
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
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
        Enumerable.Range(0, UlidGenerator.UlidSize).Select(i => (byte)(seed + i)).ToArray();

    private static byte[] Key(byte seed) =>
        Enumerable.Range(0, WalEntry.KeySize).Select(i => (byte)(seed + i)).ToArray();

    private static void Ok<T>(Result<T> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    private static void Ok(Result result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    private CheckpointWriter NewCheckpointWriter() => new(_manager, FileId);

    private static CheckpointContents MinimalContents() => new()
    {
        FolderTreeRoot = CheckpointRootPointer.None,
        PrimaryIndexRoot = CheckpointRootPointer.None,
        LocationIndexRoot = CheckpointRootPointer.None,
        MetadataRoot = CheckpointRootPointer.None,
        KeyStoreRoot = CheckpointRootPointer.None,
        LiveBlockCount = 1,
        LiveByteCount = 64,
        DeadByteCount = 0,
    };

    /// <summary>Commits a Checkpoint and returns the writer's pointer to it (the fence).</summary>
    private CheckpointRootPointer WriteCheckpoint(CheckpointWriter writer)
    {
        Ok(writer.WriteCheckpoint(MinimalContents()));
        return writer.LastCheckpointPointer;
    }

    /// <summary>Appends a WAL block fenced to <paramref name="fence"/> with an explicit sequence, returning its offset.</summary>
    private long AppendWal(byte[] fence, ulong sequence, params WalEntry[] entries)
    {
        var wal = new WalBlock
        {
            WalSequence = sequence,
            CheckpointBlockId = (byte[])fence.Clone(),
            Entries = entries,
        };
        var payload = WalSerializer.Serialize(wal);
        var appended = _manager.Append(BlockType.WAL, PayloadEncoding.Custom, payload);
        Ok(appended);
        return appended.Value.Offset;
    }

    /// <summary>Records the operations the replayer dispatches, in order, and can be armed to fail.</summary>
    private sealed class RecordingSink : IWalReplaySink
    {
        public readonly List<string> Ops = new();
        public string? FailOnKeyPrefix;

        public Result ApplyInsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId)
        {
            string k = Convert.ToHexString(key);
            Ops.Add($"I:{k}:{Convert.ToHexString(blockId)}");
            return Maybe(k);
        }

        public Result ApplyDelete(ReadOnlySpan<byte> key)
        {
            string k = Convert.ToHexString(key);
            Ops.Add($"D:{k}");
            return Maybe(k);
        }

        public Result ApplyFolderOp(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId, ReadOnlySpan<byte> aux)
        {
            string k = Convert.ToHexString(key);
            Ops.Add($"F:{k}:{Convert.ToHexString(blockId)}:{Convert.ToHexString(aux)}");
            return Maybe(k);
        }

        private Result Maybe(string keyHex) =>
            FailOnKeyPrefix is not null && keyHex.StartsWith(FailOnKeyPrefix)
                ? Result.Failure("sink armed to fail")
                : Result.Success();
    }

    // --------------------------------------------------------------- Tests

    [Fact]
    public void Replay_AppliesMatchingWal_InSequenceOrder()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1)));
        AppendWal(fence.BlockId, 1, WalEntry.Delete(Key(2)));

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(fence, sink);
        Ok(result);

        Assert.Equal(2, result.Value.ReplayedEntryCount);
        Assert.Equal(2, result.Value.ReplayedBlocks.Count);
        Assert.Equal(0, result.Value.SkippedCommittedBlockCount);
        Assert.Equal(new[]
        {
            $"I:{Convert.ToHexString(Key(1))}:{Convert.ToHexString(Ulid(1))}",
            $"D:{Convert.ToHexString(Key(2))}",
        }, sink.Ops);
    }

    [Fact]
    public void Replay_OrdersByWalSequence_NotFileOrder()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        // Written out of sequence order on disk: seq 2, then 0, then 1.
        AppendWal(fence.BlockId, 2, WalEntry.Insert(Key(30), Ulid(30)));
        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(10), Ulid(10)));
        AppendWal(fence.BlockId, 1, WalEntry.Insert(Key(20), Ulid(20)));

        var sink = new RecordingSink();
        Ok(new WalReplayer(_manager).Replay(fence, sink));

        Assert.Equal(new[]
        {
            $"I:{Convert.ToHexString(Key(10))}:{Convert.ToHexString(Ulid(10))}",
            $"I:{Convert.ToHexString(Key(20))}:{Convert.ToHexString(Ulid(20))}",
            $"I:{Convert.ToHexString(Key(30))}:{Convert.ToHexString(Ulid(30))}",
        }, sink.Ops);
    }

    [Fact]
    public void Replay_SkipsWalFencingOlderCheckpoint()
    {
        var cw = NewCheckpointWriter();
        var p0 = WriteCheckpoint(cw);      // Checkpoint 0
        var p1 = WriteCheckpoint(cw);      // Checkpoint 1 — the last valid one

        // A WAL block AFTER Checkpoint 1 but fencing the older Checkpoint 0
        // (committed history), plus one fencing Checkpoint 1 (uncommitted).
        AppendWal(p0.BlockId, 5, WalEntry.Insert(Key(1), Ulid(1)));   // committed history: skip
        AppendWal(p1.BlockId, 6, WalEntry.Insert(Key(2), Ulid(2)));   // uncommitted: replay

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(p1, sink);
        Ok(result);

        Assert.Equal(1, result.Value.SkippedCommittedBlockCount);
        Assert.Equal(1, result.Value.ReplayedEntryCount);
        Assert.Equal(new[] { $"I:{Convert.ToHexString(Key(2))}:{Convert.ToHexString(Ulid(2))}" }, sink.Ops);
    }

    [Fact]
    public void Replay_MultiGenerationFences_AllAfterLastCheckpoint_OnlyLastValidReplays()
    {
        var cw = NewCheckpointWriter();
        var p0 = WriteCheckpoint(cw);      // Checkpoint N-2
        var p1 = WriteCheckpoint(cw);      // Checkpoint N-1
        var p2 = WriteCheckpoint(cw);      // Checkpoint N — the last valid one

        // Three generations of WAL, ALL appended physically AFTER the last valid
        // Checkpoint (N) and interleaved by generation. Only the N-fenced blocks
        // are uncommitted; the N-1 and N-2 blocks fence an older Checkpoint and are
        // committed history (spec Section 10.4) — they must never reach the sink,
        // even though they sit at file positions past the last valid Checkpoint.
        AppendWal(p0.BlockId, 10, WalEntry.Insert(Key(1), Ulid(1)));  // N-2: history
        AppendWal(p2.BlockId, 11, WalEntry.Insert(Key(2), Ulid(2)));  // N:   replay
        AppendWal(p1.BlockId, 12, WalEntry.Insert(Key(3), Ulid(3)));  // N-1: history
        AppendWal(p2.BlockId, 13, WalEntry.Insert(Key(4), Ulid(4)));  // N:   replay
        AppendWal(p0.BlockId, 14, WalEntry.Insert(Key(5), Ulid(5)));  // N-2: history

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(p2, sink);
        Ok(result);

        // Three older-generation blocks skipped as committed history; only the two
        // N-fenced blocks replayed, in WalSequence order (11 then 13).
        Assert.Equal(3, result.Value.SkippedCommittedBlockCount);
        Assert.Equal(2, result.Value.ReplayedBlocks.Count);
        Assert.Equal(2, result.Value.ReplayedEntryCount);
        Assert.Equal(new[]
        {
            $"I:{Convert.ToHexString(Key(2))}:{Convert.ToHexString(Ulid(2))}",
            $"I:{Convert.ToHexString(Key(4))}:{Convert.ToHexString(Ulid(4))}",
        }, sink.Ops);
    }

    [Fact]
    public void Replay_ForeignUnknownFence_TreatedAsHistory_NotReplayed_NotError()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);   // the only real Checkpoint in the chain

        // A WAL block fencing a Checkpoint that exists nowhere in the chain — e.g. a
        // torn Checkpoint that was superseded and never became valid, whose BlockId
        // no surviving Checkpoint carries. Per spec Section 10.4 recovery replays
        // "exactly the WAL blocks whose CheckpointBlockId matches the last valid
        // Checkpoint"; a foreign fence is not that BlockId, so it is committed
        // history — never replayed, and never treated as an error (the file stays
        // recoverable with the foreign-fenced block present).
        byte[] foreignFence = Ulid(0xF0);
        Assert.NotEqual(Convert.ToHexString(fence.BlockId), Convert.ToHexString(foreignFence));

        AppendWal(foreignFence, 20, WalEntry.Insert(Key(1), Ulid(1)));   // foreign: history
        AppendWal(fence.BlockId, 21, WalEntry.Insert(Key(2), Ulid(2)));  // uncommitted: replay

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(fence, sink);

        // (b) not an error: recovery succeeds with the foreign-fenced block present.
        Ok(result);
        // (a/d) the foreign block was treated as history (skipped), not replayed;
        // only the block matching the last valid Checkpoint reached the sink.
        Assert.Equal(1, result.Value.SkippedCommittedBlockCount);
        Assert.Equal(1, result.Value.ReplayedEntryCount);
        Assert.Equal(new[] { $"I:{Convert.ToHexString(Key(2))}:{Convert.ToHexString(Ulid(2))}" }, sink.Ops);
    }

    [Fact]
    public void Replay_WritesFreshCheckpoint_WhenWalReplayed()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);   // Checkpoint sequence 0
        Assert.Equal(0UL, cw.LastSequence);

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1)));

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(
            fence, sink, cw, MinimalContents);
        Ok(result);

        Assert.NotNull(result.Value.FreshCheckpoint);
        Assert.Equal(1UL, result.Value.FreshCheckpoint!.CheckpointSequence);
        Assert.Equal(1UL, cw.LastSequence); // the writer advanced to the fresh Checkpoint
    }

    [Fact]
    public void Replay_FreshCheckpoint_ReadBackFromFile_LinksPreviousAndReflectsReplayedState()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);   // Checkpoint sequence 0 — the fence
        Assert.Equal(0UL, cw.LastSequence);

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1)));
        AppendWal(fence.BlockId, 1, WalEntry.Insert(Key(2), Ulid(2)));

        var sink = new RecordingSink();
        // The fresh Checkpoint's contents reflect the replayed ops: its
        // LiveBlockCount is the number of entries the sink applied. The factory is
        // evaluated only after the whole WAL has been folded into the sink, so it
        // captures the post-replay state.
        var result = new WalReplayer(_manager).Replay(
            fence, sink, cw,
            () => new CheckpointContents
            {
                FolderTreeRoot = CheckpointRootPointer.None,
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = CheckpointRootPointer.None,
                MetadataRoot = CheckpointRootPointer.None,
                KeyStoreRoot = CheckpointRootPointer.None,
                LiveBlockCount = sink.Ops.Count,
                LiveByteCount = 64,
                DeadByteCount = 0,
            });
        Ok(result);
        Assert.NotNull(result.Value.FreshCheckpoint);

        // Read the fresh Checkpoint back OFF DISK (not just the in-memory outcome)
        // via the pointer the writer now advertises.
        var freshPointer = cw.LastCheckpointPointer;
        var reader = new CheckpointReader(_manager, _runtimeMap);
        var readBack = reader.Load(freshPointer.Offset);
        Ok(readBack);
        var fresh = readBack.Value;

        // (1) higher sequence than the Checkpoint it replaced.
        Assert.Equal(1UL, fresh.CheckpointSequence);
        Assert.True(fresh.CheckpointSequence > 0UL);
        // (2) links the previous (fenced) Checkpoint by BlockId AND offset hint.
        Assert.False(fresh.PreviousCheckpoint.IsAbsent);
        Assert.Equal(Convert.ToHexString(fence.BlockId),
            Convert.ToHexString(fresh.PreviousCheckpoint.BlockId));
        Assert.Equal(fence.Offset, fresh.PreviousCheckpoint.Offset);
        // (3) its committed state reflects the two replayed ops.
        Assert.Equal(2, result.Value.ReplayedEntryCount);
        Assert.Equal(2L, fresh.LiveBlockCount);
    }

    [Fact]
    public void Replay_SecondRecoveryPass_AfterFreshCheckpoint_ReplaysNothing()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);   // Checkpoint 0 — the fence for pass one

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1)));
        AppendWal(fence.BlockId, 1, WalEntry.Insert(Key(2), Ulid(2)));

        // Pass one: replay the uncommitted WAL and commit a fresh Checkpoint 1.
        var firstSink = new RecordingSink();
        var first = new WalReplayer(_manager).Replay(fence, firstSink, cw, MinimalContents);
        Ok(first);
        Assert.Equal(2, first.Value.ReplayedEntryCount);
        Assert.NotNull(first.Value.FreshCheckpoint);
        Assert.Equal(1UL, cw.LastSequence);

        // The fence has now moved to the fresh Checkpoint 1. A second recovery pass
        // against it must find nothing to replay — the just-replayed WAL is now
        // committed history (it fences Checkpoint 0), proving replay is idempotent.
        var newFence = cw.LastCheckpointPointer;
        var secondSink = new RecordingSink();
        var second = new WalReplayer(_manager).Replay(newFence, secondSink, cw, MinimalContents);
        Ok(second);

        Assert.Empty(secondSink.Ops);
        Assert.Equal(0, second.Value.ReplayedEntryCount);
        Assert.Null(second.Value.FreshCheckpoint);      // no second fresh Checkpoint
        Assert.Equal(1UL, cw.LastSequence);             // still Checkpoint 1
    }

    [Fact]
    public void Replay_TornWal_StillWritesFreshCheckpoint_ForValidPrefix()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);   // Checkpoint 0

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1))); // valid prefix
        long tornOffset = AppendWal(fence.BlockId, 1, WalEntry.Insert(Key(2), Ulid(2))); // torn

        Ok(_manager.Flush());
        CorruptByte(tornOffset + 40);

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(fence, sink, cw, MinimalContents);
        Ok(result);

        // Only the valid prefix replayed; the torn suffix did not.
        Assert.Equal(1, result.Value.ReplayedEntryCount);
        Assert.Equal(tornOffset, result.Value.ScanCutoffOffset);
        Assert.Equal(new[] { $"I:{Convert.ToHexString(Key(1))}:{Convert.ToHexString(Ulid(1))}" }, sink.Ops);

        // A fresh Checkpoint is still committed for that valid prefix, and it reads
        // back off disk linking the fenced Checkpoint 0.
        Assert.NotNull(result.Value.FreshCheckpoint);
        Assert.Equal(1UL, cw.LastSequence);
        var reader = new CheckpointReader(_manager, _runtimeMap);
        var readBack = reader.Load(cw.LastCheckpointPointer.Offset);
        Ok(readBack);
        Assert.Equal(1UL, readBack.Value.CheckpointSequence);
        Assert.Equal(fence.Offset, readBack.Value.PreviousCheckpoint.Offset);
        Assert.Equal(Convert.ToHexString(fence.BlockId),
            Convert.ToHexString(readBack.Value.PreviousCheckpoint.BlockId));
    }

    [Fact]
    public void Replay_NoMatchingWal_IsNoOp_NoFreshCheckpoint()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);   // Checkpoint 0, no WAL after it

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(
            fence, sink, cw, MinimalContents);
        Ok(result);

        Assert.Empty(sink.Ops);
        Assert.Equal(0, result.Value.ReplayedEntryCount);
        Assert.Null(result.Value.FreshCheckpoint);
        Assert.Equal(0UL, cw.LastSequence); // still the original Checkpoint 0
    }

    [Fact]
    public void Replay_StopsAtTornWalBlock_ReplayingValidPrefix()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1))); // valid
        long tornOffset = AppendWal(fence.BlockId, 1, WalEntry.Insert(Key(2), Ulid(2)));

        // Corrupt a header byte of the second WAL block so its block read fails;
        // the scan reports a damaged tail starting there.
        Ok(_manager.Flush());
        CorruptByte(tornOffset + 40);

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(fence, sink);
        Ok(result);

        Assert.Equal(1, result.Value.ReplayedEntryCount);
        Assert.Equal(tornOffset, result.Value.ScanCutoffOffset);
        Assert.NotEmpty(result.Value.DamagedRanges);
        Assert.Equal(new[] { $"I:{Convert.ToHexString(Key(1))}:{Convert.ToHexString(Ulid(1))}" }, sink.Ops);
    }

    [Fact]
    public void Replay_DuplicateSequence_IsDeterministic_BothApplied()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        // Two WAL blocks with the SAME sequence (a corrupt file) — both replay,
        // in ascending offset order, so the outcome is deterministic.
        AppendWal(fence.BlockId, 7, WalEntry.Insert(Key(1), Ulid(1)));
        AppendWal(fence.BlockId, 7, WalEntry.Insert(Key(2), Ulid(2)));

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(fence, sink);
        Ok(result);

        Assert.Equal(2, result.Value.ReplayedEntryCount);
        Assert.Equal(new[]
        {
            $"I:{Convert.ToHexString(Key(1))}:{Convert.ToHexString(Ulid(1))}",
            $"I:{Convert.ToHexString(Key(2))}:{Convert.ToHexString(Ulid(2))}",
        }, sink.Ops);
    }

    [Fact]
    public void Replay_DispatchesFolderOp_WithAux()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        var aux = new byte[] { 9, 8, 7 };
        AppendWal(fence.BlockId, 0, WalEntry.FolderOp(Key(5), aux: aux, blockId: Ulid(5)));

        var sink = new RecordingSink();
        Ok(new WalReplayer(_manager).Replay(fence, sink));

        Assert.Equal(new[]
        {
            $"F:{Convert.ToHexString(Key(5))}:{Convert.ToHexString(Ulid(5))}:{Convert.ToHexString(aux)}",
        }, sink.Ops);
    }

    [Fact]
    public void Replay_IgnoresNonWalBlocksAfterCheckpoint()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        // An uncommitted non-WAL block (e.g. an email content block appended but
        // not yet committed) sits between the Checkpoint and the WAL.
        Ok(_manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[] { 1, 2, 3 }));
        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1)));

        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(fence, sink);
        Ok(result);

        Assert.Equal(1, result.Value.ReplayedEntryCount);
        Assert.Single(sink.Ops);
    }

    [Fact]
    public void Replay_SinkFailure_AbortsReplay()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);

        AppendWal(fence.BlockId, 0, WalEntry.Insert(Key(1), Ulid(1)));
        AppendWal(fence.BlockId, 1, WalEntry.Delete(Key(2)));

        var sink = new RecordingSink { FailOnKeyPrefix = Convert.ToHexString(Key(2)) };
        var result = new WalReplayer(_manager).Replay(fence, sink);

        Assert.True(result.IsFailure);
        // The first entry was applied before the second failed.
        Assert.Contains($"I:{Convert.ToHexString(Key(1))}:{Convert.ToHexString(Ulid(1))}", sink.Ops);
    }

    [Fact]
    public void Replay_AbsentCheckpointFence_Fails()
    {
        var sink = new RecordingSink();
        var result = new WalReplayer(_manager).Replay(CheckpointRootPointer.None, sink);

        Assert.True(result.IsFailure);
        Assert.Contains("Checkpoint", result.Error);
    }

    [Fact]
    public void Replay_MismatchedCheckpointWriterAndContents_Throws()
    {
        var cw = NewCheckpointWriter();
        var fence = WriteCheckpoint(cw);
        var sink = new RecordingSink();
        var replayer = new WalReplayer(_manager);

        // Writer supplied without a contents factory: both-or-neither.
        Assert.Throws<ArgumentException>(() => replayer.Replay(fence, sink, cw, null));
    }

    /// <summary>Flips every bit of the on-disk byte at <paramref name="offset"/> through the shared stream.</summary>
    private void CorruptByte(long offset)
    {
        _stream.Seek(offset, SeekOrigin.Begin);
        int original = _stream.ReadByte();
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.WriteByte((byte)(original ^ 0xFF));
        _stream.Flush();
    }
}
