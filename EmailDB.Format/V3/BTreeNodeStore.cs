using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Stores serialized B+-tree nodes in the v3 block stream and reads them back
/// with mandatory Merkle verification (EmailDB_FileFormat_Spec.md Section 6,
/// docs/BTree_Index.md Section 5). Leaf nodes are appended as BlockType 4
/// (BTreeLeaf) blocks and internal nodes as BlockType 5 (BTreeInternal), both
/// with <see cref="PayloadEncoding.Custom"/>.
///
/// Every append returns a <see cref="BTreeNodeRef"/> carrying the node's
/// address (BlockId or file offset per the tree's addressing mode) and its
/// NodeContentHash; every read verifies the stored payload against the
/// expected hash BEFORE the caller may deserialize it — path verification is
/// mandatory (BTree_Index.md Section 5). A mismatch fails the read
/// (<see cref="Result{T}"/> failure, never an exception — spec Section 13) with
/// a contracted error (<see cref="MerkleVerificationErrorCode"/> /
/// <see cref="IsMerkleVerificationFailure"/>) so the fallback layer can tell
/// index corruption/tampering apart from ordinary I/O and fall back to the
/// previous Checkpoint's root per the corruption contract (spec Section 13).
///
/// Verify-on-cache-load (BTree_Index.md Sections 5, 8): a bounded LRU keeps the
/// verified payload of recently traversed nodes keyed by their address. A node
/// validated once against its parent's ChildHash is served from cache WITHOUT
/// re-hashing the payload — the cheap 32-byte comparison of the caller's
/// expected hash against the cached (trusted) content hash still detects a
/// tampered parent child record, so caching never weakens verification. Blocks
/// are append-only immutable (spec Section 14), so a cached payload at an
/// address is always the true content at that address.
///
/// Durability is the caller's concern: appends are buffered in the
/// <see cref="BlockManager"/> and made durable by its Flush at commit points
/// (write all new nodes → fsync → IndexRoot → Checkpoint, BTree_Index.md
/// Section 4). This type is NOT thread-safe (the cache is unsynchronized).
/// </summary>
public sealed class BTreeNodeStore
{
    /// <summary>
    /// Default node LRU capacity: the number of most-recently verified node
    /// payloads served without re-hashing. The primary index's non-leaf levels
    /// fit in a few thousand nodes (BTree_Index.md Section 8).
    /// </summary>
    public const int DefaultNodeCacheCapacity = 4096;

    /// <summary>
    /// Stable marker prefixing every Merkle verification failure message. The
    /// corruption contract (spec Section 13, "Merkle ChildHash mismatch")
    /// requires callers to distinguish index corruption/tampering — which falls
    /// back to the previous Checkpoint's root — from ordinary read failures;
    /// they key on <see cref="IsMerkleVerificationFailure"/>.
    /// </summary>
    public const string MerkleVerificationErrorCode = "EMDB_MERKLE_MISMATCH";

    private readonly BlockManager _blockManager;
    private readonly IBlockIdResolver? _blockIdResolver;

    private readonly int _cacheCapacity;
    private readonly Dictionary<NodeKey, LinkedListNode<CacheEntry>> _cacheIndex;
    private readonly LinkedList<CacheEntry> _cacheOrder = new();

    /// <summary>
    /// Creates a node store over the block layer.
    /// </summary>
    /// <param name="blockManager">The v3 append-only block writer/reader.</param>
    /// <param name="blockIdResolver">
    /// BlockId-to-offset resolution for reads of BlockId-addressed nodes
    /// (spec Section 7 precedence chain; typically the
    /// <see cref="RuntimeBlockOffsetMap"/> until the BlockLocationIndex layer
    /// exists). May be null for trees that only use offset addressing.
    /// </param>
    /// <param name="nodeCacheCapacity">
    /// Maximum verified node payloads kept for verify-on-cache-load; 0 disables
    /// caching (every read re-reads and re-hashes). Defaults to
    /// <see cref="DefaultNodeCacheCapacity"/>.
    /// </param>
    public BTreeNodeStore(
        BlockManager blockManager,
        IBlockIdResolver? blockIdResolver = null,
        int nodeCacheCapacity = DefaultNodeCacheCapacity)
    {
        ArgumentNullException.ThrowIfNull(blockManager);
        ArgumentOutOfRangeException.ThrowIfNegative(nodeCacheCapacity);
        _blockManager = blockManager;
        _blockIdResolver = blockIdResolver;
        _cacheCapacity = nodeCacheCapacity;
        _cacheIndex = new Dictionary<NodeKey, LinkedListNode<CacheEntry>>(
            _cacheCapacity == 0 ? 0 : Math.Min(_cacheCapacity, 1024));
    }

