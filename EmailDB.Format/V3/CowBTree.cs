namespace EmailDB.Format.V3;

/// <summary>
/// Copy-on-write B+-tree over the generic v3 node format
/// (EmailDB_FileFormat_Spec.md Section 6, docs/BTree_Index.md Sections 1, 4).
/// One tree instance serves one index (IndexKind + declared key/value widths);
/// tree versions are identified by <see cref="BTreeRoot"/> handles, which this
/// class treats as immutable snapshots:
///
/// - A mutation rewrites ONLY the root-to-leaf path it touches, appending new
///   node blocks through <see cref="BTreeNodeStore"/>; unchanged subtrees are
///   shared between the old and new versions (CouchDB model). Old roots stay
///   fully readable — nothing is ever overwritten.
/// - Leaf overflow splits the leaf and promotes the right half's first key as
///   the parent separator; internal overflow moves its middle key up. Splits
///   propagate upward; a root split creates a new root with one key and grows
///   the height by exactly 1.
/// - Every traversed node is Merkle-verified against its parent's ChildHash
///   (RootHash for the root) by the node store — mandatory path verification
///   (BTree_Index.md Section 5).
///
/// Argument errors (wrong key/value widths, null root) throw — they are
/// programming errors, matching <see cref="BTreeNodeSerializer"/>; I/O errors
/// and on-disk corruption return failed <see cref="Result{T}"/>s (spec
/// Section 13). Durability is the caller's concern: node appends are buffered
/// and made durable by <see cref="BlockManager.Flush"/> at commit points
/// (BTree_Index.md Section 4).
///
/// Deletion is COW the same way: the touched path is rewritten, a node that
/// falls below minimum occupancy borrows from or merges with a sibling —
/// leaves AND internal nodes (internal rebalancing rotates separators through
/// the parent) — and a root left with a single child collapses, shrinking the
/// height by exactly 1 (BTree_Index.md Section 4). Range scans iterate in
/// sorted key order WITHOUT sibling pointers (BTree_Index.md Section 1):
/// leaf-to-leaf advancement backtracks through the in-memory parent stack to
/// the nearest ancestor with an unvisited child and descends to its leftmost
/// leaf, so a sibling split never cascades rewrites into neighbors.
/// </summary>
public sealed class CowBTree
{
    private readonly BTreeNodeStore _store;

