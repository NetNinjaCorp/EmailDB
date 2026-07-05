using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Wiring tests for the per-failure handlers of the v3 corruption-handling contract
/// (US-EMDB-75-6, EmailDB_FileFormat_Spec.md Section 13). Each Section 13 row's
/// required behavior is exercised through the real read/open paths and asserted to
/// surface the correct taxonomy error on <see cref="Result{T}.VerificationError"/>:
/// header/payload checksum mismatches and insane lengths as
/// <see cref="CorruptionError"/>, a Merkle mismatch as <see cref="IntegrityError"/>,
/// a decompression bomb as corruption, and a corrupt payload of a referenced-live
/// block as a data-loss error naming the BlockId. The exhaustive per-row
/// fault-injection suite is follow-up US-EMDB-75-7; this proves each handler is wired.
/// </summary>
public class PerFailureHandlerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-perfailure-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private BlockManager CreateManager(
        long maxPayloadLength = Superblock.DefaultMaxPayloadLength,
        IBlockOffsetMap? offsetMap = null) =>
        new(OpenStream(), maxPayloadLength, offsetMap: offsetMap, firstBlockOffset: 0, ownsStream: true);

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    private void CorruptByte(long offset)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        var b = (byte)fs.ReadByte();
        fs.Seek(offset, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    // ---- Section 13: HeaderChecksum mismatch → corruption, offset logged ----

    [Fact]
    public void HeaderChecksumMismatch_SurfacesCorruptionErrorWithOffset()
    {
        using var manager = CreateManager();
        var location = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40)).Value;
        manager.Flush();

        CorruptByte(location.Offset + 32); // a header byte the checksum covers

        var read = manager.Read(location.Offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
        Assert.Equal(CorruptionCause.HeaderChecksum, error.Cause);
        Assert.Equal(location.Offset, error.Offset); // damaged byte range is located
        Assert.False(error.ReferencedLiveData);
    }

    // ---- Section 13: PayloadLength insane (> MaxPayloadLength) → corruption, no alloc ----

    [Fact]
    public void InsaneLength_SurfacesCorruptionErrorInsaneLength()
    {
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(1000)).Value.Offset;
            writer.Flush();
        }

        using var reader = CreateManager(maxPayloadLength: 999);
        var read = reader.Read(offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.InsaneLength, error.Cause);
        Assert.Equal(offset, error.Offset);
        Assert.Contains("MaxPayloadLength", error.Message);
    }

    // ---- Section 13: PayloadChecksum mismatch → corruption ----

    [Fact]
    public void PayloadChecksumMismatch_SurfacesCorruptionErrorPayloadChecksum()
    {
        using var manager = CreateManager();
        var location = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(64)).Value;
        manager.Flush();

        CorruptByte(location.Offset + BlockSerializer.PayloadOffset + 10);

        var read = manager.Read(location.Offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.PayloadChecksum, error.Cause);
        Assert.Equal(location.Offset, error.Offset);
        Assert.Equal(location.BlockId, error.BlockId); // the block names itself
    }

    // ---- Section 13: torn final append (header past EOF) → corruption, torn tail ----

    [Fact]
    public void HeaderPastEof_SurfacesCorruptionErrorTornTail()
    {
        long offset;
        using (var writer = CreateManager())
        {
            offset = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(500)).Value.Offset;
            writer.Flush();
        }

        // Truncate so not even the 64 header bytes remain: a torn tail (spec Section 13).
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(offset + 10);

        using var manager = CreateManager();
        var read = manager.Read(offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.TornTail, error.Cause);
        Assert.Contains("EOF", error.Message);
    }

    // ---- Section 13: decompression bomb → treated as payload corruption ----

    [Fact]
    public void DecompressionBomb_SurfacesCorruptionErrorDecompressionBomb()
    {
        var compressed = BlockCompressor.Compress(new byte[100_000], CompressionAlgorithm.Zstd);
        Assert.True(compressed.IsSuccess, compressed.Error);

        var decompressed = BlockCompressor.Decompress(compressed.Value, CompressionAlgorithm.Zstd, maxPayloadLength: 1024);
        Assert.True(decompressed.IsFailure);

        var error = Assert.IsType<CorruptionError>(decompressed.VerificationError);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
        Assert.Equal(CorruptionCause.DecompressionBomb, error.Cause);
        Assert.Contains("bomb guard", error.Message);
    }

    [Fact]
    public void ReadDecompressed_CorruptFrame_ReStampsErrorWithBlockOffset()
    {
        using var manager = CreateManager();
        // Store bytes that are NOT a valid Zstd frame but flag the block as Zstd:
        // ReadDecompressed must fail the decode and treat the payload as corrupt,
        // re-stamped with this block's offset (spec Section 13).
        var garbage = Enumerable.Repeat((byte)0xAB, 64).ToArray();
        var location = manager.Append(
            BlockType.EmailContent, PayloadEncoding.RawBytes, garbage, CompressionAlgorithm.Zstd).Value;
        manager.Flush();

        var read = manager.ReadDecompressed(location.Offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(location.Offset, error.Offset);
        Assert.Equal(location.BlockId, error.BlockId);
        Assert.Contains($"offset {location.Offset}", error.Message);
    }

    // ---- Section 13: Merkle ChildHash mismatch → integrity error ----

    [Fact]
    public void MerkleMismatch_SurfacesIntegrityErrorAndStaysContracted()
    {
        var offsetMap = new RuntimeBlockOffsetMap();
        using var manager = CreateManager(offsetMap: offsetMap);
        var store = new BTreeNodeStore(manager, offsetMap);

        var payload = SamplePayload(EmailDB.Format.V3.BTreeNodeSerializer.NodeHeaderSize + 16);
        var nodeRef = store.Append(BTreeNodeKind.Leaf, payload, BTreeChildAddressing.Offset).Value;
        manager.Flush();

        // Flip the expected hash so path verification (not the block checksum) fails.
        var tampered = new BTreeNodeRef
        {
            Addressing = nodeRef.Addressing,
            Reference = nodeRef.Reference,
            NodeHash = FlipLast(nodeRef.NodeHash),
        };
        var read = store.ReadVerified(tampered, BTreeNodeKind.Leaf);
        Assert.True(read.IsFailure);

        // Still the contracted failure the CowBTree fallback keys on…
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(read.Error), read.Error);
        // …and now also a typed IntegrityError carrying the hashes.
        var error = Assert.IsType<IntegrityError>(read.VerificationError);
        Assert.Equal(VerificationFailureKind.Integrity, error.Kind);
        Assert.Equal(tampered.NodeHash, error.ExpectedHash);
        Assert.NotNull(error.ActualHash);
    }

    // ---- Section 13: PayloadChecksum on a referenced-live block → data loss naming BlockId ----

    [Fact]
    public void ReferencedLiveCorruptPayload_SurfacesDataLossNamingBlockId()
    {
        var offsetMap = new RuntimeBlockOffsetMap();
        using var manager = CreateManager(offsetMap: offsetMap);
        var store = new BTreeNodeStore(manager, offsetMap);

        // A BlockId-addressed node is one the resolver reports as a live block.
        var payload = SamplePayload(64);
        var nodeRef = store.Append(BTreeNodeKind.Leaf, payload, BTreeChildAddressing.BlockId).Value;
        manager.Flush();

        Assert.True(offsetMap.TryGetLocation(nodeRef.Reference, out var loc) && loc is not null);
        CorruptByte(loc!.Offset + BlockSerializer.PayloadOffset + 4); // damage the live payload

        var read = store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.ReferencedLiveDataLoss, error.Cause);
        Assert.True(error.ReferencedLiveData);
        Assert.Equal(nodeRef.Reference, error.BlockId);
        Assert.Contains(Convert.ToHexStringLower(nodeRef.Reference), error.Message);
    }

    // ---- The three classes surface as distinct, catchable error types ----

    [Fact]
    public void CorruptionAndIntegrity_SurfaceAsDistinctTypesThroughResult()
    {
        var offsetMap = new RuntimeBlockOffsetMap();
        using var manager = CreateManager(offsetMap: offsetMap);
        var store = new BTreeNodeStore(manager, offsetMap);

        var payload = SamplePayload(EmailDB.Format.V3.BTreeNodeSerializer.NodeHeaderSize + 16);
        var nodeRef = store.Append(BTreeNodeKind.Leaf, payload, BTreeChildAddressing.Offset).Value;
        manager.Flush();

        // Integrity: tampered expected hash.
        var tampered = new BTreeNodeRef
        {
            Addressing = nodeRef.Addressing, Reference = nodeRef.Reference, NodeHash = FlipLast(nodeRef.NodeHash),
        };
        var integrity = store.ReadVerified(tampered, BTreeNodeKind.Leaf);

        // Corruption: header checksum broken. Read through a FRESH manager so the
        // on-disk corruption is not masked by the writer stream's read buffer.
        CorruptByte(BlockSerializer.PayloadOffset - 1); // last header-checksum byte of the first block
        using var reader = CreateManager(offsetMap: new RuntimeBlockOffsetMap());
        var corruption = reader.Read(0);

        Assert.True(integrity.IsFailure && corruption.IsFailure);
        Assert.IsType<IntegrityError>(integrity.VerificationError);
        Assert.IsType<CorruptionError>(corruption.VerificationError);
        Assert.NotEqual(integrity.VerificationError!.Kind, corruption.VerificationError!.Kind);
    }

    private static byte[] FlipLast(byte[] hash)
    {
        var copy = (byte[])hash.Clone();
        copy[^1] ^= 0x01;
        return copy;
    }
}
