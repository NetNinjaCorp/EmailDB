using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of an FTSSegmentMeta block (BlockType 14, docs/Search.md Phase 1):
/// the header describing one immutable <b>segment</b> of the trigram index. A segment is
/// a self-contained slice — one <see cref="FtsTermDictionary"/> and the posting lists it
/// points at — built from a batch of ingested emails. Modelling the index as a set of
/// segments (an LSM-style layout) is what lets ingest and delete (task 91-7) grow the
/// index by appending a new segment and retiring old ones without rewriting the whole
/// index, and lets the query path (task 91-8) union results across the live segments
/// named by the <see cref="FtsSearchRoot"/>. Always encrypted (spec Section 9.5).
///
/// <para>Layout (serialized by <see cref="FtsSegmentMetaSerializer"/>, little-endian per
/// spec Section 4):</para>
/// <code>
///   SegmentSequence       (uint64, 8)   — monotonic segment id (order segments were built)
///   TermDictionaryBlockId (16)          — ULID of this segment's FTSTermDictionary block
///   TrigramCount          (uint32, 4)   — distinct trigrams in the segment (= dict entries)
///   EmailCount            (uint32, 4)   — emails indexed into the segment
/// </code>
/// </summary>
public sealed class FtsSegmentMeta
{
    /// <summary>Number of raw bytes in a block ULID.</summary>
    public const int BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>Monotonic id ordering segments by build order; newer segments have a higher sequence.</summary>
    public required ulong SegmentSequence { get; init; }

    /// <summary>16-byte ULID of this segment's <see cref="FtsTermDictionary"/> block.</summary>
    public required byte[] TermDictionaryBlockId { get; init; }

    /// <summary>Distinct trigrams in the segment (equals the term dictionary's entry count).</summary>
    public required uint TrigramCount { get; init; }

    /// <summary>Number of emails indexed into the segment.</summary>
    public required uint EmailCount { get; init; }

    /// <summary>Creates a segment-meta model, validating the term-dictionary block-id width.</summary>
    /// <exception cref="ArgumentException"><paramref name="termDictionaryBlockId"/> is not <see cref="BlockIdSize"/> bytes.</exception>
    public static FtsSegmentMeta Create(
        ulong segmentSequence, byte[] termDictionaryBlockId, uint trigramCount, uint emailCount)
    {
        ArgumentNullException.ThrowIfNull(termDictionaryBlockId);
        if (termDictionaryBlockId.Length != BlockIdSize)
            throw new ArgumentException(
                $"TermDictionaryBlockId must be exactly {BlockIdSize} bytes, got {termDictionaryBlockId.Length}.",
                nameof(termDictionaryBlockId));
        return new FtsSegmentMeta
        {
            SegmentSequence = segmentSequence,
            TermDictionaryBlockId = (byte[])termDictionaryBlockId.Clone(),
            TrigramCount = trigramCount,
            EmailCount = emailCount,
        };
    }
}

/// <summary>Serializes the fixed-size <see cref="FtsSegmentMeta"/> payload (BlockType 14).</summary>
public static class FtsSegmentMetaSerializer
{
    private const int SegmentSequenceOffset = 0;                              // 8
    private const int TermDictionaryBlockIdOffset = 8;                        // 16
    private const int TrigramCountOffset = 24;                               // 4
    private const int EmailCountOffset = 28;                                 // 4

    /// <summary>Fixed payload size in bytes.</summary>
    public const int PayloadSize = 32;

    /// <exception cref="ArgumentException">The term-dictionary block id is null or not the right width.</exception>
    public static byte[] Serialize(FtsSegmentMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (meta.TermDictionaryBlockId is null || meta.TermDictionaryBlockId.Length != FtsSegmentMeta.BlockIdSize)
            throw new ArgumentException(
                $"{nameof(FtsSegmentMeta.TermDictionaryBlockId)} must be exactly {FtsSegmentMeta.BlockIdSize} bytes.",
                nameof(meta));

        var buffer = new byte[PayloadSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(SegmentSequenceOffset, 8), meta.SegmentSequence);
        meta.TermDictionaryBlockId.CopyTo(span.Slice(TermDictionaryBlockIdOffset, FtsSegmentMeta.BlockIdSize));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(TrigramCountOffset, 4), meta.TrigramCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(EmailCountOffset, 4), meta.EmailCount);
        return buffer;
    }

    public static Result<FtsSegmentMeta> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadSize)
            return Result<FtsSegmentMeta>.Failure(
                $"FTS segment meta payload must be exactly {PayloadSize} bytes, got {payload.Length} " +
                "(truncated, oversized, or corrupt).");

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(SegmentSequenceOffset, 8));
        var blockId = payload.Slice(TermDictionaryBlockIdOffset, FtsSegmentMeta.BlockIdSize).ToArray();
        uint trigramCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(TrigramCountOffset, 4));
        uint emailCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(EmailCountOffset, 4));

        return Result<FtsSegmentMeta>.Success(new FtsSegmentMeta
        {
            SegmentSequence = sequence,
            TermDictionaryBlockId = blockId,
            TrigramCount = trigramCount,
            EmailCount = emailCount,
        });
    }
}
