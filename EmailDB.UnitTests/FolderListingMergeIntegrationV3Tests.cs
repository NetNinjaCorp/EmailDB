using System.Text;
using EmailDB.Format;
using EmailDB.Format.Protobuf.V3;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-end verification of story US-EMDB-82's acceptance criterion
/// <b>"Listing merges pending deltas with pages in memory"</b> (docs/Folder_Listing.md Sections 2-3).
/// Where <see cref="FolderListingMergeV3Tests"/> exercises <see cref="FolderListingMerger"/> over
/// hand-built in-memory records, this test drives the whole on-disk listing path with a real
/// <see cref="BlockManager"/>:
/// <list type="number">
///   <item>persist encrypted, Zstd'd <see cref="FolderPage"/>s through a <see cref="FolderPageStore"/>;</item>
///   <item>buffer folder mutations as a <b>two-block</b> <c>FolderDeltaLog</c> chain through
///   <see cref="FolderDeltaLogStore.AppendChained"/>, advancing the directory's
///   <see cref="FolderPageDirectory.HeadDeltaBlockId"/>;</item>
///   <item>persist the directory (BlockType 11) through a <see cref="FolderPageDirectoryStore"/>, flush;</item>
///   <item>then, reading only from disk, resolve and decrypt the pages, walk the delta chain
///   head-to-root via <see cref="FolderPageDirectory.HeadDeltaBlockId"/> /
///   <see cref="FolderDeltaLog.PreviousDeltaBlockId"/>, and hand both to
///   <see cref="FolderListingMerger"/>.</item>
/// </list>
/// The merged listing must reflect the pending Adds (inserted date-descending), Deletes (masking page
/// rows), and FlagChanges (overriding flags) — with the head block winning over the root block for the
/// same email id — proving the deltas are merged with the pages entirely in memory on the read path.
/// </summary>
public class FolderListingMergeIntegrationV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-mergeint-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x70 + i)).ToArray();

    private const ushort ActiveEpoch = 2;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static EpochDekProvider MakeProvider()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 41);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

    private static V3Id IdOf(int seed)
    {
        var raw = new byte[V3Id.Size];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new V3Id(raw);
    }

    private static byte[] UlidOf(int seed)
    {
        var ulid = new byte[UlidGenerator.UlidSize];
        for (var i = 0; i < ulid.Length; i++) ulid[i] = (byte)(seed * 11 + i);
        return ulid;
    }

    private static ListingRecord Record(int seed, long dateTicks, ListingFlags flags = ListingFlags.None) =>
        new()
        {
            EmailHashedId = IdOf(seed),
            ContentBlockId = UlidOf(seed),
            DateTicks = dateTicks,
            Flags = flags,
            MessageSize = 1_000 + seed,
            From = $"sender{seed}@example.com",
            Subject = $"Subject {seed}",
            Preview = $"Preview {seed}",
        };

    [Fact]
    public void Listing_MergesPendingDeltaChain_WithOnDiskPages_InMemory()
    {
        using var provider = MakeProvider();
        using var stream = new FileStream(
            _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var map = new RuntimeBlockOffsetMap();
        using var manager = new BlockManager(stream, offsetMap: map);
        var pageStore = new FolderPageStore(manager, provider, EncryptionPolicy.Default);
        var dirStore = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);
        var deltaStore = new FolderDeltaLogStore(manager, provider, EncryptionPolicy.Default);

        // ---- Persist two encrypted pages (newest-first): page A dates 500/400/300, page B 200/100. ----
        var pageA = FolderPage.FromRecords(new[] { Record(1, 500), Record(2, 400), Record(3, 300) });
        var pageB = FolderPage.FromRecords(new[] { Record(4, 200), Record(5, 100) });

        var locA = pageStore.WritePage(pageA);
        var locB = pageStore.WritePage(pageB);
        Ok(locA);
        Ok(locB);

        var pageEntries = new[]
        {
            new PageEntry(locA.Value.BlockId, dateFrom: 300, dateTo: 500, entryCount: pageA.Count),
            new PageEntry(locB.Value.BlockId, dateFrom: 100, dateTo: 200, entryCount: pageB.Count),
        };

        // ---- Buffer mutations as a two-block delta chain via AppendChained. ----
        // Root block (oldest): add id9 @450, flag id2 Read, delete id5 (@100).
        var folderId = UlidOf(300);
        var dirV0 = FolderPageDirectory.Create(folderId, pageEntries); // no pending delta yet
        var appendRoot = deltaStore.AppendChained(dirV0, new[]
        {
            FolderDeltaEntry.Add(Record(9, 450)),
            FolderDeltaEntry.FlagChange(IdOf(2), ListingFlags.Read),
            FolderDeltaEntry.Delete(IdOf(5)),
        });
        Ok(appendRoot);

        // Head block (newest): re-flag id2 Answered (must beat root's Read), delete id4 (@200),
        // add id10 @50 (new oldest). Chained onto the directory the root append returned.
        var appendHead = deltaStore.AppendChained(appendRoot.Value.Directory, new[]
        {
            FolderDeltaEntry.FlagChange(IdOf(2), ListingFlags.Answered),
            FolderDeltaEntry.Delete(IdOf(4)),
            FolderDeltaEntry.Add(Record(10, 50)),
        });
        Ok(appendHead);

        // Persist the final directory (head advanced to the newest delta block) and flush to disk.
        var dirLoc = dirStore.WriteDirectory(appendHead.Value.Directory);
        Ok(dirLoc);
        var flushed = manager.Flush();
        Assert.True(flushed.IsSuccess, flushed.IsFailure ? flushed.Error : null);

        // ================= READ PATH: everything below comes off disk =================

        // Read the directory (BlockType 11) back.
        var readDir = dirStore.ReadDirectory(dirLoc.Value.Offset);
        Ok(readDir);
        var dir = readDir.Value;
        Assert.Equal(2, dir.PageCount);
        Assert.True(dir.HasPendingDelta);

        // Resolve and decrypt each page, gathering its records.
        var pageRecords = new List<ListingRecord>();
        foreach (var entry in dir.PageEntries)
        {
            Assert.True(map.TryGetLocation(entry.PageBlockId, out var pageLoc));
            var readPage = pageStore.ReadPage(pageLoc!.Offset);
            Ok(readPage);
            pageRecords.AddRange(readPage.Value.Records);
        }
        Assert.Equal(5, pageRecords.Count); // 3 + 2 original page rows

        // Walk the delta chain head-to-root off disk: HeadDeltaBlockId → PreviousDeltaBlockId → ... → zero.
        var chainHeadToRoot = new List<FolderDeltaLog>();
        var cursor = dir.HeadDeltaBlockId;
        while (true)
        {
            Assert.True(map.TryGetLocation(cursor, out var deltaLoc));
            var readDelta = deltaStore.ReadDeltaBlock(deltaLoc!.Offset);
            Ok(readDelta);
            var block = readDelta.Value;
            chainHeadToRoot.Add(block);
            if (!block.HasPrevious)
                break;
            cursor = block.PreviousDeltaBlockId;
        }
        Assert.Equal(2, chainHeadToRoot.Count); // head + root, walked in that order

        // ---- The merge: pending deltas overlaid on the on-disk pages, in memory. ----
        var merged = FolderListingMerger.Merge(pageRecords, chainHeadToRoot);

        // Effective listing newest-first: 500(id1), 450(id9 added), 400(id2), 300(id3), 50(id10 added).
        // id4 and id5 are deleted; id2 carries the head block's Answered flag (not root's Read).
        Assert.Equal(new long[] { 500, 450, 400, 300, 50 }, merged.Select(r => r.DateTicks).ToArray());
        Assert.Contains(merged, r => r.EmailHashedId == IdOf(9) && r.DateTicks == 450);
        Assert.Contains(merged, r => r.EmailHashedId == IdOf(10) && r.DateTicks == 50);
        Assert.DoesNotContain(merged, r => r.EmailHashedId == IdOf(4));
        Assert.DoesNotContain(merged, r => r.EmailHashedId == IdOf(5));

        var id2 = merged.Single(r => r.EmailHashedId == IdOf(2));
        Assert.Equal(ListingFlags.Answered, id2.Flags); // head block won over the root block
        Assert.Equal("Subject 2", id2.Subject);          // only flags changed; row otherwise intact
    }

    // A compact RFC 5322 message; its raw octets are the Tier 3 content whose block must survive
    // a folder move byte-for-byte.
    private static byte[] SampleMime() => Encoding.ASCII.GetBytes(
        "Message-Id: <move-probe@example.com>\r\n" +
        "From: Alice <alice@example.com>\r\n" +
        "To: Bob <bob@example.com>\r\n" +
        "Subject: Move probe\r\n" +
        "Date: Mon, 05 Jul 2021 10:00:00 +0000\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "This email is moved from Inbox to Archive; its content block must not move.\r\n");

    /// <summary>Number of fully-verified blocks of <paramref name="type"/> currently on disk.</summary>
    private static int CountBlocks(BlockManager manager, BlockType type)
    {
        var scan = manager.ScanForward();
        Assert.True(scan.IsSuccess, scan.IsFailure ? scan.Error : null);
        var count = 0;
        foreach (var loc in scan.Value.Blocks)
        {
            var block = manager.Read(loc.Offset);
            Assert.True(block.IsSuccess, block.IsFailure ? block.Error : null);
            if (block.Value.Header.Type == type)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Story US-EMDB-82 acceptance criterion <b>"Move is two delta entries with content blocks
    /// untouched"</b>, proven end-to-end on disk. An email lives in a source folder (page-resident) and
    /// owns a Tier 3 <see cref="EmailContent"/> (BlockType 7) and Tier 2 <see cref="EmailMetadata"/>
    /// (BlockType 10) block. Moving it to a destination folder is expressed as exactly two delta
    /// entries — a <see cref="FolderDeltaOp.Delete"/> appended to the source folder's chain plus a
    /// <see cref="FolderDeltaOp.Add"/> (carrying the full row) appended to the destination folder's
    /// chain. Afterwards:
    /// <list type="bullet">
    ///   <item>the source folder's merged listing no longer contains the email, the destination's does,
    ///   and the moved row's <see cref="ListingRecord.ContentBlockId"/> still points at the same block;</item>
    ///   <item>the Tier 3 content and Tier 2 metadata blocks are neither rewritten (their on-disk bytes
    ///   are byte-identical), duplicated (still exactly one of each), nor moved (same offset) — the move
    ///   touched only the two folders' delta chains and directories.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Move_BetweenFolders_IsDeleteAndAdd_LeavesContentAndMetadataBlocksUntouched()
    {
        using var provider = MakeProvider();
        using var stream = new FileStream(
            _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var map = new RuntimeBlockOffsetMap();
        using var manager = new BlockManager(stream, offsetMap: map);
        var pageStore = new FolderPageStore(manager, provider, EncryptionPolicy.Default);
        var dirStore = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);
        var deltaStore = new FolderDeltaLogStore(manager, provider, EncryptionPolicy.Default);
        var emailStore = new EmailBlockStore(manager, provider, EncryptionPolicy.Default);

        // ---- Persist the moved email's Tier 3 content + Tier 2 metadata blocks. ----
        var raw = SampleMime();
        var contentLoc = emailStore.WriteContent(EmailModelBuilder.BuildContent(raw));
        Ok(contentLoc);
        var contentBlockId = contentLoc.Value.BlockId; // the Tier 3 ULID the listing row points at
        var metaLoc = emailStore.WriteMetadata(EmailModelBuilder.BuildMetadata(raw, contentBlockId));
        Ok(metaLoc);

        var movedId = V3Id.ComputeFromRawContent(raw);
        var moved = new ListingRecord
        {
            EmailHashedId = movedId,
            ContentBlockId = contentBlockId, // points at the Tier 3 block written above
            DateTicks = 400,
            Flags = ListingFlags.Read,
            MessageSize = raw.LongLength,
            From = "alice@example.com",
            Subject = "Move probe",
            Preview = "This email is moved",
        };
        // A second email that stays put in the source folder, so the source listing is non-empty
        // both before and after the move.
        var staysPut = Record(1, 500);

        // ---- Source folder: a page holding both emails. Destination folder: empty. ----
        var sourcePage = FolderPage.FromRecords(new[] { moved, staysPut });
        var sourcePageLoc = pageStore.WritePage(sourcePage);
        Ok(sourcePageLoc);
        var sourceDir0 = FolderPageDirectory.Create(
            UlidOf(400),
            new[] { new PageEntry(sourcePageLoc.Value.BlockId, dateFrom: 400, dateTo: 500, entryCount: sourcePage.Count) });
        var destDir0 = FolderPageDirectory.Create(UlidOf(401), Array.Empty<PageEntry>());

        Ok(dirStore.WriteDirectory(sourceDir0));
        Ok(dirStore.WriteDirectory(destDir0));
        var flushedBefore = manager.Flush();
        Assert.True(flushedBefore.IsSuccess, flushedBefore.IsFailure ? flushedBefore.Error : null);

        // Capture the exact on-disk bytes of the content + metadata blocks before the move.
        var beforeBytes = File.ReadAllBytes(_path);
        byte[] Region(BlockLocation loc) =>
            beforeBytes.Skip((int)loc.Offset).Take((int)loc.TotalBlockLength).ToArray();
        var contentRegionBefore = Region(contentLoc.Value);
        var metaRegionBefore = Region(metaLoc.Value);
        Assert.Equal(1, CountBlocks(manager, BlockType.EmailContent));
        Assert.Equal(1, CountBlocks(manager, BlockType.EmailMetadata));

        // ================= THE MOVE: exactly two delta entries =================
        // One Delete appended to the source folder's chain, one Add to the destination folder's chain.
        var srcAppend = deltaStore.AppendChained(sourceDir0, new[] { FolderDeltaEntry.Delete(movedId) });
        Ok(srcAppend);
        var dstAppend = deltaStore.AppendChained(destDir0, new[] { FolderDeltaEntry.Add(moved) });
        Ok(dstAppend);
        Ok(dirStore.WriteDirectory(srcAppend.Value.Directory));
        Ok(dirStore.WriteDirectory(dstAppend.Value.Directory));
        var flushedAfter = manager.Flush();
        Assert.True(flushedAfter.IsSuccess, flushedAfter.IsFailure ? flushedAfter.Error : null);

        // The move is a single Delete entry in the source chain and a single Add in the destination
        // chain; the Add carries the row verbatim, ContentBlockId included.
        var srcBlock = deltaStore.ReadDeltaBlock(srcAppend.Value.DeltaLocation.Offset);
        var dstBlock = deltaStore.ReadDeltaBlock(dstAppend.Value.DeltaLocation.Offset);
        Ok(srcBlock);
        Ok(dstBlock);
        var srcEntry = Assert.Single(srcBlock.Value.Entries);
        Assert.Equal(FolderDeltaOp.Delete, srcEntry.Op);
        Assert.Equal(movedId, srcEntry.EmailHashedId);
        var dstEntry = Assert.Single(dstBlock.Value.Entries);
        Assert.Equal(FolderDeltaOp.Add, dstEntry.Op);
        Assert.Equal(movedId, dstEntry.EmailHashedId);
        Assert.Equal(contentBlockId, dstEntry.Record!.ContentBlockId);

        // ---- Listing merge (off disk): gone from the source, present in the destination. ----
        List<FolderDeltaLog> WalkChain(byte[] head)
        {
            var chain = new List<FolderDeltaLog>();
            var cursor = head;
            while (true)
            {
                Assert.True(map.TryGetLocation(cursor, out var loc));
                var read = deltaStore.ReadDeltaBlock(loc!.Offset);
                Ok(read);
                chain.Add(read.Value);
                if (!read.Value.HasPrevious)
                    break;
                cursor = read.Value.PreviousDeltaBlockId;
            }
            return chain;
        }

        var mergedSource = FolderListingMerger.Merge(sourcePage.Records, WalkChain(srcAppend.Value.Directory.HeadDeltaBlockId));
        Assert.DoesNotContain(mergedSource, r => r.EmailHashedId == movedId); // deleted from source
        Assert.Contains(mergedSource, r => r.EmailHashedId == staysPut.EmailHashedId); // sibling stays

        var mergedDest = FolderListingMerger.Merge(Array.Empty<ListingRecord>(), WalkChain(dstAppend.Value.Directory.HeadDeltaBlockId));
        var arrived = Assert.Single(mergedDest);
        Assert.Equal(movedId, arrived.EmailHashedId);
        Assert.Equal(contentBlockId, arrived.ContentBlockId); // same block — Tier 3 pointer unchanged

        // ---- Content + metadata blocks untouched: not rewritten, not duplicated, not moved. ----
        var afterBytes = File.ReadAllBytes(_path);
        byte[] RegionAfter(BlockLocation loc) =>
            afterBytes.Skip((int)loc.Offset).Take((int)loc.TotalBlockLength).ToArray();
        Assert.Equal(contentRegionBefore, RegionAfter(contentLoc.Value)); // byte-identical in place
        Assert.Equal(metaRegionBefore, RegionAfter(metaLoc.Value));
        Assert.Equal(1, CountBlocks(manager, BlockType.EmailContent));  // no duplicate written
        Assert.Equal(1, CountBlocks(manager, BlockType.EmailMetadata));

        // And the Tier 3 block still decrypts to the original raw MIME at its original offset.
        var reContent = emailStore.ReadContent(contentLoc.Value.Offset);
        Ok(reContent);
        Assert.Equal(raw, reContent.Value.RawContent);
    }
}
