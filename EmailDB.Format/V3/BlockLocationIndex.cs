using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// The BlockLocationIndex — the indirection table (EmailDB_FileFormat_Spec.md
/// Section 7, IndexKind 1): a persistent copy-on-write B+-tree mapping
/// <c>BlockId (16) → (Offset, Length)</c> for every live block in the file, so
/// ULID-only logical pointers resolve in O(log n) and open never scans the file.
///
/// <para><b>The one structure allowed raw offsets.</b> Every other index
/// addresses its child nodes by ChildBlockId and resolves each through the
/// location precedence chain — but THIS index <i>is</i> that chain's tree, so it
/// cannot depend on itself. Its internal nodes therefore address children by
/// ChildOffset (8-byte little-endian file offset, 40-byte child record) rather
/// than ChildBlockId (spec Sections 6.1, 7). This is a typed facade over the
/// generic offset-addressed <see cref="CowBTree"/> that fixes the shape — 16-byte
/// BlockId key, 16-byte value = Offset (8) ‖ Length (8) — and gives the raw byte
/// tree the semantic Put/Get/Delete of a location table. It is safe to derive
/// from raw offsets because the index is derived data: compaction rebuilds it for
/// the new file and a full scan can always regenerate it.</para>
///
/// <para><b>State.</b> Like <see cref="BTreeRoot"/> handles generally, mutations
/// are copy-on-write: each <see cref="Put"/>/<see cref="PutBatch"/>/<see cref="Delete"/>
/// rewrites only the touched root-to-leaf path as new node blocks and advances
/// <see cref="Root"/> to the new version; the previous <see cref="Root"/> stays a
/// fully readable snapshot. Because the root is offset-addressed it is NOT
/// described by an <see cref="IndexRoot.RootBlockId"/> (a 16-byte ULID) — the
/// Checkpoint layer records the offset-addressed root separately. Durability is
/// the caller's concern: node appends are buffered and made durable by
/// <see cref="BlockManager.Flush"/> at the Checkpoint commit point.</para>
///
/// <para><b>Resolution.</b> Implements <see cref="IBlockIdResolver"/> so it slots
/// into the precedence chain (runtime map → BlockLocationIndex → scan, spec
/// Section 7): <see cref="TryGetLocation"/> returns false for an unknown block —
/// or a read that fails Merkle/I/O verification — so the caller falls through to
/// the next resolver (ultimately the disaster-only full scan). Detailed callers
/// that must distinguish "absent" from "corrupt" use the <see cref="Result{T}"/>
/// -returning <see cref="Lookup"/> instead.</para>
///
/// Not thread-safe: mutations advance <see cref="Root"/> in place. Concurrent
/// readers should snapshot <see cref="Root"/> and read through <see cref="Lookup"/>.
/// </summary>
public sealed class BlockLocationIndex : IBlockIdResolver
{
    /// <summary>BlockId key width: a 16-byte ULID (spec Section 7).</summary>
    public const byte BlockIdKeySize = UlidGenerator.UlidSize;

    /// <summary>Bytes of the leaf value holding the file offset (little-endian).</summary>
    public const int OffsetSize = 8;

    /// <summary>Bytes of the leaf value holding the block length (little-endian).</summary>
    public const int LengthSize = 8;

    /// <summary>Leaf value width: Offset (8) ‖ Length (8) = 16 (spec Section 7).</summary>
    public const ushort LocationValueSize = OffsetSize + LengthSize;

    private readonly CowBTree _tree;

    /// <summary>
    /// Creates a BlockLocationIndex over the block layer, constructing the
    /// underlying offset-addressed <see cref="CowBTree"/> with the fixed
    /// IndexKind-1 shape so it cannot be misconfigured.
    /// </summary>
    /// <param name="store">Node persistence with Merkle verification. Offset-addressed reads need no BlockId resolver.</param>
    /// <param name="initialRoot">The committed root at open, or null for an empty index.</param>
    /// <param name="maxLeafEntries">Test hook to force splits cheaply; defaults to the spec capacity for the 16→16 shape (~124 entries/leaf at 4 KiB).</param>
    /// <param name="maxInternalKeys">Test hook to force splits cheaply; defaults to the spec capacity.</param>
    public BlockLocationIndex(
        BTreeNodeStore store,
        BTreeRoot? initialRoot = null,
        int? maxLeafEntries = null,
        int? maxInternalKeys = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _tree = new CowBTree(
            store,
            BTreeIndexKind.BlockLocation,
            keySize: BlockIdKeySize,
            leafValueSize: LocationValueSize,
            maxLeafEntries: maxLeafEntries,
            maxInternalKeys: maxInternalKeys);
        Root = initialRoot;
    }

