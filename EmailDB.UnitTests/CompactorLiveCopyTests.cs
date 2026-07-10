using EmailDB.Format;
using EmailDB.Format.V3;
using EmailDB.UnitTests.FaultInjection;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="Compactor"/> (US-EMDB-89-6, EmailDB_FileFormat_Spec.md
/// Section 11.2, docs/Compaction.md Section 2): the live-block walk and side-file
/// copy — the first slice of side-file compaction. Each test builds a realistic
/// clean v3 file (a padded block stream, a committed BlockLocationIndex over the
/// live blocks, a folder root, a Checkpoint, and a dual-slot superblock) with
/// <see cref="V3TestFileBuilder"/>, then compacts it. Covers:
/// <list type="bullet">
/// <item>the walk enumerates exactly the live BlockLocationIndex entries, in
/// ascending BlockId order, with the right offsets/lengths;</item>
/// <item>the copy reproduces exactly those blocks in the side file with identical
/// BlockIds and byte-for-byte identical content — and copies neither the location
/// index's own nodes nor the old Checkpoint;</item>
/// <item>the side file gets fresh dual-slot superblocks: same FileId, continued
/// SuperblockSequence, no committed Checkpoint hint yet (CleanShutdown = 0);</item>
/// <item>an encrypted live block is copied verbatim without any key;</item>
/// <item>the old→new offset map (<see cref="Compactor.TryGetNewLocation"/>) the
/// follow-on slices consume;</item>
/// <item>the BlockId cross-check that rejects a corrupt/misdirected index entry.</item>
/// </list>
/// </summary>
public class CompactorLiveCopyTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-{Guid.NewGuid():N}.emdb");

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
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static BlockManager OpenManager(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return new BlockManager(stream, ownsStream: true);
    }

    // -------------------------------------------------------------- Walk

    [Fact]
    public void WalkLiveBlocks_enumerates_exactly_the_live_index_entries_in_ascending_order()
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(16).Build(_path);

        using var compactor = Require(Compactor.Begin(_path));
        var walk = Compactor.WalkLiveBlocks(compactor.SourceLocationIndex);
        Ok(walk);

        // Exactly the live set: every data block, nothing else (folder block, index
        // nodes, and the Checkpoint are not in the location index).
        Assert.Equal(file.DataBlocks.Count, walk.Value.Count);
        Assert.Equal(
            compactor.SourceCheckpoint.Checkpoint.LiveBlockCount, walk.Value.Count);

        // Ascending unsigned-lexicographic BlockId order.
        for (int i = 1; i < walk.Value.Count; i++)
            Assert.True(
                walk.Value[i - 1].BlockId.AsSpan().SequenceCompareTo(walk.Value[i].BlockId) < 0,
                "live blocks must come back in ascending BlockId order");

        // Each walked entry matches the source data block's actual location.
        var byId = file.DataBlocks.ToDictionary(b => Convert.ToHexString(b.BlockId));
        foreach (var walked in walk.Value)
        {
            var source = byId[Convert.ToHexString(walked.BlockId)];
            Assert.Equal(source.Offset, walked.Offset);
            Assert.Equal(source.TotalBlockLength, walked.TotalBlockLength);
        }
    }

    // -------------------------------------------------------------- Copy

    [Fact]
    public void CopyLiveBlocks_copies_exactly_the_live_blocks_with_identical_ids_and_content()
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(16, size: 300).Build(_path);

        int copiedCount;
        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            var result = compactor.CopyLiveBlocks();
            Ok(result);
            copiedCount = result.Value;
            copied = compactor.CopiedBlocks.ToArray();
        }

        // Exactly the live set was copied — not the folder block, index nodes, or Checkpoint.
        Assert.Equal(file.DataBlocks.Count, copiedCount);
        Assert.Equal(
            file.DataBlocks.Select(b => Convert.ToHexString(b.BlockId)).OrderBy(h => h),
            copied.Select(c => Convert.ToHexString(c.BlockId)).OrderBy(h => h));

        // The side file really has fewer blocks than the source (it dropped the index
        // nodes and the Checkpoint), proving the walk did not copy the whole file.
        using (var sourceScan = OpenManager(_path))
        using (var sideScan = OpenManager(SideFile))
        {
            var sourceBlocks = Require(sourceScan.ScanForward()).Blocks.Count;
            var sideBlocks = Require(sideScan.ScanForward()).Blocks.Count;
            Assert.Equal(copiedCount, sideBlocks);
            Assert.True(sideBlocks < sourceBlocks,
                $"side file ({sideBlocks} blocks) must be smaller than the source ({sourceBlocks} blocks)");
        }

        // Every copied block is byte-for-byte identical to its source at the recorded offsets.
        using (var srcMgr = OpenManager(_path))
        using (var dstMgr = OpenManager(SideFile))
        {
            foreach (var cb in copied)
            {
                var src = srcMgr.Read(cb.SourceOffset);
                Ok(src);
                var dst = dstMgr.Read(cb.NewOffset);
                Ok(dst);

                Assert.Equal(cb.BlockId, dst.Value.Header.BlockId);
                Assert.Equal(src.Value.Header.BlockId, dst.Value.Header.BlockId);
                Assert.Equal(src.Value.Header.Type, dst.Value.Header.Type);
                Assert.Equal(src.Value.Header.Encoding, dst.Value.Header.Encoding);
                Assert.Equal(src.Value.Header.Compression, dst.Value.Header.Compression);
                Assert.Equal(src.Value.Header.Flags, dst.Value.Header.Flags);
                Assert.Equal(src.Value.Header.KeyEpoch, dst.Value.Header.KeyEpoch);
                Assert.Equal(src.Value.Payload, dst.Value.Payload);
                Assert.Equal(cb.NewLength, dst.Value.Header.PayloadLength + BlockSerializerOverhead);
            }
        }
    }

    // BlockSerializer fixed overhead is 96 bytes (spec Section 4); a copied block's
    // total length is its payload plus that overhead.
    private const int BlockSerializerOverhead = 96;

    // ---------------------------------------------------- Fresh superblocks

    [Fact]
    public void SideFile_gets_fresh_superblocks_with_same_fileid_and_continued_sequence()
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(8).Build(_path);

        ulong sourceSequence;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        using (var sb = new SuperblockManager(stream, ownsStream: true))
        {
            sourceSequence = Require(sb.Load()).SuperblockSequence;
        }

        ulong continued;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            continued = compactor.ContinuedSuperblockSequence;
            Ok(compactor.CopyLiveBlocks());
        }

        Assert.Equal(sourceSequence + 2, continued);

        using var sideStream = new FileStream(SideFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var sideSb = new SuperblockManager(sideStream, ownsStream: true);
        var loaded = Require(sideSb.Load());

        Assert.Equal(file.FileId, loaded.FileId);
        Assert.Equal(sourceSequence + 2, loaded.SuperblockSequence);
        // In-progress side file: no committed Checkpoint hint, not marked clean.
        Assert.Equal(0, loaded.CleanShutdown);
        Assert.True(loaded.LastCheckpointBlockId.All(b => b == 0),
            "a freshly copied side file has no committed Checkpoint hint yet");
    }

    // ----------------------------------------------------- Encrypted block

    [Fact]
    public void Encrypted_live_block_is_copied_verbatim_without_keys()
    {
        new V3TestFileBuilder().WithFillerBlocks(8).WithEncryptedBlock().Build(_path);

        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Ok(compactor.CopyLiveBlocks());
            copied = compactor.CopiedBlocks.ToArray();
        }

        using var srcMgr = OpenManager(_path);
        using var dstMgr = OpenManager(SideFile);

        int encryptedSeen = 0;
        foreach (var cb in copied)
        {
            var dst = Require(dstMgr.Read(cb.NewOffset));
            if (!dst.Header.IsEncrypted)
                continue;

            encryptedSeen++;
            var src = Require(srcMgr.Read(cb.SourceOffset));
            Assert.True(src.Header.IsEncrypted);
            Assert.Equal(src.Header.KeyEpoch, dst.Header.KeyEpoch);
            Assert.NotEqual(0, dst.Header.KeyEpoch);
            // The on-disk (ciphertext) payload is copied verbatim — no key involved.
            Assert.Equal(src.Payload, dst.Payload);
        }

        Assert.Equal(1, encryptedSeen);
    }

    // ------------------------------------------------ Old→new offset map

    [Fact]
    public void TryGetNewLocation_maps_copied_blockids_and_rejects_unknown()
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(10).Build(_path);

        using var compactor = Require(Compactor.Begin(_path));
        Ok(compactor.CopyLiveBlocks());

        var newByOffset = compactor.CopiedBlocks.ToDictionary(c => Convert.ToHexString(c.BlockId));
        foreach (var data in file.DataBlocks)
        {
            Assert.True(compactor.TryGetNewLocation(data.BlockId, out long newOffset, out long newLength));
            var expected = newByOffset[Convert.ToHexString(data.BlockId)];
            Assert.Equal(expected.NewOffset, newOffset);
            Assert.Equal(expected.NewLength, newLength);
            // The new offset lies inside the side file's block stream (after the superblocks).
            Assert.True(newOffset >= BlockManager.DefaultFirstBlockOffset);
        }

        var unknown = new byte[16];
        unknown[0] = 0xFF;
        Assert.False(compactor.TryGetNewLocation(unknown, out _, out _));
    }

    // ------------------------------------------------------- Guard rails

    [Fact]
    public void CopyLiveBlocks_runs_once_per_compaction()
    {
        new V3TestFileBuilder().WithFillerBlocks(4).Build(_path);

        using var compactor = Require(Compactor.Begin(_path));
        Ok(compactor.CopyLiveBlocks());

        var second = compactor.CopyLiveBlocks();
        Assert.True(second.IsFailure, "a second copy pass must be refused");
    }

    [Fact]
    public void Begin_rejects_a_file_that_is_not_cleanly_openable()
    {
        // CleanShutdown = 0 means the file needs dirty-open recovery first; compaction
        // must not run over an unrecovered snapshot.
        new V3TestFileBuilder().WithFillerBlocks(4).WithCleanShutdown(0).Build(_path);

        var begun = Compactor.Begin(_path);
        Assert.True(begun.IsFailure, "compaction must refuse a file needing recovery");
        Assert.False(File.Exists(SideFile), "a refused compaction leaves no side file");
    }

    // -------------------------------------------- Copy-primitive guard

    [Fact]
    public void CopyBlocks_rejects_an_index_entry_whose_offset_holds_a_different_block()
    {
        // Two bare files; the "index" claims a BlockId lives at an offset that actually
        // holds a different block — the verbatim-copy guard must catch the mismatch.
        var sourcePath = Path.Combine(Path.GetTempPath(), $"emaildb-src-{Guid.NewGuid():N}.emdb");
        var destPath = Path.Combine(Path.GetTempPath(), $"emaildb-dst-{Guid.NewGuid():N}.emdb");
        try
        {
            BlockLocation first, second;
            using (var srcStream = new FileStream(sourcePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var srcMgr = new BlockManager(srcStream, ownsStream: true))
            {
                first = Require(srcMgr.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[] { 1, 2, 3 }));
                second = Require(srcMgr.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[] { 4, 5, 6 }));
                Ok(srcMgr.Flush());
            }

            using var readMgr = OpenManager(sourcePath);
            using var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var dstMgr = new BlockManager(dstStream, ownsStream: true);

            // Claim `first.BlockId` lives at `second.Offset` — a lie the block at that offset exposes.
            var lying = new[]
            {
                new BlockLocation
                {
                    BlockId = first.BlockId,
                    Offset = second.Offset,
                    TotalBlockLength = second.TotalBlockLength,
                },
            };

            var copy = Compactor.CopyBlocks(readMgr, dstMgr, lying);
            Assert.True(copy.IsFailure, "a BlockId/offset mismatch must fail the copy");
        }
        finally
        {
            Delete(sourcePath);
            Delete(destPath);
        }
    }

    private static T Require<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        return result.Value;
    }
}
