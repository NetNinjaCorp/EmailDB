using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the Tier 1 per-folder page index (US-EMDB-81-7, story US-EMDB-81,
/// docs/Folder_Listing.md Section 2):
/// <list type="bullet">
///   <item>A <see cref="FolderPageDirectory"/> (BlockType 11) packs FolderId, FolderVersion,
///   HeadDeltaBlockId and date-ranged <see cref="PageEntry"/>s and round-trips its payload.</item>
///   <item>Date-jump uses binary search over the directory's date ranges.</item>
///   <item><see cref="FolderPageDirectory.FolderVersion"/> increments on every COW rewrite.</item>
///   <item>The directory persists through the Zstd + Default-encryption pipeline
///   (<see cref="FolderPageDirectoryStore"/>) as a genuinely encrypted BlockType 11 block under a
///   stable BlockId (the folder ULID).</item>
/// </list>
/// </summary>
public class FolderPageDirectoryV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-folderdir-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x50 + i)).ToArray();

    private const ushort ActiveEpoch = 2;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static EpochDekProvider MakeProvider()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 11);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

    private static byte[] UlidOf(byte seed)
    {
        var ulid = new byte[UlidGenerator.UlidSize];
        for (var i = 0; i < ulid.Length; i++) ulid[i] = (byte)(seed * 5 + i);
        return ulid;
    }

    /// <summary>Three contiguous, non-overlapping newest-first pages: [100,200], [40,99], [0,39].</summary>
    private static FolderPageDirectory SampleDirectory(byte[]? head = null, ulong version = 0) =>
        FolderPageDirectory.Create(
            folderId: UlidOf(1),
            pageEntries: new[]
            {
                new PageEntry(UlidOf(10), dateFrom: 100, dateTo: 200, entryCount: 80),
                new PageEntry(UlidOf(11), dateFrom: 40, dateTo: 99, entryCount: 80),
                new PageEntry(UlidOf(12), dateFrom: 0, dateTo: 39, entryCount: 40),
            },
            headDeltaBlockId: head,
            folderVersion: version);

    // ---- Payload: FolderId / FolderVersion / HeadDeltaBlockId / PageEntries (task DoD) ----

    [Fact]
    public void Directory_PayloadRoundTrips_AllFields()
    {
        var head = UlidOf(99);
        var dir = SampleDirectory(head, version: 7);

        var got = FolderPageDirectory.UnpackPayload(dir.PackPayload());

        Assert.Equal(dir.FolderId, got.FolderId);
        Assert.Equal(7UL, got.FolderVersion);
        Assert.Equal(head, got.HeadDeltaBlockId);
        Assert.True(got.HasPendingDelta);
        Assert.Equal(3, got.PageCount);
        for (int i = 0; i < dir.PageCount; i++)
        {
            Assert.Equal(dir.PageEntries[i].PageBlockId, got.PageEntries[i].PageBlockId);
            Assert.Equal(dir.PageEntries[i].DateFrom, got.PageEntries[i].DateFrom);
            Assert.Equal(dir.PageEntries[i].DateTo, got.PageEntries[i].DateTo);
            Assert.Equal(dir.PageEntries[i].EntryCount, got.PageEntries[i].EntryCount);
        }
    }

    [Fact]
    public void Directory_NoPendingDelta_WhenHeadIsNull()
    {
        var dir = SampleDirectory(head: null);
        Assert.False(dir.HasPendingDelta);
        Assert.Equal(new byte[UlidGenerator.UlidSize], dir.HeadDeltaBlockId);

        var got = FolderPageDirectory.UnpackPayload(dir.PackPayload());
        Assert.False(got.HasPendingDelta);
    }

    [Fact]
    public void Directory_HeaderLengthMatchesLayout_And_EmptyRoundTrips()
    {
        Assert.Equal(45, FolderPageDirectory.HeaderLength);
        var empty = FolderPageDirectory.Create(UlidOf(2), Array.Empty<PageEntry>());
        Assert.Equal(FolderPageDirectory.HeaderLength, empty.PayloadLength);

        var got = FolderPageDirectory.UnpackPayload(empty.PackPayload());
        Assert.Equal(0, got.PageCount);
        Assert.Equal(-1, got.FindPageByDate(123));
    }

    [Fact]
    public void Directory_UnpackPayload_RejectsWrongVersion()
    {
        var payload = SampleDirectory().PackPayload();
        payload[0] = 42;
        Assert.Throws<ArgumentException>(() => FolderPageDirectory.UnpackPayload(payload));
    }

    [Fact]
    public void Directory_Create_RejectsPagesNotNewestFirst()
    {
        // DateTo ascending (oldest-first) violates the newest-first ordering the search relies on.
        Assert.Throws<ArgumentException>(() => FolderPageDirectory.Create(
            UlidOf(1),
            new[]
            {
                new PageEntry(UlidOf(1), 0, 39, 1),
                new PageEntry(UlidOf(2), 100, 200, 1),
            }));
    }

    // ---- Binary search by date (story AC 2 / task DoD) ----------------------

    [Theory]
    [InlineData(150, 0)]  // inside page 0 [100,200]
    [InlineData(200, 0)]  // page 0 upper bound
    [InlineData(100, 0)]  // page 0 lower bound
    [InlineData(99, 1)]   // page 1 upper bound
    [InlineData(50, 1)]   // inside page 1 [40,99]
    [InlineData(40, 1)]   // page 1 lower bound
    [InlineData(20, 2)]   // inside page 2 [0,39]
    [InlineData(0, 2)]    // page 2 lower bound
    public void Directory_FindPageByDate_LandsOnContainingPage(long date, int expected)
    {
        Assert.Equal(expected, SampleDirectory().FindPageByDate(date));
    }

    [Fact]
    public void Directory_FindPageByDate_ClampsOutsideSpan()
    {
        var dir = SampleDirectory();
        Assert.Equal(0, dir.FindPageByDate(10_000)); // newer than all → newest page
        Assert.Equal(2, dir.FindPageByDate(-10));    // older than all → oldest page
    }

    [Fact]
    public void Directory_TryFindContainingPage_ReportsGapsAndSpanMisses()
    {
        // Gapped pages: [100,200] and [0,39] with 40..99 unrepresented.
        var dir = FolderPageDirectory.Create(
            UlidOf(1),
            new[]
            {
                new PageEntry(UlidOf(10), 100, 200, 5),
                new PageEntry(UlidOf(11), 0, 39, 5),
            });

        Assert.True(dir.TryFindContainingPage(150, out var hit));
        Assert.Equal(0, hit);

        Assert.False(dir.TryFindContainingPage(70, out var gap));   // in the 40..99 gap
        Assert.Equal(-1, gap);

        Assert.False(dir.TryFindContainingPage(999, out _));        // newer than the folder
        Assert.False(dir.TryFindContainingPage(-5, out _));         // older than the folder
    }

    [Fact]
    public void Directory_FindPageByDate_ScalesToA50KFolderCostingOneDirectoryRead()
    {
        // ~50K emails / 80 per page ≈ 625 pages, newest-first, contiguous 80-tick ranges.
        const int pages = 625;
        var entries = new List<PageEntry>(pages);
        for (int i = 0; i < pages; i++)
        {
            long dateTo = (pages - i) * 100L;      // page 0 newest
            long dateFrom = dateTo - 79;
            entries.Add(new PageEntry(UlidOf((byte)i), dateFrom, dateTo, 80));
        }
        var dir = FolderPageDirectory.Create(UlidOf(3), entries);

        // A date in the middle resolves to its exact page via binary search (no page reads).
        int mid = pages / 2;
        long target = (pages - mid) * 100L - 10; // inside page `mid`
        Assert.True(dir.TryFindContainingPage(target, out var idx));
        Assert.Equal(mid, idx);
    }

    // A deliberately naive O(n) reference: the last index whose page still holds an email at least
    // as new as the target (DateTo >= date), clamped to the newest page. This is the linear-scan
    // definition FindPageByDate's binary search must reproduce for every input.
    private static int LinearFindPageByDate(IReadOnlyList<PageEntry> pages, long date)
    {
        if (pages.Count == 0) return -1;
        int result = -1;
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].DateTo >= date) result = i;
        return result < 0 ? 0 : result;
    }

    // Linear reference for TryFindContainingPage: the first page whose inclusive range contains the
    // date (unique for the non-overlapping directories generated below).
    private static bool LinearContainingPage(IReadOnlyList<PageEntry> pages, long date, out int idx)
    {
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Contains(date)) { idx = i; return true; }
        idx = -1;
        return false;
    }

    /// <summary>
    /// Proof that FindPageByDate/TryFindContainingPage are a genuine date binary search and not a
    /// hand-tuned special case: across many randomized newest-first directories (varied sizes, span
    /// widths, and inter-page gaps), the result agrees with an independent linear-scan oracle for
    /// every probe date — every page boundary ±1, every gap, and points beyond both ends of the span.
    /// </summary>
    [Fact]
    public void Directory_FindPageByDate_AgreesWithLinearScanOracle_AcrossRandomDirectories()
    {
        var rng = new Random(20260705); // deterministic
        for (int trial = 0; trial < 300; trial++)
        {
            int pageCount = 1 + rng.Next(40); // 1..40 pages
            // Build oldest-first with random non-negative gaps (gap==0 => contiguous, >0 => a gap),
            // then reverse to the newest-first order the directory requires.
            var oldestFirst = new List<PageEntry>(pageCount);
            long cursor = rng.Next(-500, 500);
            for (int i = 0; i < pageCount; i++)
            {
                cursor += rng.Next(0, 5);              // gap before this page
                long from = cursor;
                long to = from + rng.Next(0, 8);       // span width 0..7 (0 => single-tick page)
                cursor = to + 1;                       // ensure strictly non-overlapping
                oldestFirst.Add(new PageEntry(UlidOf((byte)i), from, to, 1 + rng.Next(80)));
            }
            oldestFirst.Reverse(); // newest-first
            var dir = FolderPageDirectory.Create(UlidOf(7), oldestFirst);

            long lo = dir.PageEntries[^1].DateFrom - 2; // just older than the whole folder
            long hi = dir.PageEntries[0].DateTo + 2;    // just newer than the whole folder
            for (long date = lo; date <= hi; date++)
            {
                Assert.Equal(LinearFindPageByDate(dir.PageEntries, date), dir.FindPageByDate(date));

                bool expectedHit = LinearContainingPage(dir.PageEntries, date, out int expectedIdx);
                bool actualHit = dir.TryFindContainingPage(date, out int actualIdx);
                Assert.Equal(expectedHit, actualHit);
                Assert.Equal(expectedIdx, actualIdx);
            }
        }
    }

    // ---- FolderVersion increments on every rewrite (story AC 3 / task DoD) ---

    [Fact]
    public void Directory_Rewrite_IncrementsFolderVersion_AndKeepsFolderId()
    {
        var v0 = FolderPageDirectory.Create(UlidOf(1), SampleDirectory().PageEntries);
        Assert.Equal(0UL, v0.FolderVersion);

        var v1 = v0.Rewrite(v0.PageEntries, headDeltaBlockId: UlidOf(77));
        var v2 = v1.Rewrite(v1.PageEntries); // delta-head cleared, still a rewrite

        Assert.Equal(1UL, v1.FolderVersion);
        Assert.Equal(2UL, v2.FolderVersion);
        Assert.Equal(v0.FolderId, v2.FolderId);     // same folder across versions
        Assert.True(v1.HasPendingDelta);
        Assert.False(v2.HasPendingDelta);
        Assert.Equal(0UL, v0.FolderVersion);        // rewrite is copy-on-write; v0 untouched
    }

    [Fact]
    public void Directory_Rewrite_AtMaxVersion_ThrowsRatherThanWrapping()
    {
        // A directory already at the counter's ceiling cannot rewrite: the 64-bit FolderVersion
        // must never silently wrap to 0 and desynchronise replication.
        var maxed = FolderPageDirectory.Create(
            UlidOf(1), SampleDirectory().PageEntries, folderVersion: ulong.MaxValue);

        Assert.Throws<OverflowException>(() => maxed.Rewrite(maxed.PageEntries));
        Assert.Equal(ulong.MaxValue, maxed.FolderVersion); // original untouched by the failed rewrite
    }

    // ---- Persists encrypted under Default, stable BlockId (story AC 4) ------

    [Fact]
    public void Directory_RoundTrips_ThroughStore_EncryptedZstd()
    {
        var dir = SampleDirectory(head: UlidOf(88), version: 3);

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);

        var written = store.WriteDirectory(dir);
        Ok(written);

        // Stable BlockId: the block's BlockId is the folder ULID, not a freshly minted one.
        Assert.Equal(dir.FolderId, written.Value.BlockId);

        var block = manager.Read(written.Value.Offset);
        Ok(block);
        var header = block.Value.Header;
        Assert.Equal(BlockType.FolderPageDirectory, header.Type);
        Assert.Equal(CompressionAlgorithm.Zstd, header.Compression);
        Assert.Equal(PayloadEncoding.Custom, header.Encoding);
        Assert.True(header.IsEncrypted);
        Assert.Equal(ActiveEpoch, header.KeyEpoch);
        Assert.Equal(dir.FolderId, header.BlockId);

        var read = store.ReadDirectory(written.Value.Offset);
        Ok(read);
        Assert.Equal(3UL, read.Value.FolderVersion);
        Assert.Equal(dir.FolderId, read.Value.FolderId);
        Assert.Equal(dir.HeadDeltaBlockId, read.Value.HeadDeltaBlockId);
        Assert.Equal(dir.PageCount, read.Value.PageCount);
        Assert.Equal(1, read.Value.FindPageByDate(50));
    }

    [Fact]
    public void Directory_CowRewrite_ReappendsUnderSameBlockId_NewestVersionWins()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);

        var v0 = SampleDirectory();
        var loc0 = store.WriteDirectory(v0);
        Ok(loc0);

        var v1 = v0.Rewrite(v0.PageEntries, headDeltaBlockId: UlidOf(200));
        var loc1 = store.WriteDirectory(v1);
        Ok(loc1);

        // Same BlockId, distinct offsets — a new on-disk version of one directory.
        Assert.Equal(loc0.Value.BlockId, loc1.Value.BlockId);
        Assert.NotEqual(loc0.Value.Offset, loc1.Value.Offset);

        var read1 = store.ReadDirectory(loc1.Value.Offset);
        Ok(read1);
        Assert.Equal(1UL, read1.Value.FolderVersion);
        Assert.True(read1.Value.HasPendingDelta);
    }

    [Fact]
    public void Directory_ChainedRewrites_FolderVersionMonotonicThroughEncryptedStore()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);

        // Persist the initial directory, then apply a chain of COW rewrites, appending each and
        // reading it back through the Zstd + Default-encryption pipeline. FolderVersion must climb
        // by exactly one per rewrite and survive the encrypted round-trip at every step.
        var dir = SampleDirectory();
        var loc = store.WriteDirectory(dir);
        Ok(loc);

        var read0 = store.ReadDirectory(loc.Value.Offset);
        Ok(read0);
        Assert.Equal(0UL, read0.Value.FolderVersion);

        ulong previous = 0;
        var offsets = new List<long> { loc.Value.Offset };
        for (ulong step = 1; step <= 5; step++)
        {
            // Alternate setting and clearing the delta head so both rewrite paths bump the counter.
            var head = step % 2 == 1 ? UlidOf((byte)(100 + step)) : null;
            dir = dir.Rewrite(dir.PageEntries, headDeltaBlockId: head);

            var written = store.WriteDirectory(dir);
            Ok(written);
            Assert.Equal(dir.FolderId, written.Value.BlockId); // stable BlockId across the chain
            offsets.Add(written.Value.Offset);

            var read = store.ReadDirectory(written.Value.Offset);
            Ok(read);
            Assert.Equal(step, read.Value.FolderVersion);          // increments by exactly one
            Assert.Equal(previous + 1, read.Value.FolderVersion);  // strictly monotonic
            Assert.Equal(step % 2 == 1, read.Value.HasPendingDelta);
            previous = read.Value.FolderVersion;
        }

        // Every version still readable at its own offset — earlier versions are not mutated in place.
        for (int v = 0; v < offsets.Count; v++)
        {
            var back = store.ReadDirectory(offsets[v]);
            Ok(back);
            Assert.Equal((ulong)v, back.Value.FolderVersion);
        }
    }

    [Fact]
    public void Directory_PageEntry_RejectsInvertedRange()
    {
        Assert.Throws<ArgumentException>(() => new PageEntry(UlidOf(1), dateFrom: 100, dateTo: 50, 1));
    }
}
