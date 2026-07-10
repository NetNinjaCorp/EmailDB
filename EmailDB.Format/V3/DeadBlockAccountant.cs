namespace EmailDB.Format.V3;

/// <summary>
/// Incremental live/dead byte accounting for a single writer session
/// (docs/Compaction.md Section 3, EmailDB_FileFormat_Spec.md Sections 10.1, 11.2).
/// A v3 file is append-only, so copy-on-write and delete continuously produce dead
/// blocks; rather than scan the file to decide when to compact, every Checkpoint
/// carries a <c>LiveByteCount</c>/<c>DeadByteCount</c> pair that is maintained
/// incrementally: <b>a block's bytes move from live to dead the moment a new
/// version or a delete supersedes it</b>. The trigger evaluation (a sibling task)
/// then compares the two counters with no I/O at all.
///
/// <para><b>Order-independent bookkeeping.</b> The accountant is seeded from the
/// counters of the Checkpoint the session opened on (<see cref="FromCheckpoint"/>)
/// and then accumulates two session deltas separately — bytes appended this session
/// (<see cref="RecordAppend"/>) and bytes superseded this session
/// (<see cref="RecordSupersession"/>). The live and dead totals are derived from
/// those accumulators (<see cref="LiveByteCount"/>, <see cref="DeadByteCount"/>),
/// so the result is identical no matter what order appends and supersessions
/// interleave — in particular, superseding a block that was appended earlier in the
/// same session never drives the running total transiently negative. The net effect
/// on a session is always:</para>
///
/// <code>
///   LiveByteCount = baseLive + appendedBytes − supersededBytes
///   DeadByteCount = baseDead + supersededBytes
/// </code>
///
/// <para>Both counters are non-negative by construction and by the seed's own
/// non-negative invariant; <see cref="LiveByteCount"/> asserts this (a negative
/// live total means more bytes were superseded than ever existed — a caller bug).
/// Bytes are counted as <see cref="BlockLocation.TotalBlockLength"/> (the entire
/// on-disk block including header and footer), matching how the file-creation and
/// close paths already tally live bytes.</para>
/// </summary>
public sealed class DeadBlockAccountant
{
    private readonly long _baseLiveBytes;
    private readonly long _baseDeadBytes;
    private long _appendedBytes;
    private long _supersededBytes;

    /// <summary>
    /// Seeds the accountant with the live/dead byte totals in effect at the start of
    /// the session (typically the Checkpoint the file opened on).
    /// </summary>
    /// <param name="baseLiveBytes">Live bytes carried by the seed Checkpoint; must be non-negative.</param>
    /// <param name="baseDeadBytes">Dead bytes carried by the seed Checkpoint; must be non-negative.</param>
    public DeadBlockAccountant(long baseLiveBytes = 0, long baseDeadBytes = 0)
    {
        if (baseLiveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(baseLiveBytes), baseLiveBytes,
                "Seed live byte count must be non-negative.");
        if (baseDeadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(baseDeadBytes), baseDeadBytes,
                "Seed dead byte count must be non-negative.");
        _baseLiveBytes = baseLiveBytes;
        _baseDeadBytes = baseDeadBytes;
    }

    /// <summary>
    /// Creates an accountant seeded from a Checkpoint's persisted counters — the
    /// "restored on open" half of the accounting contract: reopening a file resumes
    /// from exactly the live/dead totals the last Checkpoint committed.
    /// </summary>
    public static DeadBlockAccountant FromCheckpoint(Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return new DeadBlockAccountant(checkpoint.LiveByteCount, checkpoint.DeadByteCount);
    }

    /// <summary>Live bytes seeded from the session's opening Checkpoint.</summary>
    public long BaseLiveByteCount => _baseLiveBytes;

    /// <summary>Dead bytes seeded from the session's opening Checkpoint.</summary>
    public long BaseDeadByteCount => _baseDeadBytes;

    /// <summary>Total on-disk bytes appended (as new live blocks) this session.</summary>
    public long AppendedByteCount => _appendedBytes;

    /// <summary>Total on-disk bytes moved from live to dead (superseded/deleted) this session.</summary>
    public long SupersededByteCount => _supersededBytes;

    /// <summary>
    /// The current live byte total: <c>base live + appended − superseded</c>. This is the
    /// value written into the next Checkpoint's <c>LiveByteCount</c> (spec Section 10.1).
    /// </summary>
    public long LiveByteCount
    {
        get
        {
            long live = _baseLiveBytes + _appendedBytes - _supersededBytes;
            if (live < 0)
                throw new InvalidOperationException(
                    $"Live byte count went negative ({live}): more bytes were superseded " +
                    $"({_supersededBytes}) than ever lived (base {_baseLiveBytes} + appended {_appendedBytes}). " +
                    "A block was superseded that was not counted live (double-supersession or an untracked block).");
            return live;
        }
    }

    /// <summary>
    /// The current dead byte total: <c>base dead + superseded</c>. This is the value written
    /// into the next Checkpoint's <c>DeadByteCount</c> (spec Section 10.1); with
    /// <see cref="LiveByteCount"/> it drives compaction triggers without scanning (Section 11.2).
    /// </summary>
    public long DeadByteCount => _baseDeadBytes + _supersededBytes;

    /// <summary>
    /// Records a newly appended live block of <paramref name="totalBlockLength"/> on-disk bytes
    /// (its entire block including header and footer, i.e. <see cref="BlockLocation.TotalBlockLength"/>).
    /// </summary>
    public void RecordAppend(long totalBlockLength)
    {
        if (totalBlockLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBlockLength), totalBlockLength,
                "An appended block's on-disk length must be positive.");
        _appendedBytes += totalBlockLength;
    }

    /// <summary>
    /// Records one superseded block: a copy-on-write rewrite or a delete has made
    /// <paramref name="superseded"/> unreachable, so its bytes move from live to dead
    /// (docs/Compaction.md Section 3). Every such call is the single place a block's bytes
    /// cross the live/dead boundary.
    /// </summary>
    public void RecordSupersession(BlockLocation superseded)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        RecordSupersession(superseded.TotalBlockLength);
    }

    /// <summary>
    /// Records a superseded block by its on-disk byte length (the overload used when only the
    /// size is known). Moves <paramref name="totalBlockLength"/> bytes from live to dead.
    /// </summary>
    public void RecordSupersession(long totalBlockLength)
    {
        if (totalBlockLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBlockLength), totalBlockLength,
                "A superseded block's on-disk length must be positive.");
        _supersededBytes += totalBlockLength;
    }
}
