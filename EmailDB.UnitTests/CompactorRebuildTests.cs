using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="Compactor.RebuildLocationIndexAndWriteCheckpoint"/> (US-EMDB-89-7,
/// EmailDB_FileFormat_Spec.md Section 11.2 steps 3-4, docs/Compaction.md Section 2):
/// the second slice of side-file compaction — rebuilding the derived BlockLocationIndex
/// for the copied blocks' new physical layout and writing the compacted file's fresh
/// IndexRoots + Checkpoint. Each test builds a realistic clean v3 source whose named
/// roots (folder tree, metadata) are themselves live blocks in the location index — so
/// compaction copies them and can remap their Checkpoint pointers — then compacts it and
/// asserts:
/// <list type="bullet">
/// <item>the rebuilt index maps every copied BlockId to its NEW side-file offset/length,
/// one entry per copied block, and nothing else;</item>
/// <item>the rebuilt index is durable on the side file: reconstructed purely from the
/// emitted <see cref="LocationIndexRoot"/> it resolves every copied block;</item>
/// <item>the fresh Checkpoint on the side file resolves every named root (each remapped
/// to its new offset), continues the source's CheckpointSequence, and starts a fresh
/// chain (no previous) with zero dead bytes;</item>
/// <item>a multi-level rebuilt tree (forced small fan-out) is still correct — the
/// bottom-up bulk load handles splits;</item>
/// <item>the guard rails: copy-first and run-once.</item>
/// </list>
/// </summary>
public class CompactorRebuildTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-rebuild-{Guid.NewGuid():N}.emdb");

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

    // A distinctive live-byte count the source Checkpoint carries; compaction must
    // preserve it (the copied payload bytes are identical) rather than recompute it.
    private const long SourceLiveByteCount = 987_654;

    // A non-zero dead-byte count the source carries; the compacted file has reclaimed
    // all dead space, so its fresh Checkpoint must record zero.
    private const long SourceDeadByteCount = 555_111;

    // ------------------------------------------------------------- Core rebuild

    [Fact]
    public void Rebuilt_index_maps_every_copied_block_to_its_new_location()
    {
        var source = BuildSource(dataCount: 12);

        using var compactor = Require(Compactor.Begin(_path));
        Require(compactor.CopyLiveBlocks());
        Require(compactor.RebuildLocationIndexAndWriteCheckpoint());

        var copied = compactor.CopiedBlocks;
        var index = compactor.NewLocationIndex!;

        // One entry per copied block — the whole live set, nothing else.
        Assert.Equal(copied.Count, source.LiveBlocks.Count);
        Assert.Equal(copied.Count, index.Count);

        // Every copied block resolves to its NEW side-file offset/length (not the source).
        foreach (var cb in copied)
        {
            var lookup = Require(index.Lookup(cb.BlockId));
            Assert.True(lookup.Found, $"copied block {Convert.ToHexString(cb.BlockId)} missing from the rebuilt index");
            Assert.Equal(cb.NewOffset, lookup.Offset);
            Assert.Equal(cb.NewLength, lookup.Length);
        }

        // The rebuilt index equals an independent index built over the new layout — the
        // entry-by-entry "regenerates and matches" acceptance (spec Section 7).
        AssertMatchesIndependentRebuild(compactor);
    }

    // -------------------------------------------------- Durable on the side file

    [Fact]
    public void Rebuilt_index_is_durable_and_resolves_every_copied_block_from_the_emitted_root()
    {
        BuildSource(dataCount: 20);

        IReadOnlyList<CopiedBlock> copied;
        LocationIndexRoot locationRoot;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            copied = compactor.CopiedBlocks.ToArray();
            locationRoot = compactor.NewLocationIndexRoot!;
        }

        // Reconstruct the location index purely from the emitted LocationIndexRoot and a
        // fresh reader over the side file — nothing in-memory from the compaction. This
        // proves the rebuilt nodes are on disk and the descriptor names them correctly.
        using var sideManager = OpenManager(SideFile);
        var index = ReconstructLocationIndex(sideManager, locationRoot);

        Assert.Equal(copied.Count, index.Count);
        foreach (var cb in copied)
        {
            var lookup = Require(index.Lookup(cb.BlockId));
            Assert.True(lookup.Found);
            Assert.Equal(cb.NewOffset, lookup.Offset);
            Assert.Equal(cb.NewLength, lookup.Length);
        }
    }

    // --------------------------------------------- Fresh Checkpoint + remapped roots

    [Fact]
    public void Fresh_checkpoint_continues_sequence_starts_fresh_chain_and_resolves_every_remapped_root()
    {
        var source = BuildSource(dataCount: 10);

        Checkpoint fresh;
        CheckpointRootPointer checkpointPointer;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            fresh = Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            checkpointPointer = compactor.NewCheckpointPointer!;
        }

        // Continues the source sequence; starts a fresh chain; same FileId; live set and
        // byte accounting reflect the compacted layout (all dead space reclaimed).
        Assert.Equal(source.CheckpointSequence + 1, fresh.CheckpointSequence);
        Assert.True(fresh.PreviousCheckpoint.IsAbsent, "the compacted file starts a fresh Checkpoint chain");
        Assert.Equal(source.FileId, fresh.FileId);
        Assert.Equal(source.LiveBlocks.Count, fresh.LiveBlockCount);
        Assert.Equal(SourceLiveByteCount, fresh.LiveByteCount);
        Assert.Equal(0, fresh.DeadByteCount);

        // The Checkpoint block is really on the side file and its every named root
        // resolves against the new layout — read it back through the real reader. Fresh
        // hints (exact new offsets) mean no root needs re-resolution.
        using var sideManager = OpenManager(SideFile);
        var reader = new CheckpointReader(sideManager, new RuntimeBlockOffsetMap());
        var resolved = Require(reader.Open(checkpointPointer.Offset, source.FileId));

        Assert.NotNull(resolved.LocationIndexRoot);
        Assert.False(resolved.LocationIndexRoot!.HintWasStale);

        // Folder + metadata roots kept their BlockIds and resolve at their new offsets.
        AssertRootRemapped(resolved.FolderTreeRoot, source.FolderBlockId);
        AssertRootRemapped(resolved.MetadataRoot, source.MetadataBlockId);
    }

    private static void AssertRootRemapped(ResolvedRoot? root, byte[] expectedBlockId)
    {
        Assert.NotNull(root);
        Assert.Equal(expectedBlockId, root!.BlockId);
        Assert.False(root.HintWasStale, "the remapped hint should be exact (block copied to a known new offset)");
        Assert.True(root.Offset >= BlockManager.DefaultFirstBlockOffset);
    }

    // ------------------------------------------------------ Multi-level rebuild

    [Fact]
    public void Rebuilt_index_is_correct_when_the_bulk_load_splits_into_a_multi_level_tree()
    {
        var source = BuildSource(dataCount: 40);

        LocationIndexRoot root;
        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            // Force a small fan-out so the bottom-up bulk load must split into several levels.
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint(maxLeafEntries: 4, maxInternalKeys: 3));
            root = compactor.NewLocationIndexRoot!;
            copied = compactor.CopiedBlocks.ToArray();
        }

        Assert.True(root.TreeHeight > 1, "a 42-entry index at fan-out 4/3 must be more than one level deep");
        Assert.Equal(source.LiveBlocks.Count, root.EntryCount);

        // Round-trip from the emitted root on the side file still resolves every block.
        using var sideManager = OpenManager(SideFile);
        var index = ReconstructLocationIndex(sideManager, root);
        Assert.Equal(copied.Count, index.Count);
        foreach (var cb in copied)
        {
            var lookup = Require(index.Lookup(cb.BlockId));
            Assert.True(lookup.Found);
            Assert.Equal(cb.NewOffset, lookup.Offset);
        }
    }

    // ------------------------------------------------------------- Guard rails

    [Fact]
    public void Rebuild_requires_the_copy_pass_to_have_run_first()
    {
        BuildSource(dataCount: 4);

        using var compactor = Require(Compactor.Begin(_path));
        var rebuilt = compactor.RebuildLocationIndexAndWriteCheckpoint();
        Assert.True(rebuilt.IsFailure, "the rebuild must refuse to run before the copy pass");
    }

    [Fact]
    public void Rebuild_runs_once_per_compaction()
    {
        BuildSource(dataCount: 6);

        using var compactor = Require(Compactor.Begin(_path));
        Require(compactor.CopyLiveBlocks());
        Require(compactor.RebuildLocationIndexAndWriteCheckpoint());

        var second = compactor.RebuildLocationIndexAndWriteCheckpoint();
        Assert.True(second.IsFailure, "a second rebuild pass must be refused");
    }

    // ================================================================= Helpers

    /// <summary>
    /// Confirms the compactor's rebuilt index equals an independent BlockLocationIndex
    /// built (over a throwaway stream) from the same copied blocks — the entry-by-entry
    /// verification of <see cref="LocationIndexRegenerator.VerifyMatchesLiveTree"/>.
    /// </summary>
    private void AssertMatchesIndependentRebuild(Compactor compactor)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"emaildb-expected-{Guid.NewGuid():N}.emdb");
        try
        {
            using var scratchManager = new FileStream(scratch, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var manager = new BlockManager(scratchManager, ownsStream: false);
            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var expected = new BlockLocationIndex(store);
            Ok(expected.PutBatch(compactor.CopiedBlocks.Select(cb => new BlockLocation
            {
                BlockId = cb.BlockId,
                Offset = cb.NewOffset,
                TotalBlockLength = cb.NewLength,
            }).ToArray()));

            var report = Require(LocationIndexRegenerator.VerifyMatchesLiveTree(compactor.NewLocationIndex!, expected));
            Assert.True(report.Matches, report.Mismatch);
            Assert.Equal(compactor.CopiedBlocks.Count, report.ComparedEntries);
        }
        finally
        {
            Delete(scratch);
        }
    }

    /// <summary>
    /// Rebuilds a live <see cref="BlockLocationIndex"/> over <paramref name="manager"/>
    /// from an emitted <see cref="LocationIndexRoot"/> descriptor — the same offset-root
    /// reconstruction a clean open performs, exercised here directly against the side
    /// file to prove the rebuilt tree is durable and self-describing.
    /// </summary>
    private static BlockLocationIndex ReconstructLocationIndex(BlockManager manager, LocationIndexRoot root)
    {
        var reference = new byte[BTreeNodeRef.OffsetReferenceSize];
        BinaryPrimitives.WriteInt64LittleEndian(reference, root.RootOffset);
        var btreeRoot = new BTreeRoot
        {
            RootRef = new BTreeNodeRef
            {
                Addressing = BTreeChildAddressing.Offset,
                Reference = reference,
                NodeHash = root.RootHash,
            },
            Height = root.TreeHeight,
            EntryCount = root.EntryCount,
        };
        var store = new BTreeNodeStore(manager, blockIdResolver: null);
        return new BlockLocationIndex(store, initialRoot: btreeRoot);
    }

    /// <summary>
    /// Facts about a generated clean v3 source: its FileId, the full live set (data
    /// blocks plus the folder-tree and metadata root blocks — all in the location
    /// index so compaction copies and remaps them), the two remapped roots' source
    /// offsets, and the committed Checkpoint sequence.
    /// </summary>
    private sealed record SourceFile(
        byte[] FileId,
        IReadOnlyList<BlockLocation> LiveBlocks,
        byte[] FolderBlockId,
        long FolderSourceOffset,
        byte[] MetadataBlockId,
        long MetadataSourceOffset,
        ulong CheckpointSequence);

    /// <summary>
    /// Builds a clean v3 file at <see cref="_path"/> whose named roots are themselves
    /// live blocks in the BlockLocationIndex, so a compaction can copy them and remap
    /// their Checkpoint pointers — unlike the fault-injection builder, which keeps the
    /// folder block out of the index.
    /// </summary>
    private SourceFile BuildSource(int dataCount, int dataSize = 300)
    {
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();
        var live = new List<BlockLocation>();
        BlockLocation folder, metadata;
        ulong sequence;
        CheckpointRootPointer checkpointPointer;

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            var buf = new byte[dataSize];
            for (int i = 0; i < dataCount; i++)
            {
                Array.Fill(buf, (byte)(i & 0xFF));
                live.Add(Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, buf)));
            }

            // The named roots are real live blocks IN the location index.
            folder = Require(manager.Append(BlockType.FolderTree, PayloadEncoding.RawBytes, new byte[64]));
            live.Add(folder);
            metadata = Require(manager.Append(BlockType.Metadata, PayloadEncoding.RawBytes, new byte[48]));
            live.Add(metadata);

            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
            Ok(index.PutBatch(live));
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
                LiveBlockCount = live.Count,
                LiveByteCount = SourceLiveByteCount,
                DeadByteCount = SourceDeadByteCount,
            }));
            sequence = writer.LastSequence!.Value;
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

        return new SourceFile(
            fileId, live, folder.BlockId, folder.Offset, metadata.BlockId, metadata.Offset, sequence);
    }
}
