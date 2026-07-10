using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-end cost bound for story US-EMDB-81 (docs/Folder_Listing.md Section 2):
/// <b>listing one page of a 50K-email folder costs at most 3 block reads</b>, independent of
/// folder size. The listing path is: read the <see cref="FolderPageDirectory"/> (1 block), binary
/// -search its date ranges <em>in memory</em> to pick a page and resolve that page's BlockId to an
/// offset through the runtime location map (no I/O), read that one <see cref="FolderPage"/>
/// (1 block), and — only when a delta is pending — read the head <c>FolderDeltaLog</c> block
/// (1 block). A 50K-email folder is ~625 pages, yet a page listing still touches at most three
/// blocks.
///
/// <para>Block reads are counted at the stream boundary: every <see cref="BlockManager.Read"/>
/// issues exactly one <see cref="FileStream.Seek"/> to the block's start offset before reading its
/// header and payload, so counting seeks during the measured listing counts blocks read.</para>
/// </summary>
public class FolderListingReadCostV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-listcost-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x60 + i)).ToArray();

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
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 23);
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
        for (var i = 0; i < ulid.Length; i++) ulid[i] = (byte)(seed * 7 + i);
        return ulid;
    }

    /// <summary>A FileStream that counts the absolute Seeks the block manager issues — one per block read.</summary>
    private sealed class CountingFileStream : FileStream
    {
        public CountingFileStream(string path)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) { }

        /// <summary>Block reads observed since the last reset (each <see cref="BlockManager.Read"/> seeks once).</summary>
        public long SeekCount;

        public override long Seek(long offset, SeekOrigin origin)
        {
            SeekCount++;
            return base.Seek(offset, origin);
        }
    }

    [Fact]
    public void Listing_OnePageOf50KEmailFolder_CostsAtMost3BlockReads()
    {
        const int recordsPerPage = FolderPage.TargetRecordsPerPage; // 80
        const int pageCount = 625;                                  // 625 * 80 = 50,000 emails
        const int spacingPerPage = 1_000;                           // >> 80 so pages don't overlap

        using var provider = MakeProvider();
        using var stream = new CountingFileStream(_path);
        var map = new RuntimeBlockOffsetMap();
        using var manager = new BlockManager(stream, offsetMap: map);
        var pageStore = new FolderPageStore(manager, provider, EncryptionPolicy.Default);
        var dirStore = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);

        // Build and persist 625 encrypted, Zstd'd pages newest-first (page 0 newest). Each page's
        // 80 records occupy 80 contiguous ticks inside a wider, non-overlapping slot.
        var entries = new List<PageEntry>(pageCount);
        int targetPageIndex = pageCount / 2;
        long target = 0;
        for (int p = 0; p < pageCount; p++)
        {
            long dateTo = (long)(pageCount - p) * spacingPerPage; // newest page has the largest date
            long dateFrom = dateTo - (recordsPerPage - 1);        // 80 distinct ticks per page

            var records = new List<ListingRecord>(recordsPerPage);
            for (int r = 0; r < recordsPerPage; r++)
            {
                records.Add(new ListingRecord
                {
                    EmailHashedId = IdOf(p + r),
                    ContentBlockId = UlidOf(p + r),
                    DateTicks = dateTo - r,      // dateTo (newest) down to dateFrom (oldest)
                    Flags = ListingFlags.None,
                    MessageSize = 1_000 + r,
                    From = "sender@example.com",
                    Subject = $"Subject page {p} row {r}",
                    Preview = new string('x', 120),
                });
            }

            var loc = pageStore.WritePage(FolderPage.FromRecords(records));
            Ok(loc);
            entries.Add(new PageEntry(loc.Value.BlockId, dateFrom, dateTo, recordsPerPage));

            if (p == targetPageIndex)
                target = dateFrom + 3; // a date squarely inside the page we will jump to
        }

        // A pending FolderDeltaLog head block, so the listing must also consult the delta chain head.
        var deltaLoc = manager.Append(BlockType.FolderDeltaLog, PayloadEncoding.Custom, new byte[] { 1, 2, 3, 4 });
        Ok(deltaLoc);

        // Directory WITH a pending delta (worst case: 3 reads) and one WITHOUT (2 reads).
        var dirWithDelta = FolderPageDirectory.Create(UlidOf(200), entries, headDeltaBlockId: deltaLoc.Value.BlockId);
        var dirWithDeltaLoc = dirStore.WriteDirectory(dirWithDelta);
        Ok(dirWithDeltaLoc);

        var dirNoDelta = FolderPageDirectory.Create(UlidOf(201), entries);
        var dirNoDeltaLoc = dirStore.WriteDirectory(dirNoDelta);
        Ok(dirNoDeltaLoc);

        var flushed = manager.Flush();
        Assert.True(flushed.IsSuccess, flushed.IsFailure ? flushed.Error : null);

        // ---- Worst case: pending delta → exactly 3 block reads ----
        stream.SeekCount = 0;

        // (1) read the directory
        var readDir = dirStore.ReadDirectory(dirWithDeltaLoc.Value.Offset);
        Ok(readDir);
        var dir = readDir.Value;
        Assert.Equal(pageCount, dir.PageCount);

        // Binary-search the date ranges and resolve the page BlockId — both in memory, no block reads.
        Assert.True(dir.TryFindContainingPage(target, out int idx));
        Assert.Equal(targetPageIndex, idx);
        Assert.True(map.TryGetLocation(dir.PageEntries[idx].PageBlockId, out var pageLocation));

        // (2) read that one page
        var readPage = pageStore.ReadPage(pageLocation!.Offset);
        Ok(readPage);
        Assert.Equal(recordsPerPage, readPage.Value.Count);
        Assert.Contains(readPage.Value.Records, rec => rec.DateTicks == target);

        // (3) read the pending delta head
        Assert.True(dir.HasPendingDelta);
        Assert.True(map.TryGetLocation(dir.HeadDeltaBlockId, out var deltaLocation));
        Ok(manager.Read(deltaLocation!.Offset));

        long readsWithDelta = stream.SeekCount;
        Assert.True(readsWithDelta <= 3,
            $"Listing one page of a {pageCount}-page ({pageCount * recordsPerPage}-email) folder took " +
            $"{readsWithDelta} block reads; the story bounds it at 3.");
        Assert.Equal(3, readsWithDelta); // directory + page + delta head, no page-scan cost

        // ---- No pending delta → only 2 block reads (the delta read is the sole optional third) ----
        stream.SeekCount = 0;

        var readDir2 = dirStore.ReadDirectory(dirNoDeltaLoc.Value.Offset);
        Ok(readDir2);
        Assert.False(readDir2.Value.HasPendingDelta);
        Assert.True(readDir2.Value.TryFindContainingPage(target, out int idx2));
        Assert.True(map.TryGetLocation(readDir2.Value.PageEntries[idx2].PageBlockId, out var pageLocation2));
        Ok(pageStore.ReadPage(pageLocation2!.Offset));

        long readsNoDelta = stream.SeekCount;
        Assert.Equal(2, readsNoDelta);
        Assert.True(readsNoDelta <= 3);
    }
}
