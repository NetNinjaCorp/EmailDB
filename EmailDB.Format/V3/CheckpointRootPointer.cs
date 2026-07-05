namespace EmailDB.Format.V3;

/// <summary>
/// A verified offset hint to a persisted root block, as recorded in a Checkpoint
/// (EmailDB_FileFormat_Spec.md Section 10.1): a durable 16-byte ULID
/// <see cref="BlockId"/> paired with the file <see cref="Offset"/> the block was
/// last known to live at.
///
/// <para>The ULID is the authoritative pointer; the offset is a hint only. On use,
/// the reader confirms the block at <see cref="Offset"/> carries <see cref="BlockId"/>;
/// a mismatch (stale hint / misdirected write) is re-resolved through the
/// BlockLocationIndex and is never an error by itself (spec Sections 10.1, 13).
/// The offset-hint verification itself belongs to the reader (US-EMDB-72-6); this
/// type only carries the pair.</para>
///
/// <para>An absent root (e.g. the KeyStore of an unencrypted file, or the
/// previous-checkpoint link of the very first checkpoint) is represented by
/// <see cref="None"/>: an all-zero ULID with offset 0. The zero ULID is not a
/// valid block id, so it is an unambiguous "no block" sentinel.</para>
/// </summary>
public sealed class CheckpointRootPointer
{
    /// <summary>Size of the <see cref="BlockId"/> field: a 16-byte ULID.</summary>
    public const int BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>The 16-byte ULID of the referenced root block; all-zero when absent.</summary>
    public required byte[] BlockId { get; init; }

    /// <summary>
    /// The file offset the block was last known to live at — a verified hint,
    /// non-negative. 0 when the pointer is <see cref="None"/>.
    /// </summary>
    public required long Offset { get; init; }

    /// <summary>True when this pointer references no block (all-zero ULID).</summary>
    public bool IsAbsent
    {
        get
        {
            foreach (byte b in BlockId)
            {
                if (b != 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>The "no block" pointer: an all-zero ULID at offset 0.</summary>
    public static CheckpointRootPointer None => new()
    {
        BlockId = new byte[BlockIdSize],
        Offset = 0,
    };

    /// <summary>
    /// Creates a pointer to a block, validating the ULID width and offset sign.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="blockId"/> is not 16 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    public static CheckpointRootPointer Create(byte[] blockId, long offset)
    {
        ArgumentNullException.ThrowIfNull(blockId);
        if (blockId.Length != BlockIdSize)
            throw new ArgumentException(
                $"{nameof(BlockId)} must be exactly {BlockIdSize} bytes, got {blockId.Length}.",
                nameof(blockId));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new CheckpointRootPointer { BlockId = blockId, Offset = offset };
    }
}