    /// <summary>
    /// The current committed tree version; null for an empty index. Advances on
    /// every successful mutation; each value is an immutable snapshot the
    /// Checkpoint layer can persist and readers can hold.
    /// </summary>
    public BTreeRoot? Root { get; private set; }

    /// <summary>The underlying offset-addressed tree (IndexKind 1, ChildOffset internal records).</summary>
    public CowBTree Tree => _tree;

    /// <summary>Live entries in the current committed version (0 when empty).</summary>
    public long Count => Root?.EntryCount ?? 0;

    // ------------------------------------------------------------- Mutations

    /// <summary>
    /// Copy-on-write upsert of one block's location. Rewrites the touched
    /// root-to-leaf path and advances <see cref="Root"/> on success (the previous
    /// version is untouched); an existing BlockId has its (Offset, Length)
    /// replaced. A failed result leaves <see cref="Root"/> unchanged (any
    /// partially written nodes are orphans for compaction).
    /// </summary>
    /// <param name="blockId">ULID as exactly <see cref="BlockIdKeySize"/> raw bytes (big-endian binary layout).</param>
    /// <param name="offset">The block's file offset (non-negative).</param>
    /// <param name="length">The block's total on-disk length (non-negative).</param>
    /// <exception cref="ArgumentException">The BlockId width is wrong.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Offset or length is negative.</exception>
    public Result Put(ReadOnlySpan<byte> blockId, long offset, long length)
    {
        ValidateBlockId(blockId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Span<byte> value = stackalloc byte[LocationValueSize];
        EncodeValue(offset, length, value);

        var inserted = _tree.Insert(Root, blockId, value);
        if (inserted.IsFailure)
            return Result.Failure(inserted.Error);
        Root = inserted.Value;
        return Result.Success();
    }

    /// <summary>Convenience overload of <see cref="Put(ReadOnlySpan{byte}, long, long)"/> from a <see cref="BlockLocation"/>.</summary>
    public Result Put(BlockLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return Put(location.BlockId, location.Offset, location.TotalBlockLength);
    }

    /// <summary>
    /// Copy-on-write batch upsert — the checkpoint-time insert path (spec
    /// Section 7: "entries for blocks appended since the last Checkpoint are
    /// batch-inserted"). Applies every location in ONE ascending-key COW pass
    /// (sorting internally by unsigned-lexicographic BlockId so shared leaves are
    /// rewritten once, not once per entry), advancing <see cref="Root"/> only if
    /// every insert succeeds; on any failure <see cref="Root"/> is left at its
    /// pre-batch version and the partial nodes are orphans. An empty batch is a
    /// successful no-op.
    /// </summary>
    /// <param name="locations">The blocks to insert; duplicates resolve last-wins within the batch.</param>
    public Result PutBatch(IEnumerable<BlockLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);

        // Collapse duplicates (last wins) and sort by key so the COW pass touches
        // each shared leaf once — the batch-amortized write amplification the
        // spec's per-Checkpoint delta relies on.
        var ordered = new SortedDictionary<byte[], BlockLocation>(UnsignedByteComparer.Instance);
        foreach (var location in locations)
        {
            ArgumentNullException.ThrowIfNull(location);
            ValidateBlockId(location.BlockId);
            ArgumentOutOfRangeException.ThrowIfNegative(location.Offset);
            ArgumentOutOfRangeException.ThrowIfNegative(location.TotalBlockLength);
            ordered[(byte[])location.BlockId.Clone()] = location;
        }
        if (ordered.Count == 0)
            return Result.Success();

        var working = Root;
        Span<byte> value = stackalloc byte[LocationValueSize];
        foreach (var (key, location) in ordered)
        {
            EncodeValue(location.Offset, location.TotalBlockLength, value);
            var inserted = _tree.Insert(working, key, value);
            if (inserted.IsFailure)
                return Result.Failure(inserted.Error);
            working = inserted.Value;
        }

        Root = working;
        return Result.Success();
    }

