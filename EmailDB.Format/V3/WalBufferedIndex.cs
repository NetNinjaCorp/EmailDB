namespace EmailDB.Format.V3;

/// <summary>
/// Persists a flushed <see cref="IndexRoot"/> descriptor (BlockType 6). Behind
/// an interface so the flush layer's failure contract can be exercised without a
/// real disk fault: the default <see cref="BlockManagerIndexRootStore"/> appends
/// to the v3 block stream, and a failing implementation stands in for a torn
/// IndexRoot write in tests.
/// </summary>
public interface IIndexRootStore
{
    /// <summary>
    /// Appends the given IndexRoot as an IndexRoot block, returning failure
    /// (never throwing) on an I/O error so the flush can leave the previous
    /// root authoritative (docs/BTree_Index.md Section 6).
    /// </summary>
    Result WriteIndexRoot(IndexRoot indexRoot);
}

/// <summary>
/// Default <see cref="IIndexRootStore"/>: serializes the 68-byte IndexRoot
/// payload and appends it as a <see cref="BlockType.IndexRoot"/> block through
/// the <see cref="BlockManager"/>.
/// </summary>
public sealed class BlockManagerIndexRootStore(BlockManager blockManager) : IIndexRootStore
{
    private readonly BlockManager _blockManager =
        blockManager ?? throw new ArgumentNullException(nameof(blockManager));

    /// <inheritdoc/>
    public Result WriteIndexRoot(IndexRoot indexRoot)
    {
        ArgumentNullException.ThrowIfNull(indexRoot);
        var payload = IndexRootSerializer.Serialize(indexRoot);
        var appended = _blockManager.Append(BlockType.IndexRoot, PayloadEncoding.Custom, payload);
        return appended.IsSuccess
            ? Result.Success()
            : Result.Failure($"IndexRoot block write failed: {appended.Error}");
    }
}

/// <summary>
/// WAL-buffered batch flush for one B+-tree index (docs/BTree_Index.md
/// Section 4, EmailDB_FileFormat_Spec.md Section 6). Upserts and deletes
/// accumulate in an in-memory buffer; a flush sorts the buffered entries by key,
/// applies them to the tree in one copy-on-write pass, fsyncs the new nodes,
/// then writes a fresh <see cref="IndexRoot"/> with the next monotonic
/// <see cref="IndexRoot.Sequence"/>. Batching amortizes write amplification (a
/// batch touching a handful of leaves rewrites a handful of nodes, not one
/// root-to-leaf path per entry).
///
/// <para><b>Flush triggers.</b> A flush fires when the buffer reaches
/// <see cref="CountThreshold"/> distinct keys (default: one leaf's worth), when
/// <see cref="FlushInterval"/> has elapsed since the first entry of the current
/// batch (evaluated on each mutation and on <see cref="FlushIfDue"/>, using an
/// injectable <see cref="TimeProvider"/> so tests need no wall-clock sleeps), or
/// on an explicit <see cref="Flush"/>.</para>
///
/// <para><b>Failure contract (harmless flush).</b> A flush is applied against a
/// working copy of the committed root; nothing is committed until every node is
/// written, fsynced, and the IndexRoot is persisted. If any node write, the
/// fsync, or the IndexRoot write fails, the method returns a failure with the
/// PREVIOUS committed root and IndexRoot untouched (still authoritative and
/// fully readable) and the buffer retained so the batch can be re-applied — any
/// partially written nodes are unreferenced orphans left for compaction to
/// reclaim, never a torn tree (docs/BTree_Index.md Section 6).</para>
///
/// <para><b>Read-your-writes.</b> <see cref="TryGet"/> reflects buffered but
/// unflushed entries (a buffered delete reads as absent), falling back to the
/// committed tree — the write path's own reads see its pending mutations.</para>
///
/// Scope: BlockId-addressed indexes (PrimaryEmail, Date), whose 16-byte root
/// BlockId maps directly to <see cref="IndexRoot.RootBlockId"/>. Not thread-safe.
/// </summary>
public sealed class WalBufferedIndex
{
    private readonly CowBTree _tree;
    private readonly IIndexRootStore _indexRootStore;
    private readonly Func<Result> _fsync;
    private readonly TimeProvider _timeProvider;

