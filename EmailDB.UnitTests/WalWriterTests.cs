using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="WalWriter"/> (US-EMDB-73-5, EmailDB_FileFormat_Spec.md
/// Section 10.4): WAL blocks appended via the standard block machinery, stamped
/// with the CURRENT Checkpoint's BlockId and a monotonic WalSequence. Covers
/// checkpoint-BlockId stamping (including a checkpoint change mid-stream),
/// sequence monotonicity across appends and a resumed reopen, round-trip through
/// a real block read, the WAL block type on disk, and the fence/failure contracts
/// (no committed checkpoint, no sequence burn on a failed append).
/// </summary>
public class WalWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-wal-{Guid.NewGuid():N}.emdb");
    private readonly FileStream _stream;
    private readonly RuntimeBlockOffsetMap _runtimeMap = new();
    private readonly BlockManager _manager;

    private CheckpointRootPointer _currentCheckpoint = CheckpointRootPointer.None;

    public WalWriterTests()
    {
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        _manager = new BlockManager(_stream, offsetMap: _runtimeMap, firstBlockOffset: 0, ownsStream: true);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ------------------------------------------------------------- Helpers

    private static byte[] Ulid(byte seed) =>
        Enumerable.Range(0, UlidGenerator.UlidSize).Select(i => (byte)(seed + i)).ToArray();

    private static byte[] Key(byte seed) =>
        Enumerable.Range(0, WalEntry.KeySize).Select(i => (byte)(seed + i)).ToArray();

    private static void Ok<T>(Result<T> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    /// <summary>Appends a real block and returns a checkpoint pointer to it (a committed Checkpoint N).</summary>
    private CheckpointRootPointer AppendCheckpoint(byte payloadByte = 0x99)
    {
        var appended = _manager.Append(BlockType.Checkpoint, PayloadEncoding.Custom, new byte[] { payloadByte });
        Ok(appended);
        var pointer = CheckpointRootPointer.Create(appended.Value.BlockId, appended.Value.Offset);
        _currentCheckpoint = pointer;
        return pointer;
    }

    private WalWriter NewWriter() => new(_manager, () => _currentCheckpoint);

    private static IReadOnlyList<WalEntry> OneInsert(byte seed = 1) =>
        new[] { WalEntry.Insert(Key(seed), Ulid(seed)) };

    // --------------------------------------------------------------- Tests

    [Fact]
    public void Append_StampsCurrentCheckpointBlockId()
    {
        var checkpoint = AppendCheckpoint();
        var writer = NewWriter();

        var result = writer.Append(OneInsert());
        Ok(result);

        Assert.Equal(checkpoint.BlockId, result.Value.Wal.CheckpointBlockId);
    }

    [Fact]
    public void Append_RestampsWhenCheckpointAdvances()
    {
        var checkpointN = AppendCheckpoint(0x10);
        var writer = NewWriter();

        var first = writer.Append(OneInsert(1));
        Ok(first);
        Assert.Equal(checkpointN.BlockId, first.Value.Wal.CheckpointBlockId);

        // A fresh Checkpoint N+1 lands; subsequent WAL blocks fence to it.
        var checkpointNext = AppendCheckpoint(0x20);
        var second = writer.Append(OneInsert(2));
        Ok(second);
        Assert.Equal(checkpointNext.BlockId, second.Value.Wal.CheckpointBlockId);
        Assert.NotEqual(checkpointN.BlockId, checkpointNext.BlockId);
    }

    [Fact]
    public void Append_ProducesStrictlyMonotonicSequenceFromZero()
    {
        AppendCheckpoint();
        var writer = NewWriter();

        Assert.Null(writer.LastSequence);
        for (ulong expected = 0; expected < 50; expected++)
        {
            var result = writer.Append(OneInsert((byte)expected));
            Ok(result);
            Assert.Equal(expected, result.Value.Wal.WalSequence);
            Assert.Equal(expected, writer.LastSequence);
        }
    }

    [Fact]
    public void ResumeConstructor_ContinuesSequenceUnbroken()
    {
        AppendCheckpoint();

        // A resumed writer whose last durable WAL block was sequence 41.
        var resumed = new WalWriter(_manager, () => _currentCheckpoint, lastSequence: 41);
        Assert.Equal(41UL, resumed.LastSequence);

        var next = resumed.Append(OneInsert());
        Ok(next);
        Assert.Equal(42UL, next.Value.Wal.WalSequence);
    }

    [Fact]
    public void Append_WalBlock_RoundTripsThroughBlockRead()
    {
        var checkpoint = AppendCheckpoint();
        var writer = NewWriter();

        var entries = new[]
        {
            WalEntry.Insert(Key(1), Ulid(1)),
            WalEntry.Delete(Key(2)),
            WalEntry.FolderOp(Key(3), aux: new byte[] { 4, 5, 6 }, blockId: Ulid(3)),
        };
        var written = writer.Append(entries);
        Ok(written);

        // Read the raw block back and deserialize its payload.
        var block = _manager.Read(written.Value.Location.Offset);
        Ok(block);
        Assert.Equal(BlockType.WAL, block.Value.Header.Type);

        var round = WalSerializer.Deserialize(block.Value.Payload);
        Ok(round);
        Assert.Equal(0UL, round.Value.WalSequence);
        Assert.Equal(checkpoint.BlockId, round.Value.CheckpointBlockId);
        Assert.Equal(3, round.Value.Entries.Count);
        Assert.Equal(WalOpKind.Delete, round.Value.Entries[1].Op);
        Assert.Equal(new byte[] { 4, 5, 6 }, round.Value.Entries[2].Aux);
    }

    [Fact]
    public void Append_EmptyEntries_Succeeds()
    {
        AppendCheckpoint();
        var writer = NewWriter();

        var result = writer.Append(Array.Empty<WalEntry>());
        Ok(result);
        Assert.Empty(result.Value.Wal.Entries);
        Assert.Equal(0UL, writer.LastSequence);
    }

    [Fact]
    public void Append_Fails_WhenNoCommittedCheckpoint()
    {
        // No AppendCheckpoint(): the fence is absent.
        var writer = NewWriter();

        var result = writer.Append(OneInsert());
        Assert.True(result.IsFailure);
        Assert.Contains("Checkpoint", result.Error);
        // A failed append never burns a sequence number.
        Assert.Null(writer.LastSequence);
    }

    [Fact]
    public void FailedAppend_DoesNotAdvanceSequence()
    {
        AppendCheckpoint();
        var writer = NewWriter();

        Ok(writer.Append(OneInsert(1)));       // sequence 0
        Assert.Equal(0UL, writer.LastSequence);

        // Drop the fence: the next append fails and must not advance to 1.
        _currentCheckpoint = CheckpointRootPointer.None;
        Assert.True(writer.Append(OneInsert(2)).IsFailure);
        Assert.Equal(0UL, writer.LastSequence);

        // Restore the fence: the next successful append resumes at 1, no gap.
        _currentCheckpoint = AppendCheckpoint();
        var resumed = writer.Append(OneInsert(3));
        Ok(resumed);
        Assert.Equal(1UL, resumed.Value.Wal.WalSequence);
    }

    [Fact]
    public void Append_MultipleBlocks_LandAtDistinctOffsetsWithWalType()
    {
        AppendCheckpoint();
        var writer = NewWriter();

        var a = writer.Append(OneInsert(1));
        var b = writer.Append(OneInsert(2));
        Ok(a);
        Ok(b);
        Assert.NotEqual(a.Value.Location.Offset, b.Value.Location.Offset);

        foreach (var loc in new[] { a.Value.Location, b.Value.Location })
        {
            var block = _manager.Read(loc.Offset);
            Ok(block);
            Assert.Equal(BlockType.WAL, block.Value.Header.Type);
        }
    }
}
