namespace EmailDB.Format.V3;

/// <summary>
/// One superseded block recorded in a <see cref="CleanupBlock"/> for audit: the
/// ULID of the block that went dead and its on-disk byte size (the bytes that moved
/// from the live to the dead counter). Compaction later reclaims the space; until
/// then this is the durable, append-only record of <i>which</i> blocks were retired
/// and <i>when</i> (by the Cleanup block's Checkpoint sequence).
/// </summary>
public sealed class SupersededBlockRecord
{
    /// <summary>Number of raw bytes in a block ULID (16, big-endian binary layout).</summary>
    public const int BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>The 16-byte ULID of the superseded block.</summary>
    public required byte[] BlockId { get; init; }

    /// <summary>Entire on-disk size of the superseded block (header + payload + footer); positive.</summary>
    public required long TotalBlockLength { get; init; }

    /// <summary>Creates a record, validating the ULID width and the byte length.</summary>
    public static SupersededBlockRecord Create(byte[] blockId, long totalBlockLength)
    {
        ArgumentNullException.ThrowIfNull(blockId);
        if (blockId.Length != BlockIdSize)
            throw new ArgumentException(
                $"BlockId must be exactly {BlockIdSize} bytes, got {blockId.Length}.", nameof(blockId));
        if (totalBlockLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBlockLength), totalBlockLength,
                "A superseded block's on-disk length must be positive.");
        return new SupersededBlockRecord
        {
            BlockId = (byte[])blockId.Clone(),
            TotalBlockLength = totalBlockLength,
        };
    }
}

/// <summary>
/// In-memory model of a Cleanup block payload (BlockType 3, EmailDB_FileFormat_Spec.md
/// Section 5; docs/Compaction.md Section 3). When a batch of blocks is superseded by
/// copy-on-write rewrites or deletes, their bytes move from the live to the dead counter
/// (<see cref="DeadBlockAccountant"/>); a Cleanup block <b>records the superseded BlockIds
/// for audit</b> so the retirement is durable and inspectable independently of the (derived,
/// rebuildable) BlockLocationIndex. Cleanup blocks are always plaintext — they are recovery
/// metadata read before any key is available (spec Section 9.5).
///
/// <para>Layout (serialized by <see cref="CleanupSerializer"/>, all multi-byte integers
/// little-endian per spec Section 4):</para>
///
/// <code>
///   CheckpointSequence (ulong, 8)   — the Checkpoint batch this supersession commits under
///   EntryCount         (uint32, 4)
///   Entries[]          ({ BlockId (16), TotalBlockLength (long, 8) } × EntryCount)
/// </code>
/// </summary>
public sealed class CleanupBlock
{
    /// <summary>
    /// The sequence of the Checkpoint this supersession batch commits under, linking the audit
    /// record to a point in the Checkpoint chain (spec Section 10.1). A supersession recorded
    /// against the currently committed Checkpoint carries that Checkpoint's sequence.
    /// </summary>
    public required ulong CheckpointSequence { get; init; }

    /// <summary>The blocks superseded in this batch (may be empty, though callers write a Cleanup block only when non-empty).</summary>
    public required IReadOnlyList<SupersededBlockRecord> SupersededBlocks { get; init; }

    /// <summary>Total on-disk bytes retired by this batch — the amount moved live→dead (spec Section 11.2).</summary>
    public long TotalDeadBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < SupersededBlocks.Count; i++)
                total += SupersededBlocks[i].TotalBlockLength;
            return total;
        }
    }

    /// <summary>
    /// Builds a Cleanup block recording <paramref name="superseded"/> block locations under
    /// <paramref name="checkpointSequence"/>. Each location contributes its BlockId and on-disk size.
    /// </summary>
    public static CleanupBlock FromLocations(
        ulong checkpointSequence, IReadOnlyList<BlockLocation> superseded)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        var records = new SupersededBlockRecord[superseded.Count];
        for (int i = 0; i < superseded.Count; i++)
        {
            var loc = superseded[i] ?? throw new ArgumentException(
                $"Superseded location [{i}] must not be null.", nameof(superseded));
            records[i] = SupersededBlockRecord.Create(loc.BlockId, loc.TotalBlockLength);
        }
        return new CleanupBlock
        {
            CheckpointSequence = checkpointSequence,
            SupersededBlocks = records,
        };
    }
}
