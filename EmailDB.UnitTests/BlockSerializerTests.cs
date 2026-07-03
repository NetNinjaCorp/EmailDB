using System.Buffers.Binary;
using Blake3;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 block serialization layer (EmailDB_FileFormat_Spec.md Section 4):
/// 48-byte header layout at spec offsets (little-endian), BLAKE3-128 header and payload
/// checksums (empty payload stores 16 zero bytes), checksum-verified-before-trust
/// deserialization, PayloadLength sanity against MaxPayloadLength, and the 16-byte
/// footer (~magic + TotalBlockLength) that supports backward walking from EOF.
/// </summary>
public class BlockSerializerTests
{
    private const long MaxPayloadLength = 268_435_456; // superblock default, 256 MB

    private static BlockHeader CreateSampleHeader(long payloadLength) => new()
    {
        Type = EmailDB.Format.V3.BlockType.EmailContent,       // 7
        Flags = BlockHeader.EncryptedFlag,                     // 0x01
        Encoding = EmailDB.Format.V3.PayloadEncoding.RawBytes, // 4
        Compression = CompressionAlgorithm.Lz4,                // 0x01
        KeyEpoch = 0x0201,
        BlockId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        PayloadLength = payloadLength,
    };

    private static byte[] SamplePayload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 1)).ToArray();

    /// <summary>Recomputes the header checksum after a test deliberately edits header bytes.</summary>
    private static void FixHeaderChecksum(Span<byte> buffer)
    {
        var hash = Hasher.Hash(buffer[..BlockSerializer.HeaderSize]);
        hash.AsSpan()[..BlockSerializer.ChecksumSize]
            .CopyTo(buffer.Slice(BlockSerializer.HeaderSize, BlockSerializer.ChecksumSize));
    }

    // ---- Header layout: per-field offsets and endianness (spec Section 4) ----

    [Fact]
    public void SerializeHeader_Produces64Bytes()
    {
        Assert.Equal(64, BlockSerializer.SerializeHeader(CreateSampleHeader(5)).Length);
    }

    [Fact]
    public void SerializeHeader_WritesEveryFieldAtSpecOffsetLittleEndian()
    {
        var header = CreateSampleHeader(0x0102030405060708);
        var bytes = BlockSerializer.SerializeHeader(header);

        // HeaderMagic 0xEE411DBBD114EE at offset 0, little-endian.
        Assert.Equal(new byte[] { 0xEE, 0x14, 0xD1, 0xBB, 0x1D, 0x41, 0xEE, 0x00 }, bytes[0..8]);
        // FormatVersion = 3 at offset 8 (2 bytes LE).
        Assert.Equal(new byte[] { 0x03, 0x00 }, bytes[8..10]);
        // BlockType at offset 10.
        Assert.Equal(7, bytes[10]);
        // Flags at offset 11 (bit 0 = Encrypted).
        Assert.Equal(0x01, bytes[11]);
        // PayloadEncoding at offset 12 (RawBytes = 4).
        Assert.Equal(4, bytes[12]);
        // Compression at offset 13 (LZ4 = 0x01).
        Assert.Equal(0x01, bytes[13]);
        // KeyEpoch = 0x0201 at offset 14 (2 bytes LE).
        Assert.Equal(new byte[] { 0x01, 0x02 }, bytes[14..16]);
        // BlockId (16 raw ULID bytes) at offset 16.
        Assert.Equal(header.BlockId, bytes[16..32]);
        // PayloadLength at offset 32 (8 bytes LE).
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, bytes[32..40]);
        // Reserved at offset 40: 8 zero bytes.
        Assert.Equal(new byte[8], bytes[40..48]);
    }

    [Fact]
    public void SerializeHeader_ChecksumIsBlake3_128OverThe48HeaderBytes()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));

        var expected = Hasher.Hash(bytes.AsSpan(0, 48)).AsSpan()[..16].ToArray();
        Assert.Equal(expected, bytes[48..64]);
    }

    [Fact]
    public void HeaderRoundTrip_PreservesAllFields()
    {
        var original = CreateSampleHeader(12345);
        var result = BlockSerializer.DeserializeHeader(BlockSerializer.SerializeHeader(original), MaxPayloadLength);

        Assert.True(result.IsSuccess, result.Error);
        var restored = result.Value;
        Assert.Equal(original.FormatVersion, restored.FormatVersion);
        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.Flags, restored.Flags);
        Assert.Equal(original.Encoding, restored.Encoding);
        Assert.Equal(original.Compression, restored.Compression);
        Assert.Equal(original.KeyEpoch, restored.KeyEpoch);
        Assert.Equal(original.BlockId, restored.BlockId);
        Assert.Equal(original.PayloadLength, restored.PayloadLength);
        Assert.True(restored.IsEncrypted);
    }

    // ---- Serialization input validation ----

    [Fact]
    public void SerializeHeader_WrongBlockIdLength_Throws()
    {
        var header = CreateSampleHeader(0);
        header.BlockId = new byte[15];
        Assert.Throws<ArgumentException>(() => BlockSerializer.SerializeHeader(header));
    }

    [Fact]
    public void SerializeHeader_NegativePayloadLength_Throws()
    {
        var header = CreateSampleHeader(-1);
        Assert.Throws<ArgumentException>(() => BlockSerializer.SerializeHeader(header));
    }

    [Fact]
    public void SerializeHeader_ReservedFlagBitsSet_Throws()
    {
        var header = CreateSampleHeader(0);
        header.Flags = 0x02; // bit 1 is reserved
        Assert.Throws<ArgumentException>(() => BlockSerializer.SerializeHeader(header));
    }

    [Fact]
    public void SerializeHeader_WrongBufferSize_Throws()
    {
        var header = CreateSampleHeader(0);
        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> tooSmall = stackalloc byte[63];
            BlockSerializer.SerializeHeader(header, tooSmall);
        });
    }

    // ---- Checksum verified BEFORE any header field is trusted ----

    [Fact]
    public void DeserializeHeader_EverySingleBitFlipInHeader_FailsWithChecksumMismatch()
    {
        var pristine = BlockSerializer.SerializeHeader(CreateSampleHeader(5));

        for (int offset = 0; offset < 48; offset++)
        {
            var corrupted = (byte[])pristine.Clone();
            corrupted[offset] ^= 0x01;

            var result = BlockSerializer.DeserializeHeader(corrupted, MaxPayloadLength);

            Assert.True(result.IsFailure, $"Bit flip at offset {offset} was not detected.");
            Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DeserializeHeader_TamperedPayloadLength_FailsOnChecksumNotLength()
    {
        // A corrupted (huge) PayloadLength must be rejected by the checksum check,
        // proving the checksum is verified before the length field is trusted.
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32, 8), long.MaxValue);

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MaxPayloadLength", result.Error);
    }

    [Fact]
    public void DeserializeHeader_TamperedChecksumItself_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        bytes[48] ^= 0xFF;

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeHeader_TooShort_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        var result = BlockSerializer.DeserializeHeader(bytes.AsSpan(0, 63), MaxPayloadLength);
        Assert.True(result.IsFailure);
    }

    // ---- Field validation after a valid checksum ----

    [Fact]
    public void DeserializeHeader_WrongMagic_ValidChecksum_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        bytes[0] ^= 0xFF;
        FixHeaderChecksum(bytes);

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("magic", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeHeader_WrongFormatVersion_ValidChecksum_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 2);
        FixHeaderChecksum(bytes);

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("version", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeHeader_ReservedFlagBits_ValidChecksum_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        bytes[11] |= 0x80;
        FixHeaderChecksum(bytes);

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("flag", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeHeader_NonZeroReservedBytes_ValidChecksum_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        bytes[44] = 0xAB;
        FixHeaderChecksum(bytes);

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("reserved", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeHeader_NegativePayloadLength_ValidChecksum_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32, 8), -1);
        FixHeaderChecksum(bytes);

        var result = BlockSerializer.DeserializeHeader(bytes, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("non-negative", result.Error);
    }

    // ---- PayloadLength sanity against MaxPayloadLength (from the superblock) ----

    [Fact]
    public void DeserializeHeader_PayloadLengthAboveMax_Fails()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(1000));

        var result = BlockSerializer.DeserializeHeader(bytes, maxPayloadLength: 999);

        Assert.True(result.IsFailure);
        Assert.Contains("MaxPayloadLength", result.Error);
    }

    [Fact]
    public void DeserializeHeader_PayloadLengthEqualToMax_Succeeds()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(1000));

        var result = BlockSerializer.DeserializeHeader(bytes, maxPayloadLength: 1000);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1000, result.Value.PayloadLength);
    }

    [Fact]
    public void DeserializeHeader_NonPositiveMaxPayloadLength_Throws()
    {
        var bytes = BlockSerializer.SerializeHeader(CreateSampleHeader(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockSerializer.DeserializeHeader(bytes, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockSerializer.DeserializeHeader(bytes, -1));
    }

    // ---- Payload checksum (empty payload stores 16 zero bytes) ----

    [Fact]
    public void ComputePayloadChecksum_EmptyPayload_Stores16ZeroBytes()
    {
        var checksum = new byte[16];
        checksum.AsSpan().Fill(0xFF);

        BlockSerializer.ComputePayloadChecksum(ReadOnlySpan<byte>.Empty, checksum);

        Assert.Equal(new byte[16], checksum);
    }

    [Fact]
    public void ComputePayloadChecksum_NonEmptyPayload_IsBlake3_128()
    {
        var payload = SamplePayload(37);
        var checksum = new byte[16];

        BlockSerializer.ComputePayloadChecksum(payload, checksum);

        Assert.Equal(Hasher.Hash(payload).AsSpan()[..16].ToArray(), checksum);
    }

    [Fact]
    public void VerifyPayloadChecksum_MatchesAndRejects()
    {
        var payload = SamplePayload(20);
        var checksum = new byte[16];
        BlockSerializer.ComputePayloadChecksum(payload, checksum);

        Assert.True(BlockSerializer.VerifyPayloadChecksum(payload, checksum));
        checksum[0] ^= 0x01;
        Assert.False(BlockSerializer.VerifyPayloadChecksum(payload, checksum));
        Assert.False(BlockSerializer.VerifyPayloadChecksum(payload, checksum.AsSpan(0, 15)));
    }

    // ---- Footer layout and TotalBlockLength arithmetic ----

    [Fact]
    public void SerializeFooter_WritesInvertedMagicAndTotalBlockLength()
    {
        var footer = new byte[16];
        BlockSerializer.SerializeFooter(0x0102030405060708, footer);

        // FooterMagic = ~HeaderMagic: every byte is the bitwise NOT of the header magic byte.
        var headerMagicBytes = new byte[] { 0xEE, 0x14, 0xD1, 0xBB, 0x1D, 0x41, 0xEE, 0x00 };
        for (int i = 0; i < 8; i++)
            Assert.Equal((byte)~headerMagicBytes[i], footer[i]);

        // TotalBlockLength at offset 8 (8 bytes LE).
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, footer[8..16]);
    }

    [Fact]
    public void FooterRoundTrip_ReturnsTotalBlockLength()
    {
        var footer = new byte[16];
        BlockSerializer.SerializeFooter(4242, footer);

        var result = BlockSerializer.DeserializeFooter(footer);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(4242, result.Value);
    }

    [Fact]
    public void DeserializeFooter_WrongMagic_Fails()
    {
        var footer = new byte[16];
        BlockSerializer.SerializeFooter(4242, footer);
        footer[3] ^= 0x01;

        var result = BlockSerializer.DeserializeFooter(footer);

        Assert.True(result.IsFailure);
        Assert.Contains("magic", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeFooter_TotalBlockLengthBelowFixedOverhead_Fails()
    {
        var footer = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, BlockSerializer.FooterMagic);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(8), 95);

        var result = BlockSerializer.DeserializeFooter(footer);

        Assert.True(result.IsFailure);
        Assert.Contains("TotalBlockLength", result.Error);
    }

    [Fact]
    public void GetTotalBlockLength_Is96PlusPayload()
    {
        Assert.Equal(96, BlockSerializer.FixedOverhead);
        Assert.Equal(96, BlockSerializer.GetTotalBlockLength(0));
        Assert.Equal(96 + 12345, BlockSerializer.GetTotalBlockLength(12345));
    }

    // ---- Full block serialization ----

    [Fact]
    public void Serialize_FullBlock_LayoutMatchesSpec()
    {
        var payload = SamplePayload(37);
        var block = BlockSerializer.Serialize(CreateSampleHeader(37), payload);

        Assert.Equal(96 + 37, block.Length);
        // Payload sits immediately after the 64 header+checksum bytes.
        Assert.Equal(payload, block[64..(64 + 37)]);
        // Payload checksum follows the payload.
        Assert.Equal(Hasher.Hash(payload).AsSpan()[..16].ToArray(), block[(64 + 37)..(64 + 37 + 16)]);
        // Footer is the last 16 bytes: ~magic + TotalBlockLength.
        var footerResult = BlockSerializer.DeserializeFooter(block.AsSpan(block.Length - 16, 16));
        Assert.True(footerResult.IsSuccess, footerResult.Error);
        Assert.Equal(block.Length, footerResult.Value);
    }

    [Fact]
    public void Serialize_EmptyPayload_Stores16ZeroChecksumBytes_AndRoundTrips()
    {
        var block = BlockSerializer.Serialize(CreateSampleHeader(0), ReadOnlySpan<byte>.Empty);

        Assert.Equal(96, block.Length);
        Assert.Equal(new byte[16], block[64..80]); // empty payload -> 16 zero bytes

        var result = BlockSerializer.Deserialize(block, MaxPayloadLength);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value.Payload);
    }

    [Fact]
    public void Serialize_PayloadLengthMismatch_Throws()
    {
        var header = CreateSampleHeader(5);
        Assert.Throws<ArgumentException>(() => BlockSerializer.Serialize(header, SamplePayload(4)));
    }

    [Fact]
    public void FullBlockRoundTrip_PreservesHeaderAndPayload()
    {
        var original = CreateSampleHeader(129);
        var payload = SamplePayload(129);

        var result = BlockSerializer.Deserialize(BlockSerializer.Serialize(original, payload), MaxPayloadLength);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(original.Type, result.Value.Header.Type);
        Assert.Equal(original.Flags, result.Value.Header.Flags);
        Assert.Equal(original.Encoding, result.Value.Header.Encoding);
        Assert.Equal(original.Compression, result.Value.Header.Compression);
        Assert.Equal(original.KeyEpoch, result.Value.Header.KeyEpoch);
        Assert.Equal(original.BlockId, result.Value.Header.BlockId);
        Assert.Equal(129, result.Value.Header.PayloadLength);
        Assert.Equal(payload, result.Value.Payload);
    }

    [Fact]
    public void Deserialize_TamperedPayloadByte_FailsPayloadChecksum()
    {
        var block = BlockSerializer.Serialize(CreateSampleHeader(37), SamplePayload(37));
        block[64 + 10] ^= 0x01;

        var result = BlockSerializer.Deserialize(block, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("payload checksum", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_PayloadExceedsMaxPayloadLength_FailsBeforePayloadWork()
    {
        var block = BlockSerializer.Serialize(CreateSampleHeader(100), SamplePayload(100));

        var result = BlockSerializer.Deserialize(block, maxPayloadLength: 50);

        Assert.True(result.IsFailure);
        Assert.Contains("MaxPayloadLength", result.Error);
    }

    [Fact]
    public void Deserialize_TruncatedBlock_Fails()
    {
        var block = BlockSerializer.Serialize(CreateSampleHeader(37), SamplePayload(37));

        Assert.True(BlockSerializer.Deserialize(block.AsSpan(0, 95), MaxPayloadLength).IsFailure);
        Assert.True(BlockSerializer.Deserialize(block.AsSpan(0, block.Length - 1), MaxPayloadLength).IsFailure);
    }

    [Fact]
    public void Deserialize_BufferLongerThanBlock_Fails()
    {
        var block = BlockSerializer.Serialize(CreateSampleHeader(37), SamplePayload(37));
        var oversized = block.Concat(new byte[] { 0x00 }).ToArray();

        var result = BlockSerializer.Deserialize(oversized, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("length mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_TamperedFooterTotalBlockLength_Fails()
    {
        var block = BlockSerializer.Serialize(CreateSampleHeader(37), SamplePayload(37));
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(block.Length - 8, 8), block.Length + 1);

        var result = BlockSerializer.Deserialize(block, MaxPayloadLength);

        Assert.True(result.IsFailure);
        Assert.Contains("TotalBlockLength", result.Error);
    }

    // ---- Backward walk from EOF via footer TotalBlockLength ----

    [Fact]
    public void FooterTotalBlockLength_SupportsBackwardWalkFromEof()
    {
        var generator = new UlidGenerator();
        var payloadSizes = new[] { 10, 0, 33 };
        var blocks = payloadSizes.Select(size =>
        {
            var header = CreateSampleHeader(size);
            header.BlockId = generator.Next();
            return BlockSerializer.Serialize(header, SamplePayload(size));
        }).ToArray();
        var file = blocks.SelectMany(b => b).ToArray();

        // Walk backward from EOF using only footer TotalBlockLength values.
        var recoveredPayloadSizes = new List<long>();
        long position = file.Length;
        while (position > 0)
        {
            var footerResult = BlockSerializer.DeserializeFooter(file.AsSpan((int)position - 16, 16));
            Assert.True(footerResult.IsSuccess, footerResult.Error);

            long blockStart = position - footerResult.Value;
            Assert.True(blockStart >= 0);

            var blockResult = BlockSerializer.Deserialize(
                file.AsSpan((int)blockStart, (int)footerResult.Value), MaxPayloadLength);
            Assert.True(blockResult.IsSuccess, blockResult.Error);
            recoveredPayloadSizes.Add(blockResult.Value.Header.PayloadLength);

            position = blockStart;
        }

        Assert.Equal(0, position); // the walk lands exactly on offset 0
        Assert.Equal(new long[] { 33, 0, 10 }, recoveredPayloadSizes);
    }
}
