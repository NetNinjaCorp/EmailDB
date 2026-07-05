namespace EmailDB.Format.V3;

/// <summary>
/// Immutable handle to one version of a copy-on-write B+-tree: the root node
/// reference (with its RootHash), the tree height, and the entry count. Every
/// COW mutation returns a NEW root handle; the previous handle remains a
/// fully readable, consistent snapshot because nodes are never overwritten
/// (spec Section 14, docs/BTree_Index.md Section 1).
///
/// This is the in-memory counterpart of the IndexRoot payload (spec
/// Section 6: RootBlockId + EntryCount + TreeHeight + RootHash); persisting
/// it as an IndexRoot block (BlockType 6) is the flush/checkpoint layer's job.
/// </summary>
public sealed class BTreeRoot
{
    /// <summary>
    /// Reference to the root node, carrying RootHash as its
    /// <see cref="BTreeNodeRef.NodeHash"/> — the hash IndexRoot.RootHash
    /// records and reads verify against.
    /// </summary>
    public required BTreeNodeRef RootRef { get; init; }

    /// <summary>
    /// Tree height: 1 when the root is a leaf; each root split adds 1. A point
    /// lookup reads exactly this many nodes.
    /// </summary>
    public required int Height { get; init; }

    /// <summary>Number of live leaf entries in this tree version.</summary>
    public required long EntryCount { get; init; }

    /// <summary>RootHash: BLAKE3-256 of the root node's serialized payload.</summary>
    public byte[] RootHash => RootRef.NodeHash;
}
