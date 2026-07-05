using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Proves the story-level acceptance criterion "Open is O(log n) on every normal
/// path" (EmailDB_FileFormat_Spec.md Section 10.2). The two normal paths are the
/// clean-open fast path (<see cref="CleanOpener"/>, <c>CleanShutdown = 1</c>) and the
/// bounded dirty open with a small post-checkpoint tail (<see cref="DirtyOpener"/>,
/// <c>CleanShutdown = 0</c>, no newer Checkpoint and no unreplayed WAL). For each we
/// commit the SAME logical shape into files whose committed block count (the "n" of
/// O(log n) — one BlockLocationIndex entry per live block, spec Sections 7, 10.1)
/// differs by a large factor, then use the read-recording stream to prove the open's
/// read-set grows LOGARITHMICALLY (with the B+-tree height), not linearly with n. A
/// linear (scan-shaped) open would balloon its read-set by the same large factor; an
/// O(log n) open barely moves.
///
/// <para>This asserts on <b>work done</b> (reads / bytes / the tree height that bounds
/// them), never on wall-clock time — so it is deterministic, not flaky.</para>
/// </summary>
public class OpenComplexityTests : IDisposable
{
    private readonly List<string> _paths = new();

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p))
                File.Delete(p);
    }

    // ------------------------------------------------------------- Helpers

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-opencomplexity-{Guid.NewGuid():N}.emdb");
        _paths.Add(path);
        return path;
    }

    private static FileStream OpenRW(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>A 16-byte BlockId whose lexicographic order matches the numeric order of <paramref name="i"/>.</summary>
    private static byte[] LocationBlockId(int i)
    {
        var id = new byte[BlockLocationIndex.BlockIdKeySize];
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), i);
        return id;
    }

    private sealed record BuiltFile(string Path, int CommittedEntries, int LocationHeight, long FileLength);

    /// <summary>
    /// Writes a realistic v3 file committing <paramref name="committedEntries"/> live
    /// blocks into the BlockLocationIndex (a small fan-out forces a genuinely
    /// multi-level tree so height tracks log(n)), a folder root, a single Checkpoint as
    /// the LAST block (so a dirty open's post-Checkpoint tail is O(1), identical across
    /// sizes), and a dual-slot superblock hinting that Checkpoint with
    /// <c>CleanShutdown = cleanShutdown</c>.
    /// </summary>
    private BuiltFile BuildFile(int committedEntries, byte cleanShutdown)
    {
        var path = NewPath();
        long checkpointOffset;
        byte[] checkpointBlockId;
        int locationHeight;

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = OpenRW(path))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            var folder = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[64]);
            Ok(folder);

            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            // Small fan-out ⇒ many internal levels ⇒ height grows with log(entries).
            var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
            var batch = new List<BlockLocation>(committedEntries);
            for (int i = 0; i < committedEntries; i++)
                batch.Add(new BlockLocation
                {
                    BlockId = LocationBlockId(i),
                    Offset = 100_000L + 4096L * i,
                    TotalBlockLength = 128 + (i & 0x3F),
                });
            Ok(index.PutBatch(batch));
            Ok(manager.Flush());

            locationHeight = index.Root!.Height;
            long locationRootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root!.RootRef.Reference);
            var rootBlockId = runtimeMap.SnapshotOrderedByOffset()
                .First(b => b.Offset == locationRootOffset).BlockId;
            var locationPointer = CheckpointRootPointer.Create(rootBlockId, locationRootOffset);
            var folderPointer = CheckpointRootPointer.Create(folder.Value.BlockId, folder.Value.Offset);

            var writer = new CheckpointWriter(manager, FileId);
            Ok(writer.WriteCheckpoint(new CheckpointContents
            {
                FolderTreeRoot = folderPointer,
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = locationPointer,
                MetadataRoot = CheckpointRootPointer.None,
                KeyStoreRoot = CheckpointRootPointer.None,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = committedEntries,
                LiveByteCount = 4096,
                DeadByteCount = 0,
            }));
            checkpointOffset = writer.LastCheckpointPointer.Offset;
            checkpointBlockId = writer.LastCheckpointPointer.BlockId;
        }

        using (var stream = OpenRW(path))
        using (var sbManager = new SuperblockManager(stream, ownsStream: true))
        {
            Ok(sbManager.Write(new Superblock
            {
                FileId = (byte[])FileId.Clone(),
                CleanShutdown = cleanShutdown,
                LastCheckpointBlockId = (byte[])checkpointBlockId.Clone(),
                LastCheckpointOffset = checkpointOffset,
            }));
        }

        return new BuiltFile(path, committedEntries, locationHeight, new FileInfo(path).Length);
    }

    private readonly record struct OpenWork(int Reads, long Bytes, int MaxSingleRead, int Height);

    /// <summary>Runs a clean open through the recording stream and returns the work it did.</summary>
    private static OpenWork MeasureCleanOpen(BuiltFile file)
    {
        using var stream = new ReadRecordingFileStream(file.Path);
        var opened = CleanOpener.Open(stream);
        Ok(opened);
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Value.Kind);
        using var state = opened.Value.State!;
        int height = state.LocationIndex.Root!.Height;
        // Sanity: the reconstructed index really holds every committed entry.
        Assert.Equal(file.CommittedEntries, (int)state.LocationIndex.Count);
        return new OpenWork(stream.Reads.Count, stream.TotalBytesRead, stream.MaxSingleRead, height);
    }

    /// <summary>Runs a bounded dirty open (heals to clean) through the recording stream and returns the work it did.</summary>
    private static OpenWork MeasureDirtyOpen(BuiltFile file)
    {
        using var stream = new ReadRecordingFileStream(file.Path);
        var opened = DirtyOpener.Open(stream);
        Ok(opened);
        // Normal dirty path: no newer Checkpoint, no unreplayed WAL ⇒ heals straight to clean.
        Assert.True(opened.Value.HealedClean);
        Assert.False(opened.Value.AdoptedNewerCheckpoint);
        Assert.Equal(0, opened.Value.ReplayedEntryCount);
        using var state = opened.Value.State!;
        int height = state.LocationIndex.Root!.Height;
        Assert.Equal(file.CommittedEntries, (int)state.LocationIndex.Count);
        return new OpenWork(stream.Reads.Count, stream.TotalBytesRead, stream.MaxSingleRead, height);
    }

    // --------------------------------------------------------------- Tests

    [Fact]
    public void Clean_open_work_is_logarithmic_in_committed_block_count()
    {
        // Same logical shape committed at two scales: n and 32n live blocks. If the
        // clean open scanned or touched any per-block amount, its read-set would grow
        // ~32x with n. O(log n) means it grows only with the B+-tree height.
        const int small = 32;
        const int large = 32 * 32; // 1024 committed blocks — a 32x larger index.

        var smallFile = BuildFile(small, cleanShutdown: 1);
        var largeFile = BuildFile(large, cleanShutdown: 1);

        var smallWork = MeasureCleanOpen(smallFile);
        var largeWork = MeasureCleanOpen(largeFile);

        AssertLogarithmic("Clean open", small, large, smallFile, largeFile, smallWork, largeWork);
    }

    [Fact]
    public void Dirty_open_work_is_logarithmic_in_committed_block_count_with_a_bounded_tail()
    {
        // The other normal path: CleanShutdown = 0 with a bounded post-Checkpoint tail
        // (the Checkpoint is the last block — no newer Checkpoint, no unreplayed WAL).
        // Recovery reads that O(1) tail, heals the superblock, and re-opens clean via
        // the O(log n) spine walk. The committed (pre-Checkpoint) region — 32x larger in
        // the big file — must NOT enter the read-set: recovery scans only bytes after
        // the last Checkpoint (spec Section 10.2 step 4).
        const int small = 32;
        const int large = 32 * 32;

        var smallFile = BuildFile(small, cleanShutdown: 0);
        var largeFile = BuildFile(large, cleanShutdown: 0);

        var smallWork = MeasureDirtyOpen(smallFile);
        var largeWork = MeasureDirtyOpen(largeFile);

        AssertLogarithmic("Dirty open", small, large, smallFile, largeFile, smallWork, largeWork);
    }

    /// <summary>
    /// The shared O(log n) contract: committed block count grew by a large factor while
    /// the open's read-set stayed bounded by the tree height, never a scan chunk, and
    /// far below the linear cost of touching every committed block.
    /// </summary>
    private static void AssertLogarithmic(
        string label, int small, int large,
        BuiltFile smallFile, BuiltFile largeFile, OpenWork smallWork, OpenWork largeWork)
    {
        int growthFactor = large / small; // 32x

        // The committed index really grew by the intended factor, and the file with it.
        // BuildFile commits the entries with the amortized one-pass PutBatch bulk load
        // (US-EMDB-104), so the file grows with the tree's NODE count (a 32x entry
        // increase writes ~31x more nodes) rather than 32x full per-entry paths; over a
        // fixed per-file overhead (superblock slots, Checkpoint, folder block) that lands
        // the large file several times the size of the small one — still a clear dwarfing,
        // just not the inflated ~32x an un-amortized N-inserts build produced.
        Assert.Equal(large, largeFile.CommittedEntries);
        Assert.True(largeFile.FileLength > smallFile.FileLength * (growthFactor / 8),
            $"{label}: the large file ({largeFile.FileLength} B) should dwarf the small one ({smallFile.FileLength} B).");

        // The tree got TALLER (this is real multi-level descent, not a degenerate flat
        // tree) but only by O(log n) — a couple of levels for a 32x entry increase.
        Assert.True(largeWork.Height > smallWork.Height,
            $"{label}: expected a taller tree for {large} entries (small height {smallWork.Height}, large height {largeWork.Height}).");
        Assert.True(largeWork.Height - smallWork.Height <= 4,
            $"{label}: tree height grew from {smallWork.Height} to {largeWork.Height} — steeper than log(32·n).");

        // No scan chunk: every read is a discrete block read, never a 64 KiB scan window.
        Assert.True(smallWork.MaxSingleRead < 64 * 1024 && largeWork.MaxSingleRead < 64 * 1024,
            $"{label}: a single read of {Math.Max(smallWork.MaxSingleRead, largeWork.MaxSingleRead)} bytes looks like a scan chunk.");

        // THE CRITICAL ASSERTION — sub-linear growth. Committed blocks grew 32x; the
        // read-set must NOT. A linear/scan-shaped open would grow ~32x (or worse). An
        // O(log n) open grows by only a small, height-bounded additive amount. We allow
        // the read count to at most double, and cap the extra reads to a few per added
        // tree level — nowhere near the 32x a linear open would show.
        int readDelta = largeWork.Reads - smallWork.Reads;
        int heightDelta = largeWork.Height - smallWork.Height;
        Assert.True(largeWork.Reads <= smallWork.Reads * 2,
            $"{label}: read count grew from {smallWork.Reads} to {largeWork.Reads} for a {growthFactor}x larger index — a linear open would be ~{growthFactor}x. Not O(log n).");
        Assert.True(readDelta <= 4 * (heightDelta + 1),
            $"{label}: read count grew by {readDelta} across {heightDelta} extra tree levels — steeper than O(height).");

        // Bytes read grew sub-linearly too (a small slack absorbs FileStream buffer
        // re-alignment as absolute offsets shift; it must not scale with the index).
        Assert.True(largeWork.Bytes <= smallWork.Bytes * 2 + 128 * 1024,
            $"{label}: bytes read grew from {smallWork.Bytes} to {largeWork.Bytes} for a {growthFactor}x larger index. Not O(log n).");

        // Absolute anti-scan ceiling: even the large file did only a handful of reads —
        // orders of magnitude below the {large} block reads a linear walk would need.
        Assert.True(largeWork.Reads < large / 4,
            $"{label}: the {large}-block file's open issued {largeWork.Reads} reads — that is scan-shaped, not O(log n).");

        // O(height) ceiling: reads are bounded by a fixed base (superblock slots +
        // Checkpoint + folder + FileStream buffer alignment) plus a few reads per tree
        // level — the signature of an O(height) = O(log n) descent. A scan of this
        // file's hundreds of B+-tree nodes would blow past this height-linear bound.
        Assert.True(smallWork.Reads <= 8 * smallWork.Height + 24,
            $"{label}: small-file open did {smallWork.Reads} reads at height {smallWork.Height} — above the O(height) bound.");
        Assert.True(largeWork.Reads <= 8 * largeWork.Height + 24,
            $"{label}: large-file open did {largeWork.Reads} reads at height {largeWork.Height} — above the O(height) bound.");
    }
}
