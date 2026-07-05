namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of a generic B+-tree internal node (EmailDB_FileFormat_Spec.md
/// Section 6). On disk the body is <c>EntryCount</c> routing keys of KeySize
/// bytes, then <c>EntryCount + 1</c> child records of ValueSize bytes, preceded
/// by the 12-byte node header.
///
/// Child records are opaque fixed-width byte strings here; per spec Section 6.1
/// they embed the Merkle <c>ChildHash</c> (BLAKE3-256 of the child node's full
/// serialized payload, see <see cref="BTreeNodeSerializer.ComputeNodeContentHash(ReadOnlySpan{byte})"/>)
/// after either a ChildBlockId (IndexKinds 0/2) or a ChildOffset (IndexKind 1).
/// </summary>
public sealed class BTreeInternalNode
{
    /// <summary>Which index this node belongs to (spec Section 6.1).</summary>
    public BTreeIndexKind IndexKind { get; set; }

    /// <summary>Bytes per routing key (must be at least 1).</summary>
    public byte KeySize { get; set; }

    /// <summary>Bytes per child record (must be at least 1).</summary>
    public ushort ValueSize { get; set; }

    /// <summary>
    /// The routing keys, sorted strictly ascending by key (unsigned lexicographic
    /// byte order); each is exactly <see cref="KeySize"/> bytes. The header's
    /// EntryCount is this list's count.
    /// </summary>
    public List<byte[]> Keys { get; init; } = new();

    /// <summary>
    /// The child records; each is exactly <see cref="ValueSize"/> bytes and there
    /// must be exactly <c>Keys.Count + 1</c> of them.
    /// </summary>
    public List<byte[]> Children { get; init; } = new();
}
