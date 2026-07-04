using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 forward scan with resynchronization (US-EMDB-66-6,
/// EmailDB_FileFormat_Spec.md Sections 11, 13): sequential walk via header
/// magic + TotalBlockLength; on corruption, hunt forward for the next header
/// magic and fully verify the candidate (header checksum before trusting any
/// field, then payload/footer) before resynchronizing; damaged offset ranges
/// (start/end) recorded in the scan result and logged; duplicate BlockIds
/// resolve last-position-wins.
/// </summary>
public class ForwardScanTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-forwardscan-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Creates a manager over a fresh stream; firstBlockOffset 0 keeps test offsets simple.</summary>
    private BlockManager CreateManager(long firstBlockOffset = 0) =>
        new(OpenStream(), firstBlockOffset: firstBlockOffset, ownsStream: true);

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

    /// <summary>Appends raw bytes at EOF via a separate handle (simulates garbage / crafted blocks).</summary>
    private void AppendRawToFile(byte[] bytes)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(0, SeekOrigin.End);
        fs.Write(bytes);
    }

    /// <summary>Serializes one complete, valid block image with a chosen BlockId.</summary>
    private static byte[] SerializeWholeBlock(byte[] payload, int idSeed = 99) =>
        BlockSerializer.Serialize(new BlockHeader
        {
            Type = BlockType.EmailContent,
            Encoding = PayloadEncoding.RawBytes,
            BlockId = SamplePayload(16, seed: idSeed),
            PayloadLength = payload.Length,
        }, payload);

    private static void AssertLocation(BlockLocation expected, BlockLocation actual)
    {
        Assert.Equal(expected.Offset, actual.Offset);
        Assert.Equal(expected.TotalBlockLength, actual.TotalBlockLength);
        Assert.Equal(expected.BlockId, actual.BlockId);
    }

    private static ForwardScanResult Scan(
        BlockManager manager, IBlockOffsetMap? offsetMap = null, Action<string>? log = null)
    {
        var result = manager.ScanForward(offsetMap, log);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        return result.Value;
    }

    // ---- Clean files ----

    [Fact]
    public void ScanForward_CleanFile_ReturnsAllBlocksInOrder_NoDamage()
    {
        using var manager = CreateManager();
        var appended = new[]
        {
            manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40)).Value,
            manager.Append(BlockType.EmailMetadata, PayloadEncoding.Protobuf, SamplePayload(0)).Value,
            manager.Append(BlockType.WAL, PayloadEncoding.Custom, SamplePayload(200, seed: 2)).Value,
        };
        manager.Flush();

        var scan = Scan(manager);

        Assert.Equal(appended.Length, scan.Blocks.Count);
        for (int i = 0; i < appended.Length; i++)
            AssertLocation(appended[i], scan.Blocks[i]);
        Assert.Empty(scan.DamagedRanges);
        Assert.Equal(new FileInfo(_path).Length, scan.FileLength);
    }

    [Fact]
    public void ScanForward_EmptyFile_NoBlocksNoDamage()
    {
        using var manager = CreateManager();

        var scan = Scan(manager);

        Assert.Empty(scan.Blocks);
        Assert.Empty(scan.DamagedRanges);
    }

    [Fact]
    public void ScanForward_WithSuperblockRegion_StartsAtFirstBlockOffset()
    {
        // Default firstBlockOffset (8192): the superblock region must never be
        // interpreted as blocks or reported as damage.
        using var manager = new BlockManager(OpenStream(), ownsStream: true);
        var first = manager.Append(BlockType.Metadata, PayloadEncoding.Protobuf, SamplePayload(30)).Value;
        var second = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(70)).Value;
        manager.Flush();

        var scan = Scan(manager);

        Assert.Equal(2, scan.Blocks.Count);
        AssertLocation(first, scan.Blocks[0]);
        AssertLocation(second, scan.Blocks[1]);
        Assert.Equal(8192, scan.Blocks[0].Offset);
        Assert.Empty(scan.DamagedRanges);
    }

    // ---- Corruption mid-file: resynchronization and damaged-range accuracy ----

    [Fact]
    public void ScanForward_CorruptHeaderMidFile_ResyncsAtNextBlock_ReportsExactRange()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(50)).Value;
        var second = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(120, seed: 2)).Value;
        var third = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(80, seed: 3)).Value;
        manager.Flush();

        CorruptByte(second.Offset + 20); // inside the second block's 48-byte header

        var scan = Scan(manager);

        Assert.Equal(2, scan.Blocks.Count);
        AssertLocation(first, scan.Blocks[0]);
        AssertLocation(third, scan.Blocks[1]);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(second.Offset, range.Start);
        Assert.Equal(third.Offset, range.End);
        Assert.Equal(second.TotalBlockLength, range.Length);
        Assert.Contains("checksum", range.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanForward_CorruptPayloadMidFile_OwnValidHeaderMagicNotReMatched()
    {
        // Payload corruption leaves the block's own header (and its magic)
        // fully valid — the hunt must not resynchronize onto the same dead
        // block, but skip to the next one.
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(60)).Value;
        var second = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(90, seed: 2)).Value;
        var third = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40, seed: 3)).Value;
        manager.Flush();

        CorruptByte(second.Offset + BlockSerializer.PayloadOffset + 7);

        var scan = Scan(manager);

        Assert.Equal(2, scan.Blocks.Count);
        AssertLocation(first, scan.Blocks[0]);
        AssertLocation(third, scan.Blocks[1]);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(second.Offset, range.Start);
        Assert.Equal(third.Offset, range.End);
        Assert.Contains("payload checksum", range.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanForward_CorruptionAtFirstBlock_ResyncsAtSecond()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(100)).Value;
        var second = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(55, seed: 2)).Value;
        manager.Flush();

        CorruptByte(first.Offset + 3); // first block's header magic bytes

        var scan = Scan(manager);

        var found = Assert.Single(scan.Blocks);
        AssertLocation(second, found);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(first.Offset, range.Start);
        Assert.Equal(second.Offset, range.End);
    }

    [Fact]
    public void ScanForward_MultipleDamagedRanges_AllReportedInOrder()
    {
        using var manager = CreateManager();
        var b1 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(45)).Value;
        var b2 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(75, seed: 2)).Value;
        var b3 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(65, seed: 3)).Value;
        var b4 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(85, seed: 4)).Value;
        manager.Flush();

        CorruptByte(b1.Offset + 10);                                  // header of block 1
        CorruptByte(b3.Offset + BlockSerializer.PayloadOffset + 30);  // payload of block 3

        var scan = Scan(manager);

        Assert.Equal(2, scan.Blocks.Count);
        AssertLocation(b2, scan.Blocks[0]);
        AssertLocation(b4, scan.Blocks[1]);

        Assert.Equal(2, scan.DamagedRanges.Count);
        Assert.Equal(b1.Offset, scan.DamagedRanges[0].Start);
        Assert.Equal(b2.Offset, scan.DamagedRanges[0].End);
        Assert.Equal(b3.Offset, scan.DamagedRanges[1].Start);
        Assert.Equal(b4.Offset, scan.DamagedRanges[1].End);
    }

    // ---- False positives: header magic must be fully verified ----

    [Fact]
    public void ScanForward_FakeHeaderMagicInPayload_NotTrusted_ResyncsAtRealBlock()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(40)).Value;

        // Second block's payload embeds the header magic; the hunt will meet
        // it before the third block's real header and must reject it (the
        // surrounding payload bytes fail the header checksum).
        var payload = SamplePayload(150, seed: 2);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(50, 8), BlockSerializer.HeaderMagic);
        var second = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, payload).Value;
        var third = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(35, seed: 3)).Value;
        manager.Flush();

        CorruptByte(second.Offset + 12); // kill the second block's header

        var scan = Scan(manager);

        Assert.Equal(2, scan.Blocks.Count);
        AssertLocation(first, scan.Blocks[0]);
        AssertLocation(third, scan.Blocks[1]);
        Assert.DoesNotContain(scan.Blocks,
            b => b.Offset > second.Offset && b.Offset < third.Offset);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(second.Offset, range.Start);
        Assert.Equal(third.Offset, range.End);
    }

    [Fact]
    public void ScanForward_HeaderMagicSpansHuntChunkBoundary_StillResyncs()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(25)).Value;
        manager.Flush();
        long damageStart = first.Offset + first.TotalBlockLength;

        // 65533 garbage bytes put the resync block's header magic astride the
        // hunt's first 64 KiB chunk boundary (hunt starts at damageStart + 1,
        // chunk covers 65536 bytes, magic begins 4 bytes before its end), so
        // only the chunk overlap makes it visible whole.
        AppendRawToFile(SamplePayload(65533, seed: 5));
        var rescue = SerializeWholeBlock(SamplePayload(140, seed: 6));
        long rescueOffset = damageStart + 65533;
        AppendRawToFile(rescue);

        var scan = Scan(manager);

        Assert.Equal(2, scan.Blocks.Count);
        AssertLocation(first, scan.Blocks[0]);
        Assert.Equal(rescueOffset, scan.Blocks[1].Offset);
        Assert.Equal(rescue.Length, scan.Blocks[1].TotalBlockLength);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(damageStart, range.Start);
        Assert.Equal(rescueOffset, range.End);
    }

    // ---- Damage extending to EOF ----

    [Fact]
    public void ScanForward_GarbageToEof_NoResync_RangeExtendsToFileLength()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(70)).Value;
        manager.Flush();
        AppendRawToFile(SamplePayload(4000, seed: 3));

        var scan = Scan(manager);

        var found = Assert.Single(scan.Blocks);
        AssertLocation(first, found);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(first.Offset + first.TotalBlockLength, range.Start);
        Assert.Equal(new FileInfo(_path).Length, range.End);
    }

    [Fact]
    public void ScanForward_TornTailShorterThanABlock_ReportedAsDamage()
    {
        using var manager = CreateManager();
        var first = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(20)).Value;
        manager.Flush();
        AppendRawToFile(SamplePayload(BlockSerializer.FixedOverhead - 1, seed: 4)); // can't hold a block

        var scan = Scan(manager);

        var found = Assert.Single(scan.Blocks);
        AssertLocation(first, found);

        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(first.Offset + first.TotalBlockLength, range.Start);
        Assert.Equal(new FileInfo(_path).Length, range.End);
        Assert.Contains("torn tail", range.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanForward_OnlyGarbage_NoBlocks_SingleRangeCoversWholeStream()
    {
        using var manager = CreateManager();
        AppendRawToFile(SamplePayload(700, seed: 11));

        var scan = Scan(manager);

        Assert.Empty(scan.Blocks);
        var range = Assert.Single(scan.DamagedRanges);
        Assert.Equal(0, range.Start);
        Assert.Equal(700, range.End);
    }

    // ---- Damaged-range logging ----

    [Fact]
    public void ScanForward_LogsEachDamagedRangeWithOffsets()
    {
        using var manager = CreateManager();
        var b1 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(30)).Value;
        var b2 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(50, seed: 2)).Value;
        var b3 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(60, seed: 3)).Value;
        var b4 = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(70, seed: 4)).Value;
        manager.Flush();

        CorruptByte(b1.Offset + 5);
        CorruptByte(b3.Offset + 5);

        var messages = new List<string>();
        var scan = Scan(manager, log: messages.Add);

        Assert.Equal(2, messages.Count);
        Assert.Contains($"[{b1.Offset}, {b2.Offset})", messages[0]);
        Assert.Contains($"[{b3.Offset}, {b4.Offset})", messages[1]);
        Assert.All(messages, m => Assert.Contains("damaged range", m));
        Assert.Equal(2, scan.DamagedRanges.Count);
    }

    [Fact]
    public void ScanForward_CleanFile_LogsNothing()
    {
        using var manager = CreateManager();
        manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(30));
        manager.Flush();

        var messages = new List<string>();
        Scan(manager, log: messages.Add);

        Assert.Empty(messages);
    }

    // ---- Duplicate BlockIds: last-position-wins; runtime map population ----

    [Fact]
    public void ScanForward_DuplicateBlockIds_RuntimeMapResolvesLastPositionWins()
    {
        // Craft the file raw so two blocks share a BlockId (legitimate: a new
        // version of a logical block reuses its identity).
        using var manager = CreateManager();
        var v1 = SerializeWholeBlock(SamplePayload(40, seed: 1), idSeed: 42);
        var other = SerializeWholeBlock(SamplePayload(90, seed: 2), idSeed: 43);
        var v2 = SerializeWholeBlock(SamplePayload(70, seed: 3), idSeed: 42); // same BlockId as v1
        AppendRawToFile(v1);
        AppendRawToFile(other);
        AppendRawToFile(v2);

        var map = new RuntimeBlockOffsetMap();
        var scan = Scan(manager, map);

        // The scan result lists every on-disk version in file order…
        Assert.Equal(3, scan.Blocks.Count);
        Assert.Equal(SamplePayload(16, seed: 42), scan.Blocks[0].BlockId);
        Assert.Equal(SamplePayload(16, seed: 42), scan.Blocks[2].BlockId);
        Assert.Empty(scan.DamagedRanges);

        // …while the populated map resolves the duplicate last-position-wins.
        Assert.Equal(2, map.Count);
        Assert.True(map.TryGetLocation(SamplePayload(16, seed: 42), out var resolved));
        Assert.Equal(v1.Length + other.Length, resolved!.Offset); // v2's offset, not v1's
        Assert.Equal(v2.Length, resolved.TotalBlockLength);
    }

    [Fact]
    public void ScanForward_PopulatesRuntimeMap_EveryDiscoveredBlockResolvable()
    {
        using var manager = CreateManager();
        var appended = Enumerable.Range(0, 5)
            .Select(i => manager.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(20 + i * 13, seed: i)).Value)
            .ToArray();
        manager.Flush();

        var map = new RuntimeBlockOffsetMap();
        Scan(manager, map);

        Assert.Equal(appended.Length, map.Count);
        foreach (var location in appended)
        {
            Assert.True(map.TryGetLocation(location.BlockId, out var resolved));
            Assert.Equal(location.Offset, resolved!.Offset);
            Assert.Equal(location.TotalBlockLength, resolved.TotalBlockLength);
        }
    }
}
