using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes the variable-length Checkpoint block payload (BlockType 9,
/// EmailDB_FileFormat_Spec.md Section 10.1) — see <see cref="Checkpoint"/> for the
/// field layout.
///
/// <para>All multi-byte integers are little-endian (file-wide convention, spec
/// Section 4). Following the idiom of <see cref="IndexRootSerializer"/> and
/// <see cref="BTreeNodeSerializer"/>: <see cref="Serialize"/> throws
/// <see cref="ArgumentException"/> on invariant violations (serializing an
/// inconsistent payload is a programming error), while <see cref="Deserialize"/>
/// returns a <see cref="Result{T}"/> and bounds-checks the payload — the length
/// must match the declared secondary-index count exactly and the decoded counters
/// must be well-formed — before any field is trusted, so a truncated or corrupt
/// Checkpoint is rejected rather than surfaced as a bad commit point.</para>
///
/// <para>IndexKind values in the secondary-index table are NOT restricted to the
/// registered kinds (future indexes need no format change, spec Section 6.1).</para>
/// </summary>
public static class CheckpointSerializer
{
    /// <summary>Bytes of a single ULID+offset root pointer (16 + 8).</summary>
    public const int RootPointerSize = CheckpointRootPointer.BlockIdSize + sizeof(long);

    /// <summary>Bytes of a single secondary-index entry: IndexKind (2) + BlockId (16) + Offset (8).</summary>
    public const int SecondaryIndexEntrySize = sizeof(ushort) + CheckpointRootPointer.BlockIdSize + sizeof(long);

    // Fixed prefix field offsets (spec Section 10.1, in order).
    private const int FormatVersionOffset = 0;                                   // 2
    private const int CheckpointSequenceOffset = 2;                              // 8
    private const int FileIdOffset = 10;                                        // 16
    private const int FolderTreeRootOffset = 26;                               // 24
    private const int PrimaryIndexRootOffset = 50;                             // 24
    private const int LocationIndexRootOffset = 74;                            // 24
    private const int MetadataRootOffset = 98;                                 // 24
    private const int KeyStoreRootOffset = 122;                                // 24
    private const int PreviousCheckpointOffset = 146;                          // 24
    private const int SecondaryIndexCountOffset = 170;                         // 2
    private const int SecondaryIndexesOffset = 172;

    /// <summary>Bytes of the fixed prefix up to and including SecondaryIndexCount.</summary>
    public const int FixedPrefixSize = SecondaryIndexesOffset;

    /// <summary>Bytes of the fixed trailer after the secondary-index table: LiveBlockCount + LiveByteCount + DeadByteCount.</summary>
    public const int TrailerSize = 3 * sizeof(long);

    /// <summary>Smallest possible payload: fixed prefix + trailer, with zero secondary indexes.</summary>
    public const int MinPayloadSize = FixedPrefixSize + TrailerSize;

    /// <summary>Serialized byte length of a Checkpoint carrying <paramref name="secondaryIndexCount"/> entries.</summary>
    public static int PayloadSize(int secondaryIndexCount) =>
        MinPayloadSize + secondaryIndexCount * SecondaryIndexEntrySize;

    /// <summary>
    /// Serializes a Checkpoint into a new little-endian buffer at the spec offsets.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// FileId is not 16 bytes, any root pointer is malformed, a byte/block counter
    /// is negative, or more than <see cref="Checkpoint.MaxSecondaryIndexCount"/>
    /// secondary indexes are supplied.
    /// </exception>
    public static byte[] Serialize(Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (checkpoint.FileId is null || checkpoint.FileId.Length != Checkpoint.FileIdSize)
            throw new ArgumentException(
                $"{nameof(Checkpoint.FileId)} must be exactly {Checkpoint.FileIdSize} bytes, " +
                $"got {(checkpoint.FileId is null ? "null" : checkpoint.FileId.Length.ToString())}.",
                nameof(checkpoint));

        IReadOnlyList<CheckpointSecondaryIndex> secondaries =
            checkpoint.SecondaryIndexes ?? throw new ArgumentException(
                $"{nameof(Checkpoint.SecondaryIndexes)} must not be null.", nameof(checkpoint));
        if (secondaries.Count > Checkpoint.MaxSecondaryIndexCount)
            throw new ArgumentException(
                $"{nameof(Checkpoint.SecondaryIndexes)} count {secondaries.Count} exceeds the maximum " +
                $"{Checkpoint.MaxSecondaryIndexCount} (SecondaryIndexCount is a 16-bit field).",
                nameof(checkpoint));

        ThrowIfNegative(checkpoint.LiveBlockCount, nameof(Checkpoint.LiveBlockCount));
        ThrowIfNegative(checkpoint.LiveByteCount, nameof(Checkpoint.LiveByteCount));
        ThrowIfNegative(checkpoint.DeadByteCount, nameof(Checkpoint.DeadByteCount));

        var buffer = new byte[PayloadSize(secondaries.Count)];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(FormatVersionOffset, 2), checkpoint.FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(CheckpointSequenceOffset, 8), checkpoint.CheckpointSequence);
        checkpoint.FileId.CopyTo(span.Slice(FileIdOffset, Checkpoint.FileIdSize));

        WriteRootPointer(span.Slice(FolderTreeRootOffset, RootPointerSize), checkpoint.FolderTreeRoot, nameof(Checkpoint.FolderTreeRoot));
        WriteRootPointer(span.Slice(PrimaryIndexRootOffset, RootPointerSize), checkpoint.PrimaryIndexRoot, nameof(Checkpoint.PrimaryIndexRoot));
        WriteRootPointer(span.Slice(LocationIndexRootOffset, RootPointerSize), checkpoint.LocationIndexRoot, nameof(Checkpoint.LocationIndexRoot));
        WriteRootPointer(span.Slice(MetadataRootOffset, RootPointerSize), checkpoint.MetadataRoot, nameof(Checkpoint.MetadataRoot));
        WriteRootPointer(span.Slice(KeyStoreRootOffset, RootPointerSize), checkpoint.KeyStoreRoot, nameof(Checkpoint.KeyStoreRoot));
        WriteRootPointer(span.Slice(PreviousCheckpointOffset, RootPointerSize), checkpoint.PreviousCheckpoint, nameof(Checkpoint.PreviousCheckpoint));

        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(SecondaryIndexCountOffset, 2), (ushort)secondaries.Count);

        int cursor = SecondaryIndexesOffset;
        for (int i = 0; i < secondaries.Count; i++)
        {
            CheckpointSecondaryIndex entry = secondaries[i] ?? throw new ArgumentException(
                $"{nameof(Checkpoint.SecondaryIndexes)}[{i}] must not be null.", nameof(checkpoint));
            var entrySpan = span.Slice(cursor, SecondaryIndexEntrySize);
            BinaryPrimitives.WriteUInt16LittleEndian(entrySpan.Slice(0, 2), (ushort)entry.IndexKind);
            WriteRootPointer(entrySpan.Slice(2, RootPointerSize), entry.Pointer, $"{nameof(Checkpoint.SecondaryIndexes)}[{i}]");
            cursor += SecondaryIndexEntrySize;
        }

        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(cursor, 8), checkpoint.LiveBlockCount);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(cursor + 8, 8), checkpoint.LiveByteCount);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(cursor + 16, 8), checkpoint.DeadByteCount);

        return buffer;
    }

    /// <summary>
    /// Deserializes and validates a Checkpoint payload. The declared
    /// SecondaryIndexCount must account for the payload length exactly, and the
    /// decoded byte/block counters must be non-negative, so a truncated, oversized,
    /// or corrupt payload yields a failure rather than a malformed commit point.
    /// </summary>
    /// <param name="payload">The Checkpoint block's complete serialized payload.</param>
    public static Result<Checkpoint> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinPayloadSize)
            return Result<Checkpoint>.Failure(
                $"Checkpoint payload must be at least {MinPayloadSize} bytes, got {payload.Length} " +
                "(truncated or corrupt payload).");

        int secondaryCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(SecondaryIndexCountOffset, 2));
        int expectedLength = PayloadSize(secondaryCount);
        if (payload.Length != expectedLength)
            return Result<Checkpoint>.Failure(
                $"Checkpoint payload length {payload.Length} does not match the declared SecondaryIndexCount " +
                $"{secondaryCount} (expected {expectedLength} bytes; truncated, oversized, or corrupt payload).");

        var secondaries = new CheckpointSecondaryIndex[secondaryCount];
        int cursor = SecondaryIndexesOffset;
        for (int i = 0; i < secondaryCount; i++)
        {
            var entrySpan = payload.Slice(cursor, SecondaryIndexEntrySize);
            secondaries[i] = new CheckpointSecondaryIndex
            {
                IndexKind = (BTreeIndexKind)BinaryPrimitives.ReadUInt16LittleEndian(entrySpan.Slice(0, 2)),
                Pointer = ReadRootPointer(entrySpan.Slice(2, RootPointerSize)),
            };
            cursor += SecondaryIndexEntrySize;
        }

        long liveBlockCount = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor, 8));
        long liveByteCount = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor + 8, 8));
        long deadByteCount = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor + 16, 8));
        if (liveBlockCount < 0)
            return Result<Checkpoint>.Failure(
                $"Checkpoint LiveBlockCount must be non-negative, got {liveBlockCount} (corrupt payload).");
        if (liveByteCount < 0)
            return Result<Checkpoint>.Failure(
                $"Checkpoint LiveByteCount must be non-negative, got {liveByteCount} (corrupt payload).");
        if (deadByteCount < 0)
            return Result<Checkpoint>.Failure(
                $"Checkpoint DeadByteCount must be non-negative, got {deadByteCount} (corrupt payload).");

        return Result<Checkpoint>.Success(new Checkpoint
        {
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(FormatVersionOffset, 2)),
            CheckpointSequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(CheckpointSequenceOffset, 8)),
            FileId = payload.Slice(FileIdOffset, Checkpoint.FileIdSize).ToArray(),
            FolderTreeRoot = ReadRootPointer(payload.Slice(FolderTreeRootOffset, RootPointerSize)),
            PrimaryIndexRoot = ReadRootPointer(payload.Slice(PrimaryIndexRootOffset, RootPointerSize)),
            LocationIndexRoot = ReadRootPointer(payload.Slice(LocationIndexRootOffset, RootPointerSize)),
            MetadataRoot = ReadRootPointer(payload.Slice(MetadataRootOffset, RootPointerSize)),
            KeyStoreRoot = ReadRootPointer(payload.Slice(KeyStoreRootOffset, RootPointerSize)),
            PreviousCheckpoint = ReadRootPointer(payload.Slice(PreviousCheckpointOffset, RootPointerSize)),
            SecondaryIndexes = secondaries,
            LiveBlockCount = liveBlockCount,
            LiveByteCount = liveByteCount,
            DeadByteCount = deadByteCount,
        });
    }

    private static void WriteRootPointer(Span<byte> destination, CheckpointRootPointer pointer, string fieldName)
    {
        if (pointer is null)
            throw new ArgumentException($"{fieldName} must not be null.", nameof(pointer));
        if (pointer.BlockId is null || pointer.BlockId.Length != CheckpointRootPointer.BlockIdSize)
            throw new ArgumentException(
                $"{fieldName}.BlockId must be exactly {CheckpointRootPointer.BlockIdSize} bytes, " +
                $"got {(pointer.BlockId is null ? "null" : pointer.BlockId.Length.ToString())}.",
                nameof(pointer));
        if (pointer.Offset < 0)
            throw new ArgumentException(
                $"{fieldName}.Offset must be non-negative, got {pointer.Offset}.", nameof(pointer));

        pointer.BlockId.CopyTo(destination.Slice(0, CheckpointRootPointer.BlockIdSize));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(CheckpointRootPointer.BlockIdSize, 8), pointer.Offset);
    }

    private static CheckpointRootPointer ReadRootPointer(ReadOnlySpan<byte> source) =>
        new()
        {
            BlockId = source.Slice(0, CheckpointRootPointer.BlockIdSize).ToArray(),
            Offset = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(CheckpointRootPointer.BlockIdSize, 8)),
        };

    private static void ThrowIfNegative(long value, string fieldName)
    {
        if (value < 0)
            throw new ArgumentException($"{fieldName} must be non-negative, got {value}.", nameof(value));
    }
}
