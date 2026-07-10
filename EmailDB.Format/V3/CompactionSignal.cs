namespace EmailDB.Format.V3;

/// <summary>
/// The compaction decision the maintenance scheduler consumes, produced by
/// <see cref="CompactionTriggerEvaluator"/> from a Checkpoint's live/dead byte counters
/// and the DEK-pruning-pending signal — with no file scan (docs/Compaction.md Section 3,
/// EmailDB_FileFormat_Spec.md Section 11.2). It answers two orthogonal questions:
///
/// <list type="bullet">
///   <item><b>Is the file carrying enough dead space to compact, and how urgently?</b>
///   (<see cref="Urgency"/>, from the Dead:Live ratio.)</item>
///   <item><b>Are there retired DEK epochs whose keys can only be pruned by a
///   re-encrypting compaction?</b> (<see cref="ReEncryptRecommended"/>.)</item>
/// </list>
///
/// <para>The two are independent triggers: a file can be due for compaction on size alone,
/// on pending DEK pruning alone, or both. <see cref="CompactionDue"/> is true when either
/// fires; <see cref="ReEncrypt"/> tells a scheduled compaction whether to consolidate epochs
/// (docs/Compaction.md Section 4) while it copies live blocks.</para>
/// </summary>
public readonly struct CompactionSignal : IEquatable<CompactionSignal>
{
    /// <summary>How pressing a size-driven compaction is, from the Dead:Live ratio.</summary>
    public CompactionUrgency Urgency { get; }

    /// <summary>
    /// True when the KeyStore holds at least one non-active, un-pruned DEK epoch: those keys
    /// can only be dropped by a compaction run with <c>reEncrypt = true</c> (docs/Compaction.md
    /// Section 4). "Compact with reEncrypt = true when convenient."
    /// </summary>
    public bool ReEncryptRecommended { get; }

    /// <summary>The live byte total the decision was made from (spec Section 10.1).</summary>
    public long LiveByteCount { get; }

    /// <summary>The dead (reclaimable) byte total the decision was made from (spec Section 10.1).</summary>
    public long DeadByteCount { get; }

    public CompactionSignal(
        CompactionUrgency urgency,
        bool reEncryptRecommended,
        long liveByteCount,
        long deadByteCount)
    {
        Urgency = urgency;
        ReEncryptRecommended = reEncryptRecommended;
        LiveByteCount = liveByteCount;
        DeadByteCount = deadByteCount;
    }

    /// <summary>
    /// True when compaction should be scheduled for any reason — either the dead-space
    /// threshold was crossed (<see cref="Urgency"/> is not <see cref="CompactionUrgency.None"/>)
    /// or a DEK epoch is pending pruning (<see cref="ReEncryptRecommended"/>).
    /// </summary>
    public bool CompactionDue => Urgency != CompactionUrgency.None || ReEncryptRecommended;

    /// <summary>
    /// True when a scheduled compaction should run with <c>reEncrypt = true</c> to consolidate
    /// DEK epochs and prune the retired keys — i.e. whenever DEK pruning is pending. A
    /// size-only trigger with no pending pruning compacts by copying ciphertext verbatim, which
    /// is cheaper (docs/Compaction.md Section 4).
    /// </summary>
    public bool ReEncrypt => ReEncryptRecommended;

    public bool Equals(CompactionSignal other) =>
        Urgency == other.Urgency
        && ReEncryptRecommended == other.ReEncryptRecommended
        && LiveByteCount == other.LiveByteCount
        && DeadByteCount == other.DeadByteCount;

    public override bool Equals(object? obj) => obj is CompactionSignal other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Urgency, ReEncryptRecommended, LiveByteCount, DeadByteCount);

    public override string ToString() =>
        $"CompactionSignal(Urgency={Urgency}, ReEncrypt={ReEncryptRecommended}, " +
        $"Live={LiveByteCount}, Dead={DeadByteCount}, Due={CompactionDue})";
}
