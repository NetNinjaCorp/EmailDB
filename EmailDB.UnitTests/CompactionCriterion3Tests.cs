using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Acceptance-criterion coverage for US-EMDB-89:
/// <b>"Kill at every step of the swap yields either the complete old file or the
/// complete new file."</b>
///
/// <para>The swap is <see cref="Compactor.FinalizeAndSwap"/>. Reading its actual code,
/// the ordered steps a crash can fall between are:</para>
/// <list type="number">
///   <item><b>S0</b> — before finalize: copy+rebuild done, <c>FinalizeAndSwap</c> not
///   entered (or entered but nothing written). The side file's superblocks are still the
///   fresh, in-progress pair (<c>CleanShutdown = 0</c>, no Checkpoint hint).</item>
///   <item><b>S1–S3</b> — inside <c>WriteFinalizedSuperblocks</c>: slot A written, slot B
///   written, whole side file fsynced. A crash mid-way leaves a <i>torn</i> side-file
///   superblock.</item>
///   <item><b>S3–S5</b> — side file fully finalized+fsynced (internally a complete, clean
///   compacted file) but <c>File.Move</c> has NOT run yet.</item>
///   <item><b>S5</b> — the atomic rename itself. On this POSIX platform <c>rename(2)</c>
///   (via <see cref="File.Move(string,string,bool)"/>) is all-or-nothing: it cannot be
///   torn, so it has exactly two outcomes — not done (old file) or done (new file).</item>
///   <item><b>S5–S7</b> — rename done, directory fsync pending/among/after. The new file is
///   already at the source path; the directory fsync only makes the rename survive a power
///   loss (which, if lost, reverts to the complete old file).</item>
/// </list>
///
/// <para><b>The load-bearing invariant.</b> A <c>.compact</c> side file is never "current"
/// until the atomic rename makes it so — so its internal state at every pre-rename kill
/// point (S0, S1–S3, S3–S5) is irrelevant: the next open discards it wholesale by name
/// (<see cref="Compactor.CleanupLeftoverSideFile"/>) and the source is the untouched
/// complete old file. Only the atomic rename (S5) commits, and it is torn-proof. So every
/// reachable kill point yields the complete old file or the complete new file — never a
/// hybrid.</para>
///
/// <para>There is no in-process crash-injection hook (and <c>Compactor.cs</c> is owned by
/// another slice), so each kill point is verified by reconstructing the <i>exact</i>
/// on-disk pair that crash would leave — an untouched source plus a side file in the
/// corresponding state — and asserting the open outcome. The already-shipped
/// <see cref="CompactorSwapTests"/> covers the S0 abandon
/// (<c>Compaction_abandoned_before_the_swap_leaves_the_complete_old_file_and_a_stale_side_file</c>)
/// and the completed-swap new file
/// (<c>Completed_swap_replaces_the_source_with_the_compacted_file_and_removes_the_side_file</c>);
/// this file adds the missing S1–S3 torn-finalize and S3–S5 finalized-but-unrenamed kill
/// points and pins the S5 atomicity reasoning end-to-end.</para>
///
/// <para>Every handle and <see cref="Compactor"/> is disposed before a file is reopened,
/// so these pass in isolation and in the full parallel suite; no assertion is order- or
/// offset-layout-dependent across tests.</para>
/// </summary>
public class CompactionCriterion3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-kill-{Guid.NewGuid():N}.emdb");

    // A sibling shard used to mint a genuine finalized side file (see the S3–S5 test).
    private readonly string _other = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-kill-other-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    public void Dispose()
    {
        Delete(_path);
        Delete(SideFile);
        Delete(_other);
        Delete(_other + Compactor.SideFileSuffix);
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

    // ============================================================ S0: before finalize

    /// <summary>
    /// Kill point S0 — crash after copy+rebuild but before <c>WriteFinalizedSuperblocks</c>
    /// runs. The source is byte-for-byte untouched (opens clean at its ORIGINAL Checkpoint)
    /// and the leftover side file still carries the fresh, in-progress superblock pair
    /// (<c>CleanShutdown = 0</c>, no Checkpoint hint) — it was never finalized, let alone
    /// renamed, so the next open discards it. (Complements
    /// <see cref="CompactorSwapTests"/>' abandon test by additionally proving the leftover's
    /// superblock is non-finalized, distinguishing this kill point from S3–S5.)
    /// </summary>
    [Fact]
    public void Kill_before_finalize_leaves_the_old_file_and_a_nonfinalized_side_file()
    {
        var source = BuildSource(dataCount: 10);
        var originalBytes = File.ReadAllBytes(_path);

        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            // No FinalizeAndSwap — this is exactly the on-disk state an S0 crash leaves.
            Assert.False(compactor.HasSwapped);
        }

        // Source untouched and opens clean at the ORIGINAL checkpoint.
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));
        AssertOpensAsOldFile(source);

        // The leftover side file is present and NOT finalized (fresh, in-progress slots).
        Assert.True(File.Exists(SideFile));
        var sideSb = ReadSuperblock(SideFile);
        Assert.Equal((byte)0, sideSb.CleanShutdown);

        // The next open discards it, leaving the complete old file.
        Ok(Compactor.CleanupLeftoverSideFile(_path));
        Assert.False(File.Exists(SideFile));
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));
    }

    // ==================================================== S1–S3: torn finalize of side

    /// <summary>
    /// Kill point S1–S3 — crash WHILE <c>WriteFinalizedSuperblocks</c> was rewriting the
    /// side file's two superblock slots (e.g. slot A finalized, slot B half-written, no
    /// fsync). That leaves a <i>torn</i> side-file superblock. Because a <c>.compact</c>
    /// file is only current after the atomic rename, its torn internal state is never
    /// inspected: the source is the untouched complete old file and the torn side file is
    /// discarded by name. Reconstructed by dropping a garbage-superblock <c>.compact</c>
    /// next to an untouched source.
    /// </summary>
    [Fact]
    public void Kill_mid_finalize_with_a_torn_side_file_superblock_is_discarded()
    {
        var source = BuildSource(dataCount: 6);
        var originalBytes = File.ReadAllBytes(_path);

        // A half-written / corrupt superblock region — not a loadable side file.
        var torn = new byte[8192];
        Array.Fill(torn, (byte)0xAB);
        File.WriteAllBytes(SideFile, torn);

        // The source is untouched and opens clean; the torn side file is never consulted.
        AssertOpensAsOldFile(source);

        Ok(Compactor.CleanupLeftoverSideFile(_path));
        Assert.False(File.Exists(SideFile), "the torn side file must be discarded by name");
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));
    }

    // ============================================ S3–S5: finalized side, not yet renamed

    /// <summary>
    /// Kill point S3–S5 — the dangerous one. <c>WriteFinalizedSuperblocks</c> has finished
    /// and fsynced the side file, so it is internally a <i>complete, clean</i> compacted
    /// file (<c>CleanShutdown = 1</c>, fresh-Checkpoint hint, same FileId) — but the process
    /// died BEFORE <c>File.Move</c>. It must STILL be discarded: it was never renamed, so it
    /// was never current (spec Section 11.2). Reconstructs the exact pair by compacting a
    /// byte-identical copy of the source (same FileId) to completion and dropping the
    /// resulting genuine finalized file at the <c>.compact</c> path next to an untouched
    /// source.
    /// </summary>
    [Fact]
    public void Kill_after_finalize_before_rename_discards_the_finalized_side_file_and_keeps_the_old_file()
    {
        var source = BuildSource(dataCount: 9);
        var originalBytes = File.ReadAllBytes(_path);

        // Mint a genuine finalized side file: compact a same-FileId copy to completion,
        // then place its bytes at the .compact path. This is precisely what
        // WriteFinalizedSuperblocks + fsync leaves just before the (never-run) rename.
        File.Copy(_path, _other);
        Checkpoint newFileCheckpoint;
        using (var c = Require(Compactor.Begin(_other)))
        {
            Require(c.CopyLiveBlocks());
            newFileCheckpoint = Require(c.RebuildLocationIndexAndWriteCheckpoint());
            Ok(c.FinalizeAndSwap());
        }
        File.Copy(_other, SideFile, overwrite: true);

        // Sanity: the minted side file really is a complete, clean, newer compacted file
        // (CleanShutdown = 1, same FileId, continued Checkpoint sequence) — the "looks
        // perfect but was never renamed" case.
        var sideSb = ReadSuperblock(SideFile);
        Assert.Equal((byte)1, sideSb.CleanShutdown);
        Assert.Equal(source.FileId, sideSb.FileId);
        Assert.True(newFileCheckpoint.CheckpointSequence > source.CheckpointSequence);

        // The source path is byte-for-byte the OLD file and opens clean at its ORIGINAL
        // Checkpoint — the finalized side file next to it is not adopted or merged.
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));
        AssertOpensAsOldFile(source);

        // The next open discards the finalized-but-unrenamed side file wholesale.
        Ok(Compactor.CleanupLeftoverSideFile(_path));
        Assert.False(File.Exists(SideFile), "a finalized-but-unrenamed side file must be discarded");
        Assert.Equal(originalBytes, File.ReadAllBytes(_path));
    }

    // ================================= S5–S7: rename done (during/after directory fsync)

    /// <summary>
    /// Kill points S5–S7 — any crash AT the rename (atomic on POSIX, never torn) or AFTER it
    /// (before/among/after the directory fsync) leaves the complete NEW file at the source
    /// path. Runs the swap to completion and proves the source is a wholly self-consistent
    /// compacted file: it opens clean with ZERO scanning (the finalized superblock's hint
    /// reaches the fresh Checkpoint), its index holds EXACTLY the copied live set — no dead
    /// blocks bled through — and every copied block resolves at its NEW offset and re-reads
    /// with verified header/payload checksums and identical content. The side file is gone.
    /// </summary>
    [Fact]
    public void Kill_after_rename_yields_the_complete_new_file_with_no_hybrid()
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

        Assert.False(File.Exists(SideFile), "a completed swap renames the side file away");
        Assert.True(File.Exists(_path));

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;

        // The committed hint reaches the fresh Checkpoint compaction wrote.
        Assert.Equal(fresh.CheckpointSequence, state.Checkpoint.Checkpoint.CheckpointSequence);
        Assert.Equal(source.FileId, state.Superblock.FileId);

        // Exactly the live set — no extra/dead blocks indexed (no hybrid) — each at its NEW
        // offset, each re-reading intact with unchanged content.
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

    // ================================ S5 endpoints: the rename has exactly two outcomes

    /// <summary>
    /// Pins the S5 atomicity reasoning end-to-end in one run: BEFORE the swap the source is
    /// byte-identical to the old file and opens as the old file; AFTER the swap it opens as
    /// the new file and the side file is gone. Because POSIX <c>rename(2)</c> is atomic, no
    /// third (partial/hybrid) state at the source path is reachable between these two —
    /// every kill collapses onto one endpoint or the other.
    /// </summary>
    [Fact]
    public void Rename_has_exactly_two_outcomes_complete_old_then_complete_new()
    {
        var source = BuildSource(dataCount: 8);
        var oldBytes = File.ReadAllBytes(_path);

        using var compactor = Require(Compactor.Begin(_path));
        Require(compactor.CopyLiveBlocks());
        var fresh = Require(compactor.RebuildLocationIndexAndWriteCheckpoint());

        // Pre-rename endpoint: source is still exactly the old file.
        Assert.Equal(oldBytes, File.ReadAllBytes(_path));
        Assert.True(File.Exists(SideFile));
        AssertOpensAsOldFile(source);

        // The single atomic commit point.
        Ok(compactor.FinalizeAndSwap());

        // Post-rename endpoint: source is the new file, side file gone. No intermediate
        // was ever observable — rename(2) is all-or-nothing.
        Assert.False(File.Exists(SideFile));
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;
        Assert.Equal(fresh.CheckpointSequence, state.Checkpoint.Checkpoint.CheckpointSequence);
        Assert.NotEqual(source.CheckpointSequence, state.Checkpoint.Checkpoint.CheckpointSequence);
    }

    // ================================================================= Helpers

    /// <summary>Opens <see cref="_path"/> clean and asserts it is the OLD file (original Checkpoint).</summary>
    private void AssertOpensAsOldFile(SourceFile source)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;
        Assert.Equal(source.CheckpointSequence, state.Checkpoint.Checkpoint.CheckpointSequence);
        Assert.Equal(source.FileId, state.Superblock.FileId);
    }

    private static Superblock ReadSuperblock(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        return Require(sbManager.Load());
    }

    private const long SourceLiveByteCount = 424_242;

    /// <summary>Facts about a generated clean v3 source, mirroring <see cref="CompactorSwapTests"/>.</summary>
    private sealed record SourceFile(
        byte[] FileId,
        IReadOnlyList<BlockLocation> LiveBlocks,
        IReadOnlyDictionary<string, byte[]> Payloads,
        ulong CheckpointSequence);

    /// <summary>
    /// Builds a clean v3 file at <see cref="_path"/> whose named roots (folder tree,
    /// metadata) are live blocks in the BlockLocationIndex — the realistic shape a full
    /// compaction can copy, rebuild, and swap. Returns each live block's payload so tests
    /// can prove the compacted file re-reads identical content. (Same construction as
    /// <see cref="CompactorSwapTests"/>.)
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
}
