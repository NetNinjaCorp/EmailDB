using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// The Date secondary index (EmailDB_FileFormat_Spec.md Section 7,
/// docs/BTree_Index.md Section 7, IndexKind 2): a persistent copy-on-write
/// B+-tree that serves time-range queries without scanning email pages. It is a
/// typed facade over the generic BlockId-addressed <see cref="CowBTree"/> that
/// fixes the shape — a 24-byte composite key <c>DateTicks (8) ‖ BlockId (16)</c>
/// and an EMPTY (0-byte) value — and gives the raw byte tree the semantic
/// add/remove/range-query of a date index.
///
/// <para><b>Composite key.</b> The 8-byte <c>DateTicks</c> prefix is written
/// big-endian so unsigned-lexicographic key order (the order the tree compares
/// keys in) equals chronological order. The 16-byte <c>BlockId</c> suffix makes
/// every key unique, so two emails sharing a timestamp simply get two distinct
/// adjacent keys — duplicate timestamps need no overflow handling — and the
/// value carries no payload (the key IS the data). Ticks must be non-negative
/// (as <see cref="DateTime.Ticks"/> always is); a negative tick would sort after
/// every positive one under unsigned comparison and is rejected.</para>
///
/// <para><b>Range query.</b> <see cref="RangeQuery"/> seeks to
/// <c>(fromTicks, 0…0)</c> and iterates until it passes <c>(toTicks, max)</c>
/// (BTree_Index.md Section 7), returning exactly the entries whose timestamp is
/// in <c>[fromTicks, toTicks]</c> in ascending order — an O(log n + result)
/// scan through <see cref="CowBTree.Scan"/>, never a page scan. It composes as a
/// pre-filter with other search phases: the returned BlockIds narrow the
/// candidate set the remaining phases refine.</para>
///
/// <para><b>State &amp; Checkpoint.</b> Like <see cref="BlockLocationIndex"/>,
/// each <see cref="Add"/>/<see cref="Remove"/> is copy-on-write: it rewrites only
/// the touched root-to-leaf path as new node blocks and advances <see cref="Root"/>
/// to the new version; the previous <see cref="Root"/> stays a fully readable
/// snapshot. Because the index is BlockId-addressed its 16-byte root BlockId is
/// registered in the Checkpoint's generic secondary-index table under IndexKind 2
/// (<see cref="ToCheckpointSecondaryIndex"/>), from which the open path recovers
/// the index — the Checkpoint is authoritative (BTree_Index.md Section 6).
/// Durability is the caller's concern: node appends are buffered and made durable
/// by <see cref="BlockManager.Flush"/> at the Checkpoint commit point.</para>
///
/// Not thread-safe: mutations advance <see cref="Root"/> in place. Concurrent
/// readers should snapshot <see cref="Root"/> and read through <see cref="Scan"/>.
/// </summary>
public sealed class DateIndex
{
    /// <summary>Bytes of the composite key holding the timestamp (big-endian ticks).</summary>
    public const int DateTicksSize = 8;

    /// <summary>Bytes of the composite key holding the BlockId (a 16-byte ULID).</summary>
    public const byte BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>Composite key width: DateTicks (8) ‖ BlockId (16) = 24 (spec Section 7).</summary>
    public const byte CompositeKeySize = DateTicksSize + BlockIdSize;

    private readonly CowBTree _tree;