    /// <summary>
    /// Copy-on-write delete of one block's location — compaction drops an entry
    /// when a block is no longer live. Advances <see cref="Root"/> on success;
    /// deleting an absent BlockId is a successful no-op that leaves
    /// <see cref="Root"/> unchanged.
    /// </summary>
    /// <param name="blockId">ULID as exactly <see cref="BlockIdKeySize"/> raw bytes.</param>
    /// <returns>Success with true when the block existed and was removed; success with false when it was absent.</returns>
    /// <exception cref="ArgumentException">The BlockId width is wrong.</exception>
    public Result<bool> Delete(ReadOnlySpan<byte> blockId)
    {
        ValidateBlockId(blockId);
        if (Root is null)
            return Result<bool>.Success(false);

        var deleted = _tree.Delete(Root, blockId);
        if (deleted.IsFailure)
            return Result<bool>.Failure(deleted.Error);
        Root = deleted.Value.Root;
        return Result<bool>.Success(deleted.Value.Removed);
    }

    // --------------------------------------------------------------- Lookup

    /// <summary>Result of a <see cref="Lookup"/>: whether the block is indexed and, if so, its location.</summary>
    /// <param name="Found">True when the BlockId is in the searched version.</param>
    /// <param name="Offset">The block's file offset when found; 0 otherwise.</param>
    /// <param name="Length">The block's total on-disk length when found; 0 otherwise.</param>
    public readonly record struct LocationLookup(bool Found, long Offset, long Length);

    /// <summary>
    /// Verified O(log n) point lookup in the current committed version (upper
    /// levels served from the node store's cache). A missing BlockId is a
    /// SUCCESSFUL result with <c>Found == false</c>; failure means a traversed
    /// node failed Merkle verification or I/O (spec Section 13).
    /// </summary>
    /// <param name="blockId">ULID as exactly <see cref="BlockIdKeySize"/> raw bytes.</param>
    /// <exception cref="ArgumentException">The BlockId width is wrong.</exception>
    public Result<LocationLookup> Lookup(ReadOnlySpan<byte> blockId)
    {
        ValidateBlockId(blockId);
        if (Root is null)
            return Result<LocationLookup>.Success(new LocationLookup(false, 0, 0));

        var found = _tree.TryGet(Root, blockId);
        if (found.IsFailure)
            return Result<LocationLookup>.Failure(found.Error);
        if (!found.Value.Found)
            return Result<LocationLookup>.Success(new LocationLookup(false, 0, 0));

        DecodeValue(found.Value.Value!, out long offset, out long length);
        return Result<LocationLookup>.Success(new LocationLookup(true, offset, length));
    }

    /// <summary>
    /// <see cref="IBlockIdResolver"/> link in the resolution precedence chain
    /// (runtime map → BlockLocationIndex → scan, spec Section 7). Returns false
    /// when the block is not indexed OR when the lookup fails verification/I/O —
    /// in both cases the caller falls through to the next resolver (ultimately the
    /// disaster-only full scan). Detailed callers that must tell "absent" from
    /// "corrupt" use <see cref="Lookup"/>, which surfaces the error.
    /// </summary>
    /// <param name="blockId">ULID as 16 raw bytes, big-endian binary layout.</param>
    /// <param name="location">The block's location when found; null otherwise.</param>
    public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
    {
        var lookup = Lookup(blockId);
        if (lookup.IsFailure || !lookup.Value.Found)
        {
            location = null;
            return false;
        }
        location = new BlockLocation
        {
            BlockId = blockId.ToArray(),
            Offset = lookup.Value.Offset,
            TotalBlockLength = lookup.Value.Length,
        };
        return true;
    }

    // ------------------------------------------------------- Value codec

    /// <summary>Packs (Offset, Length) into the 16-byte leaf value: Offset (8 LE) ‖ Length (8 LE).</summary>
    private static void EncodeValue(long offset, long length, Span<byte> value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(value[..OffsetSize], offset);
        BinaryPrimitives.WriteInt64LittleEndian(value.Slice(OffsetSize, LengthSize), length);
    }

    /// <summary>Unpacks the 16-byte leaf value written by <see cref="EncodeValue"/>.</summary>
    private static void DecodeValue(ReadOnlySpan<byte> value, out long offset, out long length)
    {
        offset = BinaryPrimitives.ReadInt64LittleEndian(value[..OffsetSize]);
        length = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(OffsetSize, LengthSize));
    }

    private static void ValidateBlockId(ReadOnlySpan<byte> blockId)
    {
        if (blockId.Length != BlockIdKeySize)
            throw new ArgumentException(
                $"BlockId must be exactly {BlockIdKeySize} bytes, got {blockId.Length}.",
                nameof(blockId));
    }

    /// <summary>Unsigned-lexicographic byte-array ordering — the tree's own key order.</summary>
    private sealed class UnsignedByteComparer : IComparer<byte[]>
    {
        public static readonly UnsignedByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y.AsSpan());
        }
    }
}
