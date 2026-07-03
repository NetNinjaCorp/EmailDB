namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of a v3 block header (EmailDB_FileFormat_Spec.md Section 4).
/// On disk the header occupies 48 bytes (magic, version, type, flags, encoding,
/// compression, KeyEpoch, BlockId, PayloadLength, reserved) followed by a 16-byte
/// BLAKE3-128 header checksum. Serialization to/from the on-disk layout is handled
/// by <see cref="BlockSerializer"/>.
///
/// There is no timestamp field: the ULID's top 48 bits are a millisecond UTC
/// timestamp, and in an append-only file creation time is write time.
/// </summary>
public sealed class BlockHeader
{
    /// <summary>Flags bit 0: payload is encrypted.</summary>
    public const byte EncryptedFlag = 0x01;

    /// <summary>Flags bits 1-7: reserved, must be 0.</summary>
    public const byte ReservedFlagsMask = 0xFE;

    /// <summary>Format version stored in the header. Always 3 for this spec.</summary>
    public ushort FormatVersion { get; set; } = BlockSerializer.CurrentFormatVersion;

    /// <summary>Block type (spec Section 5).</summary>
    public BlockType Type { get; set; }

    /// <summary>Bit 0 = Encrypted; bits 1-7 reserved, must be 0 (spec Section 4.1).</summary>
    public byte Flags { get; set; }

    /// <summary>Payload serialization format (spec Section 4.3).</summary>
    public PayloadEncoding Encoding { get; set; }

    /// <summary>Payload compression algorithm (spec Section 4.4).</summary>
    public CompressionAlgorithm Compression { get; set; }

    /// <summary>DEK epoch, 0-65535 (0 when not encrypted).</summary>
    public ushort KeyEpoch { get; set; }

    /// <summary>Block ID: ULID as 16 raw bytes, big-endian binary layout (spec Section 4.2).</summary>
    public byte[] BlockId { get; set; } = new byte[16];

    /// <summary>On-disk payload length in bytes (includes nonce+tag when encrypted).</summary>
    public long PayloadLength { get; set; }

    /// <summary>Convenience accessor for the Encrypted flag (bit 0).</summary>
    public bool IsEncrypted
    {
        get => (Flags & EncryptedFlag) != 0;
        set => Flags = value ? (byte)(Flags | EncryptedFlag) : (byte)(Flags & ~EncryptedFlag);
    }

    /// <summary>
    /// Creates a deep copy (the BlockId array is duplicated), so the copy can be
    /// mutated without aliasing the original instance.
    /// </summary>
    public BlockHeader Clone() => new()
    {
        FormatVersion = FormatVersion,
        Type = Type,
        Flags = Flags,
        Encoding = Encoding,
        Compression = Compression,
        KeyEpoch = KeyEpoch,
        BlockId = (byte[])BlockId.Clone(),
        PayloadLength = PayloadLength,
    };
}
