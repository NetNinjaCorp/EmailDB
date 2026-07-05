using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-end tests for US-EMDB-73's acceptance criterion "WAL blocks after
/// Checkpoint N carry N's BlockId" (EmailDB_FileFormat_Spec.md Section 10.4),
/// wiring a real <see cref="WalWriter"/> to the real
/// <see cref="CheckpointWriter.LastCheckpointPointer"/> — the production supplier,
/// not a fake — over one real file, and verifying the fence by SCANNING the WAL
/// blocks back off disk (deserializing each block payload) rather than inspecting
/// in-memory state.
///
/// <para>Complements <see cref="WalWriterTests"/> (which exercises the stamping
/// contract through an in-test supplier) by proving the fence holds with the
/// genuine CheckpointWriter wiring and is durable and per-block across a composed
/// checkpoint-&gt;WAL-&gt;checkpoint-&gt;WAL sequence.</para>
/// </summary>
public class WalCheckpointFenceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-wal-fence-{Guid.NewGuid():N}.emdb");
    private readonly FileStream _stream;
    private readonly RuntimeBlockOffsetMap _runtimeMap = new();
    private readonly BlockManager _manager;
    private readonly CheckpointWriter _checkpointWriter;
    private readonly WalWriter _walWriter;

    private static readonly byte[] FileId = Ulid(0x01);

    public WalCheckpointFenceTests()
    {
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        _manager = new BlockManager(_stream, offsetMap: _runtimeMap, firstBlockOffset: 0, ownsStream: true);
        _checkpointWriter = new CheckpointWriter(_manager, FileId);
        // The exact production wiring: the WAL writer reads the current fence fresh
        // on every append from the REAL CheckpointWriter, not an in-test supplier.
        _walWriter = new WalWriter(_manager, () => _checkpointWriter.LastCheckpointPointer);
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

    private static IReadOnlyList<WalEntry> OneInsert(byte seed) =>
        new[] { WalEntry.Insert(Key(seed), Ulid(seed)) };

    private static CheckpointContents Contents() => new()
    {
        FolderTreeRoot = CheckpointRootPointer.None,
        PrimaryIndexRoot = CheckpointRootPointer.None,
        LocationIndexRoot = CheckpointRootPointer.None,
        MetadataRoot = CheckpointRootPointer.None,
        KeyStoreRoot = CheckpointRootPointer.None,
        LiveBlockCount = 1,
        LiveByteCount = 64,
        DeadByteCount = 0,
    };

    /// <summary>
    /// Commits a Checkpoint via the real <see cref="CheckpointWriter"/> and returns
    /// its on-disk BlockId — the 16-byte ULID a subsequent WAL block must fence to.
    /// </summary>
    private byte[] WriteCheckpoint()
    {
        var written = _checkpointWriter.WriteCheckpoint(Contents());
        Ok(written);
        // A committed Checkpoint is now named by LastCheckpointPointer (ULID+offset);
        // its ULID is what WalWriter stamps into CheckpointBlockId.
        Assert.False(_checkpointWriter.LastCheckpointPointer.IsAbsent);
        return (byte[])_checkpointWriter.LastCheckpointPointer.BlockId.Clone();
    }

    /// <summary>
    /// fsyncs, then scans every block off disk in file order and returns the WAL
    /// blocks, each deserialized from its persisted block payload — the fence is
    /// read from bytes on disk, never from writer state.
    /// </summary>
    private List<WalBlock> ScanWalBlocksFromDisk()
    {
        var flushed = _manager.Flush();
        Assert.True(flushed.IsSuccess, flushed.IsFailure ? flushed.Error : null);
        var scan = _manager.ScanForward();
        Ok(scan);

        var wal = new List<WalBlock>();
        foreach (var loc in scan.Value.Blocks)
        {
            var block = _manager.Read(loc.Offset);
            Ok(block);
            if (block.Value.Header.Type != BlockType.WAL)
                continue;
            var deserialized = WalSerializer.Deserialize(block.Value.Payload);
            Ok(deserialized);
            wal.Add(deserialized.Value);
        }
        return wal;
    }

    // --------------------------------------------------------------- Tests

    /// <summary>(c) A WAL append before the first real Checkpoint exists is refused —
    /// the real CheckpointWriter's fence is absent (None) until it commits one.</summary>
    [Fact]
    public void WalAppend_BeforeFirstRealCheckpoint_IsRefused()
    {
        // Nothing committed: the genuine fence supplier returns None.
        Assert.True(_checkpointWriter.LastCheckpointPointer.IsAbsent);

        var refused = _walWriter.Append(OneInsert(1));
        Assert.True(refused.IsFailure);
        Assert.Contains("Checkpoint", refused.Error);
        Assert.Null(_walWriter.LastSequence);

        // And nothing landed on disk as a WAL block.
        Assert.Empty(ScanWalBlocksFromDisk());
    }

    /// <summary>(a) After Checkpoint N is committed via the real CheckpointWriter,
    /// every WAL block read back off disk carries exactly N's BlockId.</summary>
    [Fact]
    public void WalBlocksAfterCheckpointN_ReadBackFromDisk_CarryNsBlockId()
    {
        byte[] n = WriteCheckpoint();

        for (byte i = 0; i < 5; i++)
            Ok(_walWriter.Append(OneInsert(i)));

        var wal = ScanWalBlocksFromDisk();
        Assert.Equal(5, wal.Count);
        foreach (var block in wal)
            Assert.Equal(n, block.CheckpointBlockId);
    }

    /// <summary>(b)+(d) A composed checkpoint-&gt;WAL-&gt;checkpoint-&gt;WAL sequence over
    /// one real file, scanned back, shows a durable per-block fence partition: WAL
    /// blocks before N+1 still carry N's BlockId on disk, those after carry N+1's.</summary>
    [Fact]
    public void ComposedCheckpointWalSequence_ScannedBack_ShowsDurableFencePartition()
    {
        byte[] n = WriteCheckpoint();
        Ok(_walWriter.Append(OneInsert(1)));   // seq 0, fenced to N
        Ok(_walWriter.Append(OneInsert(2)));   // seq 1, fenced to N

        byte[] nPlus1 = WriteCheckpoint();     // Checkpoint N+1 lands
        Assert.NotEqual(n, nPlus1);
        Ok(_walWriter.Append(OneInsert(3)));   // seq 2, fenced to N+1
        Ok(_walWriter.Append(OneInsert(4)));   // seq 3, fenced to N+1
        Ok(_walWriter.Append(OneInsert(5)));   // seq 4, fenced to N+1

        var wal = ScanWalBlocksFromDisk();
        Assert.Equal(5, wal.Count);

        // Blocks are on disk in append (== sequence) order.
        Assert.Equal(new ulong[] { 0, 1, 2, 3, 4 }, wal.Select(w => w.WalSequence).ToArray());

        // The fence partition, read straight off disk: seq 0-1 -> N, seq 2-4 -> N+1.
        Assert.Equal(n, wal[0].CheckpointBlockId);
        Assert.Equal(n, wal[1].CheckpointBlockId);
        Assert.Equal(nPlus1, wal[2].CheckpointBlockId);
        Assert.Equal(nPlus1, wal[3].CheckpointBlockId);
        Assert.Equal(nPlus1, wal[4].CheckpointBlockId);

        // The fence is per-block and durable: writing N+1 did NOT restamp the WAL
        // blocks already on disk under N.
        Assert.NotEqual(nPlus1, wal[0].CheckpointBlockId);
        Assert.NotEqual(nPlus1, wal[1].CheckpointBlockId);
    }
}