    /// <summary>Verified node payloads served from cache without re-hashing.</summary>
    public long CacheHitCount { get; private set; }

    /// <summary>Reads that missed the cache and read a block from the block layer.</summary>
    public long CacheMissCount { get; private set; }

    /// <summary>
    /// Node payloads hashed (BLAKE3-256) during verification. A cache hit does
    /// NOT increment this — that is exactly what verify-on-cache-load buys.
    /// </summary>
    public long HashComputationCount { get; private set; }

    /// <summary>
    /// True when <paramref name="error"/> is the contracted Merkle verification
    /// failure (spec Section 13). The read-time fallback keys on this to fall
    /// back to the previous Checkpoint's root rather than treating the failure
    /// as an I/O error.
    /// </summary>
    public static bool IsMerkleVerificationFailure(string? error) =>
        error is not null && error.StartsWith(MerkleVerificationErrorCode, StringComparison.Ordinal);

    /// <summary>
    /// Appends one serialized node as a BTreeLeaf/BTreeInternal block and
    /// returns its reference: the address per <paramref name="addressing"/>
    /// (minted BlockId, or the append offset as 8 little-endian bytes) plus
    /// the node's NodeContentHash. COW trees only ever append — existing
    /// nodes are immutable (spec Section 14).
    /// </summary>
    /// <param name="kind">Leaf or internal — selects BlockType 4 or 5.</param>
    /// <param name="nodePayload">
    /// The node's complete serialized payload from
    /// <see cref="BTreeNodeSerializer.SerializeLeaf"/> /
    /// <see cref="BTreeNodeSerializer.SerializeInternal"/>.
    /// </param>
    /// <param name="addressing">The owning tree's child addressing mode.</param>
    public Result<BTreeNodeRef> Append(
        BTreeNodeKind kind, byte[] nodePayload, BTreeChildAddressing addressing)
    {
        ArgumentNullException.ThrowIfNull(nodePayload);
        var blockType = GetBlockType(kind);

        var appended = _blockManager.Append(blockType, PayloadEncoding.Custom, nodePayload);
        if (appended.IsFailure)
            return Result<BTreeNodeRef>.Failure($"B+-tree node append failed: {appended.Error}");

        byte[] reference;
        if (addressing == BTreeChildAddressing.Offset)
        {
            reference = new byte[BTreeNodeRef.OffsetReferenceSize];
            BinaryPrimitives.WriteInt64LittleEndian(reference, appended.Value.Offset);
        }
        else
        {
            reference = appended.Value.BlockId; // already a private copy
        }

        return Result<BTreeNodeRef>.Success(new BTreeNodeRef
        {
            Addressing = addressing,
            Reference = reference,
            NodeHash = BTreeNodeSerializer.ComputeNodeContentHash(nodePayload),
        });
    }

