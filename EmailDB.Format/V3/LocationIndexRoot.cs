namespace EmailDB.Format.V3;

/// <summary>
/// In-memory descriptor of one persisted version of the BlockLocationIndex
/// (IndexKind 1) root — the offset-addressed counterpart of <see cref="IndexRoot"/>
/// (EmailDB_FileFormat_Spec.md Sections 7, 10.1, docs/BTree_Index.md Section 3).
///
/// <para><b>Why not an IndexRoot.</b> The BlockLocationIndex <i>is</i> the offset
/// map, so it cannot address its own root by a ULID resolved through itself; its
/// root node is addressed by a raw file offset (spec Section 7). It is therefore
/// NOT described by an <see cref="IndexRoot.RootBlockId"/> (a 16-byte ULID) but by
/// <see cref="RootOffset"/> — the 8-byte file offset the Checkpoint block records
/// in its <c>LocationIndexRoot Offset</c> field (spec Section 10.1, "the
/// Checkpoint layer records the offset-addressed root separately"). Every other
/// field mirrors IndexRoot: <see cref="EntryCount"/>, <see cref="TreeHeight"/>,
/// <see cref="RootHash"/> (BLAKE3-256 the root node verifies against), and a
/// per-index monotonic <see cref="Sequence"/>.</para>
///
/// <para><b>Monotonic Sequence.</b> Each newly emitted root carries
/// <c>Sequence = previous + 1</c>; use <see cref="CreateInitial"/> for the first
/// checkpoint that puts any block into the index and <see cref="NextVersion"/> for
/// each successor so the invariant holds by construction. Sequence is a fallback
/// recovery tiebreaker only — the Checkpoint is authoritative (spec Section 7,
/// BTree_Index.md Section 6).</para>
/// </summary>
public sealed class LocationIndexRoot
{
    /// <summary>The index this root always describes (spec Section 6.1 registry).</summary>
    public const BTreeIndexKind Kind = BTreeIndexKind.BlockLocation;

    /// <summary>Sequence carried by the first emitted version of the index.</summary>
    public const ulong InitialSequence = 0;

    /// <summary>
    /// File offset of the tree's root node — the offset-addressed root pointer
    /// the Checkpoint block records (spec Section 10.1). Non-negative.
    /// </summary>
    public required long RootOffset { get; init; }

    /// <summary>Number of live leaf entries (live blocks) in this tree version (non-negative).</summary>
    public required long EntryCount { get; init; }

    /// <summary>
    /// Tree height: 1 when the root is a leaf, growing by 1 per root split. A
    /// persisted root always references a real root node, so this is at least 1.
    /// </summary>
    public required int TreeHeight { get; init; }

    /// <summary>RootHash: BLAKE3-256 (32 bytes) of the root node's serialized payload.</summary>
    public required byte[] RootHash { get; init; }

    /// <summary>Per-index monotonic sequence number (fallback recovery tiebreaker).</summary>
    public required ulong Sequence { get; init; }

    /// <summary>
    /// Creates the first emitted version of the index, with <see cref="Sequence"/>
    /// = <see cref="InitialSequence"/>. Successive versions are derived with
    /// <see cref="NextVersion"/> so Sequence increments monotonically.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rootOffset"/>, <paramref name="entryCount"/> is negative, or <paramref name="treeHeight"/> is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootHash"/> is not 32 bytes.</exception>
    public static LocationIndexRoot CreateInitial(long rootOffset, long entryCount, int treeHeight, byte[] rootHash)
    {
        Validate(rootOffset, entryCount, treeHeight, rootHash);
        return new LocationIndexRoot
        {
            RootOffset = rootOffset,
            EntryCount = entryCount,
            TreeHeight = treeHeight,
            RootHash = rootHash,
            Sequence = InitialSequence,
        };
    }

    /// <summary>
    /// Derives the next emitted version: a new descriptor with the updated
    /// root/shape/hash and <see cref="Sequence"/> = this one's + 1, so the
    /// monotonic invariant holds by construction (BTree_Index.md Section 6).
    /// </summary>
    /// <exception cref="InvalidOperationException">The Sequence would overflow past <see cref="ulong.MaxValue"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rootOffset"/>, <paramref name="entryCount"/> is negative, or <paramref name="treeHeight"/> is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootHash"/> is not 32 bytes.</exception>
    public LocationIndexRoot NextVersion(long rootOffset, long entryCount, int treeHeight, byte[] rootHash)
    {
        if (Sequence == ulong.MaxValue)
            throw new InvalidOperationException(
                "LocationIndexRoot Sequence has reached ulong.MaxValue and cannot increment further.");
        Validate(rootOffset, entryCount, treeHeight, rootHash);
        return new LocationIndexRoot
        {
            RootOffset = rootOffset,
            EntryCount = entryCount,
            TreeHeight = treeHeight,
            RootHash = rootHash,
            Sequence = Sequence + 1,
        };
    }

    private static void Validate(long rootOffset, long entryCount, int treeHeight, byte[] rootHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rootOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(treeHeight, 1);
        ArgumentNullException.ThrowIfNull(rootHash);
        if (rootHash.Length != IndexRootSerializer.RootHashSize)
            throw new ArgumentException(
                $"{nameof(RootHash)} must be exactly {IndexRootSerializer.RootHashSize} bytes, got {rootHash.Length}.",
                nameof(rootHash));
    }
}
