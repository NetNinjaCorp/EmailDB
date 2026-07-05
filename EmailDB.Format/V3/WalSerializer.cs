using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes the variable-length WAL block payload (BlockType 1,
/// EmailDB_FileFormat_Spec.md Section 10.4) — see <see cref="WalBlock"/> and
/// <see cref="WalEntry"/> for the field layout.
///
/// <para>Layout (all multi-byte integers little-endian, file-wide convention,
/// spec Section 4):</para>
///
///   WalSequence (8) + CheckpointBlockId (16) + EntryCount (4) +
///   EntryCount × { Op (1) + Key (32) + BlockId (16) + AuxLength (4) + Aux }
///
/// <para>Following the idiom of <see cref="CheckpointSerializer"/> and
/// <see cref="BTreeNodeSerializer"/>: <see cref="Serialize"/> throws
/// <see cref="ArgumentException"/> on invariant violations (serializing an
/// inconsistent payload is a programming error), while <see cref="Deserialize"/>
/// returns a <see cref="Result{T}"/> and bounds-checks every declared count and
/// length against the actual payload size BEFORE reading a body byte (spec
/// Section 4), so a truncated, oversized, or corrupt WAL block is rejected rather
/// than replayed. Unknown op bytes and an all-zero CheckpointBlockId are rejected
/// too — a WAL block must fence against a real Checkpoint (Section 10.4).</para>
/// </summary>
public static class WalSerializer
{
    // Fixed prefix field offsets (spec Section 10.4, in order).
    private const int WalSequenceOffset = 0;                                    // 8
    private const int CheckpointBlockIdOffset = 8;                              // 16
    private const int EntryCountOffset = 24;                                    // 4
    private const int EntriesOffset = 28;

    /// <summary>Bytes of the fixed prefix: WalSequence + CheckpointBlockId + EntryCount.</summary>
    public const int FixedPrefixSize = EntriesOffset;

    /// <summary>Bytes of a single entry's fixed part: Op + Key + BlockId + AuxLength (aux bytes follow).</summary>
    public const int EntryFixedSize =
        sizeof(byte) + WalEntry.KeySize + WalEntry.BlockIdSize + sizeof(int);

    /// <summary>Smallest possible payload: fixed prefix with zero entries.</summary>
    public const int MinPayloadSize = FixedPrefixSize;

    /// <summary>Largest entry count representable (payload cap is enforced by the block layer).</summary>
    public const int MaxEntryCount = int.MaxValue;

    // Per-entry field offsets within an entry (relative to the entry start).
    private const int OpOffset = 0;                                             // 1
    private const int KeyOffset = 1;                                            // 32
    private const int BlockIdOffset = KeyOffset + WalEntry.KeySize;             // 16
    private const int AuxLengthOffset = BlockIdOffset + WalEntry.BlockIdSize;   // 4
    private const int AuxOffset = AuxLengthOffset + sizeof(int);

    /// <summary>
    /// Serializes a WAL block into a new little-endian buffer at the spec offsets.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The CheckpointBlockId is not 16 bytes or is all-zero, the entry list is
    /// null, or any entry has a wrong-width key/BlockId, a null aux, or an
    /// undefined op.
    /// </exception>
    public static byte[] Serialize(WalBlock wal)
    {
        ArgumentNullException.ThrowIfNull(wal);

        if (wal.CheckpointBlockId is null || wal.CheckpointBlockId.Length != WalBlock.CheckpointBlockIdSize)
            throw new ArgumentException(
                $"{nameof(WalBlock.CheckpointBlockId)} must be exactly {WalBlock.CheckpointBlockIdSize} bytes, " +
                $"got {(wal.CheckpointBlockId is null ? "null" : wal.CheckpointBlockId.Length.ToString())}.",
                nameof(wal));
        if (IsAllZero(wal.CheckpointBlockId))
            throw new ArgumentException(
                $"{nameof(WalBlock.CheckpointBlockId)} must reference a real Checkpoint (not the all-zero ULID); " +
                "a WAL block always fences against a committed Checkpoint (spec Section 10.4).",
                nameof(wal));

        IReadOnlyList<WalEntry> entries = wal.Entries
            ?? throw new ArgumentException($"{nameof(WalBlock.Entries)} must not be null.", nameof(wal));

        // Validate every entry up front and total the payload size.
        long totalSize = FixedPrefixSize;
        for (int i = 0; i < entries.Count; i++)
        {
            WalEntry entry = entries[i]
                ?? throw new ArgumentException($"{nameof(WalBlock.Entries)}[{i}] must not be null.", nameof(wal));
            if (!Enum.IsDefined(entry.Op))
                throw new ArgumentException(
                    $"{nameof(WalBlock.Entries)}[{i}].Op is an undefined WalOpKind ({(byte)entry.Op}).", nameof(wal));
            if (entry.Key is null || entry.Key.Length != WalEntry.KeySize)
                throw new ArgumentException(
                    $"{nameof(WalBlock.Entries)}[{i}].Key must be exactly {WalEntry.KeySize} bytes, " +
                    $"got {(entry.Key is null ? "null" : entry.Key.Length.ToString())}.", nameof(wal));
            if (entry.BlockId is null || entry.BlockId.Length != WalEntry.BlockIdSize)
                throw new ArgumentException(
                    $"{nameof(WalBlock.Entries)}[{i}].BlockId must be exactly {WalEntry.BlockIdSize} bytes, " +
                    $"got {(entry.BlockId is null ? "null" : entry.BlockId.Length.ToString())}.", nameof(wal));
            if (entry.Aux is null)
                throw new ArgumentException(
                    $"{nameof(WalBlock.Entries)}[{i}].Aux must not be null (use an empty array).", nameof(wal));

            totalSize += EntryFixedSize + (long)entry.Aux.Length;
            if (totalSize > int.MaxValue)
                throw new ArgumentException(
                    $"WAL block payload exceeds the maximum serialized size at entry {i}.", nameof(wal));
        }

        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(WalSequenceOffset, 8), wal.WalSequence);
        wal.CheckpointBlockId.CopyTo(span.Slice(CheckpointBlockIdOffset, WalBlock.CheckpointBlockIdSize));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(EntryCountOffset, 4), entries.Count);

        int cursor = EntriesOffset;
        foreach (WalEntry entry in entries)
        {
            span[cursor + OpOffset] = (byte)entry.Op;
            entry.Key.CopyTo(span.Slice(cursor + KeyOffset, WalEntry.KeySize));
            entry.BlockId.CopyTo(span.Slice(cursor + BlockIdOffset, WalEntry.BlockIdSize));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor + AuxLengthOffset, 4), entry.Aux.Length);
            entry.Aux.CopyTo(span.Slice(cursor + AuxOffset, entry.Aux.Length));
            cursor += EntryFixedSize + entry.Aux.Length;
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes and validates a WAL block payload. The declared EntryCount and
    /// every entry's AuxLength must account for the payload length exactly, the op
    /// bytes must be defined, and the CheckpointBlockId must not be all-zero, so a
    /// truncated, oversized, or corrupt payload yields a failure rather than a bad
    /// replay stream.
    /// </summary>
    /// <param name="payload">The WAL block's complete serialized payload.</param>
    public static Result<WalBlock> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinPayloadSize)
            return Result<WalBlock>.Failure(
                $"WAL payload must be at least {MinPayloadSize} bytes, got {payload.Length} " +
                "(truncated or corrupt payload).");

        var checkpointBlockId = payload.Slice(CheckpointBlockIdOffset, WalBlock.CheckpointBlockIdSize).ToArray();
        if (IsAllZero(checkpointBlockId))
            return Result<WalBlock>.Failure(
                "WAL CheckpointBlockId is all-zero; a WAL block must fence against a real Checkpoint (spec Section 10.4).");

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(EntryCountOffset, 4));
        if (entryCount < 0)
            return Result<WalBlock>.Failure(
                $"WAL EntryCount must be non-negative, got {entryCount} (corrupt payload).");

        var entries = new WalEntry[entryCount];
        int cursor = EntriesOffset;
        for (int i = 0; i < entryCount; i++)
        {
            // Bounds-check the entry's fixed part before reading any of it.
            if (cursor + EntryFixedSize > payload.Length)
                return Result<WalBlock>.Failure(
                    $"WAL payload truncated: entry {i} needs {EntryFixedSize} header bytes but only " +
                    $"{payload.Length - cursor} remain (declared EntryCount {entryCount}).");

            byte opByte = payload[cursor + OpOffset];
            if (!Enum.IsDefined((WalOpKind)opByte))
                return Result<WalBlock>.Failure(
                    $"WAL entry {i} carries an undefined op byte {opByte} (corrupt payload).");

            int auxLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor + AuxLengthOffset, 4));
            if (auxLength < 0)
                return Result<WalBlock>.Failure(
                    $"WAL entry {i} AuxLength must be non-negative, got {auxLength} (corrupt payload).");

            long entryEnd = (long)cursor + EntryFixedSize + auxLength;
            if (entryEnd > payload.Length)
                return Result<WalBlock>.Failure(
                    $"WAL payload truncated: entry {i} declares AuxLength {auxLength} but only " +
                    $"{payload.Length - cursor - EntryFixedSize} aux bytes remain.");

            entries[i] = new WalEntry
            {
                Op = (WalOpKind)opByte,
                Key = payload.Slice(cursor + KeyOffset, WalEntry.KeySize).ToArray(),
                BlockId = payload.Slice(cursor + BlockIdOffset, WalEntry.BlockIdSize).ToArray(),
                Aux = payload.Slice(cursor + AuxOffset, auxLength).ToArray(),
            };
            cursor = (int)entryEnd;
        }

        if (cursor != payload.Length)
            return Result<WalBlock>.Failure(
                $"WAL payload length {payload.Length} does not match the declared EntryCount {entryCount} " +
                $"(consumed {cursor} bytes; oversized or corrupt payload).");

        return Result<WalBlock>.Success(new WalBlock
        {
            WalSequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(WalSequenceOffset, 8)),
            CheckpointBlockId = checkpointBlockId,
            Entries = entries,
        });
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b != 0)
                return false;
        }
        return true;
    }
}