    // Buffered mutations, last-write-wins per key and kept sorted by unsigned
    // lexicographic key order so a flush applies them in one ascending pass.
    private readonly SortedDictionary<byte[], PendingEntry> _buffer =
        new(UnsignedLexicographicComparer.Instance);

    private BTreeRoot? _committedRoot;
    private IndexRoot? _committedIndexRoot;
    private DateTimeOffset? _batchStarted;

    /// <summary>
    /// Creates a flush buffer over an index.
    /// </summary>
    /// <param name="tree">The COW B+-tree to apply flushes to (must be BlockId-addressed).</param>
    /// <param name="indexRootStore">Persists the flushed IndexRoot descriptor.</param>
    /// <param name="fsync">Makes the freshly written nodes durable before the IndexRoot is written (typically <see cref="BlockManager.Flush"/>).</param>
    /// <param name="initialRoot">The committed root at open (null for an empty index).</param>
    /// <param name="initialIndexRoot">The committed IndexRoot at open (null before the index has ever been flushed).</param>
    /// <param name="countThreshold">Distinct buffered keys that trigger a flush; defaults to one leaf's worth (<see cref="CowBTree.MaxLeafEntries"/>).</param>
    /// <param name="flushInterval">Time since the batch's first entry that triggers a flush; null disables the time trigger.</param>
    /// <param name="timeProvider">Clock for the time trigger; defaults to <see cref="TimeProvider.System"/>.</param>
    public WalBufferedIndex(
        CowBTree tree,
        IIndexRootStore indexRootStore,
        Func<Result> fsync,
        BTreeRoot? initialRoot = null,
        IndexRoot? initialIndexRoot = null,
        int? countThreshold = null,
        TimeSpan? flushInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(indexRootStore);
        ArgumentNullException.ThrowIfNull(fsync);
        if (tree.Addressing != BTreeChildAddressing.BlockId)
            throw new ArgumentException(
                "WalBufferedIndex supports only BlockId-addressed indexes (the root's 16-byte " +
                "BlockId is the IndexRoot.RootBlockId); the BlockLocation index is offset-addressed.",
                nameof(tree));

        int count = countThreshold ?? tree.MaxLeafEntries;
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof(countThreshold));
        if (flushInterval is { } interval)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval.Ticks, nameof(flushInterval));

        _tree = tree;
        _indexRootStore = indexRootStore;
        _fsync = fsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        CountThreshold = count;
        FlushInterval = flushInterval;
        _committedRoot = initialRoot;
        _committedIndexRoot = initialIndexRoot;
    }

    /// <summary>Distinct buffered keys that trigger an automatic flush.</summary>
    public int CountThreshold { get; }

    /// <summary>Elapsed-time trigger measured from the batch's first entry, or null when disabled.</summary>
    public TimeSpan? FlushInterval { get; }

    /// <summary>The current committed tree version; null for an empty index.</summary>
    public BTreeRoot? CommittedRoot => _committedRoot;

    /// <summary>The last persisted IndexRoot descriptor; null before the first flush.</summary>
    public IndexRoot? CommittedIndexRoot => _committedIndexRoot;

    /// <summary>Number of distinct keys currently buffered (unflushed).</summary>
    public int PendingCount => _buffer.Count;

    /// <summary>The last persisted Sequence for this index, or null before the first flush.</summary>
    public ulong? Sequence => _committedIndexRoot?.Sequence;

    /// <summary>
    /// Buffers an upsert of <paramref name="key"/> → <paramref name="value"/>
    /// (last write wins if the key is re-buffered before the next flush), then
    /// flushes if a trigger fired. Returns the auto-flush result — a failed
    /// flush leaves the previous root authoritative and the buffer retained.
    /// </summary>
    /// <param name="key">Exactly <see cref="CowBTree.KeySize"/> bytes.</param>
    /// <param name="value">Exactly <see cref="CowBTree.LeafValueSize"/> bytes (empty for key-only indexes).</param>
    /// <exception cref="ArgumentException">The key or value width is wrong.</exception>
    public Result Upsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ValidateKeyWidth(key);
        if (value.Length != _tree.LeafValueSize)
            throw new ArgumentException(
                $"Value must be exactly {_tree.LeafValueSize} bytes for IndexKind {_tree.IndexKind}, got {value.Length}.",
                nameof(value));

        Buffer(key, PendingEntry.ForUpsert(value.ToArray()));
        return FlushIfTriggered();
    }

    /// <summary>
    /// Buffers a delete of <paramref name="key"/> (last write wins), then
    /// flushes if a trigger fired. A key deleted then re-upserted before the
    /// flush collapses to the final state. Returns the auto-flush result.
    /// </summary>
    /// <param name="key">Exactly <see cref="CowBTree.KeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key width is wrong.</exception>
    public Result Delete(ReadOnlySpan<byte> key)
    {
        ValidateKeyWidth(key);
        Buffer(key, PendingEntry.ForDelete());
        return FlushIfTriggered();
    }

    private void Buffer(ReadOnlySpan<byte> key, PendingEntry entry)
    {
        _buffer[key.ToArray()] = entry;
        _batchStarted ??= _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Verified point lookup with read-your-writes: a buffered upsert returns
    /// its pending value, a buffered delete reads as absent, and an unbuffered
    /// key falls back to the committed tree version. A missing key is a
    /// SUCCESSFUL result with <c>Found == false</c> (spec Section 13).
    /// </summary>
    /// <param name="key">Exactly <see cref="CowBTree.KeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key width is wrong.</exception>
    public Result<CowBTree.BTreeLookup> TryGet(ReadOnlySpan<byte> key)
    {
        ValidateKeyWidth(key);
        if (_buffer.TryGetValue(key.ToArray(), out var pending))
            return Result<CowBTree.BTreeLookup>.Success(pending.IsDelete
                ? new CowBTree.BTreeLookup(false, null)
                : new CowBTree.BTreeLookup(true, (byte[])pending.Value!.Clone()));

        if (_committedRoot is null)
            return Result<CowBTree.BTreeLookup>.Success(new CowBTree.BTreeLookup(false, null));
        return _tree.TryGet(_committedRoot, key);
    }

    /// <summary>
    /// True when a trigger currently demands a flush: the buffer is non-empty
    /// and either reached <see cref="CountThreshold"/> distinct keys or
    /// <see cref="FlushInterval"/> has elapsed since the batch's first entry.
    /// </summary>
    public bool IsFlushDue()
    {
        if (_buffer.Count == 0)
            return false;
        if (_buffer.Count >= CountThreshold)
            return true;
        return FlushInterval is { } interval
            && _batchStarted is { } started
            && _timeProvider.GetUtcNow() - started >= interval;
    }

    /// <summary>
    /// Flushes if (and only if) the time or count trigger is due — the hook a
    /// timer or idle caller polls to honor the time threshold without adding
    /// data. A no-op returning success when nothing is due.
    /// </summary>
    public Result FlushIfDue() => IsFlushDue() ? Flush() : Result.Success();

    private Result FlushIfTriggered() => IsFlushDue() ? Flush() : Result.Success();

    /// <summary>
    /// Flushes the buffer: applies every buffered mutation in ascending key
    /// order in one COW pass, fsyncs the new nodes, then persists a fresh
    /// IndexRoot with <see cref="IndexRoot.Sequence"/> incremented by one. On
    /// ANY failure (node write, fsync, or IndexRoot write) the committed root
    /// and IndexRoot are left untouched and authoritative and the buffer is
    /// retained; the partially written nodes are orphans for compaction. An
    /// empty buffer is a successful no-op.
    /// </summary>
    public Result Flush()
    {
        if (_buffer.Count == 0)
            return Result.Success();

        // Apply the sorted batch against a WORKING root; the committed root is
        // untouched until the whole flush succeeds. SortedDictionary iterates in
        // ascending key order, so this is the single sorted COW pass.
        var working = _committedRoot;
        foreach (var (key, entry) in _buffer)
        {
            if (entry.IsDelete)
            {
                if (working is null)
                    continue; // deleting from an empty tree: nothing to do
                var deleted = _tree.Delete(working, key);
                if (deleted.IsFailure)
                    return FailFlush("node write", deleted.Error);
                working = deleted.Value.Root;
            }
            else
            {
                var inserted = _tree.Insert(working, key, entry.Value);
                if (inserted.IsFailure)
                    return FailFlush("node write", inserted.Error);
                working = inserted.Value;
            }
        }

        // Make the freshly written nodes durable before the IndexRoot that
        // references them (spec write order: nodes → fsync → IndexRoot).
        var fsynced = _fsync();
        if (fsynced.IsFailure)
            return FailFlush("fsync", fsynced.Error);

        if (working is null)
        {
            // The batch emptied the index. There is no root node to describe, so
            // no IndexRoot is written; the empty state is committed in memory and
            // the persisted IndexRoot (if any) keeps its Sequence.
            _committedRoot = null;
            _buffer.Clear();
            _batchStarted = null;
            return Result.Success();
        }

        // Derive the next IndexRoot so Sequence increments monotonically per
        // index by construction (CreateInitial for the first flush).
        var rootBlockId = working.RootRef.Reference;
        var candidate = _committedIndexRoot is null
            ? IndexRoot.CreateInitial(
                _tree.IndexKind, rootBlockId, working.EntryCount, working.Height, working.RootHash)
            : _committedIndexRoot.NextVersion(
                rootBlockId, working.EntryCount, working.Height, working.RootHash);

        var persisted = _indexRootStore.WriteIndexRoot(candidate);
        if (persisted.IsFailure)
            return FailFlush("IndexRoot write", persisted.Error);

        // Commit: only now do the new root and IndexRoot become authoritative.
        _committedRoot = working;
        _committedIndexRoot = candidate;
        _buffer.Clear();
        _batchStarted = null;
        return Result.Success();
    }

    /// <summary>
    /// Reports a flush failure without mutating committed state or the buffer —
    /// the previous root stays authoritative and the batch is re-appliable; the
    /// nodes written before the failure are orphans (docs/BTree_Index.md Section 6).
    /// </summary>
    private static Result FailFlush(string phase, string error) =>
        Result.Failure(
            $"Index flush aborted during {phase}: {error} " +
            "Previous IndexRoot remains authoritative; buffered entries retained; " +
            "partially written nodes are orphans for compaction (BTree_Index.md Section 6).");

    private void ValidateKeyWidth(ReadOnlySpan<byte> key)
    {
        if (key.Length != _tree.KeySize)
            throw new ArgumentException(
                $"Key must be exactly {_tree.KeySize} bytes for IndexKind {_tree.IndexKind}, got {key.Length}.",
                nameof(key));
    }

    /// <summary>One buffered mutation: an upsert carrying a value, or a delete.</summary>
    private readonly struct PendingEntry
    {
        private PendingEntry(bool isDelete, byte[]? value)
        {
            IsDelete = isDelete;
            Value = value;
        }

        public bool IsDelete { get; }
        public byte[]? Value { get; }

        public static PendingEntry ForUpsert(byte[] value) => new(false, value);
        public static PendingEntry ForDelete() => new(true, null);
    }

    /// <summary>
    /// Orders keys by the file-wide unsigned lexicographic byte order the tree
    /// itself uses, so the buffer's iteration order is the tree's key order.
    /// </summary>
    private sealed class UnsignedLexicographicComparer : IComparer<byte[]>
    {
        public static readonly UnsignedLexicographicComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;
            return x.AsSpan().SequenceCompareTo(y.AsSpan());
        }
    }
}
