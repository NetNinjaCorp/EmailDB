using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-end tests for live/dead byte accounting and Cleanup blocks through
/// <see cref="EmailManager"/> (story US-EMDB-88, docs/Compaction.md Section 3,
/// EmailDB_FileFormat_Spec.md Sections 10.1, 11.2). They verify the accounting
/// contract on the real file: a supersession moves the retired block's bytes from the
/// live to the dead counter, the counters persist in the Checkpoint and are restored
/// on reopen, and a Cleanup block (BlockType 3, always plaintext) durably records the
/// superseded BlockIds for audit.
/// </summary>
public class DeadBlockAccountingTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-accounting-{Guid.NewGuid():N}.emdb");

    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static BlockLocation AppendData(EmailManager m, byte seed, int size)
    {
        var payload = Enumerable.Range(0, size).Select(i => (byte)(seed + i)).ToArray();
        var appended = m.BlockManager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, payload);
        Ok(appended);
        return appended.Value;
    }

    [Fact]
    public void Supersession_moves_bytes_to_dead_and_persists_across_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        // Session A: append a block and close so it is counted live by the committed Checkpoint.
        BlockLocation retired;
        using (var a = EmailManager.Open(_path).Value)
        {
            retired = AppendData(a, seed: 1, size: 900);
            Ok(a.Close());
        }

        // Session B: supersede that pre-existing block. Its bytes move live→dead immediately.
        using (var b = EmailManager.Open(_path).Value)
        {
            long baseLive = b.Checkpoint!.Checkpoint.LiveByteCount;
            Assert.Equal(0, b.Checkpoint.Checkpoint.DeadByteCount);
            Assert.Equal(0, b.Accountant!.DeadByteCount);

            var sup = b.RecordSupersession(new[] { retired });
            Ok(sup);

            Assert.Equal(retired.TotalBlockLength, b.Accountant.DeadByteCount);
            Assert.Equal(baseLive - retired.TotalBlockLength, b.Accountant.LiveByteCount);
            Ok(b.Close());
        }

        // Session C: the counters survived the reopen and are restored into the fresh accountant.
        using (var c = EmailManager.Open(_path).Value)
        {
            Assert.Equal(retired.TotalBlockLength, c.Checkpoint!.Checkpoint.DeadByteCount);
            Assert.Equal(retired.TotalBlockLength, c.Accountant!.DeadByteCount);
        }
    }

    [Fact]
    public void RecordSupersession_writes_a_cleanup_block_recording_the_superseded_block_ids()
    {
        EmailManager.Create(_path).Value.Dispose();

        using var m = EmailManager.Open(_path).Value;
        var b1 = AppendData(m, seed: 10, size: 300);
        var b2 = AppendData(m, seed: 20, size: 512);

        var sup = m.RecordSupersession(new[] { b1, b2 });
        Ok(sup);
        Assert.NotNull(sup.Value);

        // Read the appended Cleanup block back and confirm it audits both superseded blocks.
        var read = m.BlockManager.Read(sup.Value!.Offset);
        Ok(read);
        Assert.Equal(BlockType.Cleanup, read.Value.Header.Type);

        var cleanup = CleanupSerializer.Deserialize(read.Value.Payload);
        Ok(cleanup);
        Assert.Equal(m.LastCheckpointSequence, cleanup.Value.CheckpointSequence);
        Assert.Equal(2, cleanup.Value.SupersededBlocks.Count);
        Assert.Equal(b1.BlockId, cleanup.Value.SupersededBlocks[0].BlockId);
        Assert.Equal(b1.TotalBlockLength, cleanup.Value.SupersededBlocks[0].TotalBlockLength);
        Assert.Equal(b2.BlockId, cleanup.Value.SupersededBlocks[1].BlockId);
        Assert.Equal(b2.TotalBlockLength, cleanup.Value.SupersededBlocks[1].TotalBlockLength);
        Assert.Equal(b1.TotalBlockLength + b2.TotalBlockLength, cleanup.Value.TotalDeadBytes);
    }

    [Fact]
    public void Empty_supersession_is_a_noop_and_writes_nothing()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var m = EmailManager.Open(_path).Value;

        long deadBefore = m.Accountant!.DeadByteCount;
        var sup = m.RecordSupersession(Array.Empty<BlockLocation>());
        Ok(sup);
        Assert.Null(sup.Value);
        Assert.Equal(deadBefore, m.Accountant.DeadByteCount);
    }

    [Fact]
    public void RecordSupersession_on_a_created_but_unopened_instance_fails()
    {
        using var created = EmailManager.Create(_path).Value; // created, never Open()ed
        Assert.Null(created.Accountant);
        var loc = new BlockLocation { BlockId = new byte[16], Offset = 8192, TotalBlockLength = 100 };
        var sup = created.RecordSupersession(new[] { loc });
        Assert.True(sup.IsFailure);
    }

    [Fact]
    public void Cleanup_block_is_plaintext_even_in_an_encrypted_file()
    {
        var createOptions = new EmailManagerCreateOptions { Password = "correct horse", KdfParameters = FastParams };
        EmailManager.Create(_path, createOptions).Value.Dispose();

        var openOptions = new EmailManagerOpenOptions { Password = "correct horse" };
        using var m = EmailManager.Open(_path, openOptions).Value;
        Assert.True(m.IsEncrypted);

        var retired = AppendData(m, seed: 5, size: 256);
        var sup = m.RecordSupersession(new[] { retired });
        Ok(sup);

        // The Cleanup block is recovery metadata: readable without any key (spec Section 9.5).
        var read = m.BlockManager.Read(sup.Value!.Offset);
        Ok(read);
        Assert.Equal(BlockType.Cleanup, read.Value.Header.Type);
        Assert.False(read.Value.Header.IsEncrypted);

        var cleanup = CleanupSerializer.Deserialize(read.Value.Payload);
        Ok(cleanup);
        Assert.Single(cleanup.Value.SupersededBlocks);
        Assert.Equal(retired.BlockId, cleanup.Value.SupersededBlocks[0].BlockId);
    }

    [Fact]
    public void Multiple_supersessions_in_one_event_move_exactly_the_summed_bytes_to_dead()
    {
        // A single COW rewrite / delete can retire several blocks at once. Each block's full
        // on-disk size must move live→dead, so the accountant's dead delta is the exact sum.
        EmailManager.Create(_path).Value.Dispose();

        // Append the three blocks in session A and commit so they are counted live by the Checkpoint.
        BlockLocation b1, b2, b3;
        using (var a = EmailManager.Open(_path).Value)
        {
            b1 = AppendData(a, seed: 1, size: 300);
            b2 = AppendData(a, seed: 2, size: 777);
            b3 = AppendData(a, seed: 3, size: 1500);
            Ok(a.Close());
        }
        long sum = b1.TotalBlockLength + b2.TotalBlockLength + b3.TotalBlockLength;

        using var m = EmailManager.Open(_path).Value;
        long baseLive = m.Accountant!.LiveByteCount;
        long baseDead = m.Accountant.DeadByteCount;
        Assert.Equal(0, baseDead);

        var sup = m.RecordSupersession(new[] { b1, b2, b3 });
        Ok(sup);

        // All three retired blocks' full on-disk sizes move to dead in the one event.
        Assert.Equal(baseDead + sum, m.Accountant.DeadByteCount);
        // They leave the live pool by exactly the same summed amount (the appended Cleanup block
        // is itself live, folded in only at Close, so it does not affect this in-session live total).
        Assert.Equal(baseLive - sum, m.Accountant.LiveByteCount);
    }

    [Fact]
    public void Repeated_supersession_events_count_each_retired_block_once_never_the_cleanup_blocks()
    {
        // A COW rewrite then a delete, as two separate events. Each retired block's bytes cross the
        // boundary exactly once; the Cleanup blocks appended by each event stay live (never dead),
        // guarding against double-counting or counting the audit records themselves as dead.
        EmailManager.Create(_path).Value.Dispose();
        using var m = EmailManager.Open(_path).Value;

        long baseDead = m.Accountant!.DeadByteCount;
        var rewritten = AppendData(m, seed: 40, size: 640);
        var deleted = AppendData(m, seed: 50, size: 256);

        var e1 = m.RecordSupersession(new[] { rewritten }); // COW rewrite retires the old version
        Ok(e1);
        Assert.Equal(baseDead + rewritten.TotalBlockLength, m.Accountant.DeadByteCount);

        var e2 = m.RecordSupersession(new[] { deleted });   // delete retires the email's block
        Ok(e2);

        // Dead is exactly the two retired blocks — the two Cleanup blocks (e1/e2) are not dead.
        Assert.Equal(baseDead + rewritten.TotalBlockLength + deleted.TotalBlockLength,
            m.Accountant.DeadByteCount);
    }

    [Fact]
    public void Dead_count_uses_the_actual_serialized_on_disk_size_not_the_nominal_payload()
    {
        // The bytes moved to dead are the whole block on disk (header + payload + checksums + footer),
        // not the nominal payload length. Prove it two ways: the reported size equals the fixed
        // block overhead plus the payload, and equals the on-disk gap to the next block's offset.
        EmailManager.Create(_path).Value.Dispose();
        using var m = EmailManager.Open(_path).Value;

        const int payloadSize = 900;
        var retired = AppendData(m, seed: 7, size: payloadSize);
        var next = AppendData(m, seed: 8, size: 100); // forces retired's on-disk footprint to be measurable

        long onDisk = retired.TotalBlockLength;
        Assert.Equal(payloadSize + BlockSerializer.FixedOverhead, onDisk); // real serialized size, not 900
        Assert.Equal(next.Offset - retired.Offset, onDisk);               // == actual bytes it occupies on disk
        Assert.NotEqual(payloadSize, onDisk);

        long baseDead = m.Accountant!.DeadByteCount;
        var sup = m.RecordSupersession(new[] { retired });
        Ok(sup);
        Assert.Equal(baseDead + onDisk, m.Accountant.DeadByteCount);
    }

    [Fact]
    public void Counters_accumulate_across_sequential_sessions_and_persist_in_each_generations_checkpoint()
    {
        // The criterion is that EVERY committed Checkpoint carries the counters and they survive
        // reopen. Each Close writes one Checkpoint (one "generation"), so this drives three
        // successive supersession sessions and, after each, confirms the freshly committed
        // Checkpoint carries the accumulated dead total and the next open restores from it.
        EmailManager.Create(_path).Value.Dispose();

        BlockLocation b1, b2, b3;
        using (var a = EmailManager.Open(_path).Value)
        {
            b1 = AppendData(a, seed: 1, size: 300);
            b2 = AppendData(a, seed: 2, size: 500);
            b3 = AppendData(a, seed: 3, size: 700);
            Ok(a.Close());
        }

        var retirees = new[] { b1, b2, b3 };
        long expectedDead = 0;

        for (int gen = 0; gen < retirees.Length; gen++)
        {
            using (var s = EmailManager.Open(_path).Value)
            {
                // Resumed byte-exact from the previous generation's committed Checkpoint.
                Assert.Equal(expectedDead, s.Checkpoint!.Checkpoint.DeadByteCount);
                Assert.Equal(expectedDead, s.Accountant!.DeadByteCount);
                // The accountant is seeded from precisely the persisted counters (live round-trips too).
                Assert.Equal(s.Checkpoint.Checkpoint.LiveByteCount, s.Accountant.LiveByteCount);
                Assert.True(s.Accountant.LiveByteCount > 0);

                Ok(s.RecordSupersession(new[] { retirees[gen] }));
                expectedDead += retirees[gen].TotalBlockLength;
                Assert.Equal(expectedDead, s.Accountant.DeadByteCount);
                Ok(s.Close());
            }

            // The generation just committed by Close carries the accumulated dead total — observed
            // WITHOUT advancing the Checkpoint (open + dispose, no Close, leaves the file untouched).
            using (var probe = EmailManager.Open(_path).Value)
            {
                Assert.Equal(expectedDead, probe.Checkpoint!.Checkpoint.DeadByteCount);
                Assert.Equal(expectedDead, probe.Accountant!.DeadByteCount);
            }
        }

        // Every retired block's on-disk bytes are now dead, accumulated across the three sessions.
        long allDead = b1.TotalBlockLength + b2.TotalBlockLength + b3.TotalBlockLength;
        Assert.Equal(allDead, expectedDead);
        using (var final = EmailManager.Open(_path).Value)
            Assert.Equal(allDead, final.Checkpoint!.Checkpoint.DeadByteCount);
    }

    [Fact]
    public void Both_counters_round_trip_byte_exact_through_a_noop_close_and_reopen()
    {
        // Proves the WHOLE live/dead pair persists byte-for-byte across a close/reopen cycle, not
        // only the dead side. After a supersession commits non-trivial live and dead totals, a
        // no-op session (no appends, no supersessions) rewrites the Checkpoint; both counters must
        // come back identical, and the reopened accountant must seed from exactly those values.
        EmailManager.Create(_path).Value.Dispose();

        BlockLocation retired;
        using (var a = EmailManager.Open(_path).Value)
        {
            retired = AppendData(a, seed: 1, size: 900);
            AppendData(a, seed: 2, size: 400); // a second live block keeps live > dead
            Ok(a.Close());
        }
        using (var b = EmailManager.Open(_path).Value)
        {
            Ok(b.RecordSupersession(new[] { retired }));
            Ok(b.Close());
        }

        long committedLive, committedDead;
        using (var c = EmailManager.Open(_path).Value)
        {
            committedLive = c.Checkpoint!.Checkpoint.LiveByteCount;
            committedDead = c.Checkpoint.Checkpoint.DeadByteCount;
            Assert.Equal(retired.TotalBlockLength, committedDead);
            Assert.True(committedLive > 0);
            // A no-op session: nothing appended or superseded. Close still writes a fresh Checkpoint.
            Ok(c.Close());
        }

        using (var d = EmailManager.Open(_path).Value)
        {
            Assert.Equal(committedLive, d.Checkpoint!.Checkpoint.LiveByteCount);
            Assert.Equal(committedDead, d.Checkpoint.Checkpoint.DeadByteCount);
            Assert.Equal(committedLive, d.Accountant!.LiveByteCount);
            Assert.Equal(committedDead, d.Accountant.DeadByteCount);
        }
    }

    /// <summary>
    /// Reconstructs the complete supersession audit trail purely by forward-scanning the file:
    /// every fully-verified block is walked in offset order, the Cleanup blocks (BlockType 3) are
    /// picked out by their header type alone (no index, no known offsets), and each is deserialized
    /// into its recorded superseded BlockIds, sizes, and Checkpoint-sequence attribution.
    /// </summary>
    private static List<(byte[] BlockId, long TotalBlockLength, ulong CheckpointSequence)>
        RecoverAuditTrailByForwardScan(EmailManager m, out int cleanupBlockCount)
    {
        var scan = m.BlockManager.ScanForward();
        Ok(scan);
        Assert.Empty(scan.Value.DamagedRanges); // an intact file scans clean end to end

        var trail = new List<(byte[], long, ulong)>();
        cleanupBlockCount = 0;
        foreach (var loc in scan.Value.Blocks)
        {
            var read = m.BlockManager.Read(loc.Offset);
            Ok(read);
            if (read.Value.Header.Type != BlockType.Cleanup) // discover type-3 by forward scan
                continue;
            cleanupBlockCount++;
            var cleanup = CleanupSerializer.Deserialize(read.Value.Payload);
            Ok(cleanup);
            foreach (var rec in cleanup.Value.SupersededBlocks)
                trail.Add((rec.BlockId, rec.TotalBlockLength, cleanup.Value.CheckpointSequence));
        }
        return trail;
    }

    [Fact]
    public void Full_audit_trail_is_recoverable_by_forward_scan_across_events_and_sessions_after_reopen()
    {
        // The audit criterion end to end: a reader that only forward-scans the file (type-3
        // discovery, no index) must recover EVERY superseded BlockId with its correct on-disk size
        // and the Checkpoint sequence it was retired under, across multiple supersession events,
        // multiple sessions, and a final reopen. Five blocks are retired over two sessions in three
        // separate Cleanup events; a fresh sixth session rebuilds the whole trail from raw bytes.
        EmailManager.Create(_path).Value.Dispose();

        // Session A: append five blocks and commit so they are live under a committed Checkpoint.
        BlockLocation b1, b2, b3, b4, b5;
        using (var a = EmailManager.Open(_path).Value)
        {
            b1 = AppendData(a, seed: 1, size: 300);
            b2 = AppendData(a, seed: 2, size: 512);
            b3 = AppendData(a, seed: 3, size: 700);
            b4 = AppendData(a, seed: 4, size: 128);
            b5 = AppendData(a, seed: 5, size: 2048);
            Ok(a.Close());
        }

        // Session B: two SEPARATE supersession events → two distinct Cleanup blocks, both attributed
        // to session B's committed Checkpoint sequence (the Checkpoint advances only at Close).
        ulong seqB;
        using (var b = EmailManager.Open(_path).Value)
        {
            seqB = b.LastCheckpointSequence;
            Ok(b.RecordSupersession(new[] { b1, b2 })); // event 1
            Ok(b.RecordSupersession(new[] { b3 }));     // event 2
            Ok(b.Close());
        }

        // Session C: a third event under a NEW Checkpoint sequence, so attribution is distinguishable.
        ulong seqC;
        using (var c = EmailManager.Open(_path).Value)
        {
            seqC = c.LastCheckpointSequence;
            Ok(c.RecordSupersession(new[] { b4, b5 })); // event 3
            Ok(c.Close());
        }

        Assert.True(seqC > seqB, "each Close advances the Checkpoint, so session C attributes to a later sequence");

        // Fresh session: recover the entire trail from raw bytes by forward scan alone.
        var expected = new Dictionary<string, (long Length, ulong Seq)>
        {
            [Convert.ToHexString(b1.BlockId)] = (b1.TotalBlockLength, seqB),
            [Convert.ToHexString(b2.BlockId)] = (b2.TotalBlockLength, seqB),
            [Convert.ToHexString(b3.BlockId)] = (b3.TotalBlockLength, seqB),
            [Convert.ToHexString(b4.BlockId)] = (b4.TotalBlockLength, seqC),
            [Convert.ToHexString(b5.BlockId)] = (b5.TotalBlockLength, seqC),
        };

        using (var reader = EmailManager.Open(_path).Value)
        {
            var trail = RecoverAuditTrailByForwardScan(reader, out int cleanupBlockCount);

            // Three distinct events produced three discoverable type-3 Cleanup blocks.
            Assert.Equal(3, cleanupBlockCount);

            // Exactly the five retired blocks appear, each once — no gaps, no duplicates, and none of
            // the (live) Cleanup blocks themselves recorded as superseded.
            Assert.Equal(expected.Count, trail.Count);
            var recovered = trail.ToDictionary(t => Convert.ToHexString(t.BlockId), t => (t.TotalBlockLength, t.CheckpointSequence));
            Assert.Equal(expected.Keys.OrderBy(k => k), recovered.Keys.OrderBy(k => k));

            foreach (var (id, exp) in expected)
            {
                Assert.True(recovered.TryGetValue(id, out var got), $"BlockId {id} missing from the audit trail");
                Assert.Equal(exp.Length, got.TotalBlockLength);   // correct on-disk size
                Assert.Equal(exp.Seq, got.CheckpointSequence);    // correct Checkpoint attribution
            }
        }
    }

    [Fact]
    public void Dirty_open_recovery_restores_counters_from_the_last_valid_checkpoint()
    {
        // Recovery must resume the accounting from the last DURABLE Checkpoint, discarding the
        // uncommitted tail of a crashed session. A session supersedes a block and crashes (dispose
        // without Close, superblock stamped CleanShutdown = 0) with a further uncommitted
        // supersession in flight; the reopen takes the recovery path and the counters come back
        // from the last valid Checkpoint — the crashed supersession must leave no trace.
        EmailManager.Create(_path).Value.Dispose();

        BlockLocation b1, b2;
        using (var a = EmailManager.Open(_path).Value)
        {
            b1 = AppendData(a, seed: 1, size: 900);
            b2 = AppendData(a, seed: 2, size: 500);
            Ok(a.Close());
        }

        // The last VALID checkpoint: a committed supersession of b1 (dead == b1's on-disk length).
        using (var b = EmailManager.Open(_path).Value)
        {
            Ok(b.RecordSupersession(new[] { b1 }));
            Ok(b.Close());
        }

        long validLive, validDead;
        using (var probe = EmailManager.Open(_path).Value)
        {
            validLive = probe.Checkpoint!.Checkpoint.LiveByteCount;
            validDead = probe.Checkpoint.Checkpoint.DeadByteCount;
            Assert.Equal(b1.TotalBlockLength, validDead);
            // Clean open; dispose without Close leaves the durable file untouched.
        }

        // Crash: append a tail block AND supersede b2 (both uncommitted), then die without Close and
        // stamp the on-disk superblock CleanShutdown = 0, exactly the state a kill -9 leaves.
        {
            var crash = EmailManager.Open(_path).Value;
            AppendData(crash, seed: 3, size: 256);
            Ok(crash.RecordSupersession(new[] { b2 }));
            crash.Dispose(); // process death: no Close, no final Checkpoint.

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var sb = new SuperblockManager(stream, ownsStream: false);
            var loaded = sb.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            Ok(sb.Write(dirty));
        }

        // The reopen genuinely needs recovery: the clean fast path refuses the dirty file.
        using (var probeStream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var kind = CleanOpener.Open(probeStream);
            Ok(kind);
            Assert.Equal(OpenOutcomeKind.DirtyOpenRequired, kind.Value.Kind);
        }

        // Recovery restores the counters from the last valid Checkpoint; the crashed session's
        // uncommitted supersession of b2 is discarded, so dead is still exactly b1's length.
        using (var recovered = EmailManager.Open(_path).Value)
        {
            Assert.Equal(1, recovered.Superblock.CleanShutdown); // recovery ran and healed the flag
            Assert.Equal(validDead, recovered.Checkpoint!.Checkpoint.DeadByteCount);
            Assert.Equal(validLive, recovered.Checkpoint.Checkpoint.LiveByteCount);
            Assert.Equal(validDead, recovered.Accountant!.DeadByteCount);
            Assert.Equal(validLive, recovered.Accountant.LiveByteCount);
            Assert.Equal(b1.TotalBlockLength, recovered.Accountant.DeadByteCount);
            Ok(recovered.Close());
        }
    }

    [Fact]
    public void Trigger_fires_at_dead_greater_than_live_end_to_end_with_zero_block_reads_or_fsyncs()
    {
        // The acceptance criterion end to end on a real file: open EmailManager, supersede blocks one
        // at a time, and after EACH supersession evaluate the compaction trigger straight from the
        // session's live/dead counters. The urgency must track the Dead:Live ratio exactly — None
        // while Dead <= Live, Idle the instant Dead > Live, Urgent once Dead > 3x Live (strictly
        // greater-than at each boundary) — and across the whole sweep the B+-tree node store performs
        // ZERO block reads and the block manager issues ZERO fsyncs: the decision is pure arithmetic
        // over in-memory counters and never scans the file (docs/Compaction.md Section 3, spec 11.2).
        EmailManager.Create(_path).Value.Dispose();

        // Session A: append a batch of equal-size live blocks and commit them into the Checkpoint so
        // the reopened session resumes with a known live total and nothing dead.
        var appended = new List<BlockLocation>();
        using (var a = EmailManager.Open(_path).Value)
        {
            for (int i = 0; i < 16; i++)
                appended.Add(AppendData(a, seed: (byte)(i + 1), size: 4000));
            Ok(a.Close());
        }

        using var m = EmailManager.Open(_path).Value;
        Assert.NotNull(m.NodeStore); // the only structure a "scan" would read through

        // At open nothing is dead yet: Dead (0) <= Live, so the trigger must NOT fire.
        Assert.Equal(0, m.Accountant!.DeadByteCount);
        Assert.True(m.Accountant.LiveByteCount > 0);
        Assert.Equal(CompactionUrgency.None, m.EvaluateCompactionTrigger().Urgency);

        // Snapshot the I/O instruments BEFORE the supersession+evaluation sweep. Neither a buffered
        // Cleanup-block append nor a trigger evaluation may read a B+-tree node or fsync, so both
        // counters must be byte-identical at the end.
        long readsBefore = m.NodeStore!.CacheMissCount;
        long fsyncsBefore = m.BlockManager.FlushToDiskCount;

        bool sawIdleCrossing = false, sawUrgentCrossing = false;
        var prev = CompactionUrgency.None;

        foreach (var block in appended)
        {
            Ok(m.RecordSupersession(new[] { block }));

            long live = m.Accountant.LiveByteCount;
            long dead = m.Accountant.DeadByteCount;
            var signal = m.EvaluateCompactionTrigger();

            // The signal reports exactly the counters it decided from — no other input.
            Assert.Equal(live, signal.LiveByteCount);
            Assert.Equal(dead, signal.DeadByteCount);

            // Urgency is a pure function of the Dead:Live ratio, strictly greater-than at each edge.
            var expected = dead > 3 * live ? CompactionUrgency.Urgent
                         : dead > live ? CompactionUrgency.Idle
                         : CompactionUrgency.None;
            Assert.Equal(expected, signal.Urgency);
            Assert.Equal(dead > live, signal.CompactionDue); // plaintext: due iff the size threshold crossed
            Assert.False(signal.ReEncrypt);                  // no encryption, so never a re-encrypt trigger

            // Capture the exact boundary crossings: it fires the instant Dead first exceeds Live.
            if (prev == CompactionUrgency.None && signal.Urgency == CompactionUrgency.Idle)
            {
                sawIdleCrossing = true;
                Assert.True(dead > live);
            }
            if (prev != CompactionUrgency.Urgent && signal.Urgency == CompactionUrgency.Urgent)
            {
                sawUrgentCrossing = true;
                Assert.True(dead > 3 * live);
            }
            prev = signal.Urgency;
        }

        // The sweep genuinely walked through both boundaries (not e.g. urgent from the first step),
        // so the None->Idle (Dead>Live) and Idle->Urgent (Dead>3xLive) transitions were each observed.
        Assert.True(sawIdleCrossing, "the Dead>Live (idle) boundary was crossed during the sweep");
        Assert.True(sawUrgentCrossing, "the Dead>3xLive (urgent) boundary was crossed during the sweep");

        // The whole sweep — every RecordSupersession and every EvaluateCompactionTrigger — read ZERO
        // B+-tree nodes and issued ZERO fsyncs. "Without scanning" proven on the real file.
        Assert.Equal(readsBefore, m.NodeStore.CacheMissCount);
        Assert.Equal(fsyncsBefore, m.BlockManager.FlushToDiskCount);
    }
}
