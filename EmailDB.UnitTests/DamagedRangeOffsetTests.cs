using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Dedicated acceptance test for the US-EMDB-75 criterion
/// "Damaged byte ranges are logged with offsets" (EmailDB_FileFormat_Spec.md
/// Section 13). Where <see cref="PerFailureHandlerTests"/> proves each handler is
/// wired and merely checks <see cref="VerificationError.Offset"/>, this suite
/// inflicts real on-disk damage at a KNOWN file offset and asserts the surfaced
/// <see cref="VerificationError.DamagedRange"/> reports exactly WHERE the damage
/// is — exact start/end offsets, not just non-null. It covers the three shapes the
/// criterion calls out: payload damage (range = the block's payload region), header
/// damage (range = the block's 64 header bytes), and a torn tail (range = the torn
/// block's start to EOF).
/// </summary>
public class DamagedRangeOffsetTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-damagedrange-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private BlockManager CreateManager() =>
        new(OpenStream(), Superblock.DefaultMaxPayloadLength, firstBlockOffset: 0, ownsStream: true);

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

    // ---- Payload damage: DamagedRange covers exactly the block's payload region ----

    [Fact]
    public void PayloadDamage_DamagedRangeCoversPayloadRegionAtExactOffsets()
    {
        const int payloadLength = 200;
        long offset;
        // Write a leading block so the damaged block sits at a non-zero offset, then
        // the target block whose payload we will corrupt.
        using (var writer = CreateManager())
        {
            writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40, seed: 9));
            offset = writer.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(payloadLength)).Value.Offset;
            writer.Flush();
        }
        Assert.True(offset > 0);

        // Damage a byte in the middle of the payload region.
        long payloadStart = offset + BlockSerializer.PayloadOffset;
        CorruptByte(payloadStart + payloadLength / 2);

        using var reader = CreateManager();
        var read = reader.Read(offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.PayloadChecksum, error.Cause);
        Assert.Equal(offset, error.Offset);

        var range = error.DamagedRange;
        Assert.NotNull(range);
        // Range must be exactly the payload region [payloadStart, payloadStart+len).
        Assert.Equal(payloadStart, range!.Value.Start);
        Assert.Equal(payloadStart + payloadLength, range.Value.End);
        Assert.Equal(payloadLength, range.Value.Length);
        // The actually-damaged byte lies inside the reported range.
        Assert.InRange(payloadStart + payloadLength / 2, range.Value.Start, range.Value.End - 1);
    }

    // ---- Header damage: DamagedRange covers exactly the 64 header bytes ----

    [Fact]
    public void HeaderDamage_DamagedRangeCoversHeaderBytesAtExactOffsets()
    {
        long offset;
        using (var writer = CreateManager())
        {
            writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40, seed: 9));
            offset = writer.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(80)).Value.Offset;
            writer.Flush();
        }
        Assert.True(offset > 0);

        // Damage a header byte (the BlockId region, covered by the header checksum).
        long damagedByte = offset + 32;
        CorruptByte(damagedByte);

        using var reader = CreateManager();
        var read = reader.Read(offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.HeaderChecksum, error.Cause);
        Assert.Equal(offset, error.Offset);

        var range = error.DamagedRange;
        Assert.NotNull(range);
        // Range must be exactly the serialized header span [offset, offset+64).
        Assert.Equal(offset, range!.Value.Start);
        Assert.Equal(offset + BlockSerializer.SerializedHeaderSize, range.Value.End);
        Assert.Equal(BlockSerializer.SerializedHeaderSize, range.Value.Length);
        Assert.InRange(damagedByte, range.Value.Start, range.Value.End - 1);
    }

    // ---- Torn tail: DamagedRange runs from the torn block's start to EOF ----

    [Fact]
    public void TornTail_DamagedRangeRunsFromBlockStartToEof()
    {
        long offset;
        using (var writer = CreateManager())
        {
            writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40, seed: 9));
            offset = writer.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(500)).Value.Offset;
            writer.Flush();
        }
        Assert.True(offset > 0);

        // Truncate mid-header so not even the 64 header bytes remain: a torn tail.
        long tornEof = offset + 10;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(tornEof);

        using var reader = CreateManager();
        var read = reader.Read(offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.TornTail, error.Cause);
        Assert.Equal(offset, error.Offset);

        var range = error.DamagedRange;
        Assert.NotNull(range);
        // Range must run from the torn block's start to the (truncated) EOF.
        Assert.Equal(offset, range!.Value.Start);
        Assert.Equal(tornEof, range.Value.End);
        Assert.Equal(tornEof - offset, range.Value.Length);
    }
}
