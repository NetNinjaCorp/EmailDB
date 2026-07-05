namespace EmailDB.Format.V3;

/// <summary>
/// Sink that a <see cref="WalReplayer"/> feeds each replayed
/// <see cref="WalEntry"/> into during crash recovery
/// (EmailDB_FileFormat_Spec.md Sections 10.2, 10.4). The replayer decodes the
/// on-disk WAL stream, enforces the checkpoint fence, and dispatches the
/// surviving operations here in <see cref="WalBlock.WalSequence"/> order; the
/// sink applies them to whatever committed-in-memory structures recovery is
/// rebuilding — typically the <see cref="WalBufferedIndex"/> insert/delete
/// buffers and the folder-tree deltas.
///
/// <para>Each method returns a <see cref="Result"/> rather than throwing: a
/// failure aborts the whole replay (recovery cannot proceed with a half-applied
/// WAL), so an implementation that hits an unrecoverable inconsistency surfaces
/// it as a failure instead of leaving partial state.</para>
/// </summary>
public interface IWalReplaySink
{
    /// <summary>
    /// Replays an index upsert (<see cref="WalOpKind.Insert"/>): bind
    /// <paramref name="key"/> to <paramref name="blockId"/> (last write wins).
    /// </summary>
    /// <param name="key">The 32-byte index key (<see cref="WalEntry.KeySize"/>).</param>
    /// <param name="blockId">The 16-byte ULID the key now resolves to.</param>
    Result ApplyInsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId);

    /// <summary>
    /// Replays an index delete (<see cref="WalOpKind.Delete"/>): remove
    /// <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The 32-byte index key to remove.</param>
    Result ApplyDelete(ReadOnlySpan<byte> key);

    /// <summary>
    /// Replays a folder-tree mutation (<see cref="WalOpKind.FolderOp"/>):
    /// apply <paramref name="aux"/> against the folder identified by
    /// <paramref name="key"/>, binding <paramref name="blockId"/> when the op
    /// produced a block (all-zero otherwise).
    /// </summary>
    /// <param name="key">The 32-byte folder identity.</param>
    /// <param name="blockId">The 16-byte ULID the op produced, or all-zero.</param>
    /// <param name="aux">Op-specific detail bytes (may be empty).</param>
    Result ApplyFolderOp(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId, ReadOnlySpan<byte> aux);
}

/// <summary>
/// Replays the write-ahead log during crash recovery
/// (EmailDB_FileFormat_Spec.md Sections 10.2 step 4, 10.4). Given the last valid
/// Checkpoint, it scans the block stream forward, folds exactly the uncommitted
/// WAL blocks fenced to that Checkpoint into a <see cref="IWalReplaySink"/> in
/// <see cref="WalBlock.WalSequence"/> order, and (optionally) writes a fresh
/// Checkpoint so the replayed state becomes the new commit point.
///
/// <para><b>The checkpoint fence.</b> A WAL block written after Checkpoint N
/// carries N's BlockId (<see cref="WalBlock.CheckpointBlockId"/>). Only WAL
/// whose fence equals the last valid Checkpoint's BlockId is uncommitted and
/// replayed; WAL fencing an older Checkpoint is committed history and is skipped
/// (spec Section 10.4). This is what makes recovery replay "exactly the
/// uncommitted operations and nothing else".</para>
///
/// <para><b>Torn tail.</b> A crash can only tear the last append, so a corrupt
/// span in the stream marks where durable data ends. The replayer stops at the
/// first such point at or after the Checkpoint (the earliest damaged range from
/// <see cref="BlockManager.ScanForward"/>, or a WAL block whose payload fails
/// structural validation) and replays only the fully-valid blocks before it —
/// it never resynchronizes across a gap and replays blocks from beyond it, which
/// would apply operations whose ordering can no longer be trusted.</para>
///
/// <para><b>Ordering, gaps, duplicates.</b> Matching WAL blocks are replayed in
/// ascending <see cref="WalBlock.WalSequence"/>, ties broken by ascending file
/// offset — deterministic even if the sequence has a gap (tolerated: replay
/// proceeds in order) or a duplicate (both blocks replay, the later-offset one
/// last, so last-write-wins per key is well defined). A well-formed file from
/// <see cref="WalWriter"/> has neither, but recovery must be deterministic on a
/// damaged one.</para>
///
/// <para><b>Fresh Checkpoint.</b> When a <see cref="CheckpointWriter"/> and a
/// contents factory are supplied and at least one uncommitted WAL block was
/// replayed, the replayer writes a fresh Checkpoint (spec Section 10.2 step 4)
/// so the replayed state is committed and the just-replayed WAL becomes
/// committed history on the next open. When nothing matched the fence there is
/// nothing to fold in, so no Checkpoint is written and the existing one remains
/// authoritative.</para>
///
/// Runs on the single writer/recovery thread; not thread-safe.
/// </summary>
public sealed class WalReplayer
{
    private readonly BlockManager _manager;

