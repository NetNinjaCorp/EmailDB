using System.Buffers.Binary;
using Blake3;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes and deserializes the 4096-byte v3 superblock slot layout
/// (EmailDB_FileFormat_Spec.md Section 3.1).
///
/// All multi-byte integers are little-endian. ULIDs and other byte-array fields
/// are copied verbatim (ULIDs are 16 raw bytes in big-endian binary layout).
/// The reserved region (3898 bytes) is zero-filled on write. The slot checksum
/// is BLAKE3-128 (first 16 bytes of BLAKE3-256) over bytes 0..4079 of the slot,
/// stored at offset 4080.
/// </summary>
public static class SuperblockSerializer
{
    /// <summary>Size of one superblock slot on disk.</summary>
    public const int SlotSize = 4096;

    /// <summary>Superblock slot magic (spec Section 3.1).</summary>
    public const ulong SuperblockMagic = 0x53E3A11DBB00DBEE;

    /// <summary>Format version this serializer writes and accepts.</summary>
    public const ushort CurrentFormatVersion = 3;

    /// <summary>Number of leading slot bytes covered by the checksum (bytes 0..4079).</summary>
    public const int ChecksumCoverage = 4080;

    /// <summary>Size of the BLAKE3-128 slot checksum.</summary>
    public const int ChecksumSize = 16;

    // Field offsets within the slot (spec Section 3.1 table, in order).
    private const int MagicOffset = 0;                     // 8 bytes
    private const int FormatVersionOffset = 8;             // 2 bytes
    private const int SequenceOffset = 10;                 // 8 bytes
    private const int FileIdOffset = 18;                   // 16 bytes
    private const int ShardIndexOffset = 34;               // 4 bytes
    private const int CreatedTimestampOffset = 38;         // 8 bytes
    private const int CompatFlagsOffset = 46;              // 4 bytes
    private const int ReadOnlyCompatFlagsOffset = 50;      // 4 bytes
    private const int IncompatFlagsOffset = 54;            // 4 bytes
    private const int CleanShutdownOffset = 58;            // 1 byte
    private const int MaxPayloadLengthOffset = 59;         // 8 bytes
    private const int LastCheckpointBlockIdOffset = 67;    // 16 bytes
    private const int LastCheckpointOffsetOffset = 83;     // 8 bytes
    private const int EncryptionEnabledOffset = 91;        // 1 byte
    private const int AlgorithmIdOffset = 92;              // 1 byte
    private const int KdfTypeOffset = 93;                  // 1 byte
    private const int KdfParamsOffset = 94;                // 16 bytes
    private const int SaltOffset = 110;                    // 16 bytes
    private const int KeyVerificationTokenOffset = 126;    // 32 bytes
    private const int ActiveKeyStoreBlockIdOffset = 158;   // 16 bytes
    private const int ActiveKeyStoreOffsetOffset = 174;    // 8 bytes
    private const int ReservedOffset = 182;                // 3898 bytes, must be 0
    private const int ReservedSize = 3898;
    private const int ChecksumOffset = 4080;               // 16 bytes

    /// <summary>
    /// Serializes a superblock into a new 4096-byte slot buffer, including the
    /// zero-filled reserved region and the BLAKE3-128 slot checksum.
    /// </summary>
    public static byte[] Serialize(Superblock superblock)
    {
        var slot = new byte[SlotSize];
        Serialize(superblock, slot);
        return slot;
    }

