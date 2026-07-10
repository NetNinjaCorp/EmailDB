namespace EmailDB.Format.V3;

/// <summary>
/// The result of a <see cref="DirtyOpener.Open"/> attempt: the recovered read-side
/// <see cref="State"/> (when the file was healed to a clean commit point) plus the
/// recovery accounting the caller can log or assert on.
/// </summary>
public sealed class DirtyOpenResult
{
    /// <summary>
    /// The ready-to-use open-state, present only when the file was fully healed
    /// (<see cref="HealedClean"/> = true): a clean re-open against the recovered
    /// commit point. Null when recovery replayed uncommitted WAL that the caller
    /// supplied no fresh-Checkpoint contents to commit — the file is left dirty so
    /// the next open re-runs recovery rather than silently dropping those ops.
    /// </summary>
    public required OpenState? State { get; init; }

    /// <summary>
    /// True when recovery reached a durable clean commit point (no uncommitted WAL
    /// remains, or all of it was folded into a fresh Checkpoint) and the superblock
    /// was healed to it with CleanShutdown = 1. False when matching WAL was replayed
    /// but no fresh-Checkpoint contents were supplied to commit it.
    /// </summary>
    public required bool HealedClean { get; init; }

    /// <summary>
    /// True when the forward scan found a Checkpoint newer than the one the
    /// superblock's LastCheckpoint hint named — i.e. a crash struck between the
    /// Checkpoint write and the superblock update, and the scan healed it by
    /// adopting the newer Checkpoint (spec Section 10.2 step 4).
    /// </summary>
    public required bool AdoptedNewerCheckpoint { get; init; }

    /// <summary>Sequence of the Checkpoint recovery adopted as the commit point (the newest valid one, or the fresh one after replay).</summary>
    public required ulong AdoptedCheckpointSequence { get; init; }

    /// <summary>Number of uncommitted WAL blocks replayed into the sink.</summary>
    public required int ReplayedBlockCount { get; init; }

    /// <summary>Total WAL entries replayed across those blocks.</summary>
    public required int ReplayedEntryCount { get; init; }

    /// <summary>True when a fresh Checkpoint was written to commit the replayed state (spec Section 10.2 step 4).</summary>
    public required bool WroteFreshCheckpoint { get; init; }

    /// <summary>
    /// True when the caller opted in (via <c>buildStateForCallerCommit</c>) and uncommitted WAL was
    /// replayed into the caller's sink but NOT committed here: <see cref="State"/> is a ready read-side
    /// at the adopted Checkpoint over the still-dirty file, and the caller MUST apply the replayed ops
    /// to its live indexes and commit a fresh Checkpoint (which heals the file clean). Recovery wrote no
    /// fresh Checkpoint and left the on-disk superblock dirty, so a crash before that commit re-runs
    /// recovery and replays the same WAL — no data loss (spec Section 13, Section 10.4).
    /// </summary>
    public bool RequiresCallerCommit { get; init; }

    /// <summary>The offset where the bounded recovery scan stopped: the torn-tail boundary, or EOF for an intact tail.</summary>
    public required long ScanCutoffOffset { get; init; }

    /// <summary>Number of damaged byte ranges observed at or after the adopted Checkpoint (a torn tail append is one).</summary>
    public required int DamagedRangeCount { get; init; }

