using System.Buffers.Binary;
using Blake3;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes the generic B+-tree node format (EmailDB_FileFormat_Spec.md
/// Section 6) — the payload of BTreeLeaf (BlockType 4) and BTreeInternal
/// (BlockType 5) blocks. One node format serves every tree in the file: the
/// 12-byte node header declares IndexKind, KeySize, and ValueSize, so adding
/// an index never changes the file format.
///
///   [12-byte node header] [body]
///
/// Leaf body:     EntryCount × (KeySize + ValueSize), sorted by key.
/// Internal body: EntryCount routing keys of KeySize, then EntryCount + 1
///                child records of ValueSize.
///
/// All multi-byte integers are little-endian (file-wide convention, spec
/// Section 4). Keys are opaque fixed-width byte strings ordered by unsigned
/// lexicographic comparison; serialization enforces strictly ascending keys.
///
/// Merkle integrity: <see cref="ComputeNodeContentHash(ReadOnlySpan{byte})"/>
/// computes NodeContentHash = BLAKE3-256 over the node's full serialized
/// payload (header + body). Internal child records embed it as ChildHash, and
/// IndexRoot.RootHash covers the root node.
///
/// Serializers throw <see cref="ArgumentException"/> on invariant violations
/// because serializing an inconsistent node is a programming error, not corrupt
/// input. Deserializers instead return <see cref="Result{T}"/> failures: the
/// declared EntryCount/KeySize/ValueSize are validated against the actual
/// payload length BEFORE any body byte is read (spec Section 4: "Deserializers
/// MUST bounds-check all counts/lengths against the actual payload size"), so a
/// corrupt header can never cause an out-of-bounds read or oversized
/// allocation. Per-index capacity constants live in
/// <see cref="BTreeNodeCapacity"/>.
/// </summary>
public static class BTreeNodeSerializer
{
    /// <summary>Size of the node header at the start of every node payload.</summary>
    public const int NodeHeaderSize = 12;

    /// <summary>Node format version this serializer writes and accepts.</summary>
    public const byte CurrentNodeVersion = 1;

    /// <summary>Size of NodeContentHash / ChildHash: full BLAKE3-256.</summary>
    public const int NodeContentHashSize = 32;

    // Node header field offsets (spec Section 6 table, in order).
    private const int NodeKindOffset = 0;    // 1 byte
    private const int NodeVersionOffset = 1; // 1 byte
    private const int IndexKindOffset = 2;   // 2 bytes
    private const int KeySizeOffset = 4;     // 1 byte
    private const int ValueSizeOffset = 5;   // 2 bytes
    private const int EntryCountOffset = 7;  // 2 bytes
    private const int ReservedOffset = 9;    // 3 bytes, must be 0

    /// <summary>Serialized node size for a leaf with the given shape.</summary>
    public static long GetLeafNodeSize(int entryCount, int keySize, int valueSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(keySize);
        ArgumentOutOfRangeException.ThrowIfNegative(valueSize);
        return NodeHeaderSize + (long)entryCount * (keySize + valueSize);
    }

    /// <summary>
    /// Serialized node size for an internal node with the given shape
    /// (<paramref name="entryCount"/> routing keys, entryCount + 1 child records).
    /// </summary>
    public static long GetInternalNodeSize(int entryCount, int keySize, int valueSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(keySize);
        ArgumentOutOfRangeException.ThrowIfNegative(valueSize);
        return NodeHeaderSize + (long)entryCount * keySize + (long)(entryCount + 1) * valueSize;
    }

    /// <summary>
    /// Serializes the 12-byte node header into the caller-provided buffer at
    /// spec offsets, with the 3 reserved bytes zero-filled.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The buffer is the wrong size, NodeKind is not Leaf/Internal, or
    /// NodeVersion is not <see cref="CurrentNodeVersion"/>.
    /// </exception>
    public static void SerializeHeader(BTreeNodeHeader header, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (destination.Length != NodeHeaderSize)
            throw new ArgumentException(
                $"Node header buffer must be exactly {NodeHeaderSize} bytes, got {destination.Length}.",
                nameof(destination));
        if (header.NodeKind != BTreeNodeKind.Leaf && header.NodeKind != BTreeNodeKind.Internal)
            throw new ArgumentException(
                $"{nameof(BTreeNodeHeader.NodeKind)} must be Leaf (0) or Internal (1), got {(byte)header.NodeKind}.",
                nameof(header));
        if (header.NodeVersion != CurrentNodeVersion)
            throw new ArgumentException(
                $"{nameof(BTreeNodeHeader.NodeVersion)} must be {CurrentNodeVersion}, got {header.NodeVersion}.",
                nameof(header));

        destination[NodeKindOffset] = (byte)header.NodeKind;
        destination[NodeVersionOffset] = header.NodeVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(IndexKindOffset, 2), (ushort)header.IndexKind);
        destination[KeySizeOffset] = header.KeySize;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(ValueSizeOffset, 2), header.ValueSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(EntryCountOffset, 2), header.EntryCount);
        destination.Slice(ReservedOffset, 3).Clear();
    }

    /// <summary>
    /// Serializes a leaf node into a new buffer: the 12-byte node header
    /// followed by EntryCount × (KeySize + ValueSize) entry bytes. Entries must
    /// already be sorted strictly ascending by key (unsigned lexicographic);
    /// every key/value must match the node's declared widths.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// KeySize is 0, there are more than 65535 entries, an entry's key or value
    /// width does not match the declaration, or keys are not strictly ascending.
    /// </exception>
    public static byte[] SerializeLeaf(BTreeLeafNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.KeySize == 0)
            throw new ArgumentException($"{nameof(BTreeLeafNode.KeySize)} must be at least 1.", nameof(node));
        if (node.Entries.Count > ushort.MaxValue)
            throw new ArgumentException(
                $"Leaf EntryCount must fit in 16 bits (max {ushort.MaxValue}), got {node.Entries.Count}.",
                nameof(node));

        long size = GetLeafNodeSize(node.Entries.Count, node.KeySize, node.ValueSize);
        if (size > int.MaxValue)
            throw new ArgumentException($"Serialized leaf node would be {size} bytes, exceeding the 2 GiB buffer limit.", nameof(node));

        var buffer = new byte[size];
        var span = buffer.AsSpan();

        SerializeHeader(new BTreeNodeHeader
        {
            NodeKind = BTreeNodeKind.Leaf,
            NodeVersion = CurrentNodeVersion,
            IndexKind = node.IndexKind,
            KeySize = node.KeySize,
            ValueSize = node.ValueSize,
            EntryCount = (ushort)node.Entries.Count,
        }, span.Slice(0, NodeHeaderSize));

        int offset = NodeHeaderSize;
        for (int i = 0; i < node.Entries.Count; i++)
        {
            var (key, value) = node.Entries[i];
            if (key is null || key.Length != node.KeySize)
                throw new ArgumentException(
                    $"Leaf entry {i} key must be exactly KeySize ({node.KeySize}) bytes, got {(key is null ? "null" : key.Length.ToString())}.",
                    nameof(node));
            if (node.ValueSize == 0)
            {
                if (value is { Length: > 0 })
                    throw new ArgumentException(
                        $"Leaf entry {i} value must be empty (ValueSize is 0), got {value.Length} bytes.",
                        nameof(node));
            }
            else if (value is null || value.Length != node.ValueSize)
            {
                throw new ArgumentException(
                    $"Leaf entry {i} value must be exactly ValueSize ({node.ValueSize}) bytes, got {(value is null ? "null" : value.Length.ToString())}.",
                    nameof(node));
            }
            if (i > 0 && node.Entries[i - 1].Key.AsSpan().SequenceCompareTo(key) >= 0)
                throw new ArgumentException(
                    $"Leaf entries must be sorted strictly ascending by key; entry {i} is not greater than entry {i - 1}.",
                    nameof(node));

            key.CopyTo(span.Slice(offset, node.KeySize));
            offset += node.KeySize;
            if (node.ValueSize > 0)
            {
                value!.CopyTo(span.Slice(offset, node.ValueSize));
                offset += node.ValueSize;
            }
        }

        return buffer;
    }

    /// <summary>
    /// Serializes an internal node into a new buffer: the 12-byte node header,
    /// EntryCount routing keys of KeySize, then EntryCount + 1 child records of
    /// ValueSize. Routing keys must already be sorted strictly ascending
    /// (unsigned lexicographic); every key/child record must match the node's
    /// declared widths.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// KeySize or ValueSize is 0, there are more than 65535 routing keys, the
    /// child record count is not Keys.Count + 1, a key or child record width
    /// does not match the declaration, or keys are not strictly ascending.
    /// </exception>
    public static byte[] SerializeInternal(BTreeInternalNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.KeySize == 0)
            throw new ArgumentException($"{nameof(BTreeInternalNode.KeySize)} must be at least 1.", nameof(node));
        if (node.ValueSize == 0)
            throw new ArgumentException(
                $"{nameof(BTreeInternalNode.ValueSize)} must be at least 1 for internal nodes (child records embed ChildHash).",
                nameof(node));
        if (node.Keys.Count > ushort.MaxValue)
            throw new ArgumentException(
                $"Internal EntryCount must fit in 16 bits (max {ushort.MaxValue}), got {node.Keys.Count}.",
                nameof(node));
        if (node.Children.Count != node.Keys.Count + 1)
            throw new ArgumentException(
                $"Internal node must have exactly EntryCount + 1 child records: {node.Keys.Count} keys require {node.Keys.Count + 1} children, got {node.Children.Count}.",
                nameof(node));

        long size = GetInternalNodeSize(node.Keys.Count, node.KeySize, node.ValueSize);
        if (size > int.MaxValue)
            throw new ArgumentException($"Serialized internal node would be {size} bytes, exceeding the 2 GiB buffer limit.", nameof(node));

        var buffer = new byte[size];
        var span = buffer.AsSpan();

        SerializeHeader(new BTreeNodeHeader
        {
            NodeKind = BTreeNodeKind.Internal,
            NodeVersion = CurrentNodeVersion,
            IndexKind = node.IndexKind,
            KeySize = node.KeySize,
            ValueSize = node.ValueSize,
            EntryCount = (ushort)node.Keys.Count,
        }, span.Slice(0, NodeHeaderSize));

        int offset = NodeHeaderSize;
        for (int i = 0; i < node.Keys.Count; i++)
        {
            var key = node.Keys[i];
            if (key is null || key.Length != node.KeySize)
                throw new ArgumentException(
                    $"Routing key {i} must be exactly KeySize ({node.KeySize}) bytes, got {(key is null ? "null" : key.Length.ToString())}.",
                    nameof(node));
            if (i > 0 && node.Keys[i - 1].AsSpan().SequenceCompareTo(key) >= 0)
                throw new ArgumentException(
                    $"Routing keys must be sorted strictly ascending; key {i} is not greater than key {i - 1}.",
                    nameof(node));

            key.CopyTo(span.Slice(offset, node.KeySize));
            offset += node.KeySize;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child is null || child.Length != node.ValueSize)
                throw new ArgumentException(
                    $"Child record {i} must be exactly ValueSize ({node.ValueSize}) bytes, got {(child is null ? "null" : child.Length.ToString())}.",
                    nameof(node));

            child.CopyTo(span.Slice(offset, node.ValueSize));
            offset += node.ValueSize;
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes and validates the 12-byte node header from the start of a
    /// node payload, then bounds-checks the declared shape against the payload
    /// length: for the header to be accepted, the payload must be EXACTLY the
    /// size implied by NodeKind, EntryCount, KeySize, and ValueSize. This runs
    /// before any body byte is read, so a corrupt or hostile header (e.g. an
    /// EntryCount overflowing the payload) is rejected without any out-of-bounds
    /// read or allocation. IndexKind is not restricted to registered kinds —
    /// widths are declared per node, so future indexes need no format change.
    /// </summary>
    /// <param name="payload">The node's complete serialized payload (header + body).</param>
    public static Result<BTreeNodeHeader> DeserializeHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < NodeHeaderSize)
            return Result<BTreeNodeHeader>.Failure(
                $"Node payload requires at least {NodeHeaderSize} bytes (the node header), got {payload.Length}.");

        var nodeKindByte = payload[NodeKindOffset];
        if (nodeKindByte != (byte)BTreeNodeKind.Leaf && nodeKindByte != (byte)BTreeNodeKind.Internal)
            return Result<BTreeNodeHeader>.Failure(
                $"NodeKind must be 0 (leaf) or 1 (internal), got {nodeKindByte}.");
        var nodeKind = (BTreeNodeKind)nodeKindByte;

        var nodeVersion = payload[NodeVersionOffset];
        if (nodeVersion != CurrentNodeVersion)
            return Result<BTreeNodeHeader>.Failure(
                $"Unsupported node format version: expected {CurrentNodeVersion}, got {nodeVersion}.");

        if (payload[ReservedOffset] != 0 || payload[ReservedOffset + 1] != 0 || payload[ReservedOffset + 2] != 0)
            return Result<BTreeNodeHeader>.Failure("Node header reserved bytes must be 0.");

        var keySize = payload[KeySizeOffset];
        if (keySize == 0)
            return Result<BTreeNodeHeader>.Failure("Node KeySize must be at least 1.");

        var valueSize = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(ValueSizeOffset, 2));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(EntryCountOffset, 2));

        if (nodeKind == BTreeNodeKind.Internal && valueSize == 0)
            return Result<BTreeNodeHeader>.Failure(
                "Internal node ValueSize must be at least 1 (child records embed ChildHash).");

        // Bounds check (spec Section 4): the declared shape must account for the
        // payload exactly. Sizes are computed in 64-bit arithmetic, so a hostile
        // EntryCount × width product cannot overflow into a "valid" length.
        long expectedSize = nodeKind == BTreeNodeKind.Leaf
            ? GetLeafNodeSize(entryCount, keySize, valueSize)
            : GetInternalNodeSize(entryCount, keySize, valueSize);
        if (expectedSize != payload.Length)
            return Result<BTreeNodeHeader>.Failure(
                $"Node header declares EntryCount {entryCount}, KeySize {keySize}, ValueSize {valueSize} " +
                $"({(nodeKind == BTreeNodeKind.Leaf ? "leaf" : "internal")}), requiring exactly {expectedSize} " +
                $"payload bytes, but the payload is {payload.Length} bytes (truncated or corrupt node).");

        return Result<BTreeNodeHeader>.Success(new BTreeNodeHeader
        {
            NodeKind = nodeKind,
            NodeVersion = nodeVersion,
            IndexKind = (BTreeIndexKind)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(IndexKindOffset, 2)),
            KeySize = keySize,
            ValueSize = valueSize,
            EntryCount = entryCount,
        });
    }

    /// <summary>
    /// Deserializes a leaf node payload. The header is validated and its
    /// declared shape bounds-checked against the payload length (see
    /// <see cref="DeserializeHeader"/>) before any entry is read; entries must
    /// be sorted strictly ascending by key (unsigned lexicographic), matching
    /// what <see cref="SerializeLeaf"/> enforces on the way in.
    /// </summary>
    /// <param name="payload">The node's complete serialized payload (header + body).</param>
    public static Result<BTreeLeafNode> DeserializeLeaf(ReadOnlySpan<byte> payload)
    {
        var headerResult = DeserializeHeader(payload);
        if (headerResult.IsFailure)
            return Result<BTreeLeafNode>.Failure(headerResult.Error);

        var header = headerResult.Value;
        if (header.NodeKind != BTreeNodeKind.Leaf)
            return Result<BTreeLeafNode>.Failure(
                $"Expected a leaf node (NodeKind 0), got NodeKind {(byte)header.NodeKind}.");

        var node = new BTreeLeafNode
        {
            IndexKind = header.IndexKind,
            KeySize = header.KeySize,
            ValueSize = header.ValueSize,
            Entries = new List<BTreeLeafEntry>(header.EntryCount),
        };

        int offset = NodeHeaderSize;
        for (int i = 0; i < header.EntryCount; i++)
        {
            var key = payload.Slice(offset, header.KeySize).ToArray();
            offset += header.KeySize;
            var value = header.ValueSize > 0
                ? payload.Slice(offset, header.ValueSize).ToArray()
                : Array.Empty<byte>();
            offset += header.ValueSize;

            if (i > 0 && node.Entries[i - 1].Key.AsSpan().SequenceCompareTo(key) >= 0)
                return Result<BTreeLeafNode>.Failure(
                    $"Leaf entries must be sorted strictly ascending by key; entry {i} is not greater than entry {i - 1} (corrupt node).");

            node.Entries.Add(new BTreeLeafEntry(key, value));
        }

        return Result<BTreeLeafNode>.Success(node);
    }

    /// <summary>
    /// Deserializes an internal node payload. The header is validated and its
    /// declared shape bounds-checked against the payload length (see
    /// <see cref="DeserializeHeader"/>) before any key or child record is read;
    /// routing keys must be sorted strictly ascending (unsigned lexicographic),
    /// matching what <see cref="SerializeInternal"/> enforces on the way in.
    /// The returned node carries EntryCount routing keys and EntryCount + 1
    /// child records.
    /// </summary>
    /// <param name="payload">The node's complete serialized payload (header + body).</param>
    public static Result<BTreeInternalNode> DeserializeInternal(ReadOnlySpan<byte> payload)
    {
        var headerResult = DeserializeHeader(payload);
        if (headerResult.IsFailure)
            return Result<BTreeInternalNode>.Failure(headerResult.Error);

        var header = headerResult.Value;
        if (header.NodeKind != BTreeNodeKind.Internal)
            return Result<BTreeInternalNode>.Failure(
                $"Expected an internal node (NodeKind 1), got NodeKind {(byte)header.NodeKind}.");

        var node = new BTreeInternalNode
        {
            IndexKind = header.IndexKind,
            KeySize = header.KeySize,
            ValueSize = header.ValueSize,
            Keys = new List<byte[]>(header.EntryCount),
            Children = new List<byte[]>(header.EntryCount + 1),
        };

        int offset = NodeHeaderSize;
        for (int i = 0; i < header.EntryCount; i++)
        {
            var key = payload.Slice(offset, header.KeySize).ToArray();
            offset += header.KeySize;

            if (i > 0 && node.Keys[i - 1].AsSpan().SequenceCompareTo(key) >= 0)
                return Result<BTreeInternalNode>.Failure(
                    $"Routing keys must be sorted strictly ascending; key {i} is not greater than key {i - 1} (corrupt node).");

            node.Keys.Add(key);
        }

        for (int i = 0; i < header.EntryCount + 1; i++)
        {
            node.Children.Add(payload.Slice(offset, header.ValueSize).ToArray());
            offset += header.ValueSize;
        }

        return Result<BTreeInternalNode>.Success(node);
    }

    /// <summary>
    /// Computes NodeContentHash: BLAKE3-256 (full 32 bytes) over the node's
    /// complete serialized payload — the 12-byte node header plus the body,
    /// exactly the bytes stored as the block payload. Internal child records
    /// embed this as ChildHash and IndexRoot.RootHash covers the root node
    /// (Merkle integrity, spec Section 6).
    /// </summary>
    public static byte[] ComputeNodeContentHash(ReadOnlySpan<byte> serializedNode)
    {
        var hash = new byte[NodeContentHashSize];
        ComputeNodeContentHash(serializedNode, hash);
        return hash;
    }

    /// <summary>
    /// Computes NodeContentHash into the caller-provided 32-byte buffer.
    /// See <see cref="ComputeNodeContentHash(ReadOnlySpan{byte})"/>.
    /// </summary>
    public static void ComputeNodeContentHash(ReadOnlySpan<byte> serializedNode, Span<byte> destination)
    {
        if (destination.Length != NodeContentHashSize)
            throw new ArgumentException(
                $"NodeContentHash buffer must be exactly {NodeContentHashSize} bytes, got {destination.Length}.",
                nameof(destination));
        if (serializedNode.Length < NodeHeaderSize)
            throw new ArgumentException(
                $"A serialized node is at least {NodeHeaderSize} bytes (the node header), got {serializedNode.Length}.",
                nameof(serializedNode));

        var hash = Hasher.Hash(serializedNode);
        hash.AsSpan().CopyTo(destination);
    }
}
