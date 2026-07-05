using System.Buffers.Binary;
using Blake3;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes and deserializes the v3 on-disk block layout
/// (EmailDB_FileFormat_Spec.md Section 4). Total fixed overhead is 96 bytes:
///
///   [48-byte header] [16-byte header checksum] [payload]
///   [16-byte payload checksum] [16-byte footer]
///
/// All multi-byte integers are little-endian; ULIDs are 16 raw bytes in
/// big-endian binary layout. Checksums are BLAKE3-128 (first 16 bytes of
/// BLAKE3-256); an empty payload stores 16 zero bytes as its checksum. The
/// footer carries the bitwise-NOT of the header magic plus TotalBlockLength,
/// enabling backward scanning from EOF.
///
/// Read order (spec Section 4 processing order): verify the header checksum
/// BEFORE trusting any header field, then validate PayloadLength against
/// MaxPayloadLength (from the superblock) before allocating anything based
/// on it, then verify the payload checksum.
/// </summary>
public static class BlockSerializer
{
    /// <summary>Block header magic (spec Section 4).</summary>
    public const ulong HeaderMagic = 0xEE411DBBD114EE;

    /// <summary>Footer magic: bitwise NOT of <see cref="HeaderMagic"/>.</summary>
    public const ulong FooterMagic = ~HeaderMagic;

    /// <summary>Format version this serializer writes and accepts.</summary>
    public const ushort CurrentFormatVersion = 3;

    /// <summary>Size of the block header (excluding its checksum).</summary>
    public const int HeaderSize = 48;

    /// <summary>Size of each BLAKE3-128 checksum.</summary>
    public const int ChecksumSize = 16;

    /// <summary>Size of the serialized header including its checksum (48 + 16).</summary>
    public const int SerializedHeaderSize = HeaderSize + ChecksumSize;

    /// <summary>Size of the block footer (FooterMagic + TotalBlockLength).</summary>
    public const int FooterSize = 16;

    /// <summary>
    /// Total fixed per-block overhead: header (48) + header checksum (16) +
    /// payload checksum (16) + footer (16) = 96 bytes.
    /// </summary>
    public const int FixedOverhead = SerializedHeaderSize + ChecksumSize + FooterSize;

    // Header field offsets (spec Section 4 table, in order).
    private const int MagicOffset = 0;            // 8 bytes
    private const int FormatVersionOffset = 8;    // 2 bytes
    private const int BlockTypeOffset = 10;       // 1 byte
    private const int FlagsOffset = 11;           // 1 byte
    private const int PayloadEncodingOffset = 12; // 1 byte
    private const int CompressionOffset = 13;     // 1 byte
    private const int KeyEpochOffset = 14;        // 2 bytes
    private const int BlockIdOffset = 16;         // 16 bytes
    private const int PayloadLengthOffset = 32;   // 8 bytes
    private const int ReservedOffset = 40;        // 8 bytes, must be 0
    private const int HeaderChecksumOffset = 48;  // 16 bytes

    /// <summary>Offset of the payload within a serialized block.</summary>
    public const int PayloadOffset = SerializedHeaderSize;

    // Footer field offsets (within the 16-byte footer).
    private const int FooterMagicOffset = 0;        // 8 bytes
    private const int TotalBlockLengthOffset = 8;   // 8 bytes

    /// <summary>Total on-disk block size for a payload of the given length.</summary>
    public static long GetTotalBlockLength(long payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        return FixedOverhead + payloadLength;
    }

    /// <summary>
    /// Serializes a block header into a new 64-byte buffer (48 header bytes plus
    /// the BLAKE3-128 header checksum).
    /// </summary>
    public static byte[] SerializeHeader(BlockHeader header)
    {
        var buffer = new byte[SerializedHeaderSize];
        SerializeHeader(header, buffer);
        return buffer;
    }

