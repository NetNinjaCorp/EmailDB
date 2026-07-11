using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of an FTSSearchRoot block (BlockType 17, docs/Search.md Phase 1): the
/// single entry point to the address trigram index. It lists the <see cref="FtsSegmentMeta"/>
/// blocks of every live segment, and its own block is what gets registered in the
/// Checkpoint's generic secondary-index table under <see cref="BTreeIndexKind.Fts"/>
/// (IndexKind 3, spec Sections 6.1 &amp; 10.1) — so recovery reopens the whole index by
/// reading one pointer from the authoritative Checkpoint, exactly as the Date index reopens
/// from its IndexKind-2 entry (<see cref="DateIndex.ToCheckpointSecondaryIndex"/>).
///
/// <para>Ingest and delete (task 91-7) advance the index by writing a new set of segment
/// blocks and appending a new SearchRoot naming the new live-segment set, then re-registering
/// it in the next Checkpoint; the query path (task 91-8) reads the current root, then each of
/// its segments. Always encrypted (spec Section 9.5). Segment ids are stored in ascending
/// build order (<see cref="FtsSegmentMeta.SegmentSequence"/>) for determinism, though the
/// query path may consult segments in any order.</para>
///
/// <para>Layout (serialized by <see cref="FtsSearchRootSerializer"/>, little-endian per spec
/// Section 4):</para>
/// <code>
///   SearchRootSequence (uint64, 8)   — monotonic root version (advances every rebuild)
///   SegmentCount       (uint32, 4)
///   Segments[]         (SegmentMetaBlockId (16) × SegmentCount)  — ULIDs of FTSSegmentMeta blocks
/// </code>
/// </summary>
public sealed class FtsSearchRoot
{
    /// <summary>Number of raw bytes in a block ULID.</summary>
    public const int BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>Monotonic version of the root; advances each time the live-segment set changes.</summary>
    public required ulong SearchRootSequence { get; init; }

    /// <summary>ULIDs of the live segments' <see cref="FtsSegmentMeta"/> blocks (may be empty for a fresh index).</summary>
    public required IReadOnlyList<byte[]> SegmentMetaBlockIds { get; init; }

    /// <summary>Number of live segments.</summary>
    public int SegmentCount => SegmentMetaBlockIds.Count;

    /// <summary>Creates a root, validating every segment-meta block-id width.</summary>
    /// <exception cref="ArgumentException">A segment-meta block id is null or not <see cref="BlockIdSize"/> bytes.</exception>
    public static FtsSearchRoot Create(ulong searchRootSequence, IEnumerable<byte[]> segmentMetaBlockIds)
    {
        ArgumentNullException.ThrowIfNull(segmentMetaBlockIds);
        var ids = new List<byte[]>();
        foreach (var id in segmentMetaBlockIds)
        {
            if (id is null || id.Length != BlockIdSize)
                throw new ArgumentException(
                    $"Segment-meta block id must be exactly {BlockIdSize} bytes.", nameof(segmentMetaBlockIds));
            ids.Add((byte[])id.Clone());
        }
        return new FtsSearchRoot { SearchRootSequence = searchRootSequence, SegmentMetaBlockIds = ids };
    }

    /// <summary>
    /// Builds the Checkpoint secondary-index entry that registers this root under
    /// <see cref="BTreeIndexKind.Fts"/> (IndexKind 3). The caller supplies the file offset the
    /// FTSSearchRoot block was appended at (a verified hint) and its ULID; recovery reads this
    /// entry back from the Checkpoint to reopen the index (spec Sections 6.1, 10.1) — mirrors
    /// <see cref="DateIndex.ToCheckpointSecondaryIndex"/>.
    /// </summary>
    /// <param name="searchRootBlockId">The 16-byte ULID of this FTSSearchRoot block.</param>
    /// <param name="searchRootOffset">The file offset the root block lives at (non-negative).</param>
    /// <exception cref="ArgumentException"><paramref name="searchRootBlockId"/> is not 16 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="searchRootOffset"/> is negative.</exception>
    public static CheckpointSecondaryIndex ToCheckpointSecondaryIndex(
        byte[] searchRootBlockId, long searchRootOffset) =>
        CheckpointSecondaryIndex.Create(BTreeIndexKind.Fts, searchRootBlockId, searchRootOffset);
}

/// <summary>Serializes the variable-length <see cref="FtsSearchRoot"/> payload (BlockType 17).</summary>
public static class FtsSearchRootSerializer
{
    private const int SearchRootSequenceOffset = 0;   // 8
    private const int SegmentCountOffset = 8;         // 4
    private const int SegmentsOffset = 12;

    /// <summary>Bytes of the fixed prefix: SearchRootSequence (8) + SegmentCount (4).</summary>
    public const int FixedPrefixSize = SegmentsOffset;

    /// <summary>Serialized length of a root naming <paramref name="segmentCount"/> segments.</summary>
    public static int PayloadSize(int segmentCount) =>
        FixedPrefixSize + segmentCount * FtsSearchRoot.BlockIdSize;

    /// <exception cref="ArgumentException">The segment id list is null or an id has the wrong width.</exception>
    public static byte[] Serialize(FtsSearchRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        IReadOnlyList<byte[]> ids = root.SegmentMetaBlockIds
            ?? throw new ArgumentException($"{nameof(FtsSearchRoot.SegmentMetaBlockIds)} must not be null.", nameof(root));

        var buffer = new byte[PayloadSize(ids.Count)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(SearchRootSequenceOffset, 8), root.SearchRootSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(SegmentCountOffset, 4), (uint)ids.Count);

        int cursor = SegmentsOffset;
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] is null || ids[i].Length != FtsSearchRoot.BlockIdSize)
                throw new ArgumentException(
                    $"{nameof(FtsSearchRoot.SegmentMetaBlockIds)}[{i}] must be exactly " +
                    $"{FtsSearchRoot.BlockIdSize} bytes.", nameof(root));
            ids[i].CopyTo(span.Slice(cursor, FtsSearchRoot.BlockIdSize));
            cursor += FtsSearchRoot.BlockIdSize;
        }
        return buffer;
    }

    public static Result<FtsSearchRoot> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPrefixSize)
            return Result<FtsSearchRoot>.Failure(
                $"FTS search root payload must be at least {FixedPrefixSize} bytes, got {payload.Length}.");

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(SearchRootSequenceOffset, 8));
        uint segmentCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(SegmentCountOffset, 4));
        long expected = (long)FixedPrefixSize + (long)segmentCount * FtsSearchRoot.BlockIdSize;
        if (payload.Length != expected)
            return Result<FtsSearchRoot>.Failure(
                $"FTS search root length {payload.Length} does not match declared SegmentCount {segmentCount} " +
                $"(expected {expected} bytes; truncated, oversized, or corrupt).");

        var ids = new byte[segmentCount][];
        int cursor = SegmentsOffset;
        for (int i = 0; i < segmentCount; i++)
        {
            ids[i] = payload.Slice(cursor, FtsSearchRoot.BlockIdSize).ToArray();
            cursor += FtsSearchRoot.BlockIdSize;
        }

        return Result<FtsSearchRoot>.Success(new FtsSearchRoot
        {
            SearchRootSequence = sequence,
            SegmentMetaBlockIds = ids,
        });
    }
}