    /// <summary>Human-readable context (e.g. why the file was left dirty); null when nothing noteworthy happened.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The bounded dirty-open scan and heal (EmailDB_FileFormat_Spec.md Section 10.2
/// step 4, Section 10.4): the recovery path the <see cref="CleanOpener"/> defers to
/// when the superblock records <c>CleanShutdown = 0</c>. From the superblock's
/// LastCheckpoint hint it:
/// <list type="number">
/// <item>scans forward from the hinted Checkpoint for a NEWER valid Checkpoint and
/// adopts the newest — healing a crash that struck between a Checkpoint write and the
/// superblock update (the superblock's hint lags the durable Checkpoint chain);</item>
/// <item>replays the uncommitted WAL fenced to that newest Checkpoint (spec Section
/// 10.4) into the caller's <see cref="IWalReplaySink"/>, in WalSequence order,
/// stopping at the torn tail — reusing the tested <see cref="WalReplayer"/>;</item>
/// <item>writes a fresh Checkpoint committing the replayed state when the caller
/// supplies its post-replay contents;</item>
/// <item>heals the superblock to the recovered commit point (LastCheckpoint hint +
/// <c>CleanShutdown = 1</c>) so the next open takes the clean fast path;</item>
/// <item>hands back a ready <see cref="OpenState"/> by re-running the clean-open fast
/// path against the now-healed file.</item>
/// </list>
///
/// <para><b>Bounded by post-Checkpoint bytes.</b> Both the newest-Checkpoint discovery
/// walk and the <see cref="WalReplayer"/> scan begin at the hinted / newest Checkpoint
/// offset (<see cref="BlockManager.ScanForward"/>'s <c>startOffset</c>), so recovery
/// reads only the bytes written after the last Checkpoint — never the whole file (spec
/// Section 10.2: "bounded by data written after the last checkpoint, not file size").
/// A crash can only tear the last append, so the discovery walk stops at the first
/// unreadable block (it never resynchronizes across a gap to adopt a Checkpoint beyond
/// damaged bytes).</para>
///
/// <para><b>No data loss.</b> When matching WAL is replayed but the caller supplies no
/// fresh-Checkpoint contents to commit it, the superblock is NOT marked clean: the
/// file stays dirty so the next open re-runs recovery, and the just-replayed WAL —
/// still on disk — is replayed again rather than dropped. Committing the replayed
/// index state requires the caller's index integration, so it is the caller's opt-in.</para>
///
/// <para><b>Out of scope (disaster fallbacks, US-EMDB-74-8).</b> No valid superblock,
/// or a superblock whose LastCheckpoint hint names no valid Checkpoint of this file,
/// is surfaced as a failure here — the full-scan rebuild from offset 8192 (spec
/// Section 10.2 step 6, Section 13) is a separate component.</para>
/// </summary>
public static class DirtyOpener
{
    /// <summary>
    /// Runs the bounded dirty scan and heal over <paramref name="stream"/> (spec
    /// Section 10.2 step 4). The caller owns <paramref name="stream"/>; a returned
    /// <see cref="DirtyOpenResult.State"/> owns only the block manager it created.
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream over the EmailDB file.</param>
    /// <param name="replaySink">
    /// Receives each replayed WAL entry so the caller's in-memory indexes are
    /// rebuilt. When null, a discarding sink is used (recovery still bounds-scans,
    /// heals a stale hint, and reports what would replay).
    /// </param>
    /// <param name="freshCheckpointContents">
    /// Produces the post-replay roots/accounting for the fresh Checkpoint that
    /// commits the replayed state; evaluated only when uncommitted WAL was actually
    /// replayed. Supply it (with a real <paramref name="replaySink"/>) to fold the
    /// WAL into a durable commit point; omit it to recover read-only.
    /// </param>
    /// <param name="encryptionBootstrap">Encryption bootstrap seam (spec Section 10.2 step 2); required for an encrypted file.</param>
    /// <param name="log">Optional sink for one message per damaged range during the recovery scan.</param>
    /// <param name="buildStateForCallerCommit">
    /// When true and uncommitted WAL is replayed without <paramref name="freshCheckpointContents"/> to
    /// commit it here, recovery does NOT leave the file unopenable: it hands back a read-side
    /// <see cref="DirtyOpenResult.State"/> at the adopted Checkpoint (over the still-dirty file) with
    /// <see cref="DirtyOpenResult.RequiresCallerCommit"/> set, so the caller applies the replayed ops to
    /// its live indexes and commits the fresh Checkpoint itself (spec Section 13, Section 10.4). The
    /// caller reads the replayed ops from the sink it passed as <paramref name="replaySink"/>.
    /// </param>
    /// <returns>
    /// The recovery outcome; a failure only for a genuine open error (no valid
    /// superblock, no valid Checkpoint at the hint, a poisoned write during heal).
    /// </returns>
    public static Result<DirtyOpenResult> Open(
        FileStream stream,
        IWalReplaySink? replaySink = null,
        Func<CheckpointContents>? freshCheckpointContents = null,
        IEncryptionBootstrap? encryptionBootstrap = null,
        Action<string>? log = null,
        bool buildStateForCallerCommit = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // 1. Select the valid superblock (higher valid sequence). Two fixed slots.
        using var superblockManager = new SuperblockManager(stream, ownsStream: false);
        var loaded = superblockManager.Load();
        if (loaded.IsFailure)
            return Result<DirtyOpenResult>.Failure(
                $"Dirty open failed: no valid superblock (a full-scan rebuild is the disaster fallback, spec Section 10.2 step 6). {loaded.Error}");
        var superblock = loaded.Value;

        // Unknown IncompatFlags must refuse the open (spec Section 10.2 step 1).
        var unknownIncompat = SuperblockFeatureFlags.UnknownIncompatBits(superblock);
        if (unknownIncompat != 0)
            return Result<DirtyOpenResult>.Failure(
                $"Dirty open refused: superblock carries unknown IncompatFlags 0x{unknownIncompat:X8}; this build cannot open the file (spec Section 10.2 step 1).");

        // 2. Encryption bootstrap seam (spec Section 10.2 step 2): ciphertext must not
        //    be read as plaintext, so an encrypted file needs a bootstrap.
        if (superblock.EncryptionEnabled != 0)
        {
            if (encryptionBootstrap is null)
                return Result<DirtyOpenResult>.Failure(
                    "Dirty open failed: superblock marks the file encrypted but no IEncryptionBootstrap was supplied (spec Section 10.2 step 2).");
            var bootstrapped = encryptionBootstrap.Bootstrap(superblock);
            if (bootstrapped.IsFailure)
                return Result<DirtyOpenResult>.Failure(
                    $"Dirty open failed: encryption bootstrap failed (spec Section 10.2 step 2). {bootstrapped.Error}");
        }

        // A superblock with no LastCheckpoint hint has no committed Checkpoint to
        // scan forward from: that is the no-Checkpoint disaster fallback (US-EMDB-74-8).
        if (IsAllZero(superblock.LastCheckpointBlockId))
            return Result<DirtyOpenResult>.Failure(
                "Dirty open failed: the superblock records no LastCheckpoint hint; a full-scan rebuild is the disaster fallback (spec Section 10.2 step 6).");

        long fileLength = stream.Length;
        var manager = new BlockManager(
            stream,
            maxPayloadLength: superblock.MaxPayloadLength,
            offsetMap: null,
            ownsStream: false);

        Result<DirtyOpenResult> result;
        try
        {
            result = Recover(
                stream, manager, superblockManager, superblock, fileLength,
                replaySink ?? DiscardWalReplaySink.Instance, freshCheckpointContents,
                encryptionBootstrap, buildStateForCallerCommit, log);
        }
        finally
        {
            manager.Dispose();
        }
        return result;
    }

