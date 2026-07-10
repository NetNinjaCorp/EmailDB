using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="CompactionTriggerEvaluator"/> / <see cref="CompactionSignal"/>
/// (docs/Compaction.md Section 3, EmailDB_FileFormat_Spec.md Section 11.2): the idle
/// (<c>Dead &gt; 1× Live</c>) and urgent (<c>Dead &gt; 3× Live</c>) size thresholds and the
/// DEK-pruning-pending signal, all decided from the two Checkpoint counters with no file scan.
/// </summary>
public class CompactionTriggerEvaluatorTests
{
    // ---- Idle threshold: Dead > 1.0 x Live ----------------------------------------------

    [Fact]
    public void No_trigger_when_dead_below_live()
    {
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 999, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.None, s.Urgency);
        Assert.False(s.CompactionDue);
    }

    [Fact]
    public void No_trigger_at_exactly_equal_dead_and_live()
    {
        // Strictly greater-than: Dead == Live must NOT fire (acceptance criterion is "greater than").
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 1000, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.None, s.Urgency);
        Assert.False(s.CompactionDue);
    }

    [Fact]
    public void Idle_trigger_fires_when_dead_greater_than_live()
    {
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 1001, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Idle, s.Urgency);
        Assert.True(s.CompactionDue);
        Assert.False(s.ReEncrypt);
    }

    [Fact]
    public void Idle_trigger_holds_up_to_the_urgent_boundary()
    {
        // Dead == 3x Live is still only Idle (urgent is strictly greater than 3x).
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 3000, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Idle, s.Urgency);
    }

    // ---- Urgent threshold: Dead > 3 x Live ----------------------------------------------

    [Fact]
    public void Urgent_trigger_fires_when_dead_over_three_times_live()
    {
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 3001, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Urgent, s.Urgency);
        Assert.True(s.CompactionDue);
    }

    [Fact]
    public void All_dead_no_live_is_urgent()
    {
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 0, deadByteCount: 4096, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Urgent, s.Urgency);
    }

    [Fact]
    public void Empty_file_is_never_due()
    {
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 0, deadByteCount: 0, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.None, s.Urgency);
        Assert.False(s.CompactionDue);
    }

    // ---- No scanning: evaluation is pure over the counters ------------------------------

    [Fact]
    public void Evaluation_reads_only_the_two_counters_no_io()
    {
        // The evaluator takes plain longs — it cannot touch the file. This test documents that the
        // "trigger fires at Dead > Live without scanning" criterion is met by construction: the
        // decision is a function of the in-memory counters alone.
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 50, deadByteCount: 200, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Urgent, s.Urgency);
        Assert.Equal(50, s.LiveByteCount);
        Assert.Equal(200, s.DeadByteCount);
    }

    // ---- DEK-pruning-pending signal -----------------------------------------------------

    [Fact]
    public void Dek_pruning_pending_makes_compaction_due_even_below_size_threshold()
    {
        // No dead space at all, but a retired epoch is pending pruning: compaction is still due,
        // with reEncrypt = true (docs/Compaction.md Section 4).
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 0, dekPruningPending: true);
        Assert.Equal(CompactionUrgency.None, s.Urgency);
        Assert.True(s.ReEncryptRecommended);
        Assert.True(s.ReEncrypt);
        Assert.True(s.CompactionDue);
    }

    [Fact]
    public void Size_trigger_and_dek_pruning_are_independent()
    {
        var s = CompactionTriggerEvaluator.Evaluate(liveByteCount: 1000, deadByteCount: 5000, dekPruningPending: true);
        Assert.Equal(CompactionUrgency.Urgent, s.Urgency);
        Assert.True(s.ReEncrypt);
        Assert.True(s.CompactionDue);
    }

    // ---- Configurable thresholds --------------------------------------------------------

    [Fact]
    public void Custom_ratios_shift_the_thresholds()
    {
        // With an idle ratio of 0.5, Dead > 500 (half of Live) already triggers idle.
        var s = CompactionTriggerEvaluator.Evaluate(
            liveByteCount: 1000, deadByteCount: 600, dekPruningPending: false,
            idleDeadToLiveRatio: 0.5, urgentDeadToLiveRatio: 2.0);
        Assert.Equal(CompactionUrgency.Idle, s.Urgency);
    }

    [Fact]
    public void Rejects_negative_counters_and_bad_ratios()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompactionTriggerEvaluator.Evaluate(-1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompactionTriggerEvaluator.Evaluate(0, -1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompactionTriggerEvaluator.Evaluate(1, 1, false, idleDeadToLiveRatio: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompactionTriggerEvaluator.Evaluate(1, 1, false, idleDeadToLiveRatio: 3, urgentDeadToLiveRatio: 1));
    }

    // ---- Overloads: accountant and checkpoint -------------------------------------------

    [Fact]
    public void Evaluate_from_accountant_uses_its_current_counters()
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 1000, baseDeadBytes: 0);
        acc.RecordSupersession(900); // live 100, dead 900 -> dead > 3x live
        var s = CompactionTriggerEvaluator.Evaluate(acc, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Urgent, s.Urgency);
        Assert.Equal(100, s.LiveByteCount);
        Assert.Equal(900, s.DeadByteCount);
    }

    [Fact]
    public void Evaluate_from_checkpoint_uses_its_committed_counters()
    {
        var checkpoint = MakeCheckpoint(liveBytes: 1000, deadBytes: 1500);
        var s = CompactionTriggerEvaluator.Evaluate(checkpoint, dekPruningPending: false);
        Assert.Equal(CompactionUrgency.Idle, s.Urgency);
    }

    // ---- Signal value semantics ---------------------------------------------------------

    [Fact]
    public void Signal_equality_is_value_based()
    {
        var a = new CompactionSignal(CompactionUrgency.Idle, true, 10, 20);
        var b = new CompactionSignal(CompactionUrgency.Idle, true, 10, 20);
        var c = new CompactionSignal(CompactionUrgency.Urgent, true, 10, 20);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    private static Checkpoint MakeCheckpoint(long liveBytes, long deadBytes) => new()
    {
        FormatVersion = 3,
        CheckpointSequence = 7,
        FileId = new byte[16],
        FolderTreeRoot = CheckpointRootPointer.None,
        PrimaryIndexRoot = CheckpointRootPointer.None,
        LocationIndexRoot = CheckpointRootPointer.None,
        MetadataRoot = CheckpointRootPointer.None,
        KeyStoreRoot = CheckpointRootPointer.None,
        PreviousCheckpoint = CheckpointRootPointer.None,
        SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
        LiveBlockCount = 3,
        LiveByteCount = liveBytes,
        DeadByteCount = deadBytes,
    };
}