    /// <summary>
    /// Creates a DateIndex over the block layer, constructing the underlying
    /// BlockId-addressed <see cref="CowBTree"/> with the fixed IndexKind-2 shape
    /// (24-byte key, empty value) so it cannot be misconfigured.
    /// </summary>
    /// <param name="store">Node persistence with Merkle verification; its BlockId resolver resolves this index's own child nodes (spec Section 7 precedence chain).</param>
    /// <param name="initialRoot">The committed root at open (recovered from the Checkpoint secondary table), or null for an empty index.</param>
    /// <param name="maxLeafEntries">Test hook to force splits cheaply; defaults to the spec capacity for the 24→0 shape (~166 entries/leaf at 4 KiB).</param>
    /// <param name="maxInternalKeys">Test hook to force splits cheaply; defaults to the spec capacity.</param>
    public DateIndex(
        BTreeNodeStore store,
        BTreeRoot? initialRoot = null,
        int? maxLeafEntries = null,
        int? maxInternalKeys = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _tree = new CowBTree(
            store,
            BTreeIndexKind.Date,
            keySize: CompositeKeySize,
            leafValueSize: BTreeNodeCapacity.DateLeafValueSize,
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

    /// <summary>The underlying BlockId-addressed tree (IndexKind 2).</summary>
    public CowBTree Tree => _tree;

    /// <summary>Live entries in the current committed version (0 when empty) — one per indexed email.</summary>
    public long Count => Root?.EntryCount ?? 0;

    // ------------------------------------------------------------- Mutations

    /// <summary>
    /// Copy-on-write insert of one email's timestamp: adds the composite key
    /// <c>DateTicks ‖ BlockId</c> (empty value) and advances <see cref="Root"/>
    /// on success (the previous version is untouched). Adding the same
    /// (ticks, blockId) twice is idempotent — the key already exists, so the
    /// entry count is unchanged. A failed result leaves <see cref="Root"/>
    /// unchanged (partially written nodes are orphans for compaction).
    /// </summary>
    /// <param name="dateTicks">The email's timestamp in ticks; must be non-negative.</param>
    /// <param name="blockId">The EmailContent block's ULID as exactly <see cref="BlockIdSize"/> raw bytes.</param>
    /// <exception cref="ArgumentException">The BlockId width is wrong.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dateTicks"/> is negative.</exception>
    public Result Add(long dateTicks, ReadOnlySpan<byte> blockId)
    {
        Span<byte> key = stackalloc byte[CompositeKeySize];
        EncodeKey(dateTicks, blockId, key);

        var inserted = _tree.Insert(Root, key, ReadOnlySpan<byte>.Empty);
        if (inserted.IsFailure)
            return Result.Failure(inserted.Error);
        Root = inserted.Value;
        return Result.Success();
    }

    /// <summary>
    /// Copy-on-write delete of one email's timestamp entry. Advances
    /// <see cref="Root"/> on success; removing an absent (ticks, blockId) is a
    /// successful no-op that leaves <see cref="Root"/> unchanged. Because the
    /// BlockId is part of the key, deleting one email at a shared timestamp
    /// leaves the other emails at that timestamp intact.
    /// </summary>
    /// <param name="dateTicks">The email's timestamp in ticks; must be non-negative.</param>
    /// <param name="blockId">The EmailContent block's ULID as exactly <see cref="BlockIdSize"/> raw bytes.</param>
    /// <returns>Success with true when the entry existed and was removed; success with false when it was absent.</returns>
    /// <exception cref="ArgumentException">The BlockId width is wrong.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dateTicks"/> is negative.</exception>
    public Result<bool> Remove(long dateTicks, ReadOnlySpan<byte> blockId)
    {
        Span<byte> key = stackalloc byte[CompositeKeySize];
        EncodeKey(dateTicks, blockId, key);

        if (Root is null)
            return Result<bool>.Success(false);

        var deleted = _tree.Delete(Root, key);
        if (deleted.IsFailure)
            return Result<bool>.Failure(deleted.Error);
        Root = deleted.Value.Root;
        return Result<bool>.Success(deleted.Value.Removed);
    }

    // ------------------------------------------------------------- Range query

    /// <summary>One indexed email: the timestamp and the EmailContent block's ULID.</summary>
    /// <param name="DateTicks">The email's timestamp in ticks.</param>
    /// <param name="BlockId">The EmailContent block's 16-byte ULID.</param>
    public readonly record struct DateEntry(long DateTicks, byte[] BlockId);

    /// <summary>
    /// Time-range query (BTree_Index.md Section 7): returns every indexed email
    /// whose timestamp is in the INCLUSIVE range <c>[fromTicksInclusive,
    /// toTicksInclusive]</c>, ascending by (ticks, blockId). Duplicate
    /// timestamps all appear, distinguished by their BlockId suffix. The scan
    /// seeks straight to the first in-range leaf and stops at the first
    /// out-of-range entry — no email pages are scanned. A range containing no
    /// entries yields an empty list (a SUCCESS, not a failure); an inverted
    /// range (from &gt; to) is a failure.
    /// </summary>
    /// <param name="fromTicksInclusive">Lower bound in ticks, inclusive; must be non-negative.</param>
    /// <param name="toTicksInclusive">Upper bound in ticks, inclusive; must be non-negative and ≥ <paramref name="fromTicksInclusive"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either bound is negative.</exception>
    public Result<IReadOnlyList<DateEntry>> RangeQuery(long fromTicksInclusive, long toTicksInclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromTicksInclusive);
        ArgumentOutOfRangeException.ThrowIfNegative(toTicksInclusive);
        if (fromTicksInclusive > toTicksInclusive)
            return Result<IReadOnlyList<DateEntry>>.Failure(
                $"Invalid date range: fromTicks ({fromTicksInclusive}) must not exceed toTicks ({toTicksInclusive}).");

        // Seek to (fromTicks, 0…0) inclusive; end exclusive is (toTicks + 1, 0…0)
        // so every BlockId at toTicks is included. When toTicks is long.MaxValue
        // that successor overflows, so the scan runs unbounded to the tree end.
        Span<byte> start = stackalloc byte[CompositeKeySize];
        EncodeKey(fromTicksInclusive, MinBlockId, start);

        CowBTree.RangeScan scan;
        if (toTicksInclusive == long.MaxValue)
        {
            scan = _tree.Scan(Root, start);
        }
        else
        {
            Span<byte> end = stackalloc byte[CompositeKeySize];
            EncodeKey(toTicksInclusive + 1, MinBlockId, end);
            scan = _tree.Scan(Root, start, end);
        }

        var results = new List<DateEntry>();
        while (true)
        {
            var moved = scan.MoveNext();
            if (moved.IsFailure)
                return Result<IReadOnlyList<DateEntry>>.Failure(moved.Error);
            if (!moved.Value)
                break;
            DecodeKey(scan.Current.Key, out long ticks, out byte[] blockId);
            results.Add(new DateEntry(ticks, blockId));
        }
        return Result<IReadOnlyList<DateEntry>>.Success(results);
    }

    /// <summary>
    /// Opens a raw verified ascending scan over the whole index (or a key
    /// sub-range) — the streaming counterpart of <see cref="RangeQuery"/> for
    /// callers that consume entries lazily. Use <see cref="EncodeKey"/> to build
    /// composite bounds.
    /// </summary>
    /// <param name="startInclusive">First composite key to include (24 bytes), or empty for an unbounded start.</param>
    /// <param name="endExclusive">First composite key to EXCLUDE (24 bytes), or empty for an unbounded end.</param>
    public CowBTree.RangeScan Scan(
        ReadOnlySpan<byte> startInclusive = default,
        ReadOnlySpan<byte> endExclusive = default) =>
        _tree.Scan(Root, startInclusive, endExclusive);

    // ------------------------------------------------------------- Checkpoint

    /// <summary>
    /// Builds the Checkpoint secondary-index table entry that registers this
    /// index's current root under IndexKind 2 (spec Sections 6.1, 10.1). The
    /// caller supplies the file offset the root node block was appended at (its
    /// verified hint); the root BlockId comes from <see cref="Root"/>. Recovery
    /// reads this entry back from the Checkpoint to reopen the index — the
    /// Checkpoint is authoritative, so no scan is needed (BTree_Index.md
    /// Section 6).
    /// </summary>
    /// <param name="rootOffset">The file offset the current root node block lives at (non-negative).</param>
    /// <exception cref="InvalidOperationException">The index is empty (no root to register).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rootOffset"/> is negative.</exception>
    public CheckpointSecondaryIndex ToCheckpointSecondaryIndex(long rootOffset)
    {
        if (Root is null)
            throw new InvalidOperationException(
                "An empty DateIndex has no root to register in the Checkpoint secondary table.");
        return CheckpointSecondaryIndex.Create(
            BTreeIndexKind.Date, (byte[])Root.RootRef.Reference.Clone(), rootOffset);
    }

    // ------------------------------------------------------------- Key codec

    /// <summary>The smallest possible BlockId suffix — an all-zero ULID — used as the (fromTicks, 0…0) seek floor.</summary>
    private static ReadOnlySpan<byte> MinBlockId => new byte[BlockIdSize];

    /// <summary>
    /// Packs <c>DateTicks (8, big-endian) ‖ BlockId (16)</c> into a 24-byte
    /// composite key. Big-endian ticks make unsigned key order chronological.
    /// </summary>
    /// <param name="dateTicks">The timestamp in ticks; must be non-negative.</param>
    /// <param name="blockId">Exactly <see cref="BlockIdSize"/> raw bytes.</param>
    /// <param name="key">Destination span of exactly <see cref="CompositeKeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">The BlockId width is wrong.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dateTicks"/> is negative.</exception>
    public static void EncodeKey(long dateTicks, ReadOnlySpan<byte> blockId, Span<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dateTicks);
        if (blockId.Length != BlockIdSize)
            throw new ArgumentException(
                $"BlockId must be exactly {BlockIdSize} bytes, got {blockId.Length}.", nameof(blockId));
        if (key.Length != CompositeKeySize)
            throw new ArgumentException(
                $"Key span must be exactly {CompositeKeySize} bytes, got {key.Length}.", nameof(key));

        BinaryPrimitives.WriteInt64BigEndian(key[..DateTicksSize], dateTicks);
        blockId.CopyTo(key.Slice(DateTicksSize, BlockIdSize));
    }

    /// <summary>Unpacks a 24-byte composite key written by <see cref="EncodeKey"/>.</summary>
    /// <exception cref="ArgumentException">The key width is wrong.</exception>
    public static void DecodeKey(ReadOnlySpan<byte> key, out long dateTicks, out byte[] blockId)
    {
        if (key.Length != CompositeKeySize)
            throw new ArgumentException(
                $"Composite key must be exactly {CompositeKeySize} bytes, got {key.Length}.", nameof(key));
        dateTicks = BinaryPrimitives.ReadInt64BigEndian(key[..DateTicksSize]);
        blockId = key.Slice(DateTicksSize, BlockIdSize).ToArray();
    }
}