    private static Result<DirtyOpenResult> Recover(
        FileStream stream,
        BlockManager manager,
        SuperblockManager superblockManager,
        Superblock superblock,
        long fileLength,
        IWalReplaySink sink,
        Func<CheckpointContents>? freshCheckpointContents,
        IEncryptionBootstrap? encryptionBootstrap,
        bool buildStateForCallerCommit,
        Action<string>? log)
    {
        // 3. Bounded forward walk from the hinted Checkpoint for the NEWEST valid
        //    Checkpoint of this file. The hint may lag the durable chain (crash
        //    between the Checkpoint write and the superblock update); the walk heals
        //    that by adopting the newest. It reads only post-hint bytes and stops at
        //    the first unreadable block (the torn tail — never resynchronizes across
        //    a gap to trust a Checkpoint sitting beyond damaged bytes).
        var discovered = FindNewestCheckpoint(manager, superblock, fileLength, log);
        if (discovered.IsFailure)
            return Result<DirtyOpenResult>.Failure(discovered.Error);
        var newest = discovered.Value;
        bool adoptedNewer = newest.Offset != superblock.LastCheckpointOffset;

        // 4. Replay the uncommitted WAL fenced to the newest Checkpoint (spec Section
        //    10.4), and — when the caller supplied post-replay contents — write a
        //    fresh Checkpoint committing it. The WalReplayer scan is itself bounded to
        //    the bytes after the newest Checkpoint.
        var newestPointer = CheckpointRootPointer.Create(newest.BlockId, newest.Offset);
        CheckpointWriter? checkpointWriter = freshCheckpointContents is null
            ? null
            : new CheckpointWriter(manager, superblock.FileId, newest.Sequence, newestPointer);

        var replayed = new WalReplayer(manager).Replay(
            newestPointer, sink, checkpointWriter, freshCheckpointContents, log);
        if (replayed.IsFailure)
            return Result<DirtyOpenResult>.Failure(
                $"Dirty open failed during WAL replay: {replayed.Error}");
        var outcome = replayed.Value;

        bool wroteFresh = outcome.FreshCheckpoint is not null;
        // Uncommitted WAL that we could not commit (no contents supplied) must NOT be
        // marked clean — the next open must re-run recovery rather than drop it.
        bool healedClean = outcome.ReplayedBlocks.Count == 0 || wroteFresh;

        CheckpointRootPointer effective = wroteFresh
            ? checkpointWriter!.LastCheckpointPointer
            : newestPointer;
        ulong effectiveSequence = outcome.FreshCheckpoint?.CheckpointSequence ?? newest.Sequence;

        // 5. Heal the superblock: point LastCheckpoint at the recovered commit point
        //    and, when fully healed, clear the dirty flag so the next open is a clean
        //    fast path. When left dirty, still advance the hint so the next recovery
        //    scan is bounded to the newest Checkpoint.
        if (healedClean || adoptedNewer)
        {
            var healed = superblock.Clone();
            healed.LastCheckpointBlockId = (byte[])effective.BlockId.Clone();
            healed.LastCheckpointOffset = effective.Offset;
            healed.CleanShutdown = healedClean ? (byte)1 : (byte)0;
            var written = superblockManager.Write(healed);
            if (written.IsFailure)
                return Result<DirtyOpenResult>.Failure(
                    $"Dirty open recovered but healing the superblock failed: {written.Error}");
        }

        // 6. Produce the read-side state. A fully-healed file now takes the clean-open
        //    fast path; dispose our recovery manager first so the clean open owns a
        //    fresh manager over the stream. An encrypted file MUST forward the bootstrap
        //    into the post-heal clean open — otherwise the healed re-open cannot decrypt
        //    and recovery would fail on every encrypted file (spec Section 10.2 step 2).
        OpenState? state = null;
        string? detail = null;
        bool requiresCallerCommit = false;
        if (healedClean)
        {
            manager.Dispose();
            var clean = CleanOpener.Open(stream, encryptionBootstrap);
            if (clean.IsFailure)
                return Result<DirtyOpenResult>.Failure(
                    $"Dirty open healed the file but the clean re-open failed: {clean.Error}");
            if (clean.Value.Kind != OpenOutcomeKind.CleanOpen)
                return Result<DirtyOpenResult>.Failure(
                    $"Dirty open healed the file but the clean re-open reported {clean.Value.Kind}: {clean.Value.Detail}");
            state = clean.Value.State;
        }
        else if (buildStateForCallerCommit && outcome.ReplayedBlocks.Count > 0)
        {
            // Torn-checkpoint / dangling-WAL recovery (spec Section 13, Section 10.4): the
            // uncommitted WAL was replayed into the caller's sink but no fresh-Checkpoint
            // contents were supplied here. Rather than leave the file unopenable (losing the
            // previously-committed state), hand back a read-side state at the ADOPTED
            // Checkpoint over the still-dirty file. The caller re-applies the replayed ops to
            // its live indexes and commits the fresh Checkpoint (folding them in and healing
            // the file clean). The on-disk superblock stays dirty until that commit, so a
            // crash before it re-runs recovery and replays the same WAL — no data loss.
            manager.Dispose();
            var adopted = superblock.Clone();
            adopted.LastCheckpointBlockId = (byte[])newest.BlockId.Clone();
            adopted.LastCheckpointOffset = newest.Offset;
            adopted.CleanShutdown = 1; // in-memory only; the on-disk superblock is NOT rewritten here.
            var openedAt = CleanOpener.OpenAt(stream, adopted, encryptionBootstrap);
            if (openedAt.IsFailure)
                return Result<DirtyOpenResult>.Failure(
                    $"Dirty open replayed the uncommitted WAL but building the read-side state at the adopted Checkpoint failed: {openedAt.Error}");
            state = openedAt.Value;
            requiresCallerCommit = true;
            detail = $"Replayed {outcome.ReplayedEntryCount} WAL entries into the sink; the caller must apply them " +
                "and commit a fresh Checkpoint to heal the file clean (spec Section 13).";
        }
        else
        {
            detail = $"Replayed {outcome.ReplayedEntryCount} WAL entries into the sink but no fresh-Checkpoint " +
                "contents were supplied to commit them; the file is left dirty so the next open re-runs recovery.";
        }

        return Result<DirtyOpenResult>.Success(new DirtyOpenResult
        {
            State = state,
            HealedClean = healedClean,
            RequiresCallerCommit = requiresCallerCommit,
            AdoptedNewerCheckpoint = adoptedNewer,
            AdoptedCheckpointSequence = effectiveSequence,
            ReplayedBlockCount = outcome.ReplayedBlocks.Count,
            ReplayedEntryCount = outcome.ReplayedEntryCount,
            WroteFreshCheckpoint = wroteFresh,
            ScanCutoffOffset = outcome.ScanCutoffOffset,
            DamagedRangeCount = outcome.DamagedRanges.Count,
            Detail = detail,
        });
    }

