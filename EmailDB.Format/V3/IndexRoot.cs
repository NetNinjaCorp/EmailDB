namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of an IndexRoot descriptor (EmailDB_FileFormat_Spec.md
/// Section 6, docs/BTree_Index.md Sections 4, 6): the 68-byte payload of an
/// IndexRoot block (BlockType 6) that names one persisted version of a B+-tree
/// index — which index (<see cref="IndexKind"/>), where its root node lives
/// (<see cref="RootBlockId"/>), the tree's shape (<see cref="EntryCount"/>,
/// <see cref="TreeHeight"/>), the Merkle root (<see cref="RootHash"/>), and a
/// per-index monotonic <see cref="Sequence"/>.
///
/// Layout (68 bytes, all multi-byte integers little-endian, spec Section 4):
///
///   IndexKind (2) + RootBlockId (16) + EntryCount (8) + TreeHeight (2) +
///   RootHash (32) + Sequence (8)
///
/// This is the persistable counterpart of the in-memory <see cref="BTreeRoot"/>
/// handle (RootRef + Height + EntryCount + RootHash) with the index identity and
/// the recovery Sequence added. Byte serialization is
/// <see cref="IndexRootSerializer"/>.
///
/// <para><b>Monotonic Sequence.</b> Each newly persisted root for a given index
/// carries <c>Sequence = previous + 1</c>: a flush writes the new nodes, fsyncs,
/// then writes the IndexRoot with Sequence + 1 (BTree_Index.md Section 4). Use
/// <see cref="CreateInitial"/> for the first version and <see cref="NextVersion"/>
/// to derive each successor so the invariant holds by construction. Sequence is
/// a fallback recovery tiebreaker only — the Checkpoint is authoritative
/// (BTree_Index.md Section 6).</para>
/// </summary>
public sealed class IndexRoot
{
    /// <summary>Sequence carried by the first persisted version of an index.</summary>
    public const ulong InitialSequence = 0;

    /// <summary>Which index this root describes (registry in spec Section 6.1).</summary>
    public required BTreeIndexKind IndexKind { get; init; }

    /// <summary>
    /// The 16-byte BlockId (ULID) of the tree's root node — the block
    /// IndexRoot.RootHash verifies against.
    /// </summary>
    public required byte[] RootBlockId { get; init; }

    /// <summary>Number of live leaf entries in this tree version (non-negative).</summary>
    public required long EntryCount { get; init; }

    /// <summary>
    /// Tree height: 1 when the root is a leaf, growing by 1 per root split. A
    /// persisted root always references a real root node, so this is at least 1.
    /// Serialized as a 16-bit little-endian integer.
    /// </summary>
    public required int TreeHeight { get; init; }

    /// <summary>RootHash: BLAKE3-256 (32 bytes) of the root node's serialized payload.</summary>
    public required byte[] RootHash { get; init; }

    /// <summary>Per-index monotonic sequence number (fallback recovery tiebreaker).</summary>
    public required ulong Sequence { get; init; }

    /// <summary>
    /// Creates the first persisted version of an index, with
    /// <see cref="Sequence"/> = <see cref="InitialSequence"/>. Successive
    /// versions are derived with <see cref="NextVersion"/> so Sequence increments
    /// monotonically per index.
    /// </summary>
    public static IndexRoot CreateInitial(
        BTreeIndexKind indexKind, byte[] rootBlockId, long entryCount, int treeHeight, byte[] rootHash) =>
        new()
        {
            IndexKind = indexKind,
            RootBlockId = rootBlockId,
            EntryCount = entryCount,
            TreeHeight = treeHeight,
            RootHash = rootHash,
            Sequence = InitialSequence,
        };

    /// <summary>
    /// Derives the next persisted version of THIS index: a new descriptor with
    /// the updated root/shape/hash and <see cref="Sequence"/> = this one's + 1,
    /// keeping <see cref="IndexKind"/> unchanged so the monotonic-per-index
    /// invariant holds by construction (BTree_Index.md Section 4).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The Sequence would overflow past <see cref="ulong.MaxValue"/>.
    /// </exception>
    public IndexRoot NextVersion(byte[] rootBlockId, long entryCount, int treeHeight, byte[] rootHash)
    {
        if (Sequence == ulong.MaxValue)
            throw new InvalidOperationException(
                "IndexRoot Sequence has reached ulong.MaxValue and cannot increment further.");

        return new IndexRoot
        {
            IndexKind = IndexKind,
            RootBlockId = rootBlockId,
            EntryCount = entryCount,
            TreeHeight = treeHeight,
            RootHash = rootHash,
            Sequence = Sequence + 1,
        };
    }
}
