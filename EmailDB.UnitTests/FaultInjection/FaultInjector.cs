using EmailDB.Format.V3;

namespace EmailDB.UnitTests.FaultInjection;

/// <summary>
/// Reusable on-disk fault-injection primitives over a generated v3 file
/// (EmailDB_FileFormat_Spec.md Section 13). This is the single shared injection
/// toolbox the Section 13 contract-table suite (<see cref="Section13ContractTests"/>)
/// drives; the older piecemeal suites (DisasterOpenTests, DirtyOpenTests,
/// ReferencedDataLossTests, PerFailureHandlerTests, DamagedRangeOffsetTests) each
/// carry their own private <c>CorruptByte</c>/<c>PoisonRange</c> helpers and can
/// later migrate onto this class without behavioural change — every primitive here
/// is byte-for-byte equivalent to what those tests do inline.
///
/// <para>Three families:
/// <list type="bullet">
/// <item><b>Generic:</b> <see cref="ByteFlip(long)"/>, <see cref="ByteFlipRange"/>,
/// <see cref="Overwrite"/>, <see cref="Poison"/>, <see cref="Truncate"/>,
/// <see cref="Transplant"/> — the raw byte-flip / truncate / transplant utilities the
/// task calls for.</item>
/// <item><b>Block-targeted:</b> <see cref="FlipHeaderByte"/>,
/// <see cref="FlipPayloadByte"/>, <see cref="TruncateMidBlock"/> — corrupt a specific
/// block located by its file offset.</item>
/// <item><b>Structure-targeted:</b> <see cref="CorruptSuperblockSlot"/>,
/// <see cref="DestroyBothSuperblocks"/>, <see cref="TearCheckpoint"/> — corrupt a
/// named on-disk structure.</item>
/// </list></para>
///
/// Every mutation is flushed to disk (<c>flushToDisk: true</c>) so a subsequently
/// opened, cold-cache reader observes the damage rather than a warm write buffer.
/// </summary>
internal sealed class FaultInjector
{
    private readonly string _path;

    public FaultInjector(string path) => _path = path;

    // ---------------------------------------------------------- Generic

    private FileStream OpenRW() =>
        new(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Flips every bit (XOR 0xFF) of the single on-disk byte at <paramref name="offset"/>.</summary>
    public void ByteFlip(long offset)
    {
        using var fs = OpenRW();
        fs.Seek(offset, SeekOrigin.Begin);
        int original = fs.ReadByte();
        if (original < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is past EOF ({fs.Length}).");
        fs.Seek(offset, SeekOrigin.Begin);
        fs.WriteByte((byte)(original ^ 0xFF));
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Flips every bit of each of <paramref name="length"/> bytes from <paramref name="start"/>.</summary>
    public void ByteFlipRange(long start, long length)
    {
        using var fs = OpenRW();
        var buffer = new byte[length];
        fs.Seek(start, SeekOrigin.Begin);
        fs.ReadExactly(buffer, 0, buffer.Length);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] ^= 0xFF;
        fs.Seek(start, SeekOrigin.Begin);
        fs.Write(buffer, 0, buffer.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Overwrites the file at <paramref name="offset"/> with <paramref name="bytes"/>.</summary>
    public void Overwrite(long offset, byte[] bytes)
    {
        using var fs = OpenRW();
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Overwrites <c>[start, end)</c> with a fixed poison pattern so any block whose
    /// bytes intersect the range fails CRC validation if it is ever read.
    /// </summary>
    public void Poison(long start, long end, byte pattern = 0xEE)
    {
        var poison = new byte[end - start];
        Array.Fill(poison, pattern);
        Overwrite(start, poison);
    }

    /// <summary>Truncates the file to <paramref name="at"/> bytes (a torn tail).</summary>
    public void Truncate(long at)
    {
        using var fs = OpenRW();
        fs.SetLength(at);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="srcOffset"/> (in
    /// <paramref name="sourcePath"/>, or this file when null) onto this file at
    /// <paramref name="destOffset"/> — a misdirected-write / transplant primitive that
    /// plants otherwise-valid bytes where they do not belong.
    /// </summary>
    public void Transplant(long srcOffset, long destOffset, int length, string? sourcePath = null)
    {
        var buffer = new byte[length];
        using (var src = new FileStream(sourcePath ?? _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            src.Seek(srcOffset, SeekOrigin.Begin);
            src.ReadExactly(buffer, 0, length);
        }
        Overwrite(destOffset, buffer);
    }

    // ---------------------------------------------------------- Block-targeted

    /// <summary>
    /// Flips a byte inside the 64-byte serialized header of the block at
    /// <paramref name="blockOffset"/> (default: the BlockId region) — a HeaderChecksum
    /// mismatch (spec Section 13 "corrupt/torn header").
    /// </summary>
    public void FlipHeaderByte(long blockOffset, int headerByteIndex = 32)
    {
        if (headerByteIndex < 0 || headerByteIndex >= BlockSerializer.SerializedHeaderSize)
            throw new ArgumentOutOfRangeException(nameof(headerByteIndex));
        ByteFlip(blockOffset + headerByteIndex);
    }

    /// <summary>
    /// Flips a byte inside the payload of the block at <paramref name="blockOffset"/> —
    /// a PayloadChecksum mismatch (spec Section 13 "corrupt payload").
    /// </summary>
    public void FlipPayloadByte(long blockOffset, int payloadByteIndex = 4)
        => ByteFlip(blockOffset + BlockSerializer.PayloadOffset + payloadByteIndex);

    /// <summary>
    /// Truncates the file so the block at <paramref name="blockOffset"/> is left with
    /// only <paramref name="keepBytes"/> bytes (default 10, fewer than the 64-byte
    /// header) — a torn tail past which not even a header can be read.
    /// </summary>
    public void TruncateMidBlock(long blockOffset, int keepBytes = 10)
        => Truncate(blockOffset + keepBytes);

    // ---------------------------------------------------------- Structure-targeted

    /// <summary>
    /// Corrupts superblock slot <paramref name="slot"/> (0 = A, 1 = B) by flipping a
    /// byte inside it — a torn superblock write (spec Section 13). The other slot is
    /// left intact so a reader can use it.
    /// </summary>
    public void CorruptSuperblockSlot(int slot)
    {
        long slotOffset = slot switch
        {
            0 => SuperblockManager.SlotAOffset,
            1 => SuperblockManager.SlotBOffset,
            _ => throw new ArgumentOutOfRangeException(nameof(slot), "Superblock slot must be 0 (A) or 1 (B)."),
        };
        ByteFlip(slotOffset + 8);
    }

    /// <summary>Zeroes the whole [0, 8192) dual-slot superblock region — both slots destroyed (spec Section 13 "severe damage").</summary>
    public void DestroyBothSuperblocks()
        => Overwrite(0, new byte[SuperblockManager.SuperblockRegionSize]);

    /// <summary>
    /// Tears the Checkpoint block at <paramref name="checkpointOffset"/> by flipping a
    /// header byte, so the block no longer reads back (its HeaderChecksum fails) — a
    /// torn commit (spec Section 13). Matches the inline corruption the DisasterOpen
    /// suite uses (<c>checkpoint offset + 20</c>).
    /// </summary>
    public void TearCheckpoint(long checkpointOffset)
        => FlipHeaderByte(checkpointOffset, headerByteIndex: 20);
}