    private readonly record struct NewestCheckpoint(byte[] BlockId, long Offset, ulong Sequence);

    /// <summary>
    /// Single bounded forward walk from the superblock's LastCheckpoint hint that
    /// returns the newest valid Checkpoint of this file (highest CheckpointSequence).
    /// It steps block-by-block by each block's TotalBlockLength and STOPS at the first
    /// unreadable block — the torn tail of an append-only file — so it never trusts a
    /// Checkpoint beyond damaged bytes. Reads only post-hint bytes.
    /// </summary>
    private static Result<NewestCheckpoint> FindNewestCheckpoint(
        BlockManager manager, Superblock superblock, long fileLength, Action<string>? log)
    {
        long hintOffset = superblock.LastCheckpointOffset;

        // The hinted Checkpoint is the recovery baseline; it must be a valid
        // Checkpoint of this file, else the hint is unusable (disaster fallback).
        var hinted = manager.Read(hintOffset);
        if (hinted.IsFailure || hinted.Value.Header.Type != BlockType.Checkpoint)
            return Result<NewestCheckpoint>.Failure(
                $"Dirty open failed: the block at the superblock's LastCheckpoint offset {hintOffset} is not a readable Checkpoint " +
                $"(a full-scan rebuild is the disaster fallback, spec Section 10.2 step 6). {(hinted.IsFailure ? hinted.Error : $"block type {hinted.Value.Header.Type}")}");
        var hintedCheckpoint = CheckpointSerializer.Deserialize(hinted.Value.Payload);
        if (hintedCheckpoint.IsFailure)
            return Result<NewestCheckpoint>.Failure(
                $"Dirty open failed: the hinted Checkpoint at offset {hintOffset} is corrupt: {hintedCheckpoint.Error}");
        if (!hintedCheckpoint.Value.FileId.AsSpan().SequenceEqual(superblock.FileId))
            return Result<NewestCheckpoint>.Failure(
                $"Dirty open failed: the hinted Checkpoint at offset {hintOffset} carries a FileId that does not match the superblock (spec Section 10.1).");

        var newest = new NewestCheckpoint(
            (byte[])hinted.Value.Header.BlockId.Clone(), hintOffset, hintedCheckpoint.Value.CheckpointSequence);

        long offset = hintOffset + BlockSerializer.GetTotalBlockLength(hinted.Value.Header.PayloadLength);
        while (offset + BlockSerializer.FixedOverhead <= fileLength)
        {
            var block = manager.Read(offset);
            if (block.IsFailure)
            {
                // Torn tail: recovery never resynchronizes across a gap to adopt a
                // Checkpoint beyond it (a crash tears only the last append).
                log?.Invoke($"Dirty open: recovery scan stopped at the torn tail at offset {offset}. {block.Error}");
                break;
            }

            if (block.Value.Header.Type == BlockType.Checkpoint)
            {
                var candidate = CheckpointSerializer.Deserialize(block.Value.Payload);
                // A newer, valid, matching-FileId Checkpoint supersedes the hint.
                if (candidate.IsSuccess
                    && candidate.Value.FileId.AsSpan().SequenceEqual(superblock.FileId)
                    && candidate.Value.CheckpointSequence > newest.Sequence)
                {
                    newest = new NewestCheckpoint(
                        (byte[])block.Value.Header.BlockId.Clone(), offset, candidate.Value.CheckpointSequence);
                }
            }

            offset += BlockSerializer.GetTotalBlockLength(block.Value.Header.PayloadLength);
        }

        return Result<NewestCheckpoint>.Success(newest);
    }

    private static bool IsAllZero(byte[] value)
    {
        foreach (byte b in value)
        {
            if (b != 0)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A sink that discards every replayed entry: used when the caller wants recovery
    /// to bound-scan, adopt the newest Checkpoint, and report what would replay
    /// without folding the WAL into any in-memory index.
    /// </summary>
    private sealed class DiscardWalReplaySink : IWalReplaySink
    {
        public static readonly DiscardWalReplaySink Instance = new();

        public Result ApplyInsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId) => Result.Success();
        public Result ApplyDelete(ReadOnlySpan<byte> key) => Result.Success();
        public Result ApplyFolderOp(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId, ReadOnlySpan<byte> aux) => Result.Success();
    }
}
