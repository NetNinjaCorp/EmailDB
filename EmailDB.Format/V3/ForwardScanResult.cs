namespace EmailDB.Format.V3;

/// <summary>
/// A contiguous span of file bytes the forward scan could not attribute to any
/// fully-valid block (EmailDB_FileFormat_Spec.md Section 13): from the first
/// unreadable offset up to (but not including) the offset where the scan
/// resynchronized on the next fully-verified block — or up to EOF when no
/// later block verified.
/// </summary>
/// <param name="Start">Inclusive file offset where the damage begins.</param>
/// <param name="End">Exclusive end offset: the resynchronization point, or EOF.</param>
/// <param name="Reason">
/// The verification failure observed at <paramref name="Start"/> (header
/// checksum mismatch, payload checksum mismatch, torn tail, …).
/// </param>
public readonly record struct DamagedRange(long Start, long End, string Reason)
{
    /// <summary>Number of damaged bytes covered by this range.</summary>
    public long Length => End - Start;
}

/// <summary>
/// Outcome of <see cref="BlockManager.ScanForward"/> (spec Sections 11, 13):
/// every fully-verified block in file order, plus every damaged byte range the
/// scan had to hunt across. Duplicate BlockIds may appear in
/// <see cref="Blocks"/> (new versions of logical blocks are legitimate);
/// resolution is last-position-wins, which an <see cref="IBlockOffsetMap"/>
/// populated during the scan applies automatically.
/// </summary>
public sealed class ForwardScanResult
{
    /// <summary>
    /// Every fully-verified block, ordered by file offset. Duplicate BlockIds
    /// appear once per on-disk version; the greatest offset is the live one.
    /// </summary>
    public required IReadOnlyList<BlockLocation> Blocks { get; init; }

    /// <summary>
    /// Damaged byte ranges the scan hunted across, ordered by file offset.
    /// Empty for a fully-intact file. A final range ending at
    /// <see cref="FileLength"/> is typically a torn tail append (logical
    /// truncation, spec Section 13) rather than mid-file damage.
    /// </summary>
    public required IReadOnlyList<DamagedRange> DamagedRanges { get; init; }

    /// <summary>File length at the time of the scan (exclusive scan bound).</summary>
    public required long FileLength { get; init; }
}
