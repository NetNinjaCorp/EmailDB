using System.Security.Cryptography;
using EmailDB.Format.V3;
using K4os.Compression.LZ4.Streams;
using ZstdSharp;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 payload compression pipeline (EmailDB_FileFormat_Spec.md
/// Section 4.4): compression-byte dispatch (None/LZ4/Zstd, unsupported values
/// fail cleanly), round-trips both standalone and through the block
/// writer/reader, and the decompression bomb guard at MaxPayloadLength × 16.
/// </summary>
public class BlockCompressorTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-blockcompressor-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private BlockManager CreateManager(long maxPayloadLength = Superblock.DefaultMaxPayloadLength) =>
        new(new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite),
            maxPayloadLength, firstBlockOffset: 0, ownsStream: true);

    /// <summary>Compressible payload: repeating text with a deterministic sprinkle of variation.</summary>
    private static byte[] CompressiblePayload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)"EmailDB v3 block payload "[i % 25]).ToArray();

    // ---- Standalone round-trips per compression byte ----

    [Theory]
    [InlineData(CompressionAlgorithm.None)]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void CompressDecompress_RoundTrips(CompressionAlgorithm compression)
    {
        var payload = CompressiblePayload(50_000);

        var compressed = BlockCompressor.Compress(payload, compression);
        Assert.True(compressed.IsSuccess, compressed.Error);

        var decompressed = BlockCompressor.Decompress(
            compressed.Value, compression, Superblock.DefaultMaxPayloadLength);
        Assert.True(decompressed.IsSuccess, decompressed.Error);
        Assert.Equal(payload, decompressed.Value);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void Compress_CompressibleData_ShrinksAndNoneIsPassThrough(CompressionAlgorithm compression)
    {
        var payload = CompressiblePayload(50_000);

        var compressed = BlockCompressor.Compress(payload, compression);
        Assert.True(compressed.IsSuccess, compressed.Error);
        Assert.True(compressed.Value.Length < payload.Length,
            $"{compression} produced {compressed.Value.Length} bytes for a compressible {payload.Length}-byte payload.");

        var none = BlockCompressor.Compress(payload, CompressionAlgorithm.None);
        Assert.True(none.IsSuccess, none.Error);
        Assert.Equal(payload, none.Value);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.None)]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void CompressDecompress_EmptyPayload_RoundTrips(CompressionAlgorithm compression)
    {
        var compressed = BlockCompressor.Compress(ReadOnlySpan<byte>.Empty, compression);
        Assert.True(compressed.IsSuccess, compressed.Error);

        var decompressed = BlockCompressor.Decompress(
            compressed.Value, compression, Superblock.DefaultMaxPayloadLength);
        Assert.True(decompressed.IsSuccess, decompressed.Error);
        Assert.Empty(decompressed.Value);
    }

    // ---- Dispatch: unsupported compression bytes fail cleanly ----

    [Theory]
    [InlineData(CompressionAlgorithm.Brotli)]
    [InlineData(CompressionAlgorithm.Deflate)]
    [InlineData((CompressionAlgorithm)0x05)] // reserved
    [InlineData((CompressionAlgorithm)0xFF)] // reserved
    public void CompressAndDecompress_UnsupportedAlgorithm_FailsWithoutThrowing(CompressionAlgorithm compression)
    {
        var compressed = BlockCompressor.Compress(new byte[10], compression);
        Assert.True(compressed.IsFailure);
        Assert.Contains("not supported", compressed.Error);

        var decompressed = BlockCompressor.Decompress(
            new byte[10], compression, Superblock.DefaultMaxPayloadLength);
        Assert.True(decompressed.IsFailure);
        Assert.Contains("not supported", decompressed.Error);
    }

    // ---- Bomb guard: decompressed output capped at MaxPayloadLength × 16 ----

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void Decompress_OutputBeyondBombGuard_Fails(CompressionAlgorithm compression)
    {
        // 100 KB of zeros compresses to a few hundred bytes but inflates far
        // past a 1 KB MaxPayloadLength × 16 = 16 KB guard.
        var compressed = BlockCompressor.Compress(new byte[100_000], compression);
        Assert.True(compressed.IsSuccess, compressed.Error);

        var decompressed = BlockCompressor.Decompress(compressed.Value, compression, maxPayloadLength: 1024);
        Assert.True(decompressed.IsFailure);
        Assert.Contains("bomb guard", decompressed.Error);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void Decompress_OutputExactlyAtBombGuard_Succeeds(CompressionAlgorithm compression)
    {
        // 16 KB of zeros sits exactly at the 1 KB × 16 guard: allowed.
        var compressed = BlockCompressor.Compress(new byte[16 * 1024], compression);
        Assert.True(compressed.IsSuccess, compressed.Error);

        var decompressed = BlockCompressor.Decompress(compressed.Value, compression, maxPayloadLength: 1024);
        Assert.True(decompressed.IsSuccess, decompressed.Error);
        Assert.Equal(16 * 1024, decompressed.Value.Length);
    }

    /// <summary>
    /// Builds a real decompression bomb: a valid frame whose decompressed
    /// content is <paramref name="expandedSize"/> zero bytes, produced by
    /// streaming a reused chunk through the encoder so the crafted input stays
    /// small and the test itself never materializes the expanded payload.
    /// </summary>
    private static byte[] CraftBomb(CompressionAlgorithm compression, long expandedSize)
    {
        var output = new MemoryStream();
        using (Stream encoder = compression == CompressionAlgorithm.Lz4
            ? LZ4Stream.Encode(output, leaveOpen: true)
            : new CompressionStream(output, leaveOpen: true))
        {
            var zeros = new byte[64 * 1024];
            for (long written = 0; written < expandedSize; written += zeros.Length)
                encoder.Write(zeros, 0, zeros.Length);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void Decompress_CraftedBomb_TripsGuardMidStreamWithoutAllocatingExpandedSize(
        CompressionAlgorithm compression)
    {
        // A crafted bomb: the compressed input is tiny, but the frame declares
        // 256 MB of content — 16,384× past the 1 KB × 16 = 16 KB guard.
        const long expandedSize = 256L * 1024 * 1024;
        var bomb = CraftBomb(compression, expandedSize);
        Assert.True(bomb.Length < 2 * 1024 * 1024,
            $"crafted {compression} bomb should be small, got {bomb.Length} bytes");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var decompressed = BlockCompressor.Decompress(bomb, compression, maxPayloadLength: 1024);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(decompressed.IsFailure);
        Assert.Contains("bomb guard", decompressed.Error);

        // The guard must trip while streaming, long before the declared 256 MB
        // is buffered: total allocations stay far below the expanded size.
        Assert.True(allocated < 32 * 1024 * 1024,
            $"decoding a {expandedSize}-byte {compression} bomb allocated {allocated} bytes; guard did not trip mid-stream");
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void Decompress_GarbageInput_FailsWithoutThrowing(CompressionAlgorithm compression)
    {
        var garbage = new byte[64];
        RandomNumberGenerator.Fill(garbage);

        var decompressed = BlockCompressor.Decompress(
            garbage, compression, Superblock.DefaultMaxPayloadLength);
        Assert.True(decompressed.IsFailure);
    }

    // ---- Writer/reader integration: compression byte round-trips on disk ----

    [Theory]
    [InlineData(CompressionAlgorithm.None)]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void AppendCompressed_ReadDecompressed_RoundTripsPayloadAndCompressionByte(
        CompressionAlgorithm compression)
    {
        using var manager = CreateManager();
        var payload = CompressiblePayload(20_000);

        var appended = manager.AppendCompressed(
            BlockType.EmailContent, PayloadEncoding.RawBytes, payload, compression);
        Assert.True(appended.IsSuccess, appended.Error);

        // The raw on-disk block carries the compression byte and the compressed bytes.
        var raw = manager.Read(appended.Value.Offset);
        Assert.True(raw.IsSuccess, raw.Error);
        Assert.Equal(compression, raw.Value.Header.Compression);
        if (compression != CompressionAlgorithm.None)
            Assert.True(raw.Value.Payload.Length < payload.Length);

        // The decompressing read returns the original payload.
        var read = manager.ReadDecompressed(appended.Value.Offset);
        Assert.True(read.IsSuccess, read.Error);
        Assert.Equal(compression, read.Value.Header.Compression);
        Assert.Equal(payload, read.Value.Payload);
    }

    [Fact]
    public void AppendCompressed_UnsupportedAlgorithm_FailsAndWritesNothing()
    {
        using var manager = CreateManager();

        var appended = manager.AppendCompressed(
            BlockType.WAL, PayloadEncoding.Custom, new byte[10], CompressionAlgorithm.Brotli);

        Assert.True(appended.IsFailure);
        Assert.Contains("not supported", appended.Error);
        Assert.Equal(0, new FileInfo(_path).Length);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void ReadDecompressed_BlockBeyondBombGuard_Fails(CompressionAlgorithm compression)
    {
        // Guard is MaxPayloadLength × 16 = 16 KB, but the block inflates to 100 KB.
        using var manager = CreateManager(maxPayloadLength: 1024);

        var appended = manager.AppendCompressed(
            BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[100_000], compression);
        Assert.True(appended.IsSuccess, appended.Error); // compressed bytes fit MaxPayloadLength

        var read = manager.ReadDecompressed(appended.Value.Offset);
        Assert.True(read.IsFailure);
        Assert.Contains("bomb guard", read.Error);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Lz4)]
    [InlineData(CompressionAlgorithm.Zstd)]
    public void ReadDecompressed_CraftedBombBlockOnDisk_TripsGuardWithoutAllocatingExpandedSize(
        CompressionAlgorithm compression)
    {
        // A crafted bomb written as a real on-disk block via the raw Append
        // path (its compressed bytes fit MaxPayloadLength = 2 MB), declaring
        // 256 MB — 8× past the 2 MB × 16 = 32 MB guard.
        const long expandedSize = 256L * 1024 * 1024;
        using var manager = CreateManager(maxPayloadLength: 2 * 1024 * 1024);
        var bomb = CraftBomb(compression, expandedSize);

        var appended = manager.Append(
            BlockType.EmailContent, PayloadEncoding.RawBytes, bomb, compression);
        Assert.True(appended.IsSuccess, appended.Error);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = manager.ReadDecompressed(appended.Value.Offset);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(read.IsFailure);
        Assert.Contains("bomb guard", read.Error);
        Assert.True(allocated < 128 * 1024 * 1024,
            $"reading a {expandedSize}-byte {compression} bomb block allocated {allocated} bytes; guard did not trip mid-stream");
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Brotli)]
    [InlineData((CompressionAlgorithm)0xFF)] // reserved
    public void ReadDecompressed_OnDiskUnsupportedCompressionByte_FailsCleanly(
        CompressionAlgorithm compression)
    {
        using var manager = CreateManager();

        // The raw Append path stamps whatever compression byte the caller
        // claims, so a file written by a newer build can carry bytes this
        // build cannot decode.
        var appended = manager.Append(
            BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[10], compression);
        Assert.True(appended.IsSuccess, appended.Error);

        // The verifying raw read still succeeds — dispatch happens on decompress.
        var raw = manager.Read(appended.Value.Offset);
        Assert.True(raw.IsSuccess, raw.Error);
        Assert.Equal(compression, raw.Value.Header.Compression);

        var read = manager.ReadDecompressed(appended.Value.Offset);
        Assert.True(read.IsFailure);
        Assert.Contains("not supported", read.Error);
    }

    [Fact]
    public void ReadDecompressed_EncryptedBlock_FailsBecauseDecryptComesFirst()
    {
        using var manager = CreateManager();

        // On-disk bytes of an encrypted block are opaque ciphertext to this layer.
        var appended = manager.Append(BlockType.KeyStore, PayloadEncoding.Protobuf,
            new byte[32], CompressionAlgorithm.Zstd, encrypted: true, keyEpoch: 3);
        Assert.True(appended.IsSuccess, appended.Error);

        var read = manager.ReadDecompressed(appended.Value.Offset);
        Assert.True(read.IsFailure);
        Assert.Contains("decrypt", read.Error, StringComparison.OrdinalIgnoreCase);
    }
}
