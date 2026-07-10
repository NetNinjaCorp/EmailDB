using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Acceptance-criterion coverage for US-EMDB-89 criterion 1: "Compacted file
/// contains exactly the live blocks with identical BlockIds and content."
///
/// <para>The existing Compactor suites already assert the "identical BlockIds and
/// content" half byte-for-byte (<see cref="CompactorLiveCopyTests"/>) and the
/// post-swap re-read of every live block (<see cref="CompactorSwapTests"/>), and
/// they show the walk excludes the structural blocks it never indexes (the location
/// index's own nodes and the old Checkpoint). What none of them exercise is the
/// "exactly" half against a genuinely DEAD data block: a block that physically
/// exists in the source's block stream but is NOT in the committed
/// BlockLocationIndex (a superseded / unreachable block — the dead space compaction
/// exists to reclaim). These tests build such a source, run a full compaction +
/// atomic swap, and prove the compacted file drops the dead block entirely while
/// keeping exactly the live set with identical BlockIds and byte-exact content.</para>
///
/// <para>Every file handle and <see cref="Compactor"/> is disposed before the
/// compacted file is reopened, so these pass in isolation and in the full parallel
/// suite.</para>
/// </summary>
public class CompactionCriterion1Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-crit1-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    public void Dispose()
    {
        Delete(_path);
        Delete(SideFile);
    }

    private static void Delete(string p)
    {
        if (File.Exists(p))
            File.Delete(p);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static T Require<T>(Result<T> r)
    {
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
        return r.Value;
    }

    private static BlockManager OpenManager(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return new BlockManager(stream, ownsStream: true);
    }

    // The distinctive fill byte of the dead block's payload — chosen so no live block
    // (filled with its small ascending index byte, or the folder/metadata zeros) ever
    // produces the same on-disk bytes, so a run of it in the file can only be the dead
    // block's payload.
    private const byte DeadFill = 0xDD;
    private const int DeadPayloadSize = 4096;

    // ---------------------------------------------------------- Exactly the live set

    [Fact]
    public void Compacted_file_contains_exactly_the_live_blocks_and_excludes_a_dead_block()
    {
        var source = BuildSourceWithDeadBlock(liveDataCount: 12);

        // Full compaction: copy live blocks, rebuild the index + Checkpoint, atomic swap.
        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            copied = compactor.CopiedBlocks.ToArray();

            // The dead block is never even a candidate: the live walk (the source
            // BlockLocationIndex) does not surface it, so it is not copied.
            Assert.DoesNotContain(
                copied, cb => cb.BlockId.AsSpan().SequenceEqual(source.DeadBlockId));
            Assert.Equal(source.LiveBlockIds.Count, copied.Count);

            Ok(compactor.FinalizeAndSwap());
        }

        Assert.False(File.Exists(SideFile));

        // Reopen the compacted file the real way (clean, zero-scan) and inspect its
        // committed live set.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;

        // EXACTLY the live blocks: one index entry per live block, no more, no fewer —
        // the dead block did not sneak in and no live block was lost.
        Assert.Equal(source.LiveBlockIds.Count, state.LocationIndex.Count);
        Assert.Equal(source.LiveBlockIds.Count, state.Checkpoint.Checkpoint.LiveBlockCount);

        // The dead block resolves to NOTHING in the compacted file's index.
        var deadLookup = Require(state.LocationIndex.Lookup(source.DeadBlockId));
        Assert.False(deadLookup.Found, "the dead block must not be in the compacted file's index");

        // Every live block resolves and re-reads with its ORIGINAL BlockId and byte-exact
        // content (payload + the on-disk header fields that define the block).
        foreach (var liveId in source.LiveBlockIds)
        {
            var lookup = Require(state.LocationIndex.Lookup(liveId));
            Assert.True(lookup.Found, $"live block {Convert.ToHexString(liveId)} missing after compaction");

            var block = Require(state.BlockManager.Read(lookup.Offset));
            Assert.Equal(liveId, block.Header.BlockId);

            var expected = source.LiveBlocks[Convert.ToHexString(liveId)];
            Assert.Equal(expected.Payload, block.Payload);
            Assert.Equal(expected.Type, block.Header.Type);
            Assert.Equal(expected.Encoding, block.Header.Encoding);
            Assert.Equal(expected.Compression, block.Header.Compression);
        }
    }

    // ------------------------------------------------ Dead content is physically gone

    [Fact]
    public void Compacted_file_reclaims_the_dead_block_bytes()
    {
        var source = BuildSourceWithDeadBlock(liveDataCount: 8);
        var sourceBytes = File.ReadAllBytes(_path);

        // The dead block's distinctive payload really is in the source on disk.
        var deadRun = Enumerable.Repeat(DeadFill, DeadPayloadSize).ToArray();
        Assert.True(ContainsRun(sourceBytes, deadRun),
            "sanity: the source file must physically contain the dead block's payload");

        // Also confirm the source really carries the dead block AND all live blocks in
        // its raw stream (so the swap below is genuinely dropping something present).
        using (var srcScan = OpenManager(_path))
        {
            var srcIds = Require(srcScan.ScanForward()).Blocks
                .Select(b => Convert.ToHexString(b.BlockId)).ToHashSet();
            Assert.Contains(Convert.ToHexString(source.DeadBlockId), srcIds);
            foreach (var liveId in source.LiveBlockIds)
                Assert.Contains(Convert.ToHexString(liveId), srcIds);
        }

        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            Ok(compactor.FinalizeAndSwap());
        }

        var compactedBytes = File.ReadAllBytes(_path);

        // The dead block's payload bytes are physically gone — its space was reclaimed.
        Assert.False(ContainsRun(compactedBytes, deadRun),
            "the compacted file must not contain the dead block's payload bytes");

        // And the compacted file is smaller than the source (it dropped the dead block,
        // the old index nodes, and the old Checkpoint).
        Assert.True(compactedBytes.Length < sourceBytes.Length,
            $"compacted file ({compactedBytes.Length} bytes) must be smaller than the source ({sourceBytes.Length} bytes)");

        // A raw forward scan of the compacted file sees every live BlockId and NOT the
        // dead one — the block stream itself contains exactly the live set (plus the
        // rebuilt structural blocks, which are not the dead block).
        using var scan = OpenManager(_path);
        var compactedIds = Require(scan.ScanForward()).Blocks
            .Select(b => Convert.ToHexString(b.BlockId)).ToHashSet();
        Assert.DoesNotContain(Convert.ToHexString(source.DeadBlockId), compactedIds);
        foreach (var liveId in source.LiveBlockIds)
            Assert.Contains(Convert.ToHexString(liveId), compactedIds);
    }

    // ================================================================= Helpers

    private static bool ContainsRun(byte[] haystack, byte[] run)
    {
        if (run.Length == 0 || haystack.Length < run.Length)
            return false;
        return haystack.AsSpan().IndexOf(run.AsSpan()) >= 0;
    }

    /// <summary>
    /// Facts about the generated source: its live set (BlockIds in commit order plus
    /// each live block's on-disk content) and the one dead block that is physically
    /// present but not in the committed BlockLocationIndex.
    /// </summary>
    private sealed record SourceFile(
        byte[] FileId,
        IReadOnlyList<byte[]> LiveBlockIds,
        IReadOnlyDictionary<string, LiveBlockContent> LiveBlocks,
        byte[] DeadBlockId);

    private sealed record LiveBlockContent(
        byte[] Payload, BlockType Type, PayloadEncoding Encoding, CompressionAlgorithm Compression);

    /// <summary>
    /// Builds a clean v3 file at <see cref="_path"/> with <paramref name="liveDataCount"/>
    /// live EmailContent blocks plus a live folder-tree and metadata root (all in the
    /// BlockLocationIndex so compaction copies and remaps them), and — crucially — one
    /// extra EmailContent block that is appended to the block stream but deliberately
    /// LEFT OUT of the index: a dead / unreachable block, exactly the dead space
    /// compaction must reclaim. Mirrors the fixture shape the sibling Compactor suites
    /// use so the full copy → rebuild → swap pipeline runs.
    /// </summary>
    private SourceFile BuildSourceWithDeadBlock(int liveDataCount, int dataSize = 300)
    {
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();
        var liveLocations = new List<BlockLocation>();
        var liveIds = new List<byte[]>();
        var liveContent = new Dictionary<string, LiveBlockContent>(StringComparer.Ordinal);
        byte[] deadBlockId;
        CheckpointRootPointer checkpointPointer;

        void RecordLive(BlockLocation loc, byte[] payload, BlockType type)
        {
            liveLocations.Add(loc);
            liveIds.Add(loc.BlockId);
            liveContent[Convert.ToHexString(loc.BlockId)] =
                new LiveBlockContent(payload, type, PayloadEncoding.RawBytes, CompressionAlgorithm.None);
        }

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            for (int i = 0; i < liveDataCount; i++)
            {
                var buf = new byte[dataSize];
                Array.Fill(buf, (byte)(i & 0xFF));
                var loc = Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, buf));
                RecordLive(loc, buf, BlockType.EmailContent);
            }

            // The DEAD block: physically appended, distinctive payload, but NOT added to
            // the live set that seeds the BlockLocationIndex — so it is unreachable.
            var deadBuf = new byte[DeadPayloadSize];
            Array.Fill(deadBuf, DeadFill);
            var dead = Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, deadBuf));
            deadBlockId = dead.BlockId;

            var folderBuf = new byte[64];
            var folder = Require(manager.Append(BlockType.FolderTree, PayloadEncoding.RawBytes, folderBuf));
            RecordLive(folder, folderBuf, BlockType.FolderTree);

            var metadataBuf = new byte[48];
            var metadata = Require(manager.Append(BlockType.Metadata, PayloadEncoding.RawBytes, metadataBuf));
            RecordLive(metadata, metadataBuf, BlockType.Metadata);

            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
            Ok(index.PutBatch(liveLocations));
            Ok(manager.Flush());

            long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root!.RootRef.Reference);
            var rootBlockId = runtimeMap.SnapshotOrderedByOffset().First(b => b.Offset == rootOffset).BlockId;

            var writer = new CheckpointWriter(manager, fileId);
            Require(writer.WriteCheckpoint(new CheckpointContents
            {
                FolderTreeRoot = CheckpointRootPointer.Create(folder.BlockId, folder.Offset),
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = CheckpointRootPointer.Create(rootBlockId, rootOffset),
                MetadataRoot = CheckpointRootPointer.Create(metadata.BlockId, metadata.Offset),
                KeyStoreRoot = CheckpointRootPointer.None,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = liveLocations.Count,
                LiveByteCount = 4096,
                // The dead block is unreclaimed dead space in the source.
                DeadByteCount = DeadPayloadSize,
            }));
            checkpointPointer = writer.LastCheckpointPointer;
        }

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var sbManager = new SuperblockManager(stream, ownsStream: true))
        {
            Superblock MakeSuperblock() => new()
            {
                FileId = (byte[])fileId.Clone(),
                CleanShutdown = 1,
                LastCheckpointBlockId = (byte[])checkpointPointer.BlockId.Clone(),
                LastCheckpointOffset = checkpointPointer.Offset,
            };
            Require(sbManager.Write(MakeSuperblock()));
            Require(sbManager.Write(MakeSuperblock()));
        }

        return new SourceFile(fileId, liveIds, liveContent, deadBlockId);
    }
}
