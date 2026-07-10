namespace EmailDB.Format.V3;

/// <summary>
/// How pressing a full-file compaction is, decided purely from the incremental
/// live/dead byte counters a Checkpoint carries — no file scan (docs/Compaction.md
/// Section 3, EmailDB_FileFormat_Spec.md Section 11.2). Higher values are more urgent
/// and subsume the lower ones (Urgent implies the Idle threshold was also crossed).
/// </summary>
public enum CompactionUrgency
{
    /// <summary>
    /// Dead bytes are at or below the idle threshold (default <c>Dead ≤ Live</c>): the file
    /// carries no more than the tolerated amount of garbage, so no size-driven compaction is due.
    /// </summary>
    None = 0,

    /// <summary>
    /// Dead bytes exceed the idle threshold (default <c>Dead &gt; Live</c>, i.e. the file is
    /// roughly ≥ 2× its live data): compact on the next idle window, but there is no rush.
    /// </summary>
    Idle = 1,

    /// <summary>
    /// Dead bytes exceed the urgent threshold (default <c>Dead &gt; 3 × Live</c>): garbage now
    /// dominates the file, so compaction should be scheduled promptly rather than waiting for idle.
    /// </summary>
    Urgent = 2,
}
