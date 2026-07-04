using System.Buffers.Binary;
using Blake3;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 append-only block writer and verifying reader
/// (EmailDB_FileFormat_Spec.md Sections 2, 4, 13): append-at-EOF with monotonic
/// ULID BlockIds and runtime-map notification, read order (header checksum before
/// trusting fields, PayloadLength sanity before allocation, payload checksum,
/// footer), empty-payload checksum bytes, and the footer-driven backward walk
/// from EOF.
/// </summary>
public class BlockManagerV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-blockmanager-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Creates a manager over a fresh stream; firstBlockOffset 0 keeps test offsets simple.</summary>
    private BlockManager CreateManager(
        long maxPayloadLength = Superblock.DefaultMaxPayloadLength,
        IBlockOffsetMap? offsetMap = null,
        long firstBlockOffset = 0) =>
        new(OpenStream(), maxPayloadLength, offsetMap: offsetMap,
            firstBlockOffset: firstBlockOffset, ownsStream: true);

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    /// <summary>Flips one byte of the file at the given absolute offset.</summary>
    private void CorruptByte(long offset)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        var b = (byte)fs.ReadByte();
        fs.Seek(offset, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    private byte[] ReadRawFile()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Test double for the runtime BlockId-to-offset map (US-EMDB-66-5).</summary>
    private sealed class RecordingOffsetMap : IBlockOffsetMap
    {
        public List<(BlockHeader Header, long Offset, long TotalBlockLength)> Entries { get; } = new();

        public void OnBlockAppended(BlockHeader header, long offset, long totalBlockLength) =>
            Entries.Add((header, offset, totalBlockLength));
    }

    // ---- Append: offsets, lengths, ULIDs, map notification ----

    [Fact]
    public void Append_ReturnsSequentialOffsetsAndTotalLengths()
    {
        using var manager = CreateManager();

        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(100));
        var second = manager.Append(BlockType.EmailMetadata, PayloadEncoding.Protobuf, SamplePayload(10));
        var third = manager.Append(BlockType.WAL, PayloadEncoding.Custom, SamplePayload(1));

        Assert.True(first.IsSuccess, first.Error);
        Assert.Equal(0, first.Value.Offset);
        Assert.Equal(BlockSerializer.FixedOverhead + 100, first.Value.TotalBlockLength);

        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(first.Value.TotalBlockLength, second.Value.Offset);
        Assert.Equal(BlockSerializer.FixedOverhead + 10, second.Value.TotalBlockLength);

        Assert.True(third.IsSuccess, third.Error);
        Assert.Equal(second.Value.Offset + second.Value.TotalBlockLength, third.Value.Offset);
    }

    [Fact]
    public void Append_OnEmptyFile_DefaultFirstBlockOffsetReservesSuperblockRegion()
    {
        using var manager = new BlockManager(OpenStream(), ownsStream: true);

        var result = manager.Append(BlockType.Metadata, PayloadEncoding.Protobuf, SamplePayload(5));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(8192, BlockManager.DefaultFirstBlockOffset);
        Assert.Equal(8192, result.Value.Offset); // spec Section 2: block stream starts after the two superblock slots

        // And the block reads back from there.
        var read = manager.Read(result.Value.Offset);
        Assert.True(read.IsSuccess, read.Error);
        Assert.Equal(SamplePayload(5), read.Value.Payload);
    }

    [Fact]
    public void Append_MintsStrictlyIncreasingUlidBlockIds()
    {
        using var manager = CreateManager();
        var ids = new List<byte[]>();

        for (int i = 0; i < 50; i++)
        {
            var result = manager.Append(BlockType.WAL, PayloadEncoding.Custom, SamplePayload(4, seed: i));
            Assert.True(result.IsSuccess, result.Error);
            ids.Add(result.Value.BlockId);
        }

        for (int i = 1; i < ids.Count; i++)
            Assert.True(ids[i].AsSpan().SequenceCompareTo(ids[i - 1]) > 0,
                $"BlockId {i} is not strictly greater than its predecessor.");
    }

    [Fact]
    public void Append_NotifiesOffsetMapWithHeaderOffsetAndLength()
    {
        var map = new RecordingOffsetMap();
        using var manager = CreateManager(offsetMap: map);

        var first = manager.Append(BlockType.FolderPage, PayloadEncoding.Custom, SamplePayload(20),
            CompressionAlgorithm.Lz4);
        var second = manager.Append(BlockType.KeyStore, PayloadEncoding.Protobuf, SamplePayload(30),
            encrypted: true, keyEpoch: 7);

        Assert.True(first.IsSuccess, first.Error);
        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(2, map.Entries.Count);

        var (header1, offset1, length1) = map.Entries[0];
        Assert.Equal(BlockType.FolderPage, header1.Type);
        Assert.Equal(CompressionAlgorithm.Lz4, header1.Compression);
        Assert.Equal(first.Value.BlockId, header1.BlockId);
        Assert.Equal(first.Value.Offset, offset1);
        Assert.Equal(first.Value.TotalBlockLength, length1);

        var (header2, offset2, length2) = map.Entries[1];
        Assert.Equal(BlockType.KeyStore, header2.Type);
        Assert.True(header2.IsEncrypted);
        Assert.Equal(7, header2.KeyEpoch);
        Assert.Equal(second.Value.Offset, offset2);
        Assert.Equal(second.Value.TotalBlockLength, length2);
    }

    [Fact]
    public void Append_PayloadLargerThanMaxPayloadLength_FailsWithoutWriting()
    {
        using var manager = CreateManager(maxPayloadLength: 16);

        var result = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(17));

        Assert.True(result.IsFailure);
        Assert.Contains("MaxPayloadLength", result.Error);
        Assert.Equal(0, new FileInfo(_path).Length);
    }

    [Fact]
    public void Append_NonZeroKeyEpochWithoutEncryptedFlag_Fails()
    {
        using var manager = CreateManager();

        var result = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(4),
            keyEpoch: 3);

        Assert.True(result.IsFailure);
        Assert.Contains("KeyEpoch", result.Error);
    }

    // ---- Round-trip: every block type and encoding ----

    [Fact]
    public void RoundTrip_EveryBlockTypeAndEncoding_PreservesHeaderFieldsAndPayload()
    {
        using var manager = CreateManager();
        var blockTypes = Enum.GetValues<BlockType>();
        var encodings = Enum.GetValues<PayloadEncoding>();
        var locations = new List<(BlockLocation Location, BlockType Type, PayloadEncoding Encoding, byte[] Payload)>();

        int seed = 0;
        foreach (var type in blockTypes)
            foreach (var encoding in encodings)
            {
                var payload = SamplePayload(17 + seed, seed);
                var result = manager.Append(type, encoding, payload);
                Assert.True(result.IsSuccess, result.Error);
                locations.Add((result.Value, type, encoding, payload));
                seed++;
            }

        foreach (var (location, type, encoding, payload) in locations)
        {
            var read = manager.Read(location.Offset);
            Assert.True(read.IsSuccess, read.Error);
            Assert.Equal(type, read.Value.Header.Type);
            Assert.Equal(encoding, read.Value.Header.Encoding);
            Assert.Equal(location.BlockId, read.Value.Header.BlockId);
            Assert.Equal(payload.Length, read.Value.Header.PayloadLength);
            Assert.Equal(payload, read.Value.Payload);
        }
    }

    /// <summary>Compression algorithms this build supports end to end (spec Section 4.4).</summary>
    private static readonly CompressionAlgorithm[] SupportedCompressions =
    {
        CompressionAlgorithm.None,
        CompressionAlgorithm.Lz4,
        CompressionAlgorithm.Zstd,
    };

    /// <summary>
    /// Deterministic payload that mixes compressible runs with varying bytes, so
    /// compressed frames are exercised without collapsing to a trivial frame.
    /// </summary>
    private static byte[] MixedPayload(int length, int seed = 0)
    {
        var payload = new byte[length];
        for (int i = 0; i < length; i++)
            payload[i] = (i / 64) % 2 == 0 ? (byte)seed : (byte)(i * 31 + seed);
        return payload;
    }

    [Fact]
    public void RoundTrip_FullMatrix_EveryBlockTypeEncodingAndCompression_ByteIdentical()
    {
        // Acceptance (US-EMDB-64-1): every BlockType × every PayloadEncoding ×
        // every supported CompressionAlgorithm round-trips byte-identically,
        // with all header fields intact.
        using var manager = CreateManager();
        var cases = new List<(BlockLocation Location, BlockType Type, PayloadEncoding Encoding,
            CompressionAlgorithm Compression, byte[] Payload)>();

        int seed = 0;
        foreach (var type in Enum.GetValues<BlockType>())
            foreach (var encoding in Enum.GetValues<PayloadEncoding>())
                foreach (var compression in SupportedCompressions)
                {
                    var payload = MixedPayload(200 + seed, seed);
                    var result = manager.AppendCompressed(type, encoding, payload, compression);
                    Assert.True(result.IsSuccess,
                        $"{type}/{encoding}/{compression}: {result.Error}");
                    cases.Add((result.Value, type, encoding, compression, payload));
                    seed++;
                }

        // 22 block types × 4 encodings × 3 compression algorithms.
        Assert.Equal(22 * 4 * 3, cases.Count);

        foreach (var (location, type, encoding, compression, payload) in cases)
        {
            var read = manager.ReadDecompressed(location.Offset);
            Assert.True(read.IsSuccess, $"{type}/{encoding}/{compression}: {read.Error}");

            var header = read.Value.Header;
            Assert.Equal(BlockSerializer.CurrentFormatVersion, header.FormatVersion);
            Assert.Equal(type, header.Type);
            Assert.Equal(encoding, header.Encoding);
            Assert.Equal(compression, header.Compression);
            Assert.Equal(0, header.Flags); // unencrypted, reserved bits zero
            Assert.False(header.IsEncrypted);
            Assert.Equal(0, header.KeyEpoch);
            Assert.Equal(location.BlockId, header.BlockId);

            // Header PayloadLength describes the on-disk (post-compression) bytes.
            Assert.Equal(location.TotalBlockLength - BlockSerializer.FixedOverhead,
                header.PayloadLength);
            if (compression == CompressionAlgorithm.None)
                Assert.Equal(payload.Length, header.PayloadLength);

            // The logical payload round-trips byte-identically.
            Assert.Equal(payload, read.Value.Payload);
        }
    }

    public static TheoryData<CompressionAlgorithm, int> EdgePayloadMatrix()
    {
        var data = new TheoryData<CompressionAlgorithm, int>();
        foreach (var compression in SupportedCompressions)
        {
            data.Add(compression, 0); // empty
            data.Add(compression, 1); // single byte
            // Larger than one compression frame/chunk: LZ4 frames use 64 KiB
            // blocks and Zstd streams 128 KiB chunks, so 300 000 bytes forces
            // multiple internal frames/chunks for both.
            data.Add(compression, 300_000);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EdgePayloadMatrix))]
    public void RoundTrip_EdgePayloads_EveryCompression_ByteIdentical(
        CompressionAlgorithm compression, int payloadLength)
    {
        var payload = MixedPayload(payloadLength, seed: 5);

        BlockLocation location;
        using (var writer = CreateManager())
        {
            var result = writer.AppendCompressed(
                BlockType.EmailContent, PayloadEncoding.RawBytes, payload, compression);
            Assert.True(result.IsSuccess, result.Error);
            location = result.Value;
            writer.Flush();
        }

        // A fresh manager over the same file: nothing round-trips via memory.
        using var reader = CreateManager();
        var read = reader.ReadDecompressed(location.Offset);
        Assert.True(read.IsSuccess, read.Error);
        Assert.Equal(compression, read.Value.Header.Compression);
        Assert.Equal(location.BlockId, read.Value.Header.BlockId);
        Assert.Equal(payload, read.Value.Payload);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.None)]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void RoundTrip_EncryptedFlagKeyEpochAndUlid_SurviveEveryCompression(
        CompressionAlgorithm compression)
    {
        // Encrypted blocks compress explicitly, then Append the final bytes
        // (write order: Serialize → Compress → Encrypt → Checksum → Append).
        // Here the "encrypted" bytes are the compressed bytes themselves; the
        // point is that flags/KeyEpoch/ULID and every header field survive the
        // round trip for each compression byte.
        var logicalPayload = MixedPayload(2048, seed: 9);
        var compressed = BlockCompressor.Compress(logicalPayload, compression);
        Assert.True(compressed.IsSuccess, compressed.Error);

        BlockLocation location;
        using (var writer = CreateManager())
        {
            var result = writer.Append(BlockType.KeyStore, PayloadEncoding.Protobuf,
                compressed.Value, compression, encrypted: true, keyEpoch: 0xBEEF);
            Assert.True(result.IsSuccess, result.Error);
            location = result.Value;
            writer.Flush();
        }

        using var reader = CreateManager();
        var read = reader.Read(location.Offset);
        Assert.True(read.IsSuccess, read.Error);

        var header = read.Value.Header;
        Assert.Equal(BlockSerializer.CurrentFormatVersion, header.FormatVersion);
        Assert.Equal(BlockType.KeyStore, header.Type);
        Assert.Equal(PayloadEncoding.Protobuf, header.Encoding);
        Assert.Equal(compression, header.Compression);
        Assert.Equal(BlockHeader.EncryptedFlag, header.Flags); // bit 0 set, reserved bits zero
        Assert.True(header.IsEncrypted);
        Assert.Equal(0xBEEF, header.KeyEpoch);
        Assert.Equal(16, header.BlockId.Length);
        Assert.Equal(location.BlockId, header.BlockId);
        Assert.Equal(compressed.Value.Length, header.PayloadLength);

        // On-disk bytes are byte-identical to what was appended...
        Assert.Equal(compressed.Value, read.Value.Payload);
        // ...and decompress back to the byte-identical logical payload.
        var decompressed = BlockCompressor.Decompress(
            read.Value.Payload, compression, Superblock.DefaultMaxPayloadLength);
        Assert.True(decompressed.IsSuccess, decompressed.Error);
        Assert.Equal(logicalPayload, decompressed.Value);
    }

    [Fact]
    public void RoundTrip_CompressionEncryptionAndKeyEpoch_PreservedInHeader()
    {
        using var manager = CreateManager();

        var result = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(64),
            CompressionAlgorithm.Zstd, encrypted: true, keyEpoch: 0x0201);
        Assert.True(result.IsSuccess, result.Error);

        var read = manager.Read(result.Value.Offset);
        Assert.True(read.IsSuccess, read.Error);
        Assert.Equal(CompressionAlgorithm.Zstd, read.Value.Header.Compression);
        Assert.True(read.Value.Header.IsEncrypted);
        Assert.Equal(0x0201, read.Value.Header.KeyEpoch);
    }

    [Fact]
    public void RoundTrip_EmptyPayload_StoresSixteenZeroChecksumBytesOnDisk()
    {
        using var manager = CreateManager();

        var result = manager.Append(BlockType.Cleanup, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(BlockSerializer.FixedOverhead, result.Value.TotalBlockLength);
        manager.Flush();

        // Spec Section 4: empty payload -> PayloadChecksum is 16 zero bytes on disk.
        var raw = ReadRawFile();
        var payloadChecksum = raw.AsSpan(BlockSerializer.PayloadOffset, BlockSerializer.ChecksumSize);
        Assert.Equal(new byte[16], payloadChecksum.ToArray());

        var read = manager.Read(result.Value.Offset);
        Assert.True(read.IsSuccess, read.Error);
        Assert.Empty(read.Value.Payload);
    }

    // ---- Read: verification order and corruption handling (spec Sections 4, 13) ----

    [Fact]
    public void Read_CorruptHeaderByte_FailsOnHeaderChecksumBeforeTrustingFields()
    {
        using var manager = CreateManager();
        var location = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40)).Value;
        manager.Flush();

        // Corrupt the low byte of PayloadLength (header offset 32) so a reader that
        // trusted the field before checking the checksum would misread the length.
        CorruptByte(location.Offset + 32);

        var read = manager.Read(location.Offset);
        Assert.True(read.IsFailure);
        Assert.Contains("header checksum", read.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_PayloadLengthAboveMaxPayloadLength_FailsBeforeAllocation()
    {
        // Write a valid 1000-byte-payload block, then reopen with a reader whose
        // superblock bound is smaller: the (checksum-valid) header must be rejected
        // on the PayloadLength sanity check.
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(1000)).Value.Offset;
            writer.Flush();
        }

        using var reader = CreateManager(maxPayloadLength: 999);
        var read = reader.Read(offset);

        Assert.True(read.IsFailure);
        Assert.Contains("MaxPayloadLength", read.Error);
    }

    [Fact]
    public void Read_BlockExtendingPastEof_FailsWithoutAllocatingFromHeaderLength()
    {
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(500)).Value.Offset;
            writer.Flush();
        }

        // Truncate mid-payload: the header is intact and checksum-valid, but the
        // block now extends past EOF -> corrupt header / torn append (spec Section 13).
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(BlockSerializer.SerializedHeaderSize + 100);

        using var manager = CreateManager();
        var read = manager.Read(offset);

        Assert.True(read.IsFailure);
        Assert.Contains("EOF", read.Error);
    }

    [Fact]
    public void Read_CorruptPayloadByte_FailsOnPayloadChecksum()
    {
        using var manager = CreateManager();
        var location = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(64)).Value;
        manager.Flush();

        CorruptByte(location.Offset + BlockSerializer.PayloadOffset + 10);

        var read = manager.Read(location.Offset);
        Assert.True(read.IsFailure);
        Assert.Contains("payload checksum", read.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_AtNonBlockOffset_FailsWithoutThrowing()
    {
        using var manager = CreateManager();
        var location = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(200)).Value;
        manager.Flush();

        // Middle of the payload: whatever is there must fail verification, not throw.
        var read = manager.Read(location.Offset + BlockSerializer.PayloadOffset + 3);
        Assert.True(read.IsFailure);
    }

    [Fact]
    public void Read_OffsetBeforeBlockStreamStart_Fails()
    {
        using var manager = new BlockManager(OpenStream(), ownsStream: true); // firstBlockOffset 8192
        manager.Append(BlockType.Metadata, PayloadEncoding.Protobuf, SamplePayload(5));

        var read = manager.Read(0); // inside the superblock region
        Assert.True(read.IsFailure);
    }

    [Fact]
    public void Read_EmptyFile_FailsGracefully()
    {
        using var manager = CreateManager();
        var read = manager.Read(0);

        Assert.True(read.IsFailure);
        Assert.Contains("EOF", read.Error);
    }

    // ---- Acceptance (US-EMDB-64-2): header checksum verified before trusting any
    // ---- header field; PayloadLength validated against MaxPayloadLength before
    // ---- any payload-sized allocation ----

    /// <summary>
    /// Rewrites the 8 PayloadLength bytes of the header starting at
    /// <paramref name="offset"/>. With <paramref name="recomputeChecksum"/> the
    /// BLAKE3-128 header checksum is recomputed so the header is checksum-VALID and
    /// the only lie is the length itself — the reader must then reject it on the
    /// PayloadLength sanity check. Without it, the stale checksum no longer matches,
    /// so the reader must reject on the checksum before ever reading the length.
    /// </summary>
    private void TamperPayloadLength(long offset, long payloadLength, bool recomputeChecksum)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var header = new byte[BlockSerializer.SerializedHeaderSize];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(header);

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32, 8), payloadLength);
        if (recomputeChecksum)
        {
            var hash = Hasher.Hash(header.AsSpan(0, BlockSerializer.HeaderSize));
            hash.AsSpan()[..BlockSerializer.ChecksumSize]
                .CopyTo(header.AsSpan(BlockSerializer.HeaderSize, BlockSerializer.ChecksumSize));
        }

        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(header);
    }

    [Fact]
    public void Read_CorruptingEachOfThe48HeaderFieldBytes_FailsOnHeaderChecksumNotFieldValidation()
    {
        BlockLocation location;
        using (var writer = CreateManager())
        {
            location = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(32)).Value;
            writer.Flush();
        }

        for (int i = 0; i < BlockSerializer.HeaderSize; i++)
        {
            CorruptByte(location.Offset + i);

            // Fresh manager per probe so its FileStream buffer cannot serve stale bytes.
            using (var manager = CreateManager())
            {
                var read = manager.Read(location.Offset);
                Assert.True(read.IsFailure, $"Corrupting header byte {i} must be rejected.");
                // The checksum verdict must come first: whichever field the byte belongs
                // to (magic, version, type, flags, encoding, compression, KeyEpoch,
                // BlockId, PayloadLength, reserved), the error is the checksum mismatch,
                // never a field-specific validation message — proof no field was trusted.
                Assert.Contains("header checksum", read.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("magic", read.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("version", read.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("MaxPayloadLength", read.Error);
                Assert.DoesNotContain("reserved", read.Error, StringComparison.OrdinalIgnoreCase);
            }

            CorruptByte(location.Offset + i); // XOR twice restores the byte
        }

        // The restored file reads clean: only the corruption caused the failures.
        using var reader = CreateManager();
        var intact = reader.Read(location.Offset);
        Assert.True(intact.IsSuccess, intact.Error);
        Assert.Equal(SamplePayload(32), intact.Value.Payload);
    }

    [Fact]
    public void Read_CorruptingEachOfThe16HeaderChecksumBytes_FailsOnHeaderChecksum()
    {
        BlockLocation location;
        using (var writer = CreateManager())
        {
            location = writer.Append(BlockType.Metadata, PayloadEncoding.Protobuf, SamplePayload(20)).Value;
            writer.Flush();
        }

        for (int i = BlockSerializer.HeaderSize; i < BlockSerializer.SerializedHeaderSize; i++)
        {
            CorruptByte(location.Offset + i);

            using (var manager = CreateManager())
            {
                var read = manager.Read(location.Offset);
                Assert.True(read.IsFailure, $"Corrupting header checksum byte {i} must be rejected.");
                Assert.Contains("header checksum", read.Error, StringComparison.OrdinalIgnoreCase);
            }

            CorruptByte(location.Offset + i); // restore
        }

        using var reader = CreateManager();
        var intact = reader.Read(location.Offset);
        Assert.True(intact.IsSuccess, intact.Error);
    }

    [Fact]
    public void Read_AbsurdPayloadLengthWithStaleChecksum_ReportsHeaderChecksumNotLength()
    {
        BlockLocation location;
        using (var writer = CreateManager())
        {
            location = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(16)).Value;
            writer.Flush();
        }

        // Both checks would fail here; the checksum verdict must win — proof the
        // length field is never even read out of an unverified header.
        TamperPayloadLength(location.Offset, long.MaxValue, recomputeChecksum: false);

        using var manager = CreateManager();
        var read = manager.Read(location.Offset);
        Assert.True(read.IsFailure);
        Assert.Contains("header checksum", read.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MaxPayloadLength", read.Error);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(int.MaxValue)]
    [InlineData(Superblock.DefaultMaxPayloadLength + 1)]
    public void Read_TamperedPayloadLengthAboveMax_ChecksumValid_FailsFastWithoutLargeAllocation(long absurdLength)
    {
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(24)).Value.Offset;
            writer.Flush();
        }

        // Checksum-valid header whose only lie is the absurd PayloadLength: it must
        // be rejected by the MaxPayloadLength sanity bound, and fail fast rather
        // than attempt a payload-sized allocation (long.MaxValue would overflow,
        // int.MaxValue would be a 2 GiB OOM buffer).
        TamperPayloadLength(offset, absurdLength, recomputeChecksum: true);

        using var manager = CreateManager(); // default MaxPayloadLength 256 MiB
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var read = manager.Read(offset);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(read.IsFailure);
        Assert.Contains("MaxPayloadLength", read.Error);
        Assert.Contains(absurdLength.ToString(), read.Error);
        Assert.True(allocated < 1_000_000,
            $"Rejecting PayloadLength {absurdLength} allocated {allocated} bytes; the length must be validated before allocation.");
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Read_TamperedNegativePayloadLength_ChecksumValid_FailsOnLengthSanityWithoutAllocation(long negativeLength)
    {
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.WAL, PayloadEncoding.Custom, SamplePayload(24)).Value.Offset;
            writer.Flush();
        }

        TamperPayloadLength(offset, negativeLength, recomputeChecksum: true);

        using var manager = CreateManager();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var read = manager.Read(offset);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(read.IsFailure);
        Assert.Contains("non-negative", read.Error);
        Assert.True(allocated < 1_000_000,
            $"Rejecting PayloadLength {negativeLength} allocated {allocated} bytes; the length must be validated before allocation.");
    }

    [Fact]
    public void Read_TamperedPayloadLengthWithinMaxButPastEof_FailsOnEofBoundBeforeAllocation()
    {
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(24)).Value.Offset;
            writer.Flush();
        }

        // 1,000,000 passes the MaxPayloadLength sanity check, but the implied
        // 1,000,096-byte block cannot fit in the ~120-byte file: the EOF bound must
        // reject it before the ~1 MB payload buffer is ever allocated.
        TamperPayloadLength(offset, 1_000_000, recomputeChecksum: true);

        using var manager = CreateManager();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var read = manager.Read(offset);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(read.IsFailure);
        Assert.Contains("EOF", read.Error);
        Assert.True(allocated < 500_000,
            $"Rejecting a past-EOF PayloadLength allocated {allocated} bytes; the EOF bound must be checked before allocation.");
    }

    // ---- Acceptance (US-EMDB-64-3): payload checksum covers the on-disk bytes
    // ---- (post-compression, exactly as stored), and an empty payload stores
    // ---- 16 zero checksum bytes on disk ----

    /// <summary>BLAKE3-128: the first 16 bytes of BLAKE3-256 (spec Section 4).</summary>
    private static byte[] Blake3_128(ReadOnlySpan<byte> data) =>
        Hasher.Hash(data).AsSpan()[..BlockSerializer.ChecksumSize].ToArray();

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void PayloadChecksum_CompressedBlock_IsBlake3OfOnDiskBytesNotLogicalPayload(
        CompressionAlgorithm compression)
    {
        // Highly compressible logical payload so the on-disk (compressed) bytes
        // are guaranteed to differ from the logical payload.
        var logicalPayload = new byte[4096];
        Array.Fill(logicalPayload, (byte)0x42);

        BlockLocation location;
        using (var writer = CreateManager())
        {
            var result = writer.AppendCompressed(
                BlockType.EmailContent, PayloadEncoding.RawBytes, logicalPayload, compression);
            Assert.True(result.IsSuccess, result.Error);
            location = result.Value;
            writer.Flush();
        }

        var raw = ReadRawFile();
        var onDiskPayloadLength = (int)(location.TotalBlockLength - BlockSerializer.FixedOverhead);
        var onDiskPayload = raw.AsSpan(
            (int)location.Offset + BlockSerializer.PayloadOffset, onDiskPayloadLength).ToArray();
        var storedChecksum = raw.AsSpan(
            (int)location.Offset + BlockSerializer.PayloadOffset + onDiskPayloadLength,
            BlockSerializer.ChecksumSize).ToArray();

        // The stored bytes really are the compressed form, not the logical payload.
        Assert.NotEqual(logicalPayload, onDiskPayload);

        // Spec Section 4: the checksum is BLAKE3-128 over exactly the bytes as
        // stored on disk (post-compression) ...
        Assert.Equal(Blake3_128(onDiskPayload), storedChecksum);
        // ... and NOT over the logical (uncompressed) payload.
        Assert.NotEqual(Blake3_128(logicalPayload), storedChecksum);

        // The block still verifies and decompresses back to the logical payload.
        using var reader = CreateManager();
        var read = reader.ReadDecompressed(location.Offset);
        Assert.True(read.IsSuccess, read.Error);
        Assert.Equal(compression, read.Value.Header.Compression);
        Assert.Equal(logicalPayload, read.Value.Payload);
    }

    [Fact]
    public void PayloadChecksum_CorruptingEveryOnDiskPayloadByteOfCompressedBlock_FailsRead()
    {
        BlockLocation location;
        using (var writer = CreateManager())
        {
            var result = writer.AppendCompressed(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(600),
                CompressionAlgorithm.Zstd);
            Assert.True(result.IsSuccess, result.Error);
            location = result.Value;
            writer.Flush();
        }

        var onDiskPayloadLength = location.TotalBlockLength - BlockSerializer.FixedOverhead;
        Assert.True(onDiskPayloadLength > 0);

        for (long i = 0; i < onDiskPayloadLength; i++)
        {
            CorruptByte(location.Offset + BlockSerializer.PayloadOffset + i);

            // Fresh manager per probe so its FileStream buffer cannot serve stale bytes.
            using (var manager = CreateManager())
            {
                var read = manager.Read(location.Offset);
                Assert.True(read.IsFailure, $"Corrupting on-disk payload byte {i} must be rejected.");
                Assert.Contains("payload checksum", read.Error, StringComparison.OrdinalIgnoreCase);
            }

            CorruptByte(location.Offset + BlockSerializer.PayloadOffset + i); // XOR twice restores
        }

        // The restored file reads clean: only the corruption caused the failures.
        using var reader = CreateManager();
        var intact = reader.ReadDecompressed(location.Offset);
        Assert.True(intact.IsSuccess, intact.Error);
        Assert.Equal(SamplePayload(600), intact.Value.Payload);
    }

    [Fact]
    public void PayloadChecksum_CorruptingAnyOfThe16StoredChecksumBytes_FailsRead()
    {
        var payload = SamplePayload(32);
        BlockLocation location;
        using (var writer = CreateManager())
        {
            location = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, payload).Value;
            writer.Flush();
        }

        long checksumOffset = location.Offset + BlockSerializer.PayloadOffset + payload.Length;

        // The stored field is BLAKE3-128 of the on-disk payload bytes.
        var raw = ReadRawFile();
        Assert.Equal(Blake3_128(payload),
            raw.AsSpan((int)checksumOffset, BlockSerializer.ChecksumSize).ToArray());

        for (int i = 0; i < BlockSerializer.ChecksumSize; i++)
        {
            CorruptByte(checksumOffset + i);

            using (var manager = CreateManager())
            {
                var read = manager.Read(location.Offset);
                Assert.True(read.IsFailure, $"Corrupting payload checksum byte {i} must be rejected.");
                Assert.Contains("payload checksum", read.Error, StringComparison.OrdinalIgnoreCase);
            }

            CorruptByte(checksumOffset + i); // restore
        }

        using var reader = CreateManager();
        var intact = reader.Read(location.Offset);
        Assert.True(intact.IsSuccess, intact.Error);
        Assert.Equal(payload, intact.Value.Payload);
    }

    [Fact]
    public void EmptyPayload_ZeroChecksumIsSpecialCase_AndCorruptingAnyOfItsBytesFailsRead()
    {
        BlockLocation location;
        using (var writer = CreateManager())
        {
            var result = writer.Append(BlockType.Cleanup, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty);
            Assert.True(result.IsSuccess, result.Error);
            location = result.Value;
            writer.Flush();
        }

        // Spec Section 4: an empty payload stores exactly 16 zero bytes — a
        // special case, NOT the BLAKE3-128 hash of zero-length input.
        var raw = ReadRawFile();
        var stored = raw.AsSpan(
            (int)location.Offset + BlockSerializer.PayloadOffset, BlockSerializer.ChecksumSize).ToArray();
        Assert.Equal(new byte[BlockSerializer.ChecksumSize], stored);
        Assert.NotEqual(Blake3_128(ReadOnlySpan<byte>.Empty), stored);

        // The zero bytes are load-bearing: corrupting any of them fails the read.
        for (int i = 0; i < BlockSerializer.ChecksumSize; i++)
        {
            CorruptByte(location.Offset + BlockSerializer.PayloadOffset + i);

            using (var manager = CreateManager())
            {
                var read = manager.Read(location.Offset);
                Assert.True(read.IsFailure, $"Corrupting empty-payload checksum byte {i} must be rejected.");
                Assert.Contains("payload checksum", read.Error, StringComparison.OrdinalIgnoreCase);
            }

            CorruptByte(location.Offset + BlockSerializer.PayloadOffset + i); // restore
        }

        // The restored block round-trips to an empty payload.
        using var reader = CreateManager();
        var intact = reader.Read(location.Offset);
        Assert.True(intact.IsSuccess, intact.Error);
        Assert.Empty(intact.Value.Payload);
    }

    // ---- Backward walk from EOF (footer TotalBlockLength) ----

    [Fact]
    public void FindBlockStart_SupportsBackwardWalkFromEofOverAllBlocks()
    {
        using var manager = CreateManager();
        var appended = new List<BlockLocation>();
        for (int i = 0; i < 5; i++)
            appended.Add(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes,
                SamplePayload(10 + i * 37, seed: i)).Value);
        manager.Flush();

        var walked = new List<(long Offset, byte[] BlockId)>();
        long end = new FileInfo(_path).Length;
        while (end > 0)
        {
            var startResult = manager.FindBlockStart(end);
            Assert.True(startResult.IsSuccess, startResult.Error);
            var block = manager.Read(startResult.Value);
            Assert.True(block.IsSuccess, block.Error);
            walked.Add((startResult.Value, block.Value.Header.BlockId));
            end = startResult.Value;
        }

        Assert.Equal(appended.Count, walked.Count);
        for (int i = 0; i < appended.Count; i++)
        {
            var fromEnd = walked[appended.Count - 1 - i];
            Assert.Equal(appended[i].Offset, fromEnd.Offset);
            Assert.Equal(appended[i].BlockId, fromEnd.BlockId);
        }
    }

    [Fact]
    public void FindBlockStart_BackwardWalk_AcrossEmptyCompressedAndVaryingPayloads()
    {
        using var manager = CreateManager();
        var appended = new List<BlockLocation>();

        // Varying payload sizes, including empty and compressed payloads: the
        // footer arithmetic must not depend on payload size or content.
        appended.Add(manager.Append(
            BlockType.Cleanup, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty).Value);
        var compressible = new byte[4096];
        Array.Fill(compressible, (byte)0x42);
        appended.Add(manager.AppendCompressed(
            BlockType.EmailContent, PayloadEncoding.RawBytes, compressible,
            CompressionAlgorithm.Zstd).Value);
        appended.Add(manager.Append(
            BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(1)).Value);
        appended.Add(manager.AppendCompressed(
            BlockType.FolderPage, PayloadEncoding.Protobuf, compressible,
            CompressionAlgorithm.Lz4).Value);
        appended.Add(manager.Append(
            BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(10_000)).Value);
        appended.Add(manager.Append(
            BlockType.Cleanup, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty).Value);
        manager.Flush();

        var walked = new List<long>();
        long end = new FileInfo(_path).Length;
        while (end > 0)
        {
            var startResult = manager.FindBlockStart(end);
            Assert.True(startResult.IsSuccess, startResult.Error);
            // The footer-derived span (end - start) is exactly the block's true
            // on-disk length, so the walked block reads back verified.
            var block = manager.Read(startResult.Value);
            Assert.True(block.IsSuccess, block.Error);
            Assert.Equal(end - startResult.Value,
                BlockSerializer.GetTotalBlockLength(block.Value.Header.PayloadLength));
            walked.Add(startResult.Value);
            end = startResult.Value;
        }

        // Every block is visited exactly once, in reverse append order, at the
        // exact offsets Append reported.
        Assert.Equal(appended.Count, walked.Count);
        for (int i = 0; i < appended.Count; i++)
        {
            var location = appended[i];
            Assert.Equal(location.Offset, walked[appended.Count - 1 - i]);
            var stepEnd = i + 1 < appended.Count ? appended[i + 1].Offset : new FileInfo(_path).Length;
            Assert.Equal(location.TotalBlockLength, stepEnd - location.Offset);
        }
    }

    [Fact]
    public void Footer_TotalBlockLengthField_EqualsTrueOnDiskBlockSpanForEachBlock()
    {
        var appended = new List<BlockLocation>();
        using (var writer = CreateManager())
        {
            appended.Add(writer.Append(
                BlockType.Cleanup, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty).Value);
            appended.Add(writer.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(333)).Value);
            var compressible = new byte[2048];
            Array.Fill(compressible, (byte)0x17);
            appended.Add(writer.AppendCompressed(
                BlockType.EmailContent, PayloadEncoding.RawBytes, compressible,
                CompressionAlgorithm.Zstd).Value);
            writer.Flush();
        }

        var raw = ReadRawFile();
        for (int i = 0; i < appended.Count; i++)
        {
            var location = appended[i];

            // The true on-disk span: from this block's first header byte to the
            // next block's first header byte (or EOF for the last block).
            long trueSpan = (i + 1 < appended.Count ? appended[i + 1].Offset : raw.Length)
                - location.Offset;
            Assert.Equal(location.TotalBlockLength, trueSpan);

            // Raw 16-byte footer at the end of the span: ~magic + TotalBlockLength
            // (spec Section 4), with TotalBlockLength = header + payload + footer.
            var footer = raw.AsSpan(
                (int)(location.Offset + trueSpan) - BlockSerializer.FooterSize,
                BlockSerializer.FooterSize);
            Assert.Equal(BlockSerializer.FooterMagic,
                BinaryPrimitives.ReadUInt64LittleEndian(footer[..8]));
            Assert.Equal(trueSpan, BinaryPrimitives.ReadInt64LittleEndian(footer[8..]));
        }
    }

    [Fact]
    public void FindBlockStart_BackwardWalk_WithSuperblockRegion_TerminatesAtFirstBlockOffset()
    {
        // Default firstBlockOffset (8192): the walk must land exactly on
        // FirstBlockOffset after the oldest block, never inside the superblock region.
        using var manager = new BlockManager(OpenStream(), ownsStream: true);
        var appended = new List<BlockLocation>();
        for (int i = 0; i < 4; i++)
            appended.Add(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes,
                SamplePayload(25 * i, seed: i)).Value); // includes an empty payload (i = 0)
        manager.Flush();

        var walked = new List<long>();
        long end = new FileInfo(_path).Length;
        while (end > manager.FirstBlockOffset)
        {
            var startResult = manager.FindBlockStart(end);
            Assert.True(startResult.IsSuccess, startResult.Error);
            walked.Add(startResult.Value);
            end = startResult.Value;
        }

        Assert.Equal(manager.FirstBlockOffset, end);
        Assert.Equal(appended.Count, walked.Count);
        for (int i = 0; i < appended.Count; i++)
            Assert.Equal(appended[i].Offset, walked[appended.Count - 1 - i]);
    }

    [Fact]
    public void FindBlockStart_TornFinalAppend_FailsOnFooterMagic()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(50));
        manager.Flush();

        long eof = new FileInfo(_path).Length;
        CorruptByte(eof - BlockSerializer.FooterSize + 2); // footer magic byte

        var result = manager.FindBlockStart(eof);
        Assert.True(result.IsFailure);
        Assert.Contains("footer magic", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindBlockStart_BeforeBlockStreamStart_Fails()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(8));
        manager.Flush();

        // A footer cannot end where no whole block fits before it.
        var result = manager.FindBlockStart(BlockSerializer.FixedOverhead - 1);
        Assert.True(result.IsFailure);
    }

    // ---- FindLastValidBlock: backward EOF walk over a torn tail (US-EMDB-66-7) ----

    /// <summary>Appends raw bytes at EOF via a separate handle (simulates a torn/garbage tail).</summary>
    private void AppendRawToFile(byte[] bytes)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(0, SeekOrigin.End);
        fs.Write(bytes);
    }

    /// <summary>Serializes one complete, valid block image (for crafting torn tails).</summary>
    private static byte[] SerializeWholeBlock(byte[] payload) =>
        BlockSerializer.Serialize(new BlockHeader
        {
            Type = BlockType.EmailContent,
            Encoding = PayloadEncoding.RawBytes,
            BlockId = SamplePayload(16, seed: 99),
            PayloadLength = payload.Length,
        }, payload);

    private static void AssertSameLocation(BlockLocation expected, Result<BlockLocation> actual)
    {
        Assert.True(actual.IsSuccess, actual.IsFailure ? actual.Error : null);
        Assert.Equal(expected.Offset, actual.Value.Offset);
        Assert.Equal(expected.TotalBlockLength, actual.Value.TotalBlockLength);
        Assert.Equal(expected.BlockId, actual.Value.BlockId);
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_CleanEof_ReturnsFinalBlock()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40));
        var last = manager.Append(BlockType.EmailMetadata, PayloadEncoding.Protobuf, SamplePayload(200)).Value;
        manager.Flush();

        AssertSameLocation(last, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_TornTailPartialBlock_ReturnsLastFullyValidBlock()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(64));
        var last = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(120)).Value;
        manager.Flush();

        // Torn append: only the first half of the next block hit disk. The torn
        // prefix contains a fully valid 64-byte header, so header magic alone
        // would be fooled — the missing payload/footer must disqualify it.
        var whole = SerializeWholeBlock(SamplePayload(300, seed: 7));
        AppendRawToFile(whole.AsSpan(0, whole.Length / 2).ToArray());

        AssertSameLocation(last, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_GarbageTail_ReturnsLastValidBlock()
    {
        using var manager = CreateManager();
        var last = manager.Append(BlockType.WAL, PayloadEncoding.Custom, SamplePayload(75)).Value;
        manager.Flush();

        AppendRawToFile(SamplePayload(4093, seed: 3));

        AssertSameLocation(last, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_LongGarbageTail_FooterSpansChunkBoundary()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(30));
        var last = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(500, seed: 4)).Value;
        manager.Flush();

        // 65528 garbage bytes place the first backward 64 KiB chunk boundary
        // in the middle of the real footer's magic, so the walk must cross
        // chunks (with overlap) to see the footer whole.
        AppendRawToFile(SamplePayload(65528, seed: 5));

        AssertSameLocation(last, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_FakeFooterInGarbageTail_SkipsFalsePositive()
    {
        using var manager = CreateManager();
        var last = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(90)).Value;
        manager.Flush();

        // Garbage tail ending in a crafted, wholly plausible footer: correct
        // magic and a sane TotalBlockLength that lands inside the garbage.
        AppendRawToFile(SamplePayload(200, seed: 6));
        var fake = new byte[BlockSerializer.FooterSize];
        BinaryPrimitives.WriteUInt64LittleEndian(fake.AsSpan(0, 8), BlockSerializer.FooterMagic);
        BinaryPrimitives.WriteInt64LittleEndian(fake.AsSpan(8, 8), BlockSerializer.FixedOverhead + 100);
        AppendRawToFile(fake);

        AssertSameLocation(last, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_TailCopyOfRealFooter_SkipsFalsePositive()
    {
        using var manager = CreateManager();
        var last = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(150)).Value;
        manager.Flush();

        // A byte-for-byte copy of the real footer stranded after garbage: its
        // TotalBlockLength is valid for the real block but, measured from the
        // copy's position, points into garbage — full verification rejects it.
        var realFooter = ReadRawFile()[^BlockSerializer.FooterSize..];
        AppendRawToFile(SamplePayload(100, seed: 8));
        AppendRawToFile(realFooter);

        AssertSameLocation(last, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_FooterMagicInsidePayload_NotTrusted()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(60)).Value;

        // Second block's payload embeds fake footer bytes (magic + plausible length).
        var payload = SamplePayload(128, seed: 9);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(100, 8), BlockSerializer.FooterMagic);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(108, 8), BlockSerializer.FixedOverhead + 4);
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, payload);
        manager.Flush();

        // Corrupt the second block's real footer magic (torn tail): the walk
        // then meets the embedded fake footer first and must reject it rather
        // than resurrect a bogus block.
        CorruptByte(new FileInfo(_path).Length - BlockSerializer.FooterSize + 3);

        AssertSameLocation(first, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_CorruptFinalPayload_ReturnsPreviousBlock()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(80)).Value;
        var second = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(120)).Value;
        manager.Flush();

        // The final block's footer is intact, but its payload is corrupt: the
        // footer match alone must not be trusted — full verification fails and
        // the walk falls back to the previous block.
        CorruptByte(second.Offset + BlockSerializer.PayloadOffset + 5);

        AssertSameLocation(first, manager.FindLastValidBlock());
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_EmptyFile_Fails()
    {
        using var manager = CreateManager();

        var result = manager.FindLastValidBlock();

        Assert.True(result.IsFailure);
        Assert.Contains("No block can fit", result.Error);
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_OnlyGarbage_NoValidBlock_Fails()
    {
        using var manager = CreateManager();
        AppendRawToFile(SamplePayload(500, seed: 11));

        var result = manager.FindLastValidBlock();

        Assert.True(result.IsFailure);
        Assert.Contains("No valid block found", result.Error);
    }

    [Fact]
    public void FindLastValidBlock_BackwardWalk_WithSuperblockRegion_TornTail_ReturnsLastValid()
    {
        // Default firstBlockOffset (8192): the walk must never treat superblock
        // region bytes as blocks, and must survive a torn tail.
        using var manager = new BlockManager(OpenStream(), ownsStream: true);
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(33));
        var last = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(77, seed: 2)).Value;
        manager.Flush();

        var whole = SerializeWholeBlock(SamplePayload(256, seed: 12));
        AppendRawToFile(whole.AsSpan(0, whole.Length - BlockSerializer.FooterSize + 3).ToArray());

        var result = manager.FindLastValidBlock();
        AssertSameLocation(last, result);
        Assert.True(result.Value.Offset >= manager.FirstBlockOffset);
    }

    // ---- Durability ----

    [Fact]
    public void Flush_FsyncsAndCountsOperations()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.WAL, PayloadEncoding.Custom, SamplePayload(12));

        Assert.Equal(0, manager.FlushToDiskCount);
        var result = manager.Flush();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, manager.FlushToDiskCount);
        Assert.False(manager.IsPoisoned);
    }
}