    /// <summary>Creates a replayer over one file's block stream.</summary>
    /// <param name="manager">The block manager whose forward scan and block reads drive replay.</param>
    public WalReplayer(BlockManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <summary>
    /// Replays the uncommitted WAL fenced to <paramref name="lastValidCheckpoint"/>
    /// into <paramref name="sink"/>, then optionally writes a fresh Checkpoint.
    /// </summary>
    /// <param name="lastValidCheckpoint">
    /// The last valid (newest) Checkpoint's ULID+offset. Its BlockId is the fence
    /// and its offset bounds the forward scan; must not be
    /// <see cref="CheckpointRootPointer.None"/> (recovery always runs against a
    /// real Checkpoint, spec Section 10.2).
    /// </param>
    /// <param name="sink">Receives each replayed entry in replay order.</param>
    /// <param name="checkpointWriter">
    /// Writes the fresh Checkpoint after replay; pass null (with
    /// <paramref name="freshCheckpointContents"/> null) to replay only and let the
    /// caller commit.
    /// </param>
    /// <param name="freshCheckpointContents">
    /// Produces the post-replay roots/accounting for the fresh Checkpoint,
    /// evaluated only when a Checkpoint is actually written. Must be supplied iff
    /// <paramref name="checkpointWriter"/> is.
    /// </param>
    /// <param name="log">Optional sink for one message per damaged range during the forward scan.</param>
    /// <returns>The replay outcome, or a failure if the scan, a sink apply, or the fresh-Checkpoint write failed.</returns>
    public Result<WalReplayOutcome> Replay(
        CheckpointRootPointer lastValidCheckpoint,
        IWalReplaySink sink,
        CheckpointWriter? checkpointWriter = null,
        Func<CheckpointContents>? freshCheckpointContents = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(lastValidCheckpoint);
        ArgumentNullException.ThrowIfNull(sink);
        if (checkpointWriter is null != (freshCheckpointContents is null))
            throw new ArgumentException(
                "checkpointWriter and freshCheckpointContents must be supplied together (both or neither): " +
                "a fresh Checkpoint needs both a writer and its post-replay contents.");
        if (lastValidCheckpoint.IsAbsent)
            return Result<WalReplayOutcome>.Failure(
                "WAL replay aborted: the last valid Checkpoint pointer is absent (all-zero); recovery " +
                "must run against a real Checkpoint to fence WAL against (spec Section 10.2).");

        byte[] fence = lastValidCheckpoint.BlockId;
        long checkpointOffset = lastValidCheckpoint.Offset;

        // 1. Scan the stream forward FROM THE CHECKPOINT — recovery reads only the
        //    bytes written after the last Checkpoint, not the whole file (spec
        //    Section 10.2: "bounded by data written after the last checkpoint, not
        //    file size"). This reuses the tested resynchronizing scan
        //    (header→payload→footer verification, damage hunting) so a torn block
        //    surfaces as a DamagedRange rather than a mis-decoded WAL block. Blocks
        //    before the Checkpoint are committed history the fence would skip anyway.
        var scan = _manager.ScanForward(offsetMap: null, log: log, startOffset: checkpointOffset);
        if (scan.IsFailure)
            return Result<WalReplayOutcome>.Failure(
                $"WAL replay aborted: the forward scan failed: {scan.Error}");
        var scanned = scan.Value;

        // 2. Torn-tail cutoff: the earliest damaged span at or after the
        //    Checkpoint marks where durable data ends. WAL at or beyond it is not
        //    replayed (never resynchronize across a gap).
        long cutoff = scanned.FileLength;
        var damageAfterCheckpoint = new List<DamagedRange>();
        foreach (var damage in scanned.DamagedRanges)
        {
            if (damage.Start < checkpointOffset)
                continue;
            damageAfterCheckpoint.Add(damage);
            if (damage.Start < cutoff)
                cutoff = damage.Start;
        }

        // 3. Partition post-Checkpoint WAL by the fence, stopping at the cutoff or
        //    the first structurally-invalid WAL payload (a torn block that somehow
        //    passed the block checksum — treated as the durable-data boundary).
        var matching = new List<(WalBlock Wal, long Offset)>();
        int skippedCommitted = 0;
        foreach (var loc in scanned.Blocks)
        {
            if (loc.Offset < checkpointOffset)
                continue; // committed history before the Checkpoint; the fence would skip it anyway
            if (loc.Offset >= cutoff)
                break; // reached the torn tail: durable WAL ended here

            var block = _manager.Read(loc.Offset);
            if (block.IsFailure)
            {
                // A block ScanForward verified now fails to re-read: treat as the
                // durable boundary and stop, matching the torn-tail contract.
                cutoff = loc.Offset;
                log?.Invoke($"WAL replay: block at offset {loc.Offset} could not be re-read; " +
                    $"stopping replay at the last valid block. {block.Error}");
                break;
            }

            if (block.Value.Header.Type != BlockType.WAL)
                continue; // an uncommitted non-WAL block (data/node/index root): not replayed

            var wal = WalSerializer.Deserialize(block.Value.Payload);
            if (wal.IsFailure)
            {
                // Structurally corrupt WAL payload: stop at the last valid block.
                cutoff = loc.Offset;
                log?.Invoke($"WAL replay: WAL block at offset {loc.Offset} has a corrupt payload; " +
                    $"stopping replay at the last valid block. {wal.Error}");
                break;
            }

            if (wal.Value.CheckpointBlockId.AsSpan().SequenceEqual(fence))
                matching.Add((wal.Value, loc.Offset));
            else
                skippedCommitted++; // fences an older Checkpoint: committed history (spec Section 10.4)
        }

        // 4. Replay in (WalSequence, offset) order — deterministic across gaps and
        //    duplicate sequences.
        matching.Sort(static (a, b) =>
        {
            int bySequence = a.Wal.WalSequence.CompareTo(b.Wal.WalSequence);
            return bySequence != 0 ? bySequence : a.Offset.CompareTo(b.Offset);
        });

        int appliedEntries = 0;
        var replayedBlocks = new List<WalBlock>(matching.Count);
        foreach (var (wal, offset) in matching)
        {
            replayedBlocks.Add(wal);
            for (int i = 0; i < wal.Entries.Count; i++)
            {
                WalEntry entry = wal.Entries[i];
                Result applied = entry.Op switch
                {
                    WalOpKind.Insert => sink.ApplyInsert(entry.Key, entry.BlockId),
                    WalOpKind.Delete => sink.ApplyDelete(entry.Key),
                    WalOpKind.FolderOp => sink.ApplyFolderOp(entry.Key, entry.BlockId, entry.Aux),
                    _ => Result.Failure(
                        $"WAL entry carries an undefined op {(byte)entry.Op} (should have been rejected on deserialize)."),
                };
                if (applied.IsFailure)
                    return Result<WalReplayOutcome>.Failure(
                        $"WAL replay aborted: applying entry {i} of WAL block at offset {offset} " +
                        $"(sequence {wal.WalSequence}, op {entry.Op}) failed: {applied.Error}");
                appliedEntries++;
            }
        }

        // 5. Write a fresh Checkpoint so the replayed state is committed (spec
        //    Section 10.2 step 4). Skipped when nothing matched the fence: there is
        //    nothing to fold in and the existing Checkpoint stays authoritative.
        Checkpoint? freshCheckpoint = null;
        if (checkpointWriter is not null && freshCheckpointContents is not null && matching.Count > 0)
        {
            var written = checkpointWriter.WriteCheckpoint(freshCheckpointContents());
            if (written.IsFailure)
                return Result<WalReplayOutcome>.Failure(
                    $"WAL replay applied {appliedEntries} entries but committing the fresh Checkpoint failed: {written.Error}");
            freshCheckpoint = written.Value;
        }

        return Result<WalReplayOutcome>.Success(new WalReplayOutcome
        {
            ReplayedBlocks = replayedBlocks,
            ReplayedEntryCount = appliedEntries,
            SkippedCommittedBlockCount = skippedCommitted,
            ScanCutoffOffset = cutoff,
            DamagedRanges = damageAfterCheckpoint,
            FreshCheckpoint = freshCheckpoint,
        });
    }
}

/// <summary>
/// The result of a <see cref="WalReplayer.Replay"/> pass: what was folded in,
/// what was skipped as committed history, where the durable tail ended, and the
/// fresh Checkpoint written (if any).
/// </summary>
public sealed class WalReplayOutcome
{
    /// <summary>The uncommitted WAL blocks replayed, in replay order (WalSequence then offset).</summary>
    public required IReadOnlyList<WalBlock> ReplayedBlocks { get; init; }

    /// <summary>Total entries applied to the sink across all replayed blocks.</summary>
    public required int ReplayedEntryCount { get; init; }

    /// <summary>WAL blocks skipped because they fence an older Checkpoint (committed history, spec Section 10.4).</summary>
    public required int SkippedCommittedBlockCount { get; init; }

    /// <summary>
    /// The offset where the replay scan stopped: the torn-tail boundary at or
    /// after the Checkpoint, or the file length when the tail was intact.
    /// </summary>
    public required long ScanCutoffOffset { get; init; }

    /// <summary>Damaged byte ranges observed at or after the Checkpoint (empty for an intact tail).</summary>
    public required IReadOnlyList<DamagedRange> DamagedRanges { get; init; }

    /// <summary>
    /// The fresh Checkpoint committed after replay, or null when none was written
    /// (no matching WAL, or no Checkpoint writer was supplied).
    /// </summary>
    public Checkpoint? FreshCheckpoint { get; init; }
}
