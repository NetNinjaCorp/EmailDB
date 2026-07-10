using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="Compactor.FinalizeAndSwap"/> and
/// <see cref="Compactor.CleanupLeftoverSideFile"/> (US-EMDB-89-8,
/// EmailDB_FileFormat_Spec.md Section 11.2 step 5, docs/Compaction.md Section 2):
/// the final slice of side-file compaction — finalizing the side file's superblocks,
/// fsyncing it, atomically renaming it over the source, and fsyncing the directory,
/// plus discarding a leftover side file on the next open. The acceptance criteria
/// exercised here:
/// <list type="bullet">
/// <item>a completed swap yields the complete NEW file at the source path — it opens
/// clean (zero scanning) and its rebuilt index resolves and re-reads every live block
/// at its new offset — and the side file is gone;</item>
/// <item>a compaction interrupted before the rename leaves the complete OLD file
/// untouched at the source path plus a stale <c>.compact</c> side file;</item>
/// <item>that leftover side file is deleted on the next open (both via
/// <see cref="Compactor.CleanupLeftoverSideFile"/> directly and via the real
/// <see cref="EmailManager.Open"/> path), leaving the source intact;</item>
/// <item>the finalized superblock commits the fresh Checkpoint and marks the file clean,
/// continuing the FileId and superblock sequence;</item>
/// <item>the guard rails: rebuild-first and run-once.</item>
/// </list>
/// Every file handle and <see cref="Compactor"/> is disposed before a file is reopened,
/// so the tests pass in isolation and in the full parallel suite.
/// </summary>
public class CompactorSwapTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-swap-{Guid.NewGuid():N}.emdb");

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

    // -------------------------------------------------- Complete new file after swap

    [Fact]
    public void Completed_swap_replaces_the_source_with_the_compacted_file_and_removes_the_side_file()
    {
        var source = BuildSource(dataCount: 12);

        IReadOnlyList<CopiedBlock> copied;
        Checkpoint fresh;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            fresh = Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            copied = compactor.CopiedBlocks.ToArray();
            Ok(compactor.FinalizeAndSwap());
            Assert.True(compactor.HasSwapped);
        }

        // The side file was renamed over the source — it no longer exists, and the
        // source path now holds the compacted file.
        Assert.False(File.Exists(SideFile), "the side file must be renamed away by the swap");
        Assert.True(File.Exists(_path));

        // The source path now opens clean with ZERO scanning: the finalized superblock's
        // hint reaches the fresh Checkpoint and the rebuilt location index reconstructs.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;

        // The compacted file's Checkpoint is the one compaction wrote.
        Assert.Equal(fresh.CheckpointSequence, state.Checkpoint.Checkpoint.CheckpointSequence);
        Assert.Equal(source.FileId, state.Superblock.FileId);

        // Every live block resolves at its NEW offset in the compacted file and re-reads
        // intact (Read fully verifies header/payload checksums) with unchanged content —
        // the "complete new file" is self-consistent, not a hybrid.
        Assert.Equal(copied.Count, state.LocationIndex.Count);
        foreach (var cb in copied)
        {
            var lookup = Require(state.LocationIndex.Lookup(cb.BlockId));
            Assert.True(lookup.Found, $"copied block {Convert.ToHexString(cb.BlockId)} missing after swap");
            Assert.Equal(cb.NewOffset, lookup.Offset);

            var block = Require(state.BlockManager.Read(lookup.Offset));
            Assert.Equal(cb.BlockId, block.Header.BlockId);
            if (source.Payloads.TryGetValue(Convert.ToHexString(cb.BlockId), out var expected))
                Assert.Equal(expected, block.Payload);
        }
    }

    [Fact]
    public void Finalized_superblock_marks_the_file_clean_and_commits_the_fresh_checkpoint()
    {
        var source = BuildSource(dataCount: 6);

        CheckpointRootPointer checkpointPointer;
        ulong preSwapSuperblockSequence = ReadSuperblock().SuperblockSequence;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            checkpointPointer = compactor.NewCheckpointPointer!;
            Ok(compactor.FinalizeAndSwap());
        }

        var sb = ReadSuperblock();
        Assert.Equal((byte)1, sb.CleanShutdown);
        Assert.Equal(source.FileId, sb.FileId);
        // The committed hint is the fresh Checkpoint the rebuild wrote.
        Assert.Equal(checkpointPointer.Offset, sb.LastCheckpointOffset);
        Assert.Equal(checkpointPointer.BlockId, sb.LastCheckpointBlockId);
        // The superblock sequence continued past the original file's (monotonic).
        Assert.True(sb.SuperblockSequence > preSwapSuperblockSequence,
            "the finalized superblock sequence must continue past the source's");
    }

    // ---------------------------------------- Interrupted-before-rename leaves old file

    [Fact]
    public void Compaction_abandoned_before_the_swap_leaves_the_complete_old_file_and_a_stale_side_file()
    {
        var source = BuildSource(dataCount: 10);
        var originalBytes = File.ReadAllBytes(_path);

        // Run the copy + rebuild but NOT the swap, then dispose — exactly the on-disk
        // state a crash before FinalizeAndSwap's rename leaves behind.
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            Assert.False(compactor.HasSwapped);
        }

        // The source file is byte-for-byte unchanged (compaction only ever wrote the side
        // file), and the abandoned side file is still present.
        Assert.True(File.Exists(SideFile), "the abandoned side file should remain on disk");
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));

        // The complete old file still opens clean at its original Checkpoint.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var opened = Require(CleanOpener.Open(stream));
            Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
            using var state = opened.State!;
            Assert.Equal(source.CheckpointSequence, state.Checkpoint.Checkpoint.CheckpointSequence);
        }
    }

    [Fact]
    public void Leftover_side_file_is_deleted_on_next_open_leaving_the_source_intact()
    {
        BuildSource(dataCount: 8);
        var originalBytes = File.ReadAllBytes(_path);

        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
        }
        Assert.True(File.Exists(SideFile));

        // The open-path cleanup discards the stale side file and leaves the source intact.
        Ok(Compactor.CleanupLeftoverSideFile(_path));
        Assert.False(File.Exists(SideFile), "the stale side file must be deleted on open");
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));

        // Idempotent: cleaning up again when there is nothing to clean succeeds.
        Ok(Compactor.CleanupLeftoverSideFile(_path));
    }

    [Fact]
    public void EmailManager_open_discards_a_leftover_side_file()
    {
        // A minimal but real EmailManager file: create, add an email, close cleanly.
        var fileId = MakeManagerFile();

        // Simulate a compaction interrupted before its rename by dropping a stale side file
        // next to the manager file (its exact bytes are irrelevant — it is stale by name).
        File.WriteAllBytes(SideFile, new byte[] { 1, 2, 3, 4 });
        Assert.True(File.Exists(SideFile));

        using (var manager = Require(EmailManager.Open(_path)))
        {
            Assert.Equal(fileId, manager.Superblock.FileId);
        }

        Assert.False(File.Exists(SideFile), "EmailManager.Open must delete the leftover side file");
    }

    // ------------------------------------------------------------- Guard rails

    [Fact]
    public void Swap_requires_the_rebuild_to_have_run_first()
    {
        BuildSource(dataCount: 4);

        using var compactor = Require(Compactor.Begin(_path));
        Require(compactor.CopyLiveBlocks());
        var swap = compactor.FinalizeAndSwap();
        Assert.True(swap.IsFailure, "the swap must refuse to run before the rebuild");
        Assert.False(compactor.HasSwapped);
    }

    [Fact]
    public void Swap_runs_once_per_compaction()
    {
        BuildSource(dataCount: 6);

        using var compactor = Require(Compactor.Begin(_path));
        Require(compactor.CopyLiveBlocks());
        Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
        Ok(compactor.FinalizeAndSwap());

        var second = compactor.FinalizeAndSwap();
        Assert.True(second.IsFailure, "a second swap must be refused");
    }

    // ================================================================= Helpers

    private Superblock ReadSuperblock()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        return Require(sbManager.Load());
    }

    private const long SourceLiveByteCount = 424_242;

    /// <summary>
    /// Facts about a generated clean v3 source used by the swap tests.
    /// </summary>
    private sealed record SourceFile(
        byte[] FileId,
        IReadOnlyList<BlockLocation> LiveBlocks,
        IReadOnlyDictionary<string, byte[]> Payloads,
        ulong CheckpointSequence);

    /// <summary>
    /// Builds a clean v3 file at <see cref="_path"/> whose named roots (folder tree,
    /// metadata) are live blocks in the BlockLocationIndex — the same realistic shape
    /// <see cref="CompactorRebuildTests"/> uses — so a full compaction can copy, rebuild,
    /// and swap it. Returns each live block's payload so the swap test can prove the
    /// compacted file re-reads identical content.
    /// </summary>
    private SourceFile BuildSource(int dataCount, int dataSize = 200)
    {
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0x50 + i)).ToArray();
        var live = new List<BlockLocation>();
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        ulong sequence;
        CheckpointRootPointer checkpointPointer;

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            for (int i = 0; i < dataCount; i++)
            {
                var buf = new byte[dataSize];
                Array.Fill(buf, (byte)(i & 0xFF));
                var loc = Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, buf));
                live.Add(loc);
                payloads[Convert.ToHexString(loc.BlockId)] = buf;
            }

            var folderBuf = new byte[64];
            Array.Fill(folderBuf, (byte)0xF0);
            var folder = Require(manager.Append(BlockType.FolderTree, PayloadEncoding.RawBytes, folderBuf));
            live.Add(folder);
            payloads[Convert.ToHexString(folder.BlockId)] = folderBuf;

            var metadataBuf = new byte[48];
            Array.Fill(metadataBuf, (byte)0x0F);
            var metadata = Require(manager.Append(BlockType.Metadata, PayloadEncoding.RawBytes, metadataBuf));
            live.Add(metadata);
            payloads[Convert.ToHexString(metadata.BlockId)] = metadataBuf;

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
                DeadByteCount = 100_000,
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

        return new SourceFile(fileId, live, payloads, sequence);
    }

    /// <summary>
    /// Creates a real <see cref="EmailManager"/> file at <see cref="_path"/> and cleanly
    /// closes it, returning its FileId so the leftover-cleanup test can reopen it.
    /// </summary>
    private byte[] MakeManagerFile()
    {
        using var manager = Require(EmailManager.Create(_path));
        return (byte[])manager.Superblock.FileId.Clone();
    }
}