    /// <summary>
    /// Creates a tree over one index. Capacity overrides exist for tests
    /// (small fan-outs force splits cheaply); production callers use the spec
    /// defaults computed from <see cref="BTreeNodeCapacity"/> at the 4096-byte
    /// target node size.
    /// </summary>
    /// <param name="store">Node persistence with Merkle verification.</param>
    /// <param name="indexKind">Which index this tree is (spec Section 6.1). BlockLocation gets offset-addressed children; every other kind is BlockId-addressed.</param>
    /// <param name="keySize">Bytes per key (at least 1).</param>
    /// <param name="leafValueSize">Bytes per leaf value (0 for key-only indexes such as Date).</param>
    /// <param name="maxLeafEntries">Max entries per leaf before it splits; defaults to the spec capacity for the widths.</param>
    /// <param name="maxInternalKeys">Max routing keys per internal node before it splits; defaults to the spec capacity.</param>
    public CowBTree(
        BTreeNodeStore store,
        BTreeIndexKind indexKind,
        byte keySize,
        ushort leafValueSize,
        int? maxLeafEntries = null,
        int? maxInternalKeys = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfZero(keySize);

        _store = store;
        IndexKind = indexKind;
        KeySize = keySize;
        LeafValueSize = leafValueSize;
        Addressing = indexKind == BTreeIndexKind.BlockLocation
            ? BTreeChildAddressing.Offset
            : BTreeChildAddressing.BlockId;
        ChildRecordSize = (ushort)BTreeNodeRef.GetChildRecordSize(Addressing);

        MaxLeafEntries = maxLeafEntries ?? BTreeNodeCapacity.MaxLeafEntries(keySize, leafValueSize);
        MaxInternalKeys = maxInternalKeys ?? BTreeNodeCapacity.MaxInternalKeys(keySize, ChildRecordSize);
        // A split must produce two nodes that each hold at least one key, and
        // EntryCount must fit in the header's 16 bits after the transient
        // overflow entry is split away.
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLeafEntries, 2, nameof(maxLeafEntries));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxLeafEntries, ushort.MaxValue - 1, nameof(maxLeafEntries));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxInternalKeys, 2, nameof(maxInternalKeys));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxInternalKeys, ushort.MaxValue - 1, nameof(maxInternalKeys));
    }

    /// <summary>Which index this tree serves (spec Section 6.1).</summary>
    public BTreeIndexKind IndexKind { get; }

    /// <summary>Bytes per key.</summary>
    public byte KeySize { get; }

    /// <summary>Bytes per leaf value (0 for key-only indexes).</summary>
    public ushort LeafValueSize { get; }

    /// <summary>How internal child records address children (spec Section 6.1).</summary>
    public BTreeChildAddressing Addressing { get; }

    /// <summary>Bytes per internal child record: reference + 32-byte ChildHash.</summary>
    public ushort ChildRecordSize { get; }

    /// <summary>Max entries per leaf before it splits.</summary>
    public int MaxLeafEntries { get; }

    /// <summary>Max routing keys per internal node before it splits (fan-out − 1).</summary>
    public int MaxInternalKeys { get; }

    /// <summary>
    /// Minimum entries a NON-ROOT leaf may hold; a delete that drops a leaf
    /// below this triggers borrow/merge (BTree_Index.md Section 4). The root
    /// leaf is exempt — it may hold as little as 1 entry.
    /// </summary>
    public int MinLeafEntries => MaxLeafEntries / 2;

    /// <summary>
    /// Minimum routing keys a NON-ROOT internal node may hold; below this it
    /// borrows from or merges with a sibling. The root is exempt — it needs
    /// only 1 key, and at 0 keys it collapses into its single child.
    /// </summary>
    public int MinInternalKeys => MaxInternalKeys / 2;

    // ---------------------------------------------------------------- Insert

    /// <summary>
    /// COW upsert of one key: rewrites the root-to-leaf path as new node
    /// blocks and returns the NEW root handle; <paramref name="root"/> (null
    /// for an empty tree) is untouched and remains a readable snapshot.
    /// An existing key has its value replaced (EntryCount unchanged); a new
    /// key grows EntryCount by 1. Leaf/internal overflow splits with key
    /// promotion; a root split grows <see cref="BTreeRoot.Height"/> by 1.
    /// </summary>
    /// <param name="root">The tree version to mutate, or null to start an empty tree.</param>
    /// <param name="key">Exactly <see cref="KeySize"/> bytes.</param>
    /// <param name="value">Exactly <see cref="LeafValueSize"/> bytes (empty when 0).</param>
    /// <exception cref="ArgumentException">The key or value width is wrong.</exception>
    public Result<BTreeRoot> Insert(BTreeRoot? root, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ValidateKeyWidth(key);
        ValidateValueWidth(value);
        var keyBytes = key.ToArray();
        var valueBytes = value.ToArray();

        if (root is null)
        {
            var firstLeaf = NewLeaf();
            firstLeaf.Entries.Add(new BTreeLeafEntry(keyBytes, valueBytes));
            var firstWrite = WriteLeaf(firstLeaf);
            if (firstWrite.IsFailure)
                return Result<BTreeRoot>.Failure(firstWrite.Error);
            return Result<BTreeRoot>.Success(new BTreeRoot
            {
                RootRef = firstWrite.Value,
                Height = 1,
                EntryCount = 1,
            });
        }

        var outcome = InsertDescend(root.RootRef, root.Height, keyBytes, valueBytes);
        if (outcome.IsFailure)
            return Result<BTreeRoot>.Failure(outcome.Error);

        var result = outcome.Value;
        long newEntryCount = root.EntryCount + (result.AddedNewKey ? 1 : 0);

        if (result.SplitRight is null)
            return Result<BTreeRoot>.Success(new BTreeRoot
            {
                RootRef = result.Node,
                Height = root.Height,
                EntryCount = newEntryCount,
            });

        // Root split: a new root with one promoted key and two children grows
        // the tree by exactly one level (BTree_Index.md Section 4).
        var newRoot = NewInternal();
        newRoot.Keys.Add(result.SeparatorKey!);
        newRoot.Children.Add(result.Node.ToChildRecord());
        newRoot.Children.Add(result.SplitRight.ToChildRecord());
        var rootWrite = WriteInternal(newRoot);
        if (rootWrite.IsFailure)
            return Result<BTreeRoot>.Failure(rootWrite.Error);

        return Result<BTreeRoot>.Success(new BTreeRoot
        {
            RootRef = rootWrite.Value,
            Height = root.Height + 1,
            EntryCount = newEntryCount,
        });
    }

    /// <summary>
    /// Result of a COW insert into one subtree: the rewritten node, plus — when
    /// the node overflowed and split — the promoted separator key and the new
    /// right sibling for the parent to absorb.
    /// </summary>
    private sealed class InsertOutcome
    {
        /// <summary>True when a new key was added (false for a value replacement).</summary>
        public required bool AddedNewKey { get; init; }

        /// <summary>The rewritten node taking the original child's position.</summary>
        public required BTreeNodeRef Node { get; init; }

        /// <summary>The new right sibling when the node split; null otherwise.</summary>
        public BTreeNodeRef? SplitRight { get; init; }

        /// <summary>The separator key promoted to the parent when the node split.</summary>
        public byte[]? SeparatorKey { get; init; }
    }

    /// <summary>
    /// Recursive COW descent: verifies and loads the node at
    /// <paramref name="nodeRef"/>, applies the insert to the child path, and
    /// rewrites this node (splitting on overflow). Height counts down to 1 at
    /// the leaves.
    /// </summary>
    private Result<InsertOutcome> InsertDescend(BTreeNodeRef nodeRef, int height, byte[] key, byte[] value)
    {
        if (height <= 1)
            return InsertIntoLeaf(nodeRef, key, value);

        var loaded = ReadInternal(nodeRef);
        if (loaded.IsFailure)
            return Result<InsertOutcome>.Failure(loaded.Error);
        var node = loaded.Value;

        int childIndex = RouteChildIndex(node.Keys, key);
        var childRef = BTreeNodeRef.FromChildRecord(Addressing, node.Children[childIndex]);
        if (childRef.IsFailure)
            return Result<InsertOutcome>.Failure(childRef.Error);

        var subOutcome = InsertDescend(childRef.Value, height - 1, key, value);
        if (subOutcome.IsFailure)
            return subOutcome;
        var sub = subOutcome.Value;

        // COW path rewrite: the descended child was rewritten, so this node's
        // child record changes and this node is rewritten too. All OTHER child
        // records are copied verbatim — those subtrees are shared.
        node.Children[childIndex] = sub.Node.ToChildRecord();
        if (sub.SplitRight is not null)
        {
            // The child split: absorb the promoted separator and the new right
            // sibling immediately to the right of the split child.
            node.Keys.Insert(childIndex, sub.SeparatorKey!);
            node.Children.Insert(childIndex + 1, sub.SplitRight.ToChildRecord());
        }

        if (node.Keys.Count <= MaxInternalKeys)
        {
            var write = WriteInternal(node);
            if (write.IsFailure)
                return Result<InsertOutcome>.Failure(write.Error);
            return Result<InsertOutcome>.Success(new InsertOutcome
            {
                AddedNewKey = sub.AddedNewKey,
                Node = write.Value,
            });
        }

        // Internal overflow: split around the middle key, which moves UP to
        // the parent (unlike a leaf split, it is not kept in either half).
        int keyCount = node.Keys.Count;
        int middle = keyCount / 2;
        var promotedKey = node.Keys[middle];

        var left = NewInternal();
        left.Keys.AddRange(node.Keys.Take(middle));
        left.Children.AddRange(node.Children.Take(middle + 1));

        var right = NewInternal();
        right.Keys.AddRange(node.Keys.Skip(middle + 1));
        right.Children.AddRange(node.Children.Skip(middle + 1));

        var leftWrite = WriteInternal(left);
        if (leftWrite.IsFailure)
            return Result<InsertOutcome>.Failure(leftWrite.Error);
        var rightWrite = WriteInternal(right);
        if (rightWrite.IsFailure)
            return Result<InsertOutcome>.Failure(rightWrite.Error);

        return Result<InsertOutcome>.Success(new InsertOutcome
        {
            AddedNewKey = sub.AddedNewKey,
            Node = leftWrite.Value,
            SplitRight = rightWrite.Value,
            SeparatorKey = promotedKey,
        });
    }

    /// <summary>Leaf-level COW upsert, splitting on overflow.</summary>
    private Result<InsertOutcome> InsertIntoLeaf(BTreeNodeRef nodeRef, byte[] key, byte[] value)
    {
        var loaded = ReadLeaf(nodeRef);
        if (loaded.IsFailure)
            return Result<InsertOutcome>.Failure(loaded.Error);
        var leaf = loaded.Value;

        int position = LowerBound(leaf.Entries, key, out bool found);
        bool added = !found;
        if (found)
            leaf.Entries[position] = new BTreeLeafEntry(key, value);
        else
            leaf.Entries.Insert(position, new BTreeLeafEntry(key, value));

        if (leaf.Entries.Count <= MaxLeafEntries)
        {
            var write = WriteLeaf(leaf);
            if (write.IsFailure)
                return Result<InsertOutcome>.Failure(write.Error);
            return Result<InsertOutcome>.Success(new InsertOutcome
            {
                AddedNewKey = added,
                Node = write.Value,
            });
        }

        // Leaf overflow: split at the midpoint. B+-tree leaf split — the
        // separator promoted to the parent is a COPY of the right half's first
        // key; both halves keep all their entries.
        int entryCount = leaf.Entries.Count;
        int middle = entryCount / 2;

        var left = NewLeaf();
        left.Entries.AddRange(leaf.Entries.Take(middle));
        var right = NewLeaf();
        right.Entries.AddRange(leaf.Entries.Skip(middle));
        var separator = (byte[])right.Entries[0].Key.Clone();

        var leftWrite = WriteLeaf(left);
        if (leftWrite.IsFailure)
            return Result<InsertOutcome>.Failure(leftWrite.Error);
        var rightWrite = WriteLeaf(right);
        if (rightWrite.IsFailure)
            return Result<InsertOutcome>.Failure(rightWrite.Error);

        return Result<InsertOutcome>.Success(new InsertOutcome
        {
            AddedNewKey = added,
            Node = leftWrite.Value,
            SplitRight = rightWrite.Value,
            SeparatorKey = separator,
        });
    }

    // ----------------------------------------------------------- Batch insert

    /// <summary>
    /// One-pass COW bulk upsert of a whole sorted batch (BTree_Index.md
    /// Section 4: "sort buffered entries by key → apply to the tree in one
    /// pass"). Unlike looping <see cref="Insert"/> — which rewrites a full
    /// root-to-leaf path per entry — this descends the tree ONCE: entries are
    /// partitioned by target subtree at each level, every touched leaf and
    /// internal node on the shared spine is rewritten exactly once, and splits
    /// propagate up a single time. The result is semantically identical to
    /// applying the entries one-by-one (same key→value mapping, last value wins
    /// on a duplicate/existing key); only the node layout differs — a permitted
    /// bulk-load difference (BTree_Index.md Section 4) — so a batch of 100
    /// inserts touching ~10 leaves writes ~15 node blocks, not ~400.
    ///
    /// <paramref name="entries"/> MUST be sorted strictly ascending by key with
    /// no duplicates (the caller collapses duplicates last-wins before sorting).
    /// Every key/value width is validated. Occupancy invariants hold exactly as
    /// for single inserts: touched leaves and internal nodes never fall below
    /// minimum occupancy (they only grow), and every produced node stays within
    /// [min, max] except a lone root, which is exempt. A node-write failure
    /// returns a failed result and leaves <paramref name="root"/> untouched —
    /// the half-written nodes are orphans for compaction, exactly like
    /// <see cref="Insert"/>.
    /// </summary>
    /// <param name="root">The tree version to mutate, or null to bulk-build a fresh tree.</param>
    /// <param name="entries">The batch, sorted strictly ascending by key, each key/value exactly the declared width.</param>
    /// <exception cref="ArgumentException">A key or value width is wrong, or the batch is not strictly ascending.</exception>
    public Result<BTreeRoot> InsertBatch(BTreeRoot? root, IReadOnlyList<BTreeLeafEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            if (root is not null)
                return Result<BTreeRoot>.Success(root);
            throw new ArgumentException("InsertBatch requires a non-empty batch when the tree is empty.", nameof(entries));
        }

        for (int i = 0; i < entries.Count; i++)
        {
            ValidateKeyWidth(entries[i].Key);
            ValidateValueWidth(entries[i].Value);
            if (i > 0 && entries[i - 1].Key.AsSpan().SequenceCompareTo(entries[i].Key) >= 0)
                throw new ArgumentException("InsertBatch requires entries sorted strictly ascending with no duplicate keys.", nameof(entries));
        }

        // Empty tree: bulk-build the leaf row from every entry, then build the
        // internal spine up to a single root.
        if (root is null)
        {
            var leaves = SplitLeafRun(entries, 0, entries.Count);
            if (leaves.IsFailure)
                return Result<BTreeRoot>.Failure(leaves.Error);
            var built = BuildUp(leaves.Value.Nodes, leaves.Value.Separators, childHeight: 1);
            if (built.IsFailure)
                return Result<BTreeRoot>.Failure(built.Error);
            return Result<BTreeRoot>.Success(new BTreeRoot
            {
                RootRef = built.Value.Node,
                Height = built.Value.Height,
                EntryCount = entries.Count,
            });
        }

        var applied = ApplyBatch(root.RootRef, root.Height, entries, 0, entries.Count);
        if (applied.IsFailure)
            return Result<BTreeRoot>.Failure(applied.Error);

        var top = BuildUp(applied.Value.Nodes, applied.Value.Separators, childHeight: root.Height);
        if (top.IsFailure)
            return Result<BTreeRoot>.Failure(top.Error);
        return Result<BTreeRoot>.Success(new BTreeRoot
        {
            RootRef = top.Value.Node,
            Height = top.Value.Height,
            EntryCount = root.EntryCount + applied.Value.AddedKeys,
        });
    }

    /// <summary>
    /// The replacement nodes a batch produced for one subtree position: one or
    /// more sibling nodes in key order (more than one when the subtree split),
    /// the <see cref="Separators"/> between them (count = Nodes.Count − 1), and
    /// how many genuinely new keys the batch added under this subtree.
    /// </summary>
    private sealed class BatchNodes
    {
        public required long AddedKeys { get; init; }
        public required List<BTreeNodeRef> Nodes { get; init; }
        public required List<byte[]> Separators { get; init; }
    }

    /// <summary>The single node a <see cref="BuildUp"/> collapsed a level into, and the resulting tree height.</summary>
    private readonly record struct BuiltRoot(BTreeNodeRef Node, int Height);

    /// <summary>
    /// Recursive one-pass COW descent for a batch: applies the slice
    /// <c>entries[lo, hi)</c> — all of which route into the subtree at
    /// <paramref name="nodeRef"/> — rewriting this node once and returning its
    /// replacement sibling(s). Height counts down to 1 at the leaves.
    /// </summary>
    private Result<BatchNodes> ApplyBatch(
        BTreeNodeRef nodeRef, int height, IReadOnlyList<BTreeLeafEntry> entries, int lo, int hi)
    {
        if (height <= 1)
        {
            var loaded = ReadLeaf(nodeRef);
            if (loaded.IsFailure)
                return Result<BatchNodes>.Failure(loaded.Error);

            var merged = MergeLeaf(loaded.Value.Entries, entries, lo, hi, out long addedKeys);
            var split = SplitLeafRun(merged, 0, merged.Count);
            if (split.IsFailure)
                return split;
            return Result<BatchNodes>.Success(new BatchNodes
            {
                AddedKeys = addedKeys,
                Nodes = split.Value.Nodes,
                Separators = split.Value.Separators,
            });
        }

        var loadedNode = ReadInternal(nodeRef);
        if (loadedNode.IsFailure)
            return Result<BatchNodes>.Failure(loadedNode.Error);
        var node = loadedNode.Value;

        // Rebuild this node's children/keys in one pass. Entries are sorted and
        // routing is monotonic, so each child owns a contiguous entry range;
        // children with no entries are copied verbatim (shared subtrees).
        var childrenOut = new List<byte[]>(node.Children.Count);
        var keysOut = new List<byte[]>(node.Keys.Count);
        long added = 0;
        int cursor = lo;
        for (int childIndex = 0; childIndex < node.Children.Count; childIndex++)
        {
            if (childIndex > 0)
                keysOut.Add(node.Keys[childIndex - 1]);

            // The routed slice for this child ends at the first entry that routes
            // to a later child (i.e. is >= this child's right separator).
            int sliceEnd = cursor;
            if (childIndex < node.Keys.Count)
            {
                var separator = node.Keys[childIndex];
                while (sliceEnd < hi && entries[sliceEnd].Key.AsSpan().SequenceCompareTo(separator) < 0)
                    sliceEnd++;
            }
            else
            {
                sliceEnd = hi; // rightmost child absorbs everything left
            }

            if (sliceEnd == cursor)
            {
                childrenOut.Add(node.Children[childIndex]); // untouched — shared subtree
                continue;
            }

            var childRef = BTreeNodeRef.FromChildRecord(Addressing, node.Children[childIndex]);
            if (childRef.IsFailure)
                return Result<BatchNodes>.Failure(childRef.Error);

            var sub = ApplyBatch(childRef.Value, height - 1, entries, cursor, sliceEnd);
            if (sub.IsFailure)
                return sub;
            added += sub.Value.AddedKeys;

            for (int n = 0; n < sub.Value.Nodes.Count; n++)
            {
                if (n > 0)
                    keysOut.Add(sub.Value.Separators[n - 1]);
                childrenOut.Add(sub.Value.Nodes[n].ToChildRecord());
            }
            cursor = sliceEnd;
        }

        var splitInternal = SplitInternalRun(childrenOut, keysOut);
        if (splitInternal.IsFailure)
            return splitInternal;
        return Result<BatchNodes>.Success(new BatchNodes
        {
            AddedKeys = added,
            Nodes = splitInternal.Value.Nodes,
            Separators = splitInternal.Value.Separators,
        });
    }

    /// <summary>
    /// Merges a leaf's existing entries with the sorted batch slice
    /// <c>entries[lo, hi)</c> into one strictly-ascending list: on a key that
    /// already exists the batch value wins (upsert, not counted); a brand-new
    /// key is inserted and counted in <paramref name="addedKeys"/>.
    /// </summary>
    private static List<BTreeLeafEntry> MergeLeaf(
        List<BTreeLeafEntry> existing, IReadOnlyList<BTreeLeafEntry> entries, int lo, int hi, out long addedKeys)
    {
        var merged = new List<BTreeLeafEntry>(existing.Count + (hi - lo));
        addedKeys = 0;
        int a = 0, b = lo;
        while (a < existing.Count && b < hi)
        {
            int cmp = existing[a].Key.AsSpan().SequenceCompareTo(entries[b].Key);
            if (cmp < 0)
                merged.Add(existing[a++]);
            else if (cmp > 0)
            {
                merged.Add(entries[b++]);
                addedKeys++;
            }
            else
            {
                merged.Add(entries[b]); // upsert: batch value replaces the existing one
                a++;
                b++;
            }
        }
        while (a < existing.Count)
            merged.Add(existing[a++]);
        while (b < hi)
        {
            merged.Add(entries[b++]);
            addedKeys++;
        }
        return merged;
    }

    /// <summary>
    /// Writes a run of leaf entries as one or more leaves, each within
    /// [<see cref="MinLeafEntries"/>, <see cref="MaxLeafEntries"/>] (a single
    /// leaf may hold fewer only when it is the whole, soon-to-be-root tree). An
    /// over-capacity run is split into evenly sized leaves — even distribution
    /// guarantees every piece stays at or above the minimum — with each right
    /// sibling's first key promoted as its separator (B+-tree leaf split).
    /// </summary>
    private Result<BatchNodes> SplitLeafRun(IReadOnlyList<BTreeLeafEntry> entries, int lo, int hi)
    {
        int total = hi - lo;
        var nodes = new List<BTreeNodeRef>();
        var separators = new List<byte[]>();

        int pieces = (total + MaxLeafEntries - 1) / MaxLeafEntries;
        if (pieces < 1)
            pieces = 1;
        int baseSize = total / pieces;
        int remainder = total % pieces;

        int start = lo;
        for (int p = 0; p < pieces; p++)
        {
            int size = baseSize + (p < remainder ? 1 : 0);
            var leaf = NewLeaf();
            for (int i = 0; i < size; i++)
                leaf.Entries.Add(entries[start + i]);
            if (p > 0)
                separators.Add((byte[])entries[start].Key.Clone());

            var write = WriteLeaf(leaf);
            if (write.IsFailure)
                return Result<BatchNodes>.Failure(write.Error);
            nodes.Add(write.Value);
            start += size;
        }

        return Result<BatchNodes>.Success(new BatchNodes { AddedKeys = 0, Nodes = nodes, Separators = separators });
    }

    /// <summary>
    /// Writes a run of child records (with their in-between routing keys) as one
    /// or more internal nodes, each within [<see cref="MinInternalKeys"/>,
    /// <see cref="MaxInternalKeys"/>] (a single node may hold fewer keys only
    /// when it is the root). An over-capacity run is split into evenly sized
    /// internal nodes — even distribution keeps every piece at or above the
    /// minimum — with the routing key straddling each split boundary promoted
    /// UP as the separator (internal split moves the middle key up, unlike a
    /// leaf split which copies it).
    /// </summary>
    private Result<BatchNodes> SplitInternalRun(List<byte[]> childRecords, List<byte[]> keys)
    {
        int childCount = childRecords.Count;
        int maxChildren = MaxInternalKeys + 1;
        var nodes = new List<BTreeNodeRef>();
        var separators = new List<byte[]>();

        int pieces = (childCount + maxChildren - 1) / maxChildren;
        if (pieces < 1)
            pieces = 1;
        int baseSize = childCount / pieces;
        int remainder = childCount % pieces;

        int childStart = 0;
        int keyStart = 0;
        for (int p = 0; p < pieces; p++)
        {
            int size = baseSize + (p < remainder ? 1 : 0); // children in this piece
            if (p > 0)
            {
                // The routing key immediately before this piece is promoted to
                // the parent — it is consumed by neither sibling.
                separators.Add(keys[keyStart]);
                keyStart++;
            }

            var internalNode = NewInternal();
            for (int i = 0; i < size; i++)
                internalNode.Children.Add(childRecords[childStart + i]);
            for (int i = 0; i < size - 1; i++)
                internalNode.Keys.Add(keys[keyStart + i]);

            var write = WriteInternal(internalNode);
            if (write.IsFailure)
                return Result<BatchNodes>.Failure(write.Error);
            nodes.Add(write.Value);
            childStart += size;
            keyStart += size - 1;
        }

        return Result<BatchNodes>.Success(new BatchNodes { AddedKeys = 0, Nodes = nodes, Separators = separators });
    }

    /// <summary>
    /// Builds the internal spine above a produced level: while the level has
    /// more than one node it is grouped into a parent level of internal nodes
    /// (splitting where a level exceeds the fan-out, promoting straddling
    /// separators), growing the height by one each round, until a single root
    /// remains. A one-node level is already the root at <paramref name="childHeight"/>.
    /// </summary>
    private Result<BuiltRoot> BuildUp(List<BTreeNodeRef> nodes, List<byte[]> separators, int childHeight)
    {
        int height = childHeight;
        while (nodes.Count > 1)
        {
            var records = new List<byte[]>(nodes.Count);
            foreach (var node in nodes)
                records.Add(node.ToChildRecord());

            var grouped = SplitInternalRun(records, separators);
            if (grouped.IsFailure)
                return Result<BuiltRoot>.Failure(grouped.Error);
            nodes = grouped.Value.Nodes;
            separators = grouped.Value.Separators;
            height++;
        }
        return Result<BuiltRoot>.Success(new BuiltRoot(nodes[0], height));
    }

    // ---------------------------------------------------------------- Delete

    /// <summary>Result of a COW delete.</summary>
    /// <param name="Removed">True when the key existed and was removed; false when it was absent (no blocks were written).</param>
    /// <param name="Root">The new root handle; the input root when nothing was removed; null when the delete emptied the tree.</param>
    public readonly record struct BTreeDelete(bool Removed, BTreeRoot? Root);

    /// <summary>
    /// COW delete of one key: rewrites the root-to-leaf path as new node
    /// blocks and returns the NEW root handle; <paramref name="root"/> is
    /// untouched and remains a readable snapshot. A node that falls below
    /// minimum occupancy borrows an entry from a richer sibling or merges
    /// with one — for internal nodes the separator rotates through (borrow)
    /// or is pulled down from (merge) the parent, so BOTH leaf and internal
    /// levels stay at least half full (BTree_Index.md Section 4). A root left
    /// with a single child collapses, shrinking <see cref="BTreeRoot.Height"/>
    /// by exactly 1; deleting the last entry returns a null root (empty tree,
    /// symmetric with <see cref="Insert"/> accepting a null root).
    /// A missing key is a SUCCESSFUL result with <c>Removed == false</c> and
    /// the original root — nothing is appended.
    /// </summary>
    /// <param name="root">The tree version to mutate.</param>
    /// <param name="key">Exactly <see cref="KeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key width is wrong.</exception>
    public Result<BTreeDelete> Delete(BTreeRoot root, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateKeyWidth(key);
        var keyBytes = key.ToArray();

        var outcome = DeleteDescend(root.RootRef, root.Height, keyBytes);
        if (outcome.IsFailure)
            return Result<BTreeDelete>.Failure(outcome.Error);
        if (!outcome.Value.RemovedKey)
            return Result<BTreeDelete>.Success(new BTreeDelete(false, root));

        long newEntryCount = root.EntryCount - 1;

        if (root.Height == 1)
        {
            var leaf = outcome.Value.Leaf!;
            if (leaf.Entries.Count == 0)
                return Result<BTreeDelete>.Success(new BTreeDelete(true, null));
            var leafWrite = WriteLeaf(leaf);
            if (leafWrite.IsFailure)
                return Result<BTreeDelete>.Failure(leafWrite.Error);
            return Result<BTreeDelete>.Success(new BTreeDelete(true, new BTreeRoot
            {
                RootRef = leafWrite.Value,
                Height = 1,
                EntryCount = newEntryCount,
            }));
        }

        var node = outcome.Value.Internal!;
        if (node.Keys.Count == 0)
        {
            // Single-child root collapse: a merge one level down consumed the
            // root's last separator, so its lone child (already written during
            // rebalancing) becomes the root and the height shrinks by exactly 1
            // (BTree_Index.md Section 4). No new root block is needed.
            var collapsedRef = BTreeNodeRef.FromChildRecord(Addressing, node.Children[0]);
            if (collapsedRef.IsFailure)
                return Result<BTreeDelete>.Failure(collapsedRef.Error);
            return Result<BTreeDelete>.Success(new BTreeDelete(true, new BTreeRoot
            {
                RootRef = collapsedRef.Value,
                Height = root.Height - 1,
                EntryCount = newEntryCount,
            }));
        }

        var rootWrite = WriteInternal(node);
        if (rootWrite.IsFailure)
            return Result<BTreeDelete>.Failure(rootWrite.Error);
        return Result<BTreeDelete>.Success(new BTreeDelete(true, new BTreeRoot
        {
            RootRef = rootWrite.Value,
            Height = root.Height,
            EntryCount = newEntryCount,
        }));
    }

    /// <summary>
    /// Result of a COW delete inside one subtree. Unlike inserts, the
    /// rewritten node comes back IN MEMORY (unwritten): the parent may still
    /// have to mutate it during borrow/merge rebalancing, and only the parent
    /// knows the siblings. The parent writes it exactly once via the
    /// rebalancing step (<see cref="Delete"/> writes the top node itself).
    /// Exactly one of <see cref="Leaf"/>/<see cref="Internal"/> is set when
    /// <see cref="RemovedKey"/> is true; neither is set when the key was
    /// absent (the whole path is left untouched — no dead blocks).
    /// </summary>
    private sealed class DeleteOutcome
    {
        /// <summary>True when the key existed in this subtree and was removed.</summary>
        public required bool RemovedKey { get; init; }

        /// <summary>The rewritten leaf (height 1), not yet written.</summary>
        public BTreeLeafNode? Leaf { get; init; }

        /// <summary>The rewritten internal node (height > 1), not yet written.</summary>
        public BTreeInternalNode? Internal { get; init; }
    }

    /// <summary>
    /// Recursive COW delete descent: loads and verifies the node, applies the
    /// delete to the routed child path, then rebalances the child if it
    /// underflowed (borrow from a sibling above minimum occupancy, else
    /// merge). Children are written here; this node is returned unwritten for
    /// the CALLER to write (or rebalance further). Height counts down to 1 at
    /// the leaves.
    /// </summary>
    private Result<DeleteOutcome> DeleteDescend(BTreeNodeRef nodeRef, int height, byte[] key)
    {
        if (height <= 1)
        {
            var loadedLeaf = ReadLeaf(nodeRef);
            if (loadedLeaf.IsFailure)
                return Result<DeleteOutcome>.Failure(loadedLeaf.Error);
            var leaf = loadedLeaf.Value;

            int position = LowerBound(leaf.Entries, key, out bool found);
            if (!found)
                return Result<DeleteOutcome>.Success(new DeleteOutcome { RemovedKey = false });
            leaf.Entries.RemoveAt(position);
            return Result<DeleteOutcome>.Success(new DeleteOutcome { RemovedKey = true, Leaf = leaf });
        }

        var loaded = ReadInternal(nodeRef);
        if (loaded.IsFailure)
            return Result<DeleteOutcome>.Failure(loaded.Error);
        var node = loaded.Value;

        int childIndex = RouteChildIndex(node.Keys, key);
        var childRef = BTreeNodeRef.FromChildRecord(Addressing, node.Children[childIndex]);
        if (childRef.IsFailure)
            return Result<DeleteOutcome>.Failure(childRef.Error);

        var subOutcome = DeleteDescend(childRef.Value, height - 1, key);
        if (subOutcome.IsFailure)
            return subOutcome;
        if (!subOutcome.Value.RemovedKey)
            return Result<DeleteOutcome>.Success(new DeleteOutcome { RemovedKey = false });

        // The descended child was rewritten: write it back (rebalancing first
        // when it underflowed), which updates this node's keys/child records.
        // All untouched child records are copied verbatim — shared subtrees.
        var rebalanced = height == 2
            ? RebalanceLeafChild(node, childIndex, subOutcome.Value.Leaf!)
            : RebalanceInternalChild(node, childIndex, subOutcome.Value.Internal!);
        if (rebalanced.IsFailure)
            return Result<DeleteOutcome>.Failure(rebalanced.Error);

        return Result<DeleteOutcome>.Success(new DeleteOutcome { RemovedKey = true, Internal = node });
    }

    /// <summary>
    /// Writes a rewritten LEAF child back under <paramref name="parent"/>,
    /// rebalancing first when it fell below <see cref="MinLeafEntries"/>:
    /// borrow the nearest entry from a sibling above minimum (updating the
    /// separator to the borrower boundary's new first key), else merge with a
    /// sibling at minimum, removing one separator and one child record from
    /// the parent. Mutates <paramref name="parent"/> in place; the caller
    /// writes it.
    /// </summary>
    private Result RebalanceLeafChild(BTreeInternalNode parent, int childIndex, BTreeLeafNode child)
    {
        if (child.Entries.Count >= MinLeafEntries)
            return StoreLeafChild(parent, childIndex, child);

        BTreeLeafNode? left = null;
        if (childIndex > 0)
        {
            var loadedLeft = ReadLeafChild(parent, childIndex - 1);
            if (loadedLeft.IsFailure)
                return Result.Failure(loadedLeft.Error);
            left = loadedLeft.Value;
        }
        if (left is not null && left.Entries.Count > MinLeafEntries)
        {
            // Borrow from the left: its last entry becomes the child's first,
            // and the separator becomes that borrowed key (a separator equals
            // its right subtree's smallest key).
            var moved = left.Entries[^1];
            left.Entries.RemoveAt(left.Entries.Count - 1);
            child.Entries.Insert(0, moved);
            parent.Keys[childIndex - 1] = (byte[])moved.Key.Clone();
            var storeLeft = StoreLeafChild(parent, childIndex - 1, left);
            if (storeLeft.IsFailure)
                return storeLeft;
            return StoreLeafChild(parent, childIndex, child);
        }

        BTreeLeafNode? right = null;
        if (childIndex < parent.Children.Count - 1)
        {
            var loadedRight = ReadLeafChild(parent, childIndex + 1);
            if (loadedRight.IsFailure)
                return Result.Failure(loadedRight.Error);
            right = loadedRight.Value;
        }
        if (right is not null && right.Entries.Count > MinLeafEntries)
        {
            // Borrow from the right: its first entry moves to the child's end;
            // the separator becomes the right sibling's NEW first key.
            var moved = right.Entries[0];
            right.Entries.RemoveAt(0);
            child.Entries.Add(moved);
            parent.Keys[childIndex] = (byte[])right.Entries[0].Key.Clone();
            var storeChild = StoreLeafChild(parent, childIndex, child);
            if (storeChild.IsFailure)
                return storeChild;
            return StoreLeafChild(parent, childIndex + 1, right);
        }

        // No sibling can lend (all at exact minimum): merge. Leaf merges drop
        // the separator between the two leaves; the combined leaf holds
        // 2 × minimum − 1 entries, which always fits below the split threshold.
        if (left is not null)
        {
            left.Entries.AddRange(child.Entries);
            parent.Keys.RemoveAt(childIndex - 1);
            parent.Children.RemoveAt(childIndex);
            return StoreLeafChild(parent, childIndex - 1, left);
        }
        if (right is null)
            return Result.Failure(
                "Corrupt internal node: leaf child has no sibling to rebalance with " +
                "(every internal node must hold at least 2 children).");
        child.Entries.AddRange(right.Entries);
        parent.Keys.RemoveAt(childIndex);
        parent.Children.RemoveAt(childIndex + 1);
        return StoreLeafChild(parent, childIndex, child);
    }

    /// <summary>
    /// Writes a rewritten INTERNAL child back under <paramref name="parent"/>,
    /// rebalancing first when it fell below <see cref="MinInternalKeys"/>.
    /// Internal rebalancing (required — leaf-only rebalancing degrades fill
    /// factor, BTree_Index.md Section 4) rotates separators through the
    /// parent: a borrow drops the separator into the underfull child and
    /// promotes the sibling's boundary key in its place; a merge pulls the
    /// separator DOWN between the two halves (unlike leaf merges, which drop
    /// it). Mutates <paramref name="parent"/> in place; the caller writes it.
    /// </summary>
    private Result RebalanceInternalChild(BTreeInternalNode parent, int childIndex, BTreeInternalNode child)
    {
        if (child.Keys.Count >= MinInternalKeys)
            return StoreInternalChild(parent, childIndex, child);

        BTreeInternalNode? left = null;
        if (childIndex > 0)
        {
            var loadedLeft = ReadInternalChild(parent, childIndex - 1);
            if (loadedLeft.IsFailure)
                return Result.Failure(loadedLeft.Error);
            left = loadedLeft.Value;
        }
        if (left is not null && left.Keys.Count > MinInternalKeys)
        {
            // Rotate through the parent: the separator drops into the child as
            // its new first key, the left sibling's last child moves with it,
            // and the sibling's last key is promoted to separator.
            child.Keys.Insert(0, parent.Keys[childIndex - 1]);
            child.Children.Insert(0, left.Children[^1]);
            parent.Keys[childIndex - 1] = left.Keys[^1];
            left.Keys.RemoveAt(left.Keys.Count - 1);
            left.Children.RemoveAt(left.Children.Count - 1);
            var storeLeft = StoreInternalChild(parent, childIndex - 1, left);
            if (storeLeft.IsFailure)
                return storeLeft;
            return StoreInternalChild(parent, childIndex, child);
        }

        BTreeInternalNode? right = null;
        if (childIndex < parent.Children.Count - 1)
        {
            var loadedRight = ReadInternalChild(parent, childIndex + 1);
            if (loadedRight.IsFailure)
                return Result.Failure(loadedRight.Error);
            right = loadedRight.Value;
        }
        if (right is not null && right.Keys.Count > MinInternalKeys)
        {
            // Mirror rotation: the separator drops into the child as its new
            // last key, the right sibling's first child moves with it, and the
            // sibling's first key is promoted to separator.
            child.Keys.Add(parent.Keys[childIndex]);
            child.Children.Add(right.Children[0]);
            parent.Keys[childIndex] = right.Keys[0];
            right.Keys.RemoveAt(0);
            right.Children.RemoveAt(0);
            var storeChild = StoreInternalChild(parent, childIndex, child);
            if (storeChild.IsFailure)
                return storeChild;
            return StoreInternalChild(parent, childIndex + 1, right);
        }

        // Merge: the separator is pulled DOWN between the two key lists (it
        // still separates their subtrees). Combined keys are 2 × minimum,
        // which always fits below the split threshold.
        if (left is not null)
        {
            left.Keys.Add(parent.Keys[childIndex - 1]);
            left.Keys.AddRange(child.Keys);
            left.Children.AddRange(child.Children);
            parent.Keys.RemoveAt(childIndex - 1);
            parent.Children.RemoveAt(childIndex);
            return StoreInternalChild(parent, childIndex - 1, left);
        }
        if (right is null)
            return Result.Failure(
                "Corrupt internal node: internal child has no sibling to rebalance with " +
                "(every internal node must hold at least 2 children).");
        child.Keys.Add(parent.Keys[childIndex]);
        child.Keys.AddRange(right.Keys);
        child.Children.AddRange(right.Children);
        parent.Keys.RemoveAt(childIndex);
        parent.Children.RemoveAt(childIndex + 1);
        return StoreInternalChild(parent, childIndex, child);
    }

    /// <summary>Loads and verifies the leaf at one of <paramref name="parent"/>'s child records.</summary>
    private Result<BTreeLeafNode> ReadLeafChild(BTreeInternalNode parent, int index)
    {
        var childRef = BTreeNodeRef.FromChildRecord(Addressing, parent.Children[index]);
        if (childRef.IsFailure)
            return Result<BTreeLeafNode>.Failure(childRef.Error);
        return ReadLeaf(childRef.Value);
    }

    /// <summary>Loads and verifies the internal node at one of <paramref name="parent"/>'s child records.</summary>
    private Result<BTreeInternalNode> ReadInternalChild(BTreeInternalNode parent, int index)
    {
        var childRef = BTreeNodeRef.FromChildRecord(Addressing, parent.Children[index]);
        if (childRef.IsFailure)
            return Result<BTreeInternalNode>.Failure(childRef.Error);
        return ReadInternal(childRef.Value);
    }

    /// <summary>Appends a rewritten leaf child and installs its new child record in the parent.</summary>
    private Result StoreLeafChild(BTreeInternalNode parent, int index, BTreeLeafNode child)
    {
        var write = WriteLeaf(child);
        if (write.IsFailure)
            return Result.Failure(write.Error);
        parent.Children[index] = write.Value.ToChildRecord();
        return Result.Success();
    }

    /// <summary>Appends a rewritten internal child and installs its new child record in the parent.</summary>
    private Result StoreInternalChild(BTreeInternalNode parent, int index, BTreeInternalNode child)
    {
        var write = WriteInternal(child);
        if (write.IsFailure)
            return Result.Failure(write.Error);
        parent.Children[index] = write.Value.ToChildRecord();
        return Result.Success();
    }

    // ---------------------------------------------------------------- Lookup

    /// <summary>Point lookup result: whether the key exists, and its value when it does.</summary>
    /// <param name="Found">True when the key exists in the tree version searched.</param>
    /// <param name="Value">The value bytes (empty for key-only indexes) when found; null otherwise.</param>
    public readonly record struct BTreeLookup(bool Found, byte[]? Value);

    /// <summary>
    /// Verified point lookup in one tree version: descends
    /// <see cref="BTreeRoot.Height"/> nodes, Merkle-verifying each against its
    /// parent's ChildHash (RootHash for the root). Works identically on old
    /// root handles — every version is a consistent snapshot. A missing key is
    /// a SUCCESSFUL result with <c>Found == false</c>; failure means I/O error
    /// or corruption (spec Section 13: fail the lookup).
    /// </summary>
    /// <param name="root">The tree version to search.</param>
    /// <param name="key">Exactly <see cref="KeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key width is wrong.</exception>
    public Result<BTreeLookup> TryGet(BTreeRoot root, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateKeyWidth(key);

        var nodeRef = root.RootRef;
        for (int height = root.Height; height > 1; height--)
        {
            var loaded = ReadInternal(nodeRef);
            if (loaded.IsFailure)
                return Result<BTreeLookup>.Failure(loaded.Error);
            var node = loaded.Value;

            int childIndex = RouteChildIndex(node.Keys, key);
            var childRef = BTreeNodeRef.FromChildRecord(Addressing, node.Children[childIndex]);
            if (childRef.IsFailure)
                return Result<BTreeLookup>.Failure(childRef.Error);
            nodeRef = childRef.Value;
        }

        var leafLoaded = ReadLeaf(nodeRef);
        if (leafLoaded.IsFailure)
            return Result<BTreeLookup>.Failure(leafLoaded.Error);
        var leaf = leafLoaded.Value;

        int position = LowerBound(leaf.Entries, key, out bool found);
        if (!found)
            return Result<BTreeLookup>.Success(new BTreeLookup(false, null));
        return Result<BTreeLookup>.Success(
            new BTreeLookup(true, (byte[])leaf.Entries[position].Value.Clone()));
    }

    /// <summary>
    /// Verified point lookup with the corruption-contract fallback (spec
    /// Section 13, "Merkle ChildHash mismatch"): searches <paramref name="root"/>
    /// and, if the lookup fails specifically because a traversed node failed
    /// Merkle verification (<see cref="BTreeNodeStore.IsMerkleVerificationFailure"/>),
    /// retries against <paramref name="previousRoot"/> — the previous
    /// Checkpoint's root. Because COW never overwrites nodes, that older
    /// snapshot shares none of the corrupted rewritten path and verifies
    /// cleanly, so the lookup recovers. Ordinary I/O failures do NOT fall back
    /// (a lower layer already resynchronized); they surface unchanged. When
    /// <paramref name="previousRoot"/> is null (no earlier Checkpoint) the
    /// original failure is returned for the caller to escalate to rebuild.
    /// </summary>
    /// <param name="root">The current tree version to search first.</param>
    /// <param name="previousRoot">The previous Checkpoint's root to fall back to, or null.</param>
    /// <param name="key">Exactly <see cref="KeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">The key width is wrong.</exception>
    public Result<BTreeLookup> TryGetWithFallback(BTreeRoot root, BTreeRoot? previousRoot, ReadOnlySpan<byte> key)
    {
        var primary = TryGet(root, key);
        if (primary.IsSuccess || previousRoot is null || !BTreeNodeStore.IsMerkleVerificationFailure(primary.Error))
            return primary;
        return TryGet(previousRoot, key);
    }

    // -------------------------------------------------- Full-tree verification

    /// <summary>
    /// Outcome of a <see cref="VerifyFullTree"/> audit.
    /// </summary>
    /// <param name="IsIntact">
    /// True when EVERY node verified against its parent's ChildHash (the root
    /// against IndexRoot.RootHash); false when a node diverged.
    /// </param>
    /// <param name="NodesVerified">
    /// How many nodes were successfully verified before the walk stopped: the
    /// whole tree when <paramref name="IsIntact"/>, or every node visited up to
    /// (but excluding) the first divergent one otherwise.
    /// </param>
    /// <param name="DivergentNodePath">
    /// Root-to-node child-index path of the FIRST divergent node
    /// (e.g. <c>root/child[2]/child[0]</c>), or null when intact.
    /// </param>
    /// <param name="Error">
    /// The failure that stopped the walk — the contracted
    /// <see cref="BTreeNodeStore.MerkleVerificationErrorCode"/> for a hash
    /// mismatch, or an I/O/corruption error — or null when intact.
    /// </param>
    public readonly record struct FullTreeVerification(
        bool IsIntact, long NodesVerified, string? DivergentNodePath, string? Error);

    /// <summary>
    /// Full-tree verification mode (docs/BTree_Index.md Section 5, spec
    /// Section 6): walks EVERY node of <paramref name="root"/> in pre-order,
    /// Merkle-verifying each against its parent's ChildHash (the root against
    /// IndexRoot.RootHash) through the node store, and stops at the FIRST node
    /// that fails — reporting that node's root-to-node child-index path and the
    /// error. This is the O(nodes) integrity-audit / post-recovery counterpart
    /// to the O(height) path verification a lookup performs.
    ///
    /// Verification reuses the node store's read path, so it flags Merkle
    /// mismatches (<see cref="BTreeNodeStore.IsMerkleVerificationFailure"/>),
    /// block-checksum corruption, and unresolvable or malformed child records
    /// alike. For a cold on-disk audit run it against a store with a cold cache
    /// (the natural post-recovery state) so verify-on-cache-load cannot serve a
    /// block that was corrupted after it was last read. A null root (empty
    /// tree) is intact with zero nodes. Never throws — corruption is reported,
    /// not raised (spec Section 13).
    /// </summary>
    /// <param name="root">The tree version to audit, or null for an empty tree.</param>
    public FullTreeVerification VerifyFullTree(BTreeRoot? root)
    {
        if (root is null)
            return new FullTreeVerification(true, 0, null, null);

        long verified = 0;
        var failure = VerifySubtree(root.RootRef, root.Height, "root", ref verified);
        if (failure is null)
            return new FullTreeVerification(true, verified, null, null);
        return new FullTreeVerification(false, verified, failure.Value.Path, failure.Value.Error);
    }

    /// <summary>
    /// Recursively verifies and counts every node of one subtree in pre-order,
    /// returning the child-index path and error of the first divergent node, or
    /// null when the whole subtree verified. <paramref name="verified"/> is
    /// incremented once per node whose <see cref="BTreeNodeStore.ReadVerified"/>
    /// succeeds.
    /// </summary>
    private (string Path, string Error)? VerifySubtree(
        BTreeNodeRef nodeRef, int height, string path, ref long verified)
    {
        if (height <= 1)
        {
            var leaf = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            if (leaf.IsFailure)
                return (path, leaf.Error);
            verified++;
            return null;
        }

        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        if (payload.IsFailure)
            return (path, payload.Error);
        verified++;

        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        if (node.IsFailure)
            return (path, node.Error);

        var children = node.Value.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var childPath = $"{path}/child[{i}]";
            var childRef = BTreeNodeRef.FromChildRecord(Addressing, children[i]);
            if (childRef.IsFailure)
                return (childPath, childRef.Error);
            var childFailure = VerifySubtree(childRef.Value, height - 1, childPath, ref verified);
            if (childFailure is not null)
                return childFailure;
        }
        return null;
    }

    // ---------------------------------------------------------- Range scan

    /// <summary>One scanned entry: the key and its value (empty for key-only indexes such as Date).</summary>
    /// <param name="Key">The entry's key, exactly <see cref="KeySize"/> bytes.</param>
    /// <param name="Value">The entry's value, exactly <see cref="LeafValueSize"/> bytes.</param>
    public readonly record struct BTreeScanEntry(byte[] Key, byte[] Value);

    /// <summary>
    /// Opens a verified ordered scan over one tree version: entries come back
    /// in ascending unsigned-lexicographic key order from
    /// <paramref name="startInclusive"/> (or the tree's smallest key when
    /// empty) up to but excluding <paramref name="endExclusive"/> (or through
    /// the largest key when empty) — the [start, end) protocol of
    /// BTree_Index.md Section 7. An empty or inverted range simply yields
    /// nothing. Works identically on old root handles — every version is a
    /// consistent snapshot.
    ///
    /// The format has NO leaf sibling pointers (BTree_Index.md Section 1), so
    /// the cursor keeps the root-to-leaf parent stack: exhausting a leaf
    /// backtracks to the nearest ancestor with an unvisited child and descends
    /// to that subtree's leftmost leaf. Every traversed node is Merkle-verified
    /// by the node store, exactly like <see cref="TryGet"/> (Section 5).
    ///
    /// No I/O happens until the first <see cref="RangeScan.MoveNext"/>.
    /// </summary>
    /// <param name="root">The tree version to scan, or null for an empty tree (yields nothing).</param>
    /// <param name="startInclusive">First key to include — exactly <see cref="KeySize"/> bytes, or empty for an unbounded start.</param>
    /// <param name="endExclusive">First key to EXCLUDE — exactly <see cref="KeySize"/> bytes, or empty for an unbounded end.</param>
    /// <exception cref="ArgumentException">A non-empty bound has the wrong key width.</exception>
    public RangeScan Scan(
        BTreeRoot? root,
        ReadOnlySpan<byte> startInclusive = default,
        ReadOnlySpan<byte> endExclusive = default)
    {
        byte[]? start = null;
        if (!startInclusive.IsEmpty)
        {
            ValidateKeyWidth(startInclusive, nameof(startInclusive));
            start = startInclusive.ToArray();
        }
        byte[]? end = null;
        if (!endExclusive.IsEmpty)
        {
            ValidateKeyWidth(endExclusive, nameof(endExclusive));
            end = endExclusive.ToArray();
        }
        return new RangeScan(this, root, start, end);
    }

    /// <summary>
    /// Cursor over one <see cref="Scan"/>: call <see cref="MoveNext"/> until it
    /// returns false (exhausted) or fails (I/O error or corruption — spec
    /// Section 13: fail the read); <see cref="Current"/> holds the entry after
    /// each successful true step. Single-pass, forward-only, not thread-safe.
    /// </summary>
    public sealed class RangeScan
    {
        /// <summary>
        /// One level of the parent stack: an internal node on the path to the
        /// current leaf and the index of its next unvisited child. Backtracking
        /// pops exhausted frames until a frame still has a child to visit.
        /// </summary>
        private sealed class Frame
        {
            /// <summary>The internal node (already Merkle-verified on load).</summary>
            public required BTreeInternalNode Node { get; init; }

            /// <summary>This node's height (root = tree height, lowest internal level = 2).</summary>
            public required int Height { get; init; }

            /// <summary>Index into <see cref="BTreeInternalNode.Children"/> of the next unvisited child.</summary>
            public required int NextChild { get; set; }
        }

        private readonly CowBTree _tree;
        private readonly BTreeRoot? _root;
        private readonly byte[]? _startInclusive;
        private readonly byte[]? _endExclusive;

        /// <summary>Parent stack, root frame first; empty once every subtree is exhausted.</summary>
        private readonly List<Frame> _path = [];

        private BTreeLeafNode? _leaf;
        private int _position;
        private bool _initialized;
        private bool _done;

        internal RangeScan(CowBTree tree, BTreeRoot? root, byte[]? startInclusive, byte[]? endExclusive)
        {
            _tree = tree;
            _root = root;
            _startInclusive = startInclusive;
            _endExclusive = endExclusive;
        }

        /// <summary>The current entry; valid only after <see cref="MoveNext"/> returned a successful true.</summary>
        public BTreeScanEntry Current { get; private set; }

        /// <summary>
        /// Advances to the next in-range entry. Success(true) = <see cref="Current"/>
        /// was updated; Success(false) = the scan is exhausted (and stays
        /// exhausted on further calls); failure = I/O error or corruption on a
        /// traversed node, after which the scan is dead.
        /// </summary>
        public Result<bool> MoveNext()
        {
            if (_done)
                return Result<bool>.Success(false);

            if (!_initialized)
            {
                var seek = SeekToStart();
                if (seek.IsFailure)
                {
                    _done = true;
                    return Result<bool>.Failure(seek.Error);
                }
            }

            while (true)
            {
                if (_leaf is not null && _position < _leaf.Entries.Count)
                {
                    var entry = _leaf.Entries[_position];
                    if (_endExclusive is not null &&
                        entry.Key.AsSpan().SequenceCompareTo(_endExclusive) >= 0)
                    {
                        _done = true;
                        return Result<bool>.Success(false);
                    }
                    _position++;
                    Current = new BTreeScanEntry((byte[])entry.Key.Clone(), (byte[])entry.Value.Clone());
                    return Result<bool>.Success(true);
                }

                var advanced = AdvanceToNextLeaf();
                if (advanced.IsFailure)
                {
                    _done = true;
                    return advanced;
                }
                if (!advanced.Value)
                {
                    _done = true;
                    return Result<bool>.Success(false);
                }
            }
        }

        /// <summary>
        /// Initial descent: routes by the start bound (leftmost path when
        /// unbounded), pushing a parent frame per internal level, and positions
        /// the leaf cursor at the first entry ≥ start. That leaf position may
        /// be past the leaf's last entry — <see cref="MoveNext"/>'s advance
        /// loop then backtracks to the true first in-range leaf.
        /// </summary>
        private Result SeekToStart()
        {
            _initialized = true;
            if (_root is null)
                return Result.Success();

            var nodeRef = _root.RootRef;
            for (int height = _root.Height; height > 1; height--)
            {
                var loaded = _tree.ReadInternal(nodeRef);
                if (loaded.IsFailure)
                    return Result.Failure(loaded.Error);
                var node = loaded.Value;

                int childIndex = _startInclusive is null ? 0 : RouteChildIndex(node.Keys, _startInclusive);
                _path.Add(new Frame { Node = node, Height = height, NextChild = childIndex + 1 });

                var childRef = BTreeNodeRef.FromChildRecord(_tree.Addressing, node.Children[childIndex]);
                if (childRef.IsFailure)
                    return Result.Failure(childRef.Error);
                nodeRef = childRef.Value;
            }

            var leafLoaded = _tree.ReadLeaf(nodeRef);
            if (leafLoaded.IsFailure)
                return Result.Failure(leafLoaded.Error);
            _leaf = leafLoaded.Value;
            _position = _startInclusive is null ? 0 : LowerBound(_leaf.Entries, _startInclusive, out _);
            return Result.Success();
        }

        /// <summary>
        /// Parent backtracking: pops exhausted frames until an ancestor still
        /// has an unvisited child, then descends to that subtree's LEFTMOST
        /// leaf, pushing fresh frames on the way down. Success(false) when the
        /// whole stack is exhausted — the scan has left the last leaf.
        /// </summary>
        private Result<bool> AdvanceToNextLeaf()
        {
            while (_path.Count > 0)
            {
                var frame = _path[^1];
                if (frame.NextChild >= frame.Node.Children.Count)
                {
                    _path.RemoveAt(_path.Count - 1);
                    continue;
                }

                var childRef = BTreeNodeRef.FromChildRecord(
                    _tree.Addressing, frame.Node.Children[frame.NextChild]);
                if (childRef.IsFailure)
                    return Result<bool>.Failure(childRef.Error);
                frame.NextChild++;

                var nodeRef = childRef.Value;
                for (int height = frame.Height - 1; height > 1; height--)
                {
                    var loaded = _tree.ReadInternal(nodeRef);
                    if (loaded.IsFailure)
                        return Result<bool>.Failure(loaded.Error);
                    var node = loaded.Value;

                    _path.Add(new Frame { Node = node, Height = height, NextChild = 1 });
                    var leftmost = BTreeNodeRef.FromChildRecord(_tree.Addressing, node.Children[0]);
                    if (leftmost.IsFailure)
                        return Result<bool>.Failure(leftmost.Error);
                    nodeRef = leftmost.Value;
                }

                var leafLoaded = _tree.ReadLeaf(nodeRef);
                if (leafLoaded.IsFailure)
                    return Result<bool>.Failure(leafLoaded.Error);
                _leaf = leafLoaded.Value;
                _position = 0;
                return Result<bool>.Success(true);
            }
            return Result<bool>.Success(false);
        }
    }

    // ------------------------------------------------------------ Node I/O

    /// <summary>Serializes and appends a leaf; invariant violations throw (programming error).</summary>
    private Result<BTreeNodeRef> WriteLeaf(BTreeLeafNode leaf) =>
        _store.Append(BTreeNodeKind.Leaf, BTreeNodeSerializer.SerializeLeaf(leaf), Addressing);

    /// <summary>Serializes and appends an internal node.</summary>
    private Result<BTreeNodeRef> WriteInternal(BTreeInternalNode node) =>
        _store.Append(BTreeNodeKind.Internal, BTreeNodeSerializer.SerializeInternal(node), Addressing);

    /// <summary>Reads, Merkle-verifies, deserializes, and shape-checks a leaf.</summary>
    private Result<BTreeLeafNode> ReadLeaf(BTreeNodeRef nodeRef)
    {
        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
        if (payload.IsFailure)
            return Result<BTreeLeafNode>.Failure(payload.Error);

        var leaf = BTreeNodeSerializer.DeserializeLeaf(payload.Value);
        if (leaf.IsFailure)
            return leaf;

        var shapeError = CheckNodeShape(leaf.Value.IndexKind, leaf.Value.KeySize, leaf.Value.ValueSize, LeafValueSize);
        if (shapeError is not null)
            return Result<BTreeLeafNode>.Failure(shapeError);
        return leaf;
    }

    /// <summary>Reads, Merkle-verifies, deserializes, and shape-checks an internal node.</summary>
    private Result<BTreeInternalNode> ReadInternal(BTreeNodeRef nodeRef)
    {
        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        if (payload.IsFailure)
            return Result<BTreeInternalNode>.Failure(payload.Error);

        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        if (node.IsFailure)
            return node;

        var shapeError = CheckNodeShape(node.Value.IndexKind, node.Value.KeySize, node.Value.ValueSize, ChildRecordSize);
        if (shapeError is not null)
            return Result<BTreeInternalNode>.Failure(shapeError);
        return node;
    }

    /// <summary>
    /// A stored node's declared shape must match this tree's — a mismatch means
    /// the reference points at a node of a different index (corrupt record).
    /// </summary>
    private string? CheckNodeShape(BTreeIndexKind indexKind, byte keySize, ushort valueSize, ushort expectedValueSize)
    {
        if (indexKind != IndexKind || keySize != KeySize || valueSize != expectedValueSize)
            return $"Node shape mismatch: expected IndexKind {IndexKind}, KeySize {KeySize}, ValueSize {expectedValueSize}; " +
                   $"node declares IndexKind {indexKind}, KeySize {keySize}, ValueSize {valueSize} " +
                   "(reference points into a different index — corrupt child record).";
        return null;
    }

    private BTreeLeafNode NewLeaf() => new()
    {
        IndexKind = IndexKind,
        KeySize = KeySize,
        ValueSize = LeafValueSize,
    };

    private BTreeInternalNode NewInternal() => new()
    {
        IndexKind = IndexKind,
        KeySize = KeySize,
        ValueSize = ChildRecordSize,
    };

    // ------------------------------------------------------------- Routing

    /// <summary>
    /// Which child to descend for <paramref name="key"/>: the first child
    /// whose routing key is strictly greater than the key. A separator equals
    /// its right subtree's smallest key, so keys ≥ a routing key route right
    /// of it (unsigned lexicographic order throughout).
    /// </summary>
    private static int RouteChildIndex(List<byte[]> routingKeys, ReadOnlySpan<byte> key)
    {
        int low = 0, high = routingKeys.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (routingKeys[middle].AsSpan().SequenceCompareTo(key) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>
    /// Binary search over sorted leaf entries: the index of the key when
    /// found, else the insertion position keeping the entries sorted.
    /// </summary>
    private static int LowerBound(List<BTreeLeafEntry> entries, ReadOnlySpan<byte> key, out bool found)
    {
        int low = 0, high = entries.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            int comparison = entries[middle].Key.AsSpan().SequenceCompareTo(key);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else if (comparison == 0)
            {
                found = true;
                return middle;
            }
            else
            {
                high = middle;
            }
        }
        found = false;
        return low;
    }

    // ---------------------------------------------------------- Validation

    private void ValidateKeyWidth(ReadOnlySpan<byte> key, string paramName = "key")
    {
        if (key.Length != KeySize)
            throw new ArgumentException(
                $"Key must be exactly KeySize ({KeySize}) bytes for IndexKind {IndexKind}, got {key.Length}.",
                paramName);
    }

    private void ValidateValueWidth(ReadOnlySpan<byte> value)
    {
        if (value.Length != LeafValueSize)
            throw new ArgumentException(
                $"Value must be exactly ValueSize ({LeafValueSize}) bytes for IndexKind {IndexKind}, got {value.Length}.",
                nameof(value));
    }
}
