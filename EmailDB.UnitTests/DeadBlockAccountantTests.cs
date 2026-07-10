using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="DeadBlockAccountant"/> (docs/Compaction.md Section 3,
/// EmailDB_FileFormat_Spec.md Sections 10.1, 11.2): incremental live/dead byte
/// accounting where a block's bytes move from the live to the dead counter the
/// moment a COW rewrite or delete supersedes it, maintained without any scan.
/// </summary>
public class DeadBlockAccountantTests
{
    [Fact]
    public void Fresh_accountant_starts_at_zero()
    {
        var acc = new DeadBlockAccountant();
        Assert.Equal(0, acc.LiveByteCount);
        Assert.Equal(0, acc.DeadByteCount);
    }

    [Fact]
    public void Seeds_from_base_counters()
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 1000, baseDeadBytes: 250);
        Assert.Equal(1000, acc.LiveByteCount);
        Assert.Equal(250, acc.DeadByteCount);
    }

    [Fact]
    public void FromCheckpoint_restores_the_persisted_counters()
    {
        var checkpoint = MakeCheckpoint(liveBytes: 4096, deadBytes: 1536);
        var acc = DeadBlockAccountant.FromCheckpoint(checkpoint);
        Assert.Equal(4096, acc.LiveByteCount);
        Assert.Equal(1536, acc.DeadByteCount);
    }

    [Fact]
    public void Append_increases_live_only()
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 100);
        acc.RecordAppend(400);
        Assert.Equal(500, acc.LiveByteCount);
        Assert.Equal(0, acc.DeadByteCount);
    }

    [Fact]
    public void Supersession_moves_bytes_from_live_to_dead()
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 1000, baseDeadBytes: 0);
        acc.RecordSupersession(300);
        Assert.Equal(700, acc.LiveByteCount);
        Assert.Equal(300, acc.DeadByteCount);
    }

    [Fact]
    public void Supersession_of_a_block_location_uses_its_total_length()
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 2048);
        var loc = new BlockLocation { BlockId = new byte[16], Offset = 8192, TotalBlockLength = 512 };
        acc.RecordSupersession(loc);
        Assert.Equal(1536, acc.LiveByteCount);
        Assert.Equal(512, acc.DeadByteCount);
    }

    [Fact]
    public void Result_is_independent_of_append_supersession_order()
    {
        // Superseding a block appended earlier in the same session must not underflow, and the
        // net result must be identical regardless of interleaving (order-independent bookkeeping).
        var a = new DeadBlockAccountant(baseLiveBytes: 1000, baseDeadBytes: 100);
        a.RecordAppend(400);
        a.RecordSupersession(400); // supersede the just-appended block
        a.RecordSupersession(200); // and a pre-existing block

        var b = new DeadBlockAccountant(baseLiveBytes: 1000, baseDeadBytes: 100);
        b.RecordSupersession(200);
        b.RecordSupersession(400);
        b.RecordAppend(400);

        Assert.Equal(a.LiveByteCount, b.LiveByteCount);
        Assert.Equal(a.DeadByteCount, b.DeadByteCount);
        Assert.Equal(800, a.LiveByteCount);  // 1000 + 400 - 400 - 200
        Assert.Equal(700, a.DeadByteCount);  // 100 + 400 + 200
    }

    [Fact]
    public void Session_deltas_are_observable()
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 500, baseDeadBytes: 50);
        acc.RecordAppend(120);
        acc.RecordSupersession(30);
        Assert.Equal(500, acc.BaseLiveByteCount);
        Assert.Equal(50, acc.BaseDeadByteCount);
        Assert.Equal(120, acc.AppendedByteCount);
        Assert.Equal(30, acc.SupersededByteCount);
    }

    [Fact]
    public void Negative_seed_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadBlockAccountant(baseLiveBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadBlockAccountant(baseDeadBytes: -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_lengths_are_rejected(long length)
    {
        var acc = new DeadBlockAccountant(baseLiveBytes: 1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.RecordAppend(length));
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.RecordSupersession(length));
    }

    [Fact]
    public void Superseding_more_than_ever_lived_throws_on_read()
    {
        // A live total driven negative means a block was superseded that was never counted live
        // (double-supersession / untracked block) — a caller bug, surfaced when the total is read.
        var acc = new DeadBlockAccountant(baseLiveBytes: 100);
        acc.RecordSupersession(300);
        Assert.Throws<InvalidOperationException>(() => acc.LiveByteCount);
        // The dead counter is still well-defined regardless.
        Assert.Equal(300, acc.DeadByteCount);
    }

    private static Checkpoint MakeCheckpoint(long liveBytes, long deadBytes) => new()
    {
        FormatVersion = 3,
        CheckpointSequence = 7,
        FileId = new byte[16],
        FolderTreeRoot = CheckpointRootPointer.None,
        PrimaryIndexRoot = CheckpointRootPointer.None,
        LocationIndexRoot = CheckpointRootPointer.None,
        MetadataRoot = CheckpointRootPointer.None,
        KeyStoreRoot = CheckpointRootPointer.None,
        PreviousCheckpoint = CheckpointRootPointer.None,
        SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
        LiveBlockCount = 3,
        LiveByteCount = liveBytes,
        DeadByteCount = deadBytes,
    };
}
