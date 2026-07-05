using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Read-path Merkle verification for the v3 copy-on-write B+-tree (US-EMDB-69-7,
/// EmailDB_FileFormat_Spec.md Sections 6 &amp; 13, docs/BTree_Index.md Section 5):
///
/// 1. Every traversed node is verified against its parent's ChildHash (the root
///    against IndexRoot.RootHash). A single bit flipped in any node fails the
///    lookups that traverse it — and only those — with the contracted error.
/// 2. Verify-on-cache-load: a node validated once is served from cache without
///    re-hashing, proven by the store's hash-computation counter.
/// 3. A Merkle mismatch is the contracted <see cref="BTreeNodeStore.MerkleVerificationErrorCode"/>
///    failure and falls back to the previous Checkpoint's root per the
///    corruption contract (spec Section 13).
/// </summary>
public class CowBTreeReadVerificationTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-verify-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeReadVerificationTests()
    {
        var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        _manager = new BlockManager(stream, offsetMap: _offsetMap, firstBlockOffset: 0, ownsStream: true);
        _store = new BTreeNodeStore(_manager, _offsetMap);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private const byte KeySize = 32;
    private const ushort ValueSize = 16;

    private static byte[] Key(int width, int i)
    {
        var key = new byte[width];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(width - 4), i);
        return key;
    }

    private CowBTree NewTree(BTreeNodeStore store) => new(
        store, BTreeIndexKind.PrimaryEmail, KeySize, ValueSize,
        maxLeafEntries: 3, maxInternalKeys: 3);

    private BTreeRoot BuildTree(CowBTree tree, int count, int minHeight)
    {
        BTreeRoot? root = null;
        for (int i = 1; i <= count; i++)
        {
            var inserted = tree.Insert(root, Key(KeySize, i), Key(ValueSize, i));
            Assert.True(inserted.IsSuccess, inserted.IsFailure ? inserted.Error : null);
            root = inserted.Value;
        }
        Assert.NotNull(root);
        Assert.True(root!.Height >= minHeight, $"Expected height >= {minHeight}, got {root.Height}.");
        return root;
    }

    private long OffsetOf(BTreeNodeRef nodeRef)
    {
        Assert.True(_offsetMap.TryGetLocation(nodeRef.Reference, out var location) && location is not null,
            "BlockId reference did not resolve to a location.");
        return location!.Offset;
    }

    /// <summary>
    /// Independently counts every node in a tree version over verified reads —
    /// the reference count the number of hash computations a full cold-cache
    /// traversal (which verifies every node exactly once) must match.
    /// </summary>
    private long CountNodes(CowBTree tree, BTreeNodeRef nodeRef, int height)
    {
        if (height <= 1)
        {
            var leaf = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            Assert.True(leaf.IsSuccess, leaf.IsFailure ? leaf.Error : null);
            return 1;
        }

        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);

        long total = 1;
        foreach (var record in node.Value.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            total += CountNodes(tree, childRef.Value, height - 1);
        }
        return total;
    }

    private byte[] ReadPayload(BTreeNodeRef nodeRef)
    {
        var block = _manager.ReadDecompressed(OffsetOf(nodeRef));
        Assert.True(block.IsSuccess, block.IsFailure ? block.Error : null);
        return block.Value.Payload;
    }

    private static BTreeNodeRef WithFlippedHash(BTreeNodeRef nodeRef)
    {
        var tampered = (byte[])nodeRef.NodeHash.Clone();
        tampered[^1] ^= 0x01;
        return new BTreeNodeRef
        {
            Addressing = nodeRef.Addressing,
            Reference = nodeRef.Reference,
            NodeHash = tampered,
        };
    }

    private static BTreeRoot WithRoot(BTreeRoot root, BTreeNodeRef newRootRef) => new()
    {
        RootRef = newRootRef,
        Height = root.Height,
        EntryCount = root.EntryCount,
    };

    /// <summary>
    /// Rewrites the root internal node with one bit flipped in the ChildHash of
    /// <paramref name="childIndex"/>, re-appends it as a valid block (correct
    /// block checksum and a correct hash for its OWN ref), and returns a root
    /// pointing at it. The tampered child record models a corrupted/tampered
    /// parent: the referenced child block is untouched, so path verification —
    /// not the block checksum — must catch the mismatch when that child is
    /// traversed.
    /// </summary>
    private BTreeRoot TamperRootChildHash(CowBTree tree, BTreeRoot root, int childIndex)
    {
        var payload = ReadPayload(root.RootRef);
        var node = BTreeNodeSerializer.DeserializeInternal(payload);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);

        var record = (byte[])node.Value.Children[childIndex].Clone();
        record[^1] ^= 0x01; // last byte of the child record lies in its 32-byte ChildHash
        node.Value.Children[childIndex] = record;

        var reappended = _store.Append(
            BTreeNodeKind.Internal, BTreeNodeSerializer.SerializeInternal(node.Value), tree.Addressing);
        Assert.True(reappended.IsSuccess, reappended.IsFailure ? reappended.Error : null);
        return WithRoot(root, reappended.Value);
    }

    // ---- 1. Every traversed node verified; single bit flip fails the lookup ----

    [Fact]
    public void TryGet_TamperedRootHash_FailsWithContractedMerkleError()
    {
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        var tampered = WithRoot(root, WithFlippedHash(root.RootRef));
        var result = tree.TryGet(tampered, Key(KeySize, 42));

        Assert.True(result.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error),
            $"Expected the contracted Merkle failure, got: {result.Error}");
    }

    [Fact]
    public void TryGet_TamperedRootLeafHash_SingleLevelTree_FailsMerkle()
    {
        // Height-1 tree: the root IS a leaf, verified against IndexRoot.RootHash.
        var tree = NewTree(_store);
        var root = BuildTree(tree, 2, minHeight: 1);
        Assert.Equal(1, root.Height);

        var tampered = WithRoot(root, WithFlippedHash(root.RootRef));
        var result = tree.TryGet(tampered, Key(KeySize, 1));

        Assert.True(result.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
    }

    [Fact]
    public void TryGet_TamperedLeafChildHash_FailsOnlyAffectedLookups()
    {
        // Height-2 tree: the root's children ARE leaves, so tampering a child
        // record exercises leaf-level path verification.
        var tree = NewTree(_store);
        var root = BuildTree(tree, 5, minHeight: 2);
        Assert.Equal(2, root.Height);

        var tampered = TamperRootChildHash(tree, root, childIndex: 0);

        // The smallest key routes through child 0 (the tampered leaf) → fails.
        var affected = tree.TryGet(tampered, Key(KeySize, 1));
        Assert.True(affected.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(affected.Error), affected.Error);

        // The largest key routes through the last (untouched) child → succeeds.
        var unaffected = tree.TryGet(tampered, Key(KeySize, 5));
        Assert.True(unaffected.IsSuccess, unaffected.IsFailure ? unaffected.Error : null);
        Assert.True(unaffected.Value.Found);
    }

    [Fact]
    public void TryGet_TamperedInternalChildHash_FailsOnlyAffectedLookups()
    {
        // Height-3 tree: the root's children are INTERNAL nodes.
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        var tampered = TamperRootChildHash(tree, root, childIndex: 0);

        var affected = tree.TryGet(tampered, Key(KeySize, 1));
        Assert.True(affected.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(affected.Error), affected.Error);

        var unaffected = tree.TryGet(tampered, Key(KeySize, 100));
        Assert.True(unaffected.IsSuccess, unaffected.IsFailure ? unaffected.Error : null);
        Assert.True(unaffected.Value.Found);
    }

    [Fact]
    public void Scan_TamperedChildHash_FailsWhenTraversalReachesIt()
    {
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);
        var tampered = TamperRootChildHash(tree, root, childIndex: 0);

        // A full scan starts at the leftmost (tampered) subtree → fails Merkle.
        var scan = tree.Scan(tampered);
        var step = scan.MoveNext();
        Assert.True(step.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(step.Error), step.Error);
    }

    [Fact]
    public void Scan_ColdCache_VerifiesEveryTraversedNodeExactlyOnce()
    {
        // The criterion is that EVERY traversed node is verified — not merely
        // that corruption is detected. A full ordered scan traverses every node
        // of the tree (all internal levels AND all leaves); on a cold cache it
        // must Merkle-verify (hash) each one exactly once. Proven by counting:
        // the store's HashComputationCount must equal the independent node count,
        // every read is a cold miss, and no node is ever revisited (0 cache hits).
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        long totalNodes = CountNodes(tree, root.RootRef, root.Height);
        Assert.True(totalNodes > root.Height,
            "A multi-level tree must have more nodes than its height (fan-out > 1).");

        // Fresh store over the same file → cold cache and zeroed counters.
        var coldStore = new BTreeNodeStore(_manager, _offsetMap);
        var coldTree = NewTree(coldStore);

        long entries = 0;
        var scan = coldTree.Scan(root);
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            entries++;
        }

        Assert.Equal(100, entries);                              // every entry visited
        Assert.Equal(totalNodes, coldStore.HashComputationCount); // every node verified once
        Assert.Equal(totalNodes, coldStore.CacheMissCount);       // each node read exactly once
        Assert.Equal(0, coldStore.CacheHitCount);                 // no node revisited
    }

    // ---------------- 2. Verify-on-cache-load skips re-hashing ----------------

    [Fact]
    public void ReadVerified_SecondReadOfSameNode_ServedFromCacheWithoutReHashing()
    {
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        // Fresh store over the same file → cold cache and zeroed counters.
        var coldStore = new BTreeNodeStore(_manager, _offsetMap);
        var coldTree = NewTree(coldStore);
        var key = Key(KeySize, 42);

        var first = coldTree.TryGet(root, key);
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error : null);
        Assert.True(first.Value.Found);

        // One traversal hashed exactly Height nodes (root → leaf), all misses.
        Assert.Equal(root.Height, coldStore.HashComputationCount);
        Assert.Equal(root.Height, coldStore.CacheMissCount);
        Assert.Equal(0, coldStore.CacheHitCount);

        long hashesAfterFirst = coldStore.HashComputationCount;

        var second = coldTree.TryGet(root, key);
        Assert.True(second.IsSuccess, second.IsFailure ? second.Error : null);
        Assert.True(second.Value.Found);
        Assert.Equal(first.Value.Value, second.Value.Value);

        // Verify-on-cache-load: the second traversal re-hashed NOTHING and every
        // node was served from cache.
        Assert.Equal(hashesAfterFirst, coldStore.HashComputationCount);
        Assert.Equal(root.Height, coldStore.CacheHitCount);
    }

    [Fact]
    public void ReadVerified_CachedNode_StillRejectsWrongExpectedHash()
    {
        // Caching must not weaken verification: a cache hit still checks the
        // caller's expected hash against the cached (trusted) content hash.
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        var coldStore = new BTreeNodeStore(_manager, _offsetMap);

        var warm = coldStore.ReadVerified(root.RootRef, BTreeNodeKind.Internal);
        Assert.True(warm.IsSuccess, warm.IsFailure ? warm.Error : null);
        Assert.Equal(1, coldStore.CacheMissCount);

        var tamperedRef = WithFlippedHash(root.RootRef);
        var reread = coldStore.ReadVerified(tamperedRef, BTreeNodeKind.Internal);

        Assert.True(reread.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(reread.Error), reread.Error);
        Assert.Equal(1, coldStore.CacheHitCount);       // it WAS a cache hit
        Assert.Equal(1, coldStore.HashComputationCount); // but no re-hash happened
    }

    [Fact]
    public void ReadVerified_CacheDisabled_ReHashesEveryRead()
    {
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        var noCache = new BTreeNodeStore(_manager, _offsetMap, nodeCacheCapacity: 0);

        var a = noCache.ReadVerified(root.RootRef, BTreeNodeKind.Internal);
        var b = noCache.ReadVerified(root.RootRef, BTreeNodeKind.Internal);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(0, noCache.CacheHitCount);
        Assert.Equal(2, noCache.HashComputationCount); // re-hashed both times
    }

    [Fact]
    public void ReadVerified_EvictedNode_IsReHashedOnReRead()
    {
        // Verify-on-cache-load only skips re-hashing while a node is resident in
        // the bounded LRU. A capacity of 1 cannot hold a whole root→leaf path, so
        // each read evicts the node the next read of the same traversal needs:
        // repeated traversals re-hash every level and never register a cache hit.
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        var tinyCache = new BTreeNodeStore(_manager, _offsetMap, nodeCacheCapacity: 1);
        var tinyTree = NewTree(tinyCache);
        var key = Key(KeySize, 42);

        var first = tinyTree.TryGet(root, key);
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error : null);
        Assert.True(first.Value.Found);
        Assert.Equal(root.Height, tinyCache.HashComputationCount); // whole path hashed
        Assert.Equal(0, tinyCache.CacheHitCount);

        var second = tinyTree.TryGet(root, key);
        Assert.True(second.IsSuccess, second.IsFailure ? second.Error : null);
        Assert.True(second.Value.Found);
        Assert.Equal(first.Value.Value, second.Value.Value);

        // Every node was evicted before it could be reused → full re-hash, no hit.
        Assert.Equal(2 * root.Height, tinyCache.HashComputationCount);
        Assert.Equal(0, tinyCache.CacheHitCount);
    }

    // -------------- 3. Contracted error falls back to previous root --------------

    [Fact]
    public void TryGetWithFallback_MerkleMismatch_FallsBackToPreviousRoot()
    {
        var tree = NewTree(_store);
        var previousRoot = BuildTree(tree, 100, minHeight: 3);

        // A newer version that still contains the key (COW superset).
        var newer = tree.Insert(previousRoot, Key(KeySize, 101), Key(ValueSize, 101));
        Assert.True(newer.IsSuccess, newer.IsFailure ? newer.Error : null);
        var currentRoot = newer.Value;

        var corruptedCurrent = WithRoot(currentRoot, WithFlippedHash(currentRoot.RootRef));
        var key = Key(KeySize, 42);

        // Direct lookup on the corrupted current root fails with the contract.
        var direct = tree.TryGet(corruptedCurrent, key);
        Assert.True(direct.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(direct.Error), direct.Error);

        // Fallback to the previous Checkpoint's root recovers the lookup.
        var recovered = tree.TryGetWithFallback(corruptedCurrent, previousRoot, key);
        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error : null);
        Assert.True(recovered.Value.Found);
        Assert.Equal(Key(ValueSize, 42), recovered.Value.Value);

        // With no previous root there is nothing to fall back to.
        var noFallback = tree.TryGetWithFallback(corruptedCurrent, null, key);
        Assert.True(noFallback.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(noFallback.Error), noFallback.Error);
    }

    [Fact]
    public void TryGetWithFallback_HealthyRoot_DoesNotConsultFallback()
    {
        var tree = NewTree(_store);
        var root = BuildTree(tree, 100, minHeight: 3);

        var result = tree.TryGetWithFallback(root, previousRoot: null, Key(KeySize, 7));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.True(result.Value.Found);
        Assert.Equal(Key(ValueSize, 7), result.Value.Value);
    }

    [Fact]
    public void TryGetWithFallback_MerkleMismatch_ReturnsPreviousSnapshotValue()
    {
        // The corruption contract (spec Section 13) says the fallback ABANDONS the
        // newer root and reads the previous Checkpoint's root. Proving it truly
        // reads the older snapshot — not just "some" value — requires the two roots
        // to disagree on the key: overwrite key 42's value in a newer COW version,
        // corrupt that newer root, and require the fallback to yield the OLD value.
        var tree = NewTree(_store);
        var previousRoot = BuildTree(tree, 100, minHeight: 3);
        var key = Key(KeySize, 42);
        var previousValue = Key(ValueSize, 42);
        var newerValue = Key(ValueSize, 999);

        var newer = tree.Insert(previousRoot, key, newerValue);
        Assert.True(newer.IsSuccess, newer.IsFailure ? newer.Error : null);
        var currentRoot = newer.Value;

        // Sanity: the intact current root returns the NEW value.
        var currentLookup = tree.TryGet(currentRoot, key);
        Assert.True(currentLookup.IsSuccess, currentLookup.IsFailure ? currentLookup.Error : null);
        Assert.Equal(newerValue, currentLookup.Value.Value);

        // Corrupt the current root; the fallback must return the PREVIOUS snapshot's
        // value, which is only possible if it actually traversed the older root.
        var corrupted = WithRoot(currentRoot, WithFlippedHash(currentRoot.RootRef));
        var recovered = tree.TryGetWithFallback(corrupted, previousRoot, key);
        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error : null);
        Assert.True(recovered.Value.Found);
        Assert.Equal(previousValue, recovered.Value.Value);
        Assert.NotEqual(newerValue, recovered.Value.Value);
    }

    [Fact]
    public void TryGetWithFallback_NonMerkleFailure_DoesNotFallBack()
    {
        // The contract falls back ONLY on a Merkle ChildHash mismatch. An ordinary
        // read failure (here a root reference to a BlockId that resolves to nothing,
        // modelling a missing/unreadable block) is NOT index tampering — a lower
        // layer has already resynchronized — so it MUST surface unchanged and the
        // previous root MUST NOT be consulted.
        var tree = NewTree(_store);
        var previousRoot = BuildTree(tree, 100, minHeight: 3);
        var key = Key(KeySize, 42);

        var danglingRef = new BTreeNodeRef
        {
            Addressing = tree.Addressing,
            Reference = Guid.NewGuid().ToByteArray(), // 16-byte BlockId that was never minted
            NodeHash = previousRoot.RootRef.NodeHash,
        };
        var brokenCurrent = WithRoot(previousRoot, danglingRef);

        // Direct lookup fails with a NON-Merkle error.
        var direct = tree.TryGet(brokenCurrent, key);
        Assert.True(direct.IsFailure);
        Assert.False(BTreeNodeStore.IsMerkleVerificationFailure(direct.Error), direct.Error);

        // Even though an intact previous root is available, no fallback is taken:
        // the identical error is returned, so the previous root was never searched.
        var result = tree.TryGetWithFallback(brokenCurrent, previousRoot, key);
        Assert.True(result.IsFailure);
        Assert.False(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
        Assert.Equal(direct.Error, result.Error);
    }

    [Fact]
    public void TryGetWithFallback_BothRootsCorrupt_ReportsContractedMerkleError()
    {
        // Contract escalation path: when the previous root ALSO fails Merkle
        // verification, the fallback cannot recover and the caller receives the
        // contracted Merkle error (to escalate to an index rebuild, spec Section 13).
        var tree = NewTree(_store);
        var previousRoot = BuildTree(tree, 100, minHeight: 3);
        var newer = tree.Insert(previousRoot, Key(KeySize, 101), Key(ValueSize, 101));
        Assert.True(newer.IsSuccess, newer.IsFailure ? newer.Error : null);
        var currentRoot = newer.Value;
        var key = Key(KeySize, 42);

        var corruptCurrent = WithRoot(currentRoot, WithFlippedHash(currentRoot.RootRef));
        var corruptPrevious = WithRoot(previousRoot, WithFlippedHash(previousRoot.RootRef));

        var result = tree.TryGetWithFallback(corruptCurrent, corruptPrevious, key);
        Assert.True(result.IsFailure);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
    }
}
