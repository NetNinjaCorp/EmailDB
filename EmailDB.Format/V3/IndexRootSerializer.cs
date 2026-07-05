using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes the 68-byte IndexRoot descriptor (EmailDB_FileFormat_Spec.md
/// Section 6) — the payload of an IndexRoot block (BlockType 6):
///
///   IndexKind (2) + RootBlockId (16) + EntryCount (8) + TreeHeight (2) +
///   RootHash (32) + Sequence (8) = 68 bytes.
///
/// All multi-byte integers are little-endian (file-wide convention, spec
/// Section 4). Following the same idiom as <see cref="BTreeNodeSerializer"/>:
/// <see cref="Serialize"/> throws <see cref="ArgumentException"/> on invariant
/// violations (serializing an inconsistent descriptor is a programming error),
/// while <see cref="Deserialize"/> returns a <see cref="Result{T}"/> failure and
/// bounds-checks the payload — the length must be EXACTLY 68 bytes and the
/// decoded values must be well-formed — before trusting any field, so a corrupt
/// or truncated descriptor is rejected rather than surfaced as a bad root.
///
/// IndexKind is NOT restricted to the registered kinds: widths are declared per
/// node and future indexes need no format change, matching
/// <see cref="BTreeNodeSerializer.DeserializeHeader"/>.
/// </summary>
public static class IndexRootSerializer
{
    /// <summary>Fixed serialized size of an IndexRoot payload (spec Section 6).</summary>
    public const int IndexRootPayloadSize = 68;

    /// <summary>Size of the RootBlockId field: a 16-byte ULID.</summary>
    public const int RootBlockIdSize = UlidGenerator.UlidSize;

    /// <summary>Size of the RootHash field: full BLAKE3-256.</summary>
    public const int RootHashSize = BTreeNodeSerializer.NodeContentHashSize;

    // Field offsets (spec Section 6 IndexRoot layout, in order).
    private const int IndexKindOffset = 0;    // 2 bytes
    private const int RootBlockIdOffset = 2;  // 16 bytes
    private const int EntryCountOffset = 18;  // 8 bytes
    private const int TreeHeightOffset = 26;  // 2 bytes
    private const int RootHashOffset = 28;    // 32 bytes
    private const int SequenceOffset = 60;    // 8 bytes

    /// <summary>
    /// Serializes an IndexRoot into a new 68-byte buffer at the spec offsets.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// RootBlockId is not 16 bytes, RootHash is not 32 bytes, EntryCount is
    /// negative, or TreeHeight is not in [1, 65535].
    /// </exception>
    public static byte[] Serialize(IndexRoot indexRoot)
    {
        ArgumentNullException.ThrowIfNull(indexRoot);
        if (indexRoot.RootBlockId is null || indexRoot.RootBlockId.Length != RootBlockIdSize)
            throw new ArgumentException(
                $"{nameof(IndexRoot.RootBlockId)} must be exactly {RootBlockIdSize} bytes, " +
                $"got {(indexRoot.RootBlockId is null ? "null" : indexRoot.RootBlockId.Length.ToString())}.",
                nameof(indexRoot));
        if (indexRoot.RootHash is null || indexRoot.RootHash.Length != RootHashSize)
            throw new ArgumentException(
                $"{nameof(IndexRoot.RootHash)} must be exactly {RootHashSize} bytes, " +
                $"got {(indexRoot.RootHash is null ? "null" : indexRoot.RootHash.Length.ToString())}.",
                nameof(indexRoot));
        if (indexRoot.EntryCount < 0)
            throw new ArgumentException(
                $"{nameof(IndexRoot.EntryCount)} must be non-negative, got {indexRoot.EntryCount}.",
                nameof(indexRoot));
        if (indexRoot.TreeHeight < 1 || indexRoot.TreeHeight > ushort.MaxValue)
            throw new ArgumentException(
                $"{nameof(IndexRoot.TreeHeight)} must be in [1, {ushort.MaxValue}], got {indexRoot.TreeHeight}.",
                nameof(indexRoot));

        var buffer = new byte[IndexRootPayloadSize];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(IndexKindOffset, 2), (ushort)indexRoot.IndexKind);
        indexRoot.RootBlockId.CopyTo(span.Slice(RootBlockIdOffset, RootBlockIdSize));
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(EntryCountOffset, 8), indexRoot.EntryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(TreeHeightOffset, 2), (ushort)indexRoot.TreeHeight);
        indexRoot.RootHash.CopyTo(span.Slice(RootHashOffset, RootHashSize));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(SequenceOffset, 8), indexRoot.Sequence);

        return buffer;
    }

    /// <summary>
    /// Deserializes and validates a 68-byte IndexRoot payload. The payload length
    /// is bounds-checked to be EXACTLY <see cref="IndexRootPayloadSize"/> and the
    /// decoded EntryCount/TreeHeight are validated before an IndexRoot is
    /// returned, so a truncated, oversized, or corrupt descriptor yields a
    /// failure rather than a malformed root.
    /// </summary>
    /// <param name="payload">The IndexRoot block's complete serialized payload.</param>
    public static Result<IndexRoot> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != IndexRootPayloadSize)
            return Result<IndexRoot>.Failure(
                $"IndexRoot payload must be exactly {IndexRootPayloadSize} bytes, got {payload.Length} " +
                "(truncated or corrupt descriptor).");

        long entryCount = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(EntryCountOffset, 8));
        if (entryCount < 0)
            return Result<IndexRoot>.Failure(
                $"IndexRoot EntryCount must be non-negative, got {entryCount} (corrupt descriptor).");

        ushort treeHeight = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(TreeHeightOffset, 2));
        if (treeHeight < 1)
            return Result<IndexRoot>.Failure(
                "IndexRoot TreeHeight must be at least 1; a persisted root always references a root node " +
                "(corrupt descriptor).");

        return Result<IndexRoot>.Success(new IndexRoot
        {
            IndexKind = (BTreeIndexKind)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(IndexKindOffset, 2)),
            RootBlockId = payload.Slice(RootBlockIdOffset, RootBlockIdSize).ToArray(),
            EntryCount = entryCount,
            TreeHeight = treeHeight,
            RootHash = payload.Slice(RootHashOffset, RootHashSize).ToArray(),
            Sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(SequenceOffset, 8)),
        });
    }
}