    /// <summary>
    /// Reads one node's serialized payload and verifies it: the block must be
    /// the expected node BlockType, and the payload's BLAKE3-256 must equal
    /// the reference's <see cref="BTreeNodeRef.NodeHash"/> (the parent's
    /// ChildHash, or IndexRoot.RootHash for the root) — mandatory path
    /// verification, BTree_Index.md Section 5. A node verified once is served
    /// from the LRU on the next read without re-hashing (verify-on-cache-load),
    /// yet the expected hash is still checked against the cached content hash so
    /// a tampered parent record is caught. Corruption or an unresolvable
    /// BlockId yields a failed result, never an exception (spec Section 13); a
    /// Merkle mismatch is the contracted <see cref="MerkleVerificationErrorCode"/>
    /// failure.
    /// </summary>
    /// <param name="nodeRef">The node's reference with its expected hash.</param>
    /// <param name="expectedKind">The node kind the caller expects at this tree level.</param>
    public Result<byte[]> ReadVerified(BTreeNodeRef nodeRef, BTreeNodeKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(nodeRef);
        var expectedBlockType = GetBlockType(expectedKind);

        var key = new NodeKey(nodeRef.Addressing, nodeRef.Reference);

        // Verify-on-cache-load: a node validated once is served without
        // re-hashing. The expected hash is still compared against the cached
        // (trusted) content hash, so a tampered parent child record still fails.
        if (TryGetCached(key, out var cached))
        {
            CacheHitCount++;
            if (cached.BlockType != expectedBlockType)
                return Result<byte[]>.Failure(
                    $"Expected a {expectedBlockType} block (type {(byte)expectedBlockType}) for the cached node, " +
                    $"got {cached.BlockType} (type {(byte)cached.BlockType}) — corrupt child record or stale reference.");
            if (!HashMatches(nodeRef.NodeHash, cached.ContentHash))
                return MerkleFailure(
                    nodeRef, "cached node payload hash does not match the expected ChildHash/RootHash",
                    expected: nodeRef.NodeHash, actual: cached.ContentHash);
            return Result<byte[]>.Success(cached.Payload);
        }

        long offset;
        if (nodeRef.Addressing == BTreeChildAddressing.Offset)
        {
            if (nodeRef.Reference.Length != BTreeNodeRef.OffsetReferenceSize)
                return Result<byte[]>.Failure(
                    $"Offset-addressed node reference must be {BTreeNodeRef.OffsetReferenceSize} bytes, got {nodeRef.Reference.Length}.");
            offset = BinaryPrimitives.ReadInt64LittleEndian(nodeRef.Reference);
            if (offset < 0)
                return Result<byte[]>.Failure($"Node ChildOffset {offset} is negative (corrupt child record).");
        }
        else
        {
            if (_blockIdResolver is null)
                return Result<byte[]>.Failure(
                    "Cannot read a BlockId-addressed node: no IBlockIdResolver was configured on this node store.");
            if (!_blockIdResolver.TryGetLocation(nodeRef.Reference, out var location) || location is null)
                return Result<byte[]>.Failure(
                    $"Node BlockId {Convert.ToHexString(nodeRef.Reference)} did not resolve to a file offset " +
                    "(resolution precedence: runtime map → BlockLocationIndex → scan, spec Section 7).");
            offset = location.Offset;
        }

        // ReadDecompressed fully verifies the block (header checksum, payload
        // checksum, footer) and rejects encrypted payloads — decryption is a
        // higher layer that must run before this store sees plaintext nodes.
        var blockResult = _blockManager.ReadDecompressed(offset);
        if (blockResult.IsFailure)
        {
            // A BlockId-addressed node is one the resolver reported as a LIVE block, so
            // a corrupt payload here is a loss of committed data: surface a data-loss
            // error naming the BlockId (spec Section 13, "referenced live → data-loss").
            if (nodeRef.Addressing == BTreeChildAddressing.BlockId
                && blockResult.VerificationError is CorruptionError { Cause: CorruptionCause.PayloadChecksum } corrupt)
                return CorruptionError.ReferencedDataLoss(
                    nodeRef.Reference, corrupt.Offset ?? offset, corrupt.DamagedRange).ToResult<byte[]>();
            return Result<byte[]>.Failure($"B+-tree node read at offset {offset} failed: {blockResult.Error}");
        }
        CacheMissCount++;

        var block = blockResult.Value;
        if (block.Header.Type != expectedBlockType)
            return Result<byte[]>.Failure(
                $"Expected a {expectedBlockType} block (type {(byte)expectedBlockType}) at offset {offset}, " +
                $"got {block.Header.Type} (type {(byte)block.Header.Type}) — corrupt child record or stale reference.");

        if (block.Payload.Length < BTreeNodeSerializer.NodeHeaderSize)
            return Result<byte[]>.Failure(
                $"Node payload at offset {offset} is {block.Payload.Length} bytes — too short to hold the " +
                $"{BTreeNodeSerializer.NodeHeaderSize}-byte node header (corrupt node).");

        var actualHash = new byte[BTreeNodeSerializer.NodeContentHashSize];
        BTreeNodeSerializer.ComputeNodeContentHash(block.Payload, actualHash);
        HashComputationCount++;
        if (!HashMatches(nodeRef.NodeHash, actualHash))
            return MerkleFailure(
                nodeRef, $"payload hash at offset {offset} does not match the expected ChildHash/RootHash",
                offset, nodeRef.NodeHash, actualHash);

        // Cache the verified payload keyed by its address (immutable block).
        Insert(key, new CacheEntry(block.Payload, actualHash, block.Header.Type));
        return Result<byte[]>.Success(block.Payload);
    }

