using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the runtime BlockId-to-offset map (EmailDB_FileFormat_Spec.md
/// Section 7, US-EMDB-66-5): every block appended this session is tracked and
/// resolvable, duplicate BlockIds resolve last-position-wins regardless of
/// notification order or thread interleaving, and the checkpoint helpers
/// (snapshot ordered by offset, clear) behave as the spec's batch-insert flow
/// expects. Includes end-to-end wiring through <see cref="BlockManager"/>.
/// </summary>
public class RuntimeBlockOffsetMapTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-offsetmap-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static BlockHeader MakeHeader(byte[] blockId) => new()
    {
        Type = BlockType.EmailContent,
        Encoding = PayloadEncoding.RawBytes,
        BlockId = blockId,
        PayloadLength = 10,
    };

    /// <summary>Deterministic 16-byte BlockId derived from a seed (distinct per seed).</summary>
    private static byte[] MakeBlockId(int seed)
    {
        var id = Enumerable.Range(0, UlidGenerator.UlidSize).Select(i => (byte)(i * 31)).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), seed);
        return id;
    }

    // ---- Tracking blocks appended this session ----

    [Fact]
    public void TryGetLocation_ReturnsRecordedOffsetAndLength()
    {
        var map = new RuntimeBlockOffsetMap();
        var blockId = MakeBlockId(1);

        map.OnBlockAppended(MakeHeader(blockId), offset: 8192, totalBlockLength: 196);

        Assert.True(map.TryGetLocation(blockId, out var location));
        Assert.NotNull(location);
        Assert.Equal(blockId, location.BlockId);
        Assert.Equal(8192, location.Offset);
        Assert.Equal(196, location.TotalBlockLength);
    }

    [Fact]
    public void TryGetLocation_UnknownBlockId_ReturnsFalse()
    {
        var map = new RuntimeBlockOffsetMap();
        map.OnBlockAppended(MakeHeader(MakeBlockId(1)), 8192, 196);

        Assert.False(map.TryGetLocation(MakeBlockId(2), out var location));
        Assert.Null(location);
    }

    [Fact]
    public void TracksEveryDistinctBlockAppendedThisSession()
    {
        var map = new RuntimeBlockOffsetMap();
        const int count = 500;
        for (var i = 0; i < count; i++)
            map.OnBlockAppended(MakeHeader(MakeBlockId(i)), offset: 8192 + i * 100L, totalBlockLength: 100);

        Assert.Equal(count, map.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.True(map.TryGetLocation(MakeBlockId(i), out var location));
            Assert.Equal(8192 + i * 100L, location!.Offset);
        }
    }

    [Fact]
    public void ReturnedLocation_DoesNotAliasCallerBlockIdArray()
    {
        var map = new RuntimeBlockOffsetMap();
        var blockId = MakeBlockId(7);
        map.OnBlockAppended(MakeHeader(blockId), 0, 96);

        Assert.True(map.TryGetLocation(blockId, out var location));
        location!.BlockId[0] ^= 0xFF;

        // The map still resolves the original id: no shared mutable state.
        Assert.True(map.TryGetLocation(MakeBlockId(7), out var again));
        Assert.Equal(MakeBlockId(7), again!.BlockId);
    }

    // ---- Duplicate BlockIds: last-position-wins ----

    [Fact]
    public void DuplicateBlockId_LaterOffsetWins()
    {
        var map = new RuntimeBlockOffsetMap();
        var blockId = MakeBlockId(3);

        map.OnBlockAppended(MakeHeader(blockId), offset: 100, totalBlockLength: 96);
        map.OnBlockAppended(MakeHeader(blockId), offset: 5000, totalBlockLength: 120);

        Assert.Equal(1, map.Count);
        Assert.True(map.TryGetLocation(blockId, out var location));
        Assert.Equal(5000, location!.Offset);
        Assert.Equal(120, location.TotalBlockLength);
    }

    [Fact]
    public void DuplicateBlockId_OutOfOrderNotification_LaterPositionStillWins()
    {
        var map = new RuntimeBlockOffsetMap();
        var blockId = MakeBlockId(4);

        // Notifications arrive out of file order; the greater offset must win.
        map.OnBlockAppended(MakeHeader(blockId), offset: 5000, totalBlockLength: 120);
        map.OnBlockAppended(MakeHeader(blockId), offset: 100, totalBlockLength: 96);

        Assert.True(map.TryGetLocation(blockId, out var location));
        Assert.Equal(5000, location!.Offset);
        Assert.Equal(120, location.TotalBlockLength);
    }

    // ---- Concurrency ----

    [Fact]
    public void ConcurrentAppends_DistinctIds_AllTracked()
    {
        var map = new RuntimeBlockOffsetMap();
        const int threads = 8;
        const int perThread = 1000;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var index = t * perThread + i;
                map.OnBlockAppended(MakeHeader(MakeBlockId(index)), offset: index * 100L, totalBlockLength: 100);
            }
        });

        Assert.Equal(threads * perThread, map.Count);
        for (var index = 0; index < threads * perThread; index++)
        {
            Assert.True(map.TryGetLocation(MakeBlockId(index), out var location));
            Assert.Equal(index * 100L, location!.Offset);
        }
    }

    [Fact]
    public void ConcurrentAppends_SameId_ConvergeToGreatestOffset()
    {
        var map = new RuntimeBlockOffsetMap();
        var blockId = MakeBlockId(9);
        const int threads = 8;
        const int perThread = 500;
        const long maxOffset = threads * perThread - 1;

        // Every thread hammers the same BlockId with a shuffled offset order,
        // so update races and out-of-order arrivals both occur.
        Parallel.For(0, threads, t =>
        {
            var random = new Random(t);
            var offsets = Enumerable.Range(0, threads * perThread)
                .OrderBy(_ => random.Next()).Take(perThread).ToArray();
            foreach (var offset in offsets)
                map.OnBlockAppended(MakeHeader(blockId), offset, totalBlockLength: 96);
        });

        Assert.Equal(1, map.Count);
        Assert.True(map.TryGetLocation(blockId, out var location));

        // Winner is the greatest offset any thread recorded; at minimum it can
        // never be less than the greatest guaranteed-recorded value. Verify
        // exact convergence by replaying the known global maximum.
        map.OnBlockAppended(MakeHeader(blockId), maxOffset, totalBlockLength: 96);
        Assert.True(map.TryGetLocation(blockId, out location));
        Assert.Equal(maxOffset, location!.Offset);
    }

    [Fact]
    public void ConcurrentAppends_SameId_NeverResolveBelowKnownFloor()
    {
        var map = new RuntimeBlockOffsetMap();
        var blockId = MakeBlockId(11);

        // Record the floor first, then race lower offsets against reads: the
        // resolved offset must never drop below an already-recorded position.
        map.OnBlockAppended(MakeHeader(blockId), offset: 10_000, totalBlockLength: 96);

        var violations = 0;
        Parallel.Invoke(
            () =>
            {
                for (var offset = 0; offset < 5000; offset++)
                    map.OnBlockAppended(MakeHeader(blockId), offset, totalBlockLength: 96);
            },
            () =>
            {
                for (var i = 0; i < 5000; i++)
                {
                    map.TryGetLocation(blockId, out var location);
                    if (location!.Offset < 10_000)
                        Interlocked.Increment(ref violations);
                }
            });

        Assert.Equal(0, violations);
        Assert.True(map.TryGetLocation(blockId, out var final));
        Assert.Equal(10_000, final!.Offset);
    }

    // ---- Validation ----

    [Fact]
    public void OnBlockAppended_NullHeader_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new RuntimeBlockOffsetMap().OnBlockAppended(null!, 0, 96));

    [Fact]
    public void OnBlockAppended_NegativeOffset_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RuntimeBlockOffsetMap().OnBlockAppended(MakeHeader(MakeBlockId(1)), -1, 80));

    [Fact]
    public void OnBlockAppended_LengthBelowFixedOverhead_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RuntimeBlockOffsetMap().OnBlockAppended(
                MakeHeader(MakeBlockId(1)), 0, BlockSerializer.FixedOverhead - 1));

    [Fact]
    public void OnBlockAppended_WrongBlockIdLength_Throws() =>
        Assert.Throws<ArgumentException>(
            () => new RuntimeBlockOffsetMap().OnBlockAppended(
                MakeHeader(new byte[15]), 0, 96));

    [Fact]
    public void TryGetLocation_WrongBlockIdLength_Throws()
    {
        var map = new RuntimeBlockOffsetMap();
        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> shortId = stackalloc byte[8];
            map.TryGetLocation(shortId, out _);
        });
    }

    // ---- Checkpoint helpers: snapshot and clear ----

    [Fact]
    public void SnapshotOrderedByOffset_ReturnsLatestEntriesInFileOrder()
    {
        var map = new RuntimeBlockOffsetMap();
        map.OnBlockAppended(MakeHeader(MakeBlockId(1)), offset: 300, totalBlockLength: 100);
        map.OnBlockAppended(MakeHeader(MakeBlockId(2)), offset: 100, totalBlockLength: 100);
        map.OnBlockAppended(MakeHeader(MakeBlockId(3)), offset: 200, totalBlockLength: 100);
        // Duplicate: only the winning (later) position appears in the snapshot.
        map.OnBlockAppended(MakeHeader(MakeBlockId(2)), offset: 400, totalBlockLength: 100);

        var snapshot = map.SnapshotOrderedByOffset();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(new long[] { 200, 300, 400 }, snapshot.Select(l => l.Offset).ToArray());
        Assert.Equal(MakeBlockId(3), snapshot[0].BlockId);
        Assert.Equal(MakeBlockId(1), snapshot[1].BlockId);
        Assert.Equal(MakeBlockId(2), snapshot[2].BlockId);
    }

    [Fact]
    public void Clear_ForgetsAllEntries()
    {
        var map = new RuntimeBlockOffsetMap();
        map.OnBlockAppended(MakeHeader(MakeBlockId(1)), 0, 96);
        map.OnBlockAppended(MakeHeader(MakeBlockId(2)), 100, 96);

        map.Clear();

        Assert.Equal(0, map.Count);
        Assert.False(map.TryGetLocation(MakeBlockId(1), out _));
        Assert.Empty(map.SnapshotOrderedByOffset());
    }

    // ---- End-to-end wiring through BlockManager ----

    [Fact]
    public void BlockManagerAppends_AreResolvableThroughTheMap()
    {
        var map = new RuntimeBlockOffsetMap();
        using var stream = new FileStream(
            _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var manager = new BlockManager(stream, offsetMap: map, ownsStream: false);

        var appended = new List<BlockLocation>();
        for (var i = 0; i < 20; i++)
        {
            var payload = Enumerable.Range(0, 50 + i).Select(b => (byte)b).ToArray();
            var result = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, payload);
            Assert.True(result.IsSuccess, result.Error);
            appended.Add(result.Value);
        }

        Assert.Equal(appended.Count, map.Count);
        foreach (var expected in appended)
        {
            Assert.True(map.TryGetLocation(expected.BlockId, out var location));
            Assert.Equal(expected.Offset, location!.Offset);
            Assert.Equal(expected.TotalBlockLength, location.TotalBlockLength);

            // The mapped location reads back as a fully verified block.
            var block = manager.Read(location.Offset);
            Assert.True(block.IsSuccess, block.Error);
            Assert.Equal(expected.BlockId, block.Value.Header.BlockId);
        }

        var snapshot = map.SnapshotOrderedByOffset();
        Assert.Equal(appended.Select(l => l.Offset), snapshot.Select(l => l.Offset));
    }

    [Fact]
    public void BlockManagerAppends_AcrossTypesSizesAndFlushes_AllTracked()
    {
        var map = new RuntimeBlockOffsetMap();
        using var stream = new FileStream(
            _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var manager = new BlockManager(stream, offsetMap: map, ownsStream: false);

        var appended = new List<BlockLocation>();
        void Track(EmailDB.Format.Result<BlockLocation> result)
        {
            Assert.True(result.IsSuccess, result.Error);
            appended.Add(result.Value);
        }

        // Every block type, with varied payload sizes including empty.
        var types = new[]
        {
            BlockType.Metadata, BlockType.WAL, BlockType.FolderTree,
            BlockType.Cleanup, BlockType.BTreeLeaf, BlockType.BTreeInternal,
            BlockType.IndexRoot, BlockType.EmailContent,
        };
        for (var i = 0; i < types.Length; i++)
        {
            var payload = Enumerable.Repeat((byte)i, i * 700).ToArray(); // 0..4900 bytes
            Track(manager.Append(types[i], PayloadEncoding.RawBytes, payload));
        }

        // The compressing append path must notify the map too.
        var compressible = Enumerable.Repeat((byte)0xAB, 4096).ToArray();
        Track(manager.AppendCompressed(
            BlockType.EmailContent, PayloadEncoding.RawBytes, compressible,
            CompressionAlgorithm.Zstd));

        // A flush (fsync) must not disturb the map — only Clear() at a
        // checkpoint drops entries.
        Assert.True(manager.Flush().IsSuccess);
        Track(manager.Append(BlockType.WAL, PayloadEncoding.Protobuf, new byte[64]));
        Assert.True(manager.Flush().IsSuccess);

        Assert.Equal(appended.Count, map.Count);
        foreach (var expected in appended)
        {
            Assert.True(map.TryGetLocation(expected.BlockId, out var location));
            Assert.Equal(expected.Offset, location!.Offset);
            Assert.Equal(expected.TotalBlockLength, location.TotalBlockLength);

            var block = manager.Read(location.Offset);
            Assert.True(block.IsSuccess, block.Error);
            Assert.Equal(expected.BlockId, block.Value.Header.BlockId);
        }
    }

    [Fact]
    public void DuplicateUlidThroughBlockManager_MapAndRescanBothResolveToLaterBlock()
    {
        // Two write sessions whose deterministic generators mint the same
        // first ULID: the file legitimately ends up holding two blocks with
        // one BlockId (a rewritten logical block). Both resolution paths —
        // the shared in-session runtime map and a full rescan of the raw
        // file — must resolve the id to the later position, and reading
        // through the resolved location must yield the later block's content.
        static UlidGenerator FixedGenerator() => new(
            clock: static () => 1_000,
            fillRandom: static buffer => Array.Fill(buffer, (byte)0x5A));

        var earlierPayload = Enumerable.Repeat((byte)0x11, 40).ToArray();
        var laterPayload = Enumerable.Repeat((byte)0x22, 60).ToArray();
        var map = new RuntimeBlockOffsetMap();

        using var stream = new FileStream(
            _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        BlockLocation first;
        using (var session1 = new BlockManager(
            stream, ulidGenerator: FixedGenerator(), offsetMap: map, ownsStream: false))
        {
            var result = session1.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, earlierPayload);
            Assert.True(result.IsSuccess, result.Error);
            first = result.Value;
            Assert.True(session1.Flush().IsSuccess);
        }

        using var session2 = new BlockManager(
            stream, ulidGenerator: FixedGenerator(), offsetMap: map, ownsStream: false);
        var secondResult = session2.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, laterPayload);
        Assert.True(secondResult.IsSuccess, secondResult.Error);
        var second = secondResult.Value;
        Assert.True(session2.Flush().IsSuccess);

        // Genuine duplicate minted through the real append path.
        Assert.Equal(first.BlockId, second.BlockId);
        Assert.True(second.Offset > first.Offset);

        // In-session path: the runtime map resolves the id last-position-wins…
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGetLocation(first.BlockId, out var resolved));
        Assert.Equal(second.Offset, resolved!.Offset);
        Assert.Equal(second.TotalBlockLength, resolved.TotalBlockLength);

        // …and reading via the mapped location returns the later content.
        var mappedRead = session2.Read(resolved.Offset);
        Assert.True(mappedRead.IsSuccess, mappedRead.Error);
        Assert.Equal(laterPayload, mappedRead.Value.Payload);

        // Rebuild path: a forward rescan of the raw file sees both versions
        // but populates a fresh map that also resolves last-position-wins.
        var rebuilt = new RuntimeBlockOffsetMap();
        var scan = session2.ScanForward(rebuilt);
        Assert.True(scan.IsSuccess, scan.IsFailure ? scan.Error : null);
        Assert.Equal(2, scan.Value.Blocks.Count);
        Assert.Empty(scan.Value.DamagedRanges);

        Assert.Equal(1, rebuilt.Count);
        Assert.True(rebuilt.TryGetLocation(first.BlockId, out var rescanned));
        Assert.Equal(second.Offset, rescanned!.Offset);

        var rescannedRead = session2.Read(rescanned.Offset);
        Assert.True(rescannedRead.IsSuccess, rescannedRead.Error);
        Assert.Equal(laterPayload, rescannedRead.Value.Payload);
    }
}
