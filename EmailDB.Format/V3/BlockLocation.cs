namespace EmailDB.Format.V3;

/// <summary>
/// Where an appended block landed: its minted ULID, the file offset of its
/// first header byte, and its total on-disk size. Returned by
/// <see cref="BlockManager.Append"/> so callers (and the runtime offset map)
/// can locate the block without re-scanning.
/// </summary>
public sealed class BlockLocation
{
    /// <summary>The block's ULID (16 raw bytes, big-endian binary layout).</summary>
    public required byte[] BlockId { get; init; }

    /// <summary>File offset of the block's first header byte.</summary>
    public required long Offset { get; init; }

    /// <summary>Entire on-disk block size including the 16-byte footer.</summary>
    public required long TotalBlockLength { get; init; }
}
