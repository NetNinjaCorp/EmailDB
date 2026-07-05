namespace EmailDB.Format.V3;

/// <summary>
/// How a B+-tree's internal child records address child nodes
/// (EmailDB_FileFormat_Spec.md Section 6.1).
/// </summary>
public enum BTreeChildAddressing : byte
{
    /// <summary>
    /// ChildBlockId (16 bytes, ULID): the logical pointer used by every index
    /// except BlockLocation. Reading a child resolves the BlockId to a file
    /// offset through the location precedence chain (spec Section 7), which
    /// is what makes these trees compaction-safe.
    /// </summary>
    BlockId = 0,

    /// <summary>
    /// ChildOffset (8 bytes, little-endian file offset): used only by the
    /// BlockLocationIndex (IndexKind 1), which IS the offset map and so
    /// cannot depend on itself (spec Section 7).
    /// </summary>
    Offset = 1,
}

/// <summary>
/// A verified reference to one B+-tree node: the child reference bytes
/// (ChildBlockId or ChildOffset per the tree's <see cref="BTreeChildAddressing"/>)
/// plus the node's Merkle hash (ChildHash = BLAKE3-256 of the node's full
/// serialized payload, spec Section 6). An internal child record is exactly
/// <c>Reference ‖ NodeHash</c>; <see cref="ToChildRecord"/> and
/// <see cref="FromChildRecord"/> convert between the two.
///
/// The reference to the ROOT node travels the same way, paired with
/// IndexRoot.RootHash — see <see cref="BTreeRoot"/>.
/// </summary>
public sealed class BTreeNodeRef
{
    /// <summary>Reference width when children are addressed by BlockId (ULID).</summary>
    public const int BlockIdReferenceSize = UlidGenerator.UlidSize;

    /// <summary>Reference width when children are addressed by raw file offset.</summary>
    public const int OffsetReferenceSize = 8;

    /// <summary>How <see cref="Reference"/> addresses the node.</summary>
    public required BTreeChildAddressing Addressing { get; init; }

    /// <summary>
    /// The node's address: a 16-byte BlockId (ULID, big-endian binary layout)
    /// or an 8-byte little-endian file offset, per <see cref="Addressing"/>.
    /// </summary>
    public required byte[] Reference { get; init; }

    /// <summary>
    /// The node's Merkle hash: BLAKE3-256 over its full serialized payload
    /// (<see cref="BTreeNodeSerializer.ComputeNodeContentHash(ReadOnlySpan{byte})"/>).
    /// Embedded in the parent's child record as ChildHash; readers MUST verify
    /// the node against it (spec Section 6).
    /// </summary>
    public required byte[] NodeHash { get; init; }

    /// <summary>Reference width in bytes for the given addressing mode.</summary>
    public static int GetReferenceSize(BTreeChildAddressing addressing) =>
        addressing == BTreeChildAddressing.Offset ? OffsetReferenceSize : BlockIdReferenceSize;

    /// <summary>
    /// Child record width (reference + 32-byte ChildHash) for the given
    /// addressing mode: 48 for BlockId addressing, 40 for offset addressing —
    /// matching the spec Section 6.1 registry.
    /// </summary>
    public static int GetChildRecordSize(BTreeChildAddressing addressing) =>
        GetReferenceSize(addressing) + BTreeNodeSerializer.NodeContentHashSize;

    /// <summary>
    /// Packs this reference as an internal child record:
    /// <c>Reference ‖ NodeHash</c> (spec Section 6.1).
    /// </summary>
    public byte[] ToChildRecord()
    {
        var record = new byte[Reference.Length + NodeHash.Length];
        Reference.CopyTo(record, 0);
        NodeHash.CopyTo(record.AsSpan(Reference.Length));
        return record;
    }

    /// <summary>
    /// Unpacks an internal child record read from a node. Fails (rather than
    /// throwing) when the record width does not match the addressing mode —
    /// on-disk data is never trusted (spec Section 4).
    /// </summary>
    /// <param name="addressing">The owning tree's child addressing mode.</param>
    /// <param name="childRecord">The raw child record bytes from an internal node.</param>
    public static Result<BTreeNodeRef> FromChildRecord(
        BTreeChildAddressing addressing, ReadOnlySpan<byte> childRecord)
    {
        int referenceSize = GetReferenceSize(addressing);
        int expectedSize = referenceSize + BTreeNodeSerializer.NodeContentHashSize;
        if (childRecord.Length != expectedSize)
            return Result<BTreeNodeRef>.Failure(
                $"Child record must be exactly {expectedSize} bytes for {addressing} addressing " +
                $"({referenceSize}-byte reference + {BTreeNodeSerializer.NodeContentHashSize}-byte ChildHash), " +
                $"got {childRecord.Length} (corrupt node or wrong index layout).");

        return Result<BTreeNodeRef>.Success(new BTreeNodeRef
        {
            Addressing = addressing,
            Reference = childRecord[..referenceSize].ToArray(),
            NodeHash = childRecord[referenceSize..].ToArray(),
        });
    }
}
