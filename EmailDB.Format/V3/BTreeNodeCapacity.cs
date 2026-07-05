namespace EmailDB.Format.V3;

/// <summary>
/// Capacity helpers for the generic B+-tree node format
/// (EmailDB_FileFormat_Spec.md Section 6.1). Capacities are computed at the
/// 4096-byte target node size: after the 96-byte fixed block overhead (spec
/// Section 4) and the 12-byte node header, 3988 bytes remain for the node body.
///
/// At those widths the spec capacities are:
///   PrimaryEmail  — leaf 83 entries;  internal 49 keys / 50 children
///   BlockLocation — leaf 124 entries; internal 70 keys / 71 children
///   Date          — leaf 166 entries; internal 54 keys / 55 children
/// </summary>
public static class BTreeNodeCapacity
{
    /// <summary>Target on-disk size of one node block, including block overhead (spec Section 6.1).</summary>
    public const int TargetNodeSize = 4096;

    /// <summary>
    /// Bytes available for the node body at the target node size:
    /// 4096 − 96 (block overhead) − 12 (node header) = 3988.
    /// </summary>
    public const int UsableBodySize =
        TargetNodeSize - BlockSerializer.FixedOverhead - BTreeNodeSerializer.NodeHeaderSize;

    // ---- Declared widths per IndexKind (spec Section 6.1 registry) ----

    /// <summary>PrimaryEmail key: EmailHashedID (SHA3-256).</summary>
    public const byte PrimaryEmailKeySize = 32;

    /// <summary>PrimaryEmail leaf value: BlockId.</summary>
    public const ushort PrimaryEmailLeafValueSize = 16;

    /// <summary>PrimaryEmail internal child record: ChildBlockId (16) + ChildHash (32).</summary>
    public const ushort PrimaryEmailChildRecordSize = 48;

    /// <summary>BlockLocation key: BlockId.</summary>
    public const byte BlockLocationKeySize = 16;

    /// <summary>BlockLocation leaf value: Offset (8) + Length (8).</summary>
    public const ushort BlockLocationLeafValueSize = 16;

    /// <summary>BlockLocation internal child record: ChildOffset (8) + ChildHash (32) — offset-addressed.</summary>
    public const ushort BlockLocationChildRecordSize = 40;

    /// <summary>Date key: DateTicks (8) ‖ BlockId (16) composite.</summary>
    public const byte DateKeySize = 24;

    /// <summary>Date leaf value: empty (key-only index).</summary>
    public const ushort DateLeafValueSize = 0;

    /// <summary>Date internal child record: ChildBlockId (16) + ChildHash (32).</summary>
    public const ushort DateChildRecordSize = 48;

    /// <summary>
    /// Maximum leaf entries that fit in the target node body for the given
    /// declared widths: floor(3988 / (KeySize + ValueSize)).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keySize"/> is not positive or <paramref name="valueSize"/> is negative.</exception>
    public static int MaxLeafEntries(int keySize, int valueSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keySize);
        ArgumentOutOfRangeException.ThrowIfNegative(valueSize);
        return UsableBodySize / (keySize + valueSize);
    }

    /// <summary>
    /// Maximum routing keys that fit in the target node body for an internal
    /// node with the given declared widths: the largest n with
    /// n × KeySize + (n + 1) × ValueSize ≤ 3988, i.e.
    /// floor((3988 − ValueSize) / (KeySize + ValueSize)), floored at 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keySize"/> or <paramref name="valueSize"/> is not positive.</exception>
    public static int MaxInternalKeys(int keySize, int valueSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keySize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueSize);
        return Math.Max(0, (UsableBodySize - valueSize) / (keySize + valueSize));
    }

    /// <summary>
    /// Maximum children (fan-out) of an internal node with the given declared
    /// widths: <see cref="MaxInternalKeys(int, int)"/> + 1.
    /// </summary>
    public static int MaxInternalChildren(int keySize, int valueSize) =>
        MaxInternalKeys(keySize, valueSize) + 1;

    /// <summary>
    /// The declared widths for a registered IndexKind (spec Section 6.1):
    /// key bytes, leaf value bytes, and internal child record bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="indexKind"/> has no fixed registry layout (e.g. FTS, reserved, experimental).</exception>
    public static (byte KeySize, ushort LeafValueSize, ushort ChildRecordSize) GetLayout(BTreeIndexKind indexKind) => indexKind switch
    {
        BTreeIndexKind.PrimaryEmail => (PrimaryEmailKeySize, PrimaryEmailLeafValueSize, PrimaryEmailChildRecordSize),
        BTreeIndexKind.BlockLocation => (BlockLocationKeySize, BlockLocationLeafValueSize, BlockLocationChildRecordSize),
        BTreeIndexKind.Date => (DateKeySize, DateLeafValueSize, DateChildRecordSize),
        _ => throw new ArgumentOutOfRangeException(
            nameof(indexKind), indexKind,
            "Only PrimaryEmail (0), BlockLocation (1), and Date (2) have fixed registry layouts (spec Section 6.1)."),
    };

    /// <summary>Maximum leaf entries for a registered IndexKind at the target node size.</summary>
    public static int MaxLeafEntries(BTreeIndexKind indexKind)
    {
        var (keySize, leafValueSize, _) = GetLayout(indexKind);
        return MaxLeafEntries(keySize, leafValueSize);
    }

    /// <summary>Maximum routing keys for a registered IndexKind at the target node size.</summary>
    public static int MaxInternalKeys(BTreeIndexKind indexKind)
    {
        var (keySize, _, childRecordSize) = GetLayout(indexKind);
        return MaxInternalKeys(keySize, childRecordSize);
    }

    /// <summary>Maximum children (fan-out) for a registered IndexKind at the target node size.</summary>
    public static int MaxInternalChildren(BTreeIndexKind indexKind) =>
        MaxInternalKeys(indexKind) + 1;
}