    /// <summary>
    /// Serializes a block header into the caller-provided buffer (exactly 64 bytes):
    /// the 48 header bytes at spec offsets, reserved region zero-filled, followed by
    /// the BLAKE3-128 header checksum over those 48 bytes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The buffer is the wrong size, the BlockId is not exactly 16 bytes, the
    /// PayloadLength is negative, or reserved flag bits (1-7) are set.
    /// </exception>
    public static void SerializeHeader(BlockHeader header, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (destination.Length != SerializedHeaderSize)
            throw new ArgumentException(
                $"Header buffer must be exactly {SerializedHeaderSize} bytes, got {destination.Length}.",
                nameof(destination));
        if (header.BlockId is null || header.BlockId.Length != 16)
            throw new ArgumentException(
                $"{nameof(BlockHeader.BlockId)} must be exactly 16 bytes, got {(header.BlockId is null ? "null" : header.BlockId.Length.ToString())}.",
                nameof(header));
        if (header.PayloadLength < 0)
            throw new ArgumentException(
                $"{nameof(BlockHeader.PayloadLength)} must be non-negative, got {header.PayloadLength}.",
                nameof(header));
        if ((header.Flags & BlockHeader.ReservedFlagsMask) != 0)
            throw new ArgumentException(
                $"Reserved flag bits 1-7 must be 0, got flags 0x{header.Flags:X2}.",
                nameof(header));

        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(MagicOffset, 8), HeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(FormatVersionOffset, 2), header.FormatVersion);
        destination[BlockTypeOffset] = (byte)header.Type;
        destination[FlagsOffset] = header.Flags;
        destination[PayloadEncodingOffset] = (byte)header.Encoding;
        destination[CompressionOffset] = (byte)header.Compression;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(KeyEpochOffset, 2), header.KeyEpoch);
        header.BlockId.CopyTo(destination.Slice(BlockIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(PayloadLengthOffset, 8), header.PayloadLength);
        destination.Slice(ReservedOffset, 8).Clear();

        ComputeChecksum(destination.Slice(0, HeaderSize), destination.Slice(HeaderChecksumOffset, ChecksumSize));
    }

    /// <summary>
    /// Deserializes and validates a serialized block header from the first 64 bytes
    /// of <paramref name="source"/>. The BLAKE3-128 header checksum is verified
    /// BEFORE any header field is trusted; only then are magic, format version,
    /// reserved bits, and PayloadLength examined. PayloadLength is validated against
    /// <paramref name="maxPayloadLength"/> (the superblock's sanity bound) so callers
    /// never allocate based on an unverified length.
    /// </summary>
    /// <param name="source">At least 64 bytes starting at a block boundary.</param>
    /// <param name="maxPayloadLength">Sanity bound for PayloadLength, from the superblock.</param>
    /// <param name="atOffset">
    /// File offset of this block's first header byte, used only to populate the
    /// typed <see cref="CorruptionError"/> raised on a header-checksum or insane-length
    /// failure (spec Section 13). Callers over a bare span with no file offset may
    /// leave the default 0.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPayloadLength"/> is not positive.</exception>
    public static Result<BlockHeader> DeserializeHeader(
        ReadOnlySpan<byte> source, long maxPayloadLength, long atOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadLength);

        if (source.Length < SerializedHeaderSize)
            return Result<BlockHeader>.Failure(
                $"Block header requires at least {SerializedHeaderSize} bytes, got {source.Length}.");

        // 1. Verify the header checksum before trusting any field. A mismatch is the
        //    Section 13 "HeaderChecksum mismatch" row: block dead, resynchronize.
        Span<byte> expected = stackalloc byte[ChecksumSize];
        ComputeChecksum(source.Slice(0, HeaderSize), expected);
        if (!expected.SequenceEqual(source.Slice(HeaderChecksumOffset, ChecksumSize)))
            // The header (and its checksum) is the damaged span: [atOffset, +64). The
            // reader cannot trust PayloadLength, so this is as much as it can attribute.
            return CorruptionError.HeaderChecksumMismatch(
                atOffset,
                new DamagedRange(atOffset, atOffset + SerializedHeaderSize, "header checksum mismatch"))
                .ToResult<BlockHeader>();

        // 2. Header bytes are now trustworthy; validate the fields.
        var magic = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(MagicOffset, 8));
        if (magic != HeaderMagic)
            return Result<BlockHeader>.Failure(
                $"Invalid block header magic: expected 0x{HeaderMagic:X16}, got 0x{magic:X16}.");

        var formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(FormatVersionOffset, 2));
        if (formatVersion != CurrentFormatVersion)
            return Result<BlockHeader>.Failure(
                $"Unsupported block format version: expected {CurrentFormatVersion}, got {formatVersion}.");

        var flags = source[FlagsOffset];
        if ((flags & BlockHeader.ReservedFlagsMask) != 0)
            return Result<BlockHeader>.Failure(
                $"Reserved flag bits 1-7 must be 0, got flags 0x{flags:X2}.");

        if (BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(ReservedOffset, 8)) != 0)
            return Result<BlockHeader>.Failure("Block header reserved bytes must be 0.");

        // 3. Length sanity (spec Section 4): never allocate from an unchecked length.
        var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(PayloadLengthOffset, 8));
        if (payloadLength < 0)
            return Result<BlockHeader>.Failure($"PayloadLength must be non-negative, got {payloadLength}.");
        if (payloadLength > maxPayloadLength)
            // Section 13 "PayloadLength insane" row: never allocate on it.
            return CorruptionError.InsaneLength(atOffset, payloadLength, maxPayloadLength).ToResult<BlockHeader>();

        var header = new BlockHeader
        {
            FormatVersion = formatVersion,
            Type = (BlockType)source[BlockTypeOffset],
            Flags = flags,
            Encoding = (PayloadEncoding)source[PayloadEncodingOffset],
            Compression = (CompressionAlgorithm)source[CompressionOffset],
            KeyEpoch = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(KeyEpochOffset, 2)),
            BlockId = source.Slice(BlockIdOffset, 16).ToArray(),
            PayloadLength = payloadLength,
        };

        return Result<BlockHeader>.Success(header);
    }

    /// <summary>
    /// Computes the payload checksum: BLAKE3-128 over the on-disk payload bytes
    /// (ciphertext when encrypted). An empty payload stores 16 zero bytes.
    /// </summary>
    public static void ComputePayloadChecksum(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (destination.Length != ChecksumSize)
            throw new ArgumentException(
                $"Checksum buffer must be exactly {ChecksumSize} bytes, got {destination.Length}.",
                nameof(destination));

        if (payload.IsEmpty)
        {
            destination.Clear();
            return;
        }

        ComputeChecksum(payload, destination);
    }

    /// <summary>Verifies the stored 16-byte payload checksum against the payload bytes.</summary>
    public static bool VerifyPayloadChecksum(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> storedChecksum)
    {
        if (storedChecksum.Length != ChecksumSize)
            return false;

        Span<byte> expected = stackalloc byte[ChecksumSize];
        ComputePayloadChecksum(payload, expected);
        return expected.SequenceEqual(storedChecksum);
    }

    /// <summary>
    /// Serializes the 16-byte block footer: FooterMagic (~HeaderMagic) followed by
    /// TotalBlockLength, which enables backward scanning from EOF.
    /// </summary>
    public static void SerializeFooter(long totalBlockLength, Span<byte> destination)
    {
        if (destination.Length != FooterSize)
            throw new ArgumentException(
                $"Footer buffer must be exactly {FooterSize} bytes, got {destination.Length}.",
                nameof(destination));
        if (totalBlockLength < FixedOverhead)
            throw new ArgumentException(
                $"TotalBlockLength must be at least {FixedOverhead}, got {totalBlockLength}.",
                nameof(totalBlockLength));

        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(FooterMagicOffset, 8), FooterMagic);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(TotalBlockLengthOffset, 8), totalBlockLength);
    }

    /// <summary>
    /// Deserializes and validates a 16-byte block footer, returning TotalBlockLength.
    /// For a backward walk, pass the 16 bytes ending at EOF (or at the previous block
    /// boundary) and step back by the returned length.
    /// </summary>
    public static Result<long> DeserializeFooter(ReadOnlySpan<byte> source)
    {
        if (source.Length != FooterSize)
            return Result<long>.Failure($"Block footer must be exactly {FooterSize} bytes, got {source.Length}.");

        var magic = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(FooterMagicOffset, 8));
        if (magic != FooterMagic)
            return Result<long>.Failure(
                $"Invalid block footer magic: expected 0x{FooterMagic:X16}, got 0x{magic:X16}.");

        var totalBlockLength = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(TotalBlockLengthOffset, 8));
        if (totalBlockLength < FixedOverhead)
            return Result<long>.Failure(
                $"Footer TotalBlockLength {totalBlockLength} is smaller than the fixed block overhead {FixedOverhead}.");

        return Result<long>.Success(totalBlockLength);
    }

    /// <summary>
    /// Serializes a complete block (header + header checksum + payload + payload
    /// checksum + footer) into a new buffer of exactly 96 + payload.Length bytes.
    /// <paramref name="header"/>.PayloadLength must equal the payload's length.
    /// </summary>
    public static byte[] Serialize(BlockHeader header, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.PayloadLength != payload.Length)
            throw new ArgumentException(
                $"{nameof(BlockHeader.PayloadLength)} ({header.PayloadLength}) must equal the payload length ({payload.Length}).",
                nameof(header));

        var block = new byte[FixedOverhead + payload.Length];
        var span = block.AsSpan();

        SerializeHeader(header, span.Slice(0, SerializedHeaderSize));
        payload.CopyTo(span.Slice(PayloadOffset, payload.Length));
        ComputePayloadChecksum(payload, span.Slice(PayloadOffset + payload.Length, ChecksumSize));
        SerializeFooter(block.Length, span.Slice(block.Length - FooterSize, FooterSize));

        return block;
    }

    /// <summary>
    /// Deserializes and fully verifies a complete block: header checksum first
    /// (before trusting any field), then PayloadLength sanity against
    /// <paramref name="maxPayloadLength"/> and the buffer length, then the payload
    /// checksum, then the footer magic and TotalBlockLength.
    /// </summary>
    /// <param name="block">The complete block bytes, exactly one block.</param>
    /// <param name="maxPayloadLength">Sanity bound for PayloadLength, from the superblock.</param>
    /// <param name="atOffset">
    /// File offset of this block's first header byte, used only to populate the typed
    /// <see cref="CorruptionError"/> raised on a checksum/insane-length failure
    /// (spec Section 13). Callers over a bare span may leave the default 0.
    /// </param>
    public static Result<Block> Deserialize(ReadOnlySpan<byte> block, long maxPayloadLength, long atOffset = 0)
    {
        if (block.Length < FixedOverhead)
            return Result<Block>.Failure(
                $"Block requires at least {FixedOverhead} bytes, got {block.Length}.");

        var headerResult = DeserializeHeader(block, maxPayloadLength, atOffset);
        if (headerResult.IsFailure)
            return PropagateHeaderFailure(headerResult);
        var header = headerResult.Value;

        long expectedTotal = GetTotalBlockLength(header.PayloadLength);
        if (block.Length != expectedTotal)
            return Result<Block>.Failure(
                $"Block length mismatch: header PayloadLength {header.PayloadLength} implies {expectedTotal} bytes, got {block.Length}.");

        int payloadLength = (int)header.PayloadLength;
        var payload = block.Slice(PayloadOffset, payloadLength);
        if (!VerifyPayloadChecksum(payload, block.Slice(PayloadOffset + payloadLength, ChecksumSize)))
            // Section 13 "PayloadChecksum mismatch" row: block dead, resynchronize.
            // Names the block's own BlockId so a caller resolving a live reference can
            // upgrade this to a referenced-live data-loss error (spec Section 13). The
            // damaged span is the payload region: [atOffset+PayloadOffset, +PayloadLength).
            return CorruptionError.PayloadChecksumMismatch(
                atOffset, header.BlockId,
                new DamagedRange(
                    atOffset + PayloadOffset, atOffset + PayloadOffset + payloadLength,
                    "payload checksum mismatch"))
                .ToResult<Block>();

        var footerResult = DeserializeFooter(block.Slice(block.Length - FooterSize, FooterSize));
        if (footerResult.IsFailure)
            return Result<Block>.Failure(footerResult.Error);
        if (footerResult.Value != expectedTotal)
            return Result<Block>.Failure(
                $"Footer TotalBlockLength {footerResult.Value} does not match the block size {expectedTotal}.");

        return Result<Block>.Success(new Block
        {
            Header = header,
            Payload = payload.ToArray(),
        });
    }

    /// <summary>
    /// Re-types a failed header result to <see cref="Result{Block}"/> while preserving
    /// any typed <see cref="VerificationError"/> the header failure carried (so a
    /// Section 13 corruption cause is not flattened back to a bare string).
    /// </summary>
    private static Result<Block> PropagateHeaderFailure(Result<BlockHeader> headerResult) =>
        headerResult.VerificationError is not null
            ? Result<Block>.Failure(headerResult.VerificationError)
            : Result<Block>.Failure(headerResult.Error);

    /// <summary>
    /// Computes the BLAKE3-128 checksum (first 16 bytes of BLAKE3-256) over the given bytes.
    /// </summary>
    private static void ComputeChecksum(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var hash = Hasher.Hash(data);
        hash.AsSpan().Slice(0, ChecksumSize).CopyTo(destination);
    }
}