    /// <summary>
    /// Serializes a superblock into the caller-provided slot buffer (exactly 4096 bytes),
    /// including the zero-filled reserved region and the BLAKE3-128 slot checksum.
    /// </summary>
    public static void Serialize(Superblock superblock, Span<byte> slot)
    {
        ArgumentNullException.ThrowIfNull(superblock);
        if (slot.Length != SlotSize)
            throw new ArgumentException($"Slot buffer must be exactly {SlotSize} bytes, got {slot.Length}.", nameof(slot));

        BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(MagicOffset, 8), SuperblockMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(FormatVersionOffset, 2), superblock.FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(SequenceOffset, 8), superblock.SuperblockSequence);
        CopyFixed(superblock.FileId, 16, nameof(Superblock.FileId), slot.Slice(FileIdOffset, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(ShardIndexOffset, 4), superblock.ShardIndex);
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(CreatedTimestampOffset, 8), superblock.CreatedTimestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(CompatFlagsOffset, 4), superblock.CompatFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(ReadOnlyCompatFlagsOffset, 4), superblock.ReadOnlyCompatFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(IncompatFlagsOffset, 4), superblock.IncompatFlags);
        slot[CleanShutdownOffset] = superblock.CleanShutdown;
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(MaxPayloadLengthOffset, 8), superblock.MaxPayloadLength);
        CopyFixed(superblock.LastCheckpointBlockId, 16, nameof(Superblock.LastCheckpointBlockId), slot.Slice(LastCheckpointBlockIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(LastCheckpointOffsetOffset, 8), superblock.LastCheckpointOffset);
        slot[EncryptionEnabledOffset] = superblock.EncryptionEnabled;
        slot[AlgorithmIdOffset] = superblock.AlgorithmId;
        slot[KdfTypeOffset] = superblock.KdfType;
        CopyFixed(superblock.KdfParams, 16, nameof(Superblock.KdfParams), slot.Slice(KdfParamsOffset, 16));
        CopyFixed(superblock.Salt, 16, nameof(Superblock.Salt), slot.Slice(SaltOffset, 16));
        CopyFixed(superblock.KeyVerificationToken, 32, nameof(Superblock.KeyVerificationToken), slot.Slice(KeyVerificationTokenOffset, 32));
        CopyFixed(superblock.ActiveKeyStoreBlockId, 16, nameof(Superblock.ActiveKeyStoreBlockId), slot.Slice(ActiveKeyStoreBlockIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(ActiveKeyStoreOffsetOffset, 8), superblock.ActiveKeyStoreOffset);
        slot.Slice(ReservedOffset, ReservedSize).Clear();

        ComputeChecksum(slot.Slice(0, ChecksumCoverage), slot.Slice(ChecksumOffset, ChecksumSize));
    }

    /// <summary>
    /// Deserializes and validates a 4096-byte superblock slot. Fails (without throwing)
    /// when the buffer is the wrong size, the magic does not match, the format version is
    /// unsupported, or the BLAKE3-128 checksum does not verify (e.g. a torn write).
    /// </summary>
    public static Result<Superblock> Deserialize(ReadOnlySpan<byte> slot)
    {
        if (slot.Length != SlotSize)
            return Result<Superblock>.Failure($"Superblock slot must be exactly {SlotSize} bytes, got {slot.Length}.");

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(MagicOffset, 8));
        if (magic != SuperblockMagic)
            return Result<Superblock>.Failure($"Invalid superblock magic: expected 0x{SuperblockMagic:X16}, got 0x{magic:X16}.");

        Span<byte> expected = stackalloc byte[ChecksumSize];
        ComputeChecksum(slot.Slice(0, ChecksumCoverage), expected);
        if (!expected.SequenceEqual(slot.Slice(ChecksumOffset, ChecksumSize)))
            return Result<Superblock>.Failure("Superblock slot checksum mismatch (torn or corrupt slot).");

        var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(FormatVersionOffset, 2));
        if (formatVersion != CurrentFormatVersion)
            return Result<Superblock>.Failure($"Unsupported superblock format version: expected {CurrentFormatVersion}, got {formatVersion}.");

        var superblock = new Superblock
        {
            FormatVersion = formatVersion,
            SuperblockSequence = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(SequenceOffset, 8)),
            FileId = slot.Slice(FileIdOffset, 16).ToArray(),
            ShardIndex = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(ShardIndexOffset, 4)),
            CreatedTimestamp = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(CreatedTimestampOffset, 8)),
            CompatFlags = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(CompatFlagsOffset, 4)),
            ReadOnlyCompatFlags = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(ReadOnlyCompatFlagsOffset, 4)),
            IncompatFlags = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(IncompatFlagsOffset, 4)),
            CleanShutdown = slot[CleanShutdownOffset],
            MaxPayloadLength = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(MaxPayloadLengthOffset, 8)),
            LastCheckpointBlockId = slot.Slice(LastCheckpointBlockIdOffset, 16).ToArray(),
            LastCheckpointOffset = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(LastCheckpointOffsetOffset, 8)),
            EncryptionEnabled = slot[EncryptionEnabledOffset],
            AlgorithmId = slot[AlgorithmIdOffset],
            KdfType = slot[KdfTypeOffset],
            KdfParams = slot.Slice(KdfParamsOffset, 16).ToArray(),
            Salt = slot.Slice(SaltOffset, 16).ToArray(),
            KeyVerificationToken = slot.Slice(KeyVerificationTokenOffset, 32).ToArray(),
            ActiveKeyStoreBlockId = slot.Slice(ActiveKeyStoreBlockIdOffset, 16).ToArray(),
            ActiveKeyStoreOffset = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(ActiveKeyStoreOffsetOffset, 8)),
        };

        return Result<Superblock>.Success(superblock);
    }

    /// <summary>
    /// Computes the BLAKE3-128 checksum (first 16 bytes of BLAKE3-256) over the given bytes.
    /// </summary>
    private static void ComputeChecksum(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var hash = Hasher.Hash(data);
        hash.AsSpan().Slice(0, ChecksumSize).CopyTo(destination);
    }

    private static void CopyFixed(byte[]? value, int expectedLength, string fieldName, Span<byte> destination)
    {
        if (value is null || value.Length != expectedLength)
            throw new ArgumentException(
                $"{fieldName} must be exactly {expectedLength} bytes, got {(value is null ? "null" : value.Length.ToString())}.");
        value.CopyTo(destination);
    }
}