    /// <summary>
    /// Builds the contracted Merkle verification failure (spec Section 13). The
    /// message still begins with <see cref="MerkleVerificationErrorCode"/> so
    /// <see cref="IsMerkleVerificationFailure"/> and the read-time fallback keep
    /// keying on it, but it now also carries the typed <see cref="IntegrityError"/>
    /// (with the node's BlockId when BlockId-addressed, its offset, and the
    /// expected/actual content hashes) on <see cref="Result{T}.VerificationError"/>.
    /// </summary>
    private static Result<byte[]> MerkleFailure(
        BTreeNodeRef nodeRef, string detail, long? offset = null, byte[]? expected = null, byte[]? actual = null)
    {
        byte[]? blockId = nodeRef.Addressing == BTreeChildAddressing.BlockId ? nodeRef.Reference : null;
        var error = new IntegrityError(
            $"{MerkleVerificationErrorCode}: Merkle verification failed — {detail} " +
            "(corrupt or tampered node; fall back to the previous Checkpoint's root, spec Sections 6, 13).",
            blockId, offset, expected, actual);
        return error.ToResult<byte[]>();
    }

    private static bool HashMatches(byte[] expected, byte[] actual) =>
        expected.Length == BTreeNodeSerializer.NodeContentHashSize
        && actual.AsSpan().SequenceEqual(expected);

    private static BlockType GetBlockType(BTreeNodeKind kind) => kind switch
    {
        BTreeNodeKind.Leaf => BlockType.BTreeLeaf,
        BTreeNodeKind.Internal => BlockType.BTreeInternal,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Node kind must be Leaf or Internal."),
    };

    // ------------------------------------------------------------- Node LRU

    private bool TryGetCached(in NodeKey key, out CacheEntry entry)
    {
        if (_cacheCapacity > 0 && _cacheIndex.TryGetValue(key, out var node))
        {
            _cacheOrder.Remove(node);
            _cacheOrder.AddFirst(node);
            entry = node.Value;
            return true;
        }
        entry = default;
        return false;
    }

    private void Insert(in NodeKey key, CacheEntry entry)
    {
        if (_cacheCapacity == 0)
            return;
        entry = entry with { Key = key };
        if (_cacheIndex.TryGetValue(key, out var existing))
        {
            existing.Value = entry;
            _cacheOrder.Remove(existing);
            _cacheOrder.AddFirst(existing);
            return;
        }
        var node = new LinkedListNode<CacheEntry>(entry);
        _cacheOrder.AddFirst(node);
        _cacheIndex[key] = node;
        if (_cacheIndex.Count > _cacheCapacity)
        {
            var lru = _cacheOrder.Last!;
            _cacheOrder.RemoveLast();
            _cacheIndex.Remove(lru.Value.Key);
        }
    }

    private readonly struct CacheEntry(byte[] payload, byte[] contentHash, BlockType blockType)
    {
        public byte[] Payload { get; } = payload;
        public byte[] ContentHash { get; } = contentHash;
        public BlockType BlockType { get; } = blockType;

        // Set when installed so eviction can recover the key without hashing.
        public NodeKey Key { get; init; }
    }

    /// <summary>
    /// Cache identity for a node: its addressing mode plus a private copy of
    /// its reference bytes (BlockId or offset). Two references address the same
    /// node iff their addressing and reference bytes are equal.
    /// </summary>
    private readonly struct NodeKey : IEquatable<NodeKey>
    {
        private readonly byte[] _reference;
        private readonly BTreeChildAddressing _addressing;
        private readonly int _hash;

        public NodeKey(BTreeChildAddressing addressing, byte[] reference)
        {
            _addressing = addressing;
            _reference = reference;
            var hash = new HashCode();
            hash.Add((byte)addressing);
            hash.AddBytes(reference);
            _hash = hash.ToHashCode();
        }

        public bool Equals(NodeKey other) =>
            _addressing == other._addressing
            && _reference.AsSpan().SequenceEqual(other._reference);

        public override bool Equals(object? obj) => obj is NodeKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }
}
