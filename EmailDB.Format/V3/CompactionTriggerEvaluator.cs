namespace EmailDB.Format.V3;

/// <summary>
/// Evaluates the v3 compaction triggers with <b>no file scan</b> — the whole point of the
/// incremental live/dead byte accounting (docs/Compaction.md Section 3,
/// EmailDB_FileFormat_Spec.md Section 11.2). Given the two counters a Checkpoint already
/// carries (<c>LiveByteCount</c>/<c>DeadByteCount</c>) and the DEK-pruning-pending signal,
/// it produces the <see cref="CompactionSignal"/> the maintenance scheduler acts on. Every
/// input is already in memory, so evaluation is pure arithmetic: it performs no I/O and never
/// walks the file.
///
/// <para>The documented default policy (docs/Compaction.md Section 3):</para>
/// <list type="bullet">
///   <item><c>Dead &gt; 1.0 × Live</c> (file ≈ 2× live data) → compact on the next idle window.</item>
///   <item><c>Dead &gt; 3 × Live</c> → compact urgently.</item>
///   <item>Retired DEK epochs pending pruning → compact with <c>reEncrypt = true</c> when convenient.</item>
/// </list>
/// The two ratio thresholds are parameters (defaulting to the documented policy) so a scheduler
/// can tune them; the comparisons are strictly greater-than, matching the acceptance criterion
/// "trigger fires at Dead greater than Live".
/// </summary>
public static class CompactionTriggerEvaluator
{
    /// <summary>
    /// Default idle threshold: compact on the next idle window once <c>Dead &gt; 1.0 × Live</c>
    /// (the file is roughly twice its live data), docs/Compaction.md Section 3.
    /// </summary>
    public const double DefaultIdleDeadToLiveRatio = 1.0;

    /// <summary>
    /// Default urgent threshold: compact promptly once <c>Dead &gt; 3 × Live</c>,
    /// docs/Compaction.md Section 3.
    /// </summary>
    public const double DefaultUrgentDeadToLiveRatio = 3.0;

    /// <summary>
    /// Evaluates the triggers from the raw counters and DEK-pruning signal.
    /// </summary>
    /// <param name="liveByteCount">Live bytes (spec Section 10.1); must be non-negative.</param>
    /// <param name="deadByteCount">Dead/reclaimable bytes (spec Section 10.1); must be non-negative.</param>
    /// <param name="dekPruningPending">
    /// True when the KeyStore holds a non-active, un-pruned DEK epoch that only a re-encrypting
    /// compaction can drop (docs/Compaction.md Section 4). Always false for a plaintext file.
    /// </param>
    /// <param name="idleDeadToLiveRatio">Dead:Live ratio above which idle compaction is due (default 1.0).</param>
    /// <param name="urgentDeadToLiveRatio">Dead:Live ratio above which urgent compaction is due (default 3.0).</param>
    /// <returns>The scheduler-facing <see cref="CompactionSignal"/>.</returns>
    /// <remarks>
    /// When <paramref name="liveByteCount"/> is 0 the ratio thresholds collapse to
    /// <c>Dead &gt; 0</c>, so a file that is entirely dead is flagged <see cref="CompactionUrgency.Urgent"/>;
    /// a genuinely empty file (both counters 0) is <see cref="CompactionUrgency.None"/>.
    /// </remarks>
    public static CompactionSignal Evaluate(
        long liveByteCount,
        long deadByteCount,
        bool dekPruningPending,
        double idleDeadToLiveRatio = DefaultIdleDeadToLiveRatio,
        double urgentDeadToLiveRatio = DefaultUrgentDeadToLiveRatio)
    {
        if (liveByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(liveByteCount), liveByteCount,
                "Live byte count must be non-negative.");
        if (deadByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(deadByteCount), deadByteCount,
                "Dead byte count must be non-negative.");
        if (!(idleDeadToLiveRatio > 0) || double.IsNaN(idleDeadToLiveRatio))
            throw new ArgumentOutOfRangeException(nameof(idleDeadToLiveRatio), idleDeadToLiveRatio,
                "The idle Dead:Live ratio must be a positive number.");
        if (!(urgentDeadToLiveRatio >= idleDeadToLiveRatio) || double.IsNaN(urgentDeadToLiveRatio))
            throw new ArgumentOutOfRangeException(nameof(urgentDeadToLiveRatio), urgentDeadToLiveRatio,
                "The urgent Dead:Live ratio must be at least the idle ratio.");

        CompactionUrgency urgency;
        if (deadByteCount > urgentDeadToLiveRatio * liveByteCount)
            urgency = CompactionUrgency.Urgent;
        else if (deadByteCount > idleDeadToLiveRatio * liveByteCount)
            urgency = CompactionUrgency.Idle;
        else
            urgency = CompactionUrgency.None;

        return new CompactionSignal(urgency, dekPruningPending, liveByteCount, deadByteCount);
    }

    /// <summary>
    /// Evaluates the triggers from a live session's <see cref="DeadBlockAccountant"/> (its current,
    /// not-yet-checkpointed counters) and the DEK-pruning signal.
    /// </summary>
    public static CompactionSignal Evaluate(
        DeadBlockAccountant accountant,
        bool dekPruningPending,
        double idleDeadToLiveRatio = DefaultIdleDeadToLiveRatio,
        double urgentDeadToLiveRatio = DefaultUrgentDeadToLiveRatio)
    {
        ArgumentNullException.ThrowIfNull(accountant);
        return Evaluate(accountant.LiveByteCount, accountant.DeadByteCount, dekPruningPending,
            idleDeadToLiveRatio, urgentDeadToLiveRatio);
    }

    /// <summary>
    /// Evaluates the triggers from a persisted <see cref="Checkpoint"/>'s committed counters and
    /// the DEK-pruning signal — the "decide from the last commit point, no scan" path.
    /// </summary>
    public static CompactionSignal Evaluate(
        Checkpoint checkpoint,
        bool dekPruningPending,
        double idleDeadToLiveRatio = DefaultIdleDeadToLiveRatio,
        double urgentDeadToLiveRatio = DefaultUrgentDeadToLiveRatio)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Evaluate(checkpoint.LiveByteCount, checkpoint.DeadByteCount, dekPruningPending,
            idleDeadToLiveRatio, urgentDeadToLiveRatio);
    }
}
