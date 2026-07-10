using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes the variable-length Cleanup block payload (BlockType 3,
/// EmailDB_FileFormat_Spec.md Section 5; docs/Compaction.md Section 3) — see
/// <see cref="CleanupBlock"/> for the field layout.
///
/// <para>Layout (all multi-byte integers little-endian, file-wide convention, spec Section 4):</para>
///
/// <code>
///   CheckpointSequence (8) + EntryCount (4) + EntryCount × { BlockId (16) + TotalBlockLength (8) }
/// </code>
///
/// <para>Following the idiom of <see cref="CheckpointSerializer"/> and
/// <see cref="WalSerializer"/>: <see cref="Serialize"/> throws
/// <see cref="ArgumentException"/> on invariant violations (serializing an
/// inconsistent payload is a programming error), while <see cref="Deserialize"/>
/// returns a <see cref="Result{T}"/> and bounds-checks the declared entry count
/// against the actual payload length BEFORE reading a body byte, so a truncated,
/// oversized, or corrupt Cleanup block is rejected rather than trusted as an audit
/// record. A non-positive superseded block length is rejected on both paths.</para>
/// </summary>
public static class CleanupSerializer
{
    // Fixed prefix field offsets (in order).
    private const int CheckpointSequenceOffset = 0;   // 8
    private const int EntryCountOffset = 8;            // 4
    private const int EntriesOffset = 12;

    /// <summary>Bytes of the fixed prefix: CheckpointSequence + EntryCount.</summary>
    public const int FixedPrefixSize = EntriesOffset;

    /// <summary>Bytes of a single superseded-block entry: BlockId (16) + TotalBlockLength (8).</summary>
    public const int EntrySize = SupersededBlockRecord.BlockIdSize + sizeof(long);

    /// <summary>Serialized byte length of a Cleanup block carrying <paramref name="entryCount"/> entries.</summary>
    public static int PayloadSize(int entryCount) => FixedPrefixSize + entryCount * EntrySize;

    /// <summary>
    /// Serializes a Cleanup block into a new little-endian buffer at the layout above.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The block or its entry list is null, more than <see cref="int.MaxValue"/> entries are
    /// supplied, an entry is null, a BlockId is not 16 bytes, or a block length is non-positive.
    /// </exception>
    public static byte[] Serialize(CleanupBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        IReadOnlyList<SupersededBlockRecord> entries =
            block.SupersededBlocks ?? throw new ArgumentException(
                $"{nameof(CleanupBlock.SupersededBlocks)} must not be null.", nameof(block));

        var buffer = new byte[PayloadSize(entries.Count)];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(CheckpointSequenceOffset, 8), block.CheckpointSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(EntryCountOffset, 4), (uint)entries.Count);

        int cursor = EntriesOffset;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentException(
                $"{nameof(CleanupBlock.SupersededBlocks)}[{i}] must not be null.", nameof(block));
            if (entry.BlockId is null || entry.BlockId.Length != SupersededBlockRecord.BlockIdSize)
                throw new ArgumentException(
                    $"{nameof(CleanupBlock.SupersededBlocks)}[{i}].BlockId must be exactly " +
                    $"{SupersededBlockRecord.BlockIdSize} bytes, got " +
                    $"{(entry.BlockId is null ? "null" : entry.BlockId.Length.ToString())}.", nameof(block));
            if (entry.TotalBlockLength <= 0)
                throw new ArgumentException(
                    $"{nameof(CleanupBlock.SupersededBlocks)}[{i}].TotalBlockLength must be positive, " +
                    $"got {entry.TotalBlockLength}.", nameof(block));

            var entrySpan = span.Slice(cursor, EntrySize);
            entry.BlockId.CopyTo(entrySpan.Slice(0, SupersededBlockRecord.BlockIdSize));
            BinaryPrimitives.WriteInt64LittleEndian(
                entrySpan.Slice(SupersededBlockRecord.BlockIdSize, 8), entry.TotalBlockLength);
            cursor += EntrySize;
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes and validates a Cleanup block payload. The declared EntryCount must account
    /// for the payload length exactly, and every decoded block length must be positive, so a
    /// truncated, oversized, or corrupt payload yields a failure rather than a bad audit record.
    /// </summary>
    /// <param name="payload">The Cleanup block's complete serialized payload.</param>
    public static Result<CleanupBlock> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPrefixSize)
            return Result<CleanupBlock>.Failure(
                $"Cleanup payload must be at least {FixedPrefixSize} bytes, got {payload.Length} " +
                "(truncated or corrupt payload).");

        ulong checkpointSequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(CheckpointSequenceOffset, 8));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(EntryCountOffset, 4));

        // Guard the multiplication against overflow before comparing to the actual length.
        long expectedLength = (long)FixedPrefixSize + (long)entryCount * EntrySize;
        if (payload.Length != expectedLength)
            return Result<CleanupBlock>.Failure(
                $"Cleanup payload length {payload.Length} does not match the declared EntryCount " +
                $"{entryCount} (expected {expectedLength} bytes; truncated, oversized, or corrupt payload).");

        var records = new SupersededBlockRecord[entryCount];
        int cursor = EntriesOffset;
        for (int i = 0; i < entryCount; i++)
        {
            var entrySpan = payload.Slice(cursor, EntrySize);
            long length = BinaryPrimitives.ReadInt64LittleEndian(
                entrySpan.Slice(SupersededBlockRecord.BlockIdSize, 8));
            if (length <= 0)
                return Result<CleanupBlock>.Failure(
                    $"Cleanup entry [{i}] TotalBlockLength must be positive, got {length} (corrupt payload).");

            records[i] = new SupersededBlockRecord
            {
                BlockId = entrySpan.Slice(0, SupersededBlockRecord.BlockIdSize).ToArray(),
                TotalBlockLength = length,
            };
            cursor += EntrySize;
        }

        return Result<CleanupBlock>.Success(new CleanupBlock
        {
            CheckpointSequence = checkpointSequence,
            SupersededBlocks = records,
        });
    }
}
