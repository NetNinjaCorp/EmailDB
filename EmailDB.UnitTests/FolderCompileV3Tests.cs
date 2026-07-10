using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for compile-to-pages (US-EMDB-82-8, story US-EMDB-82, docs/Folder_Listing.md Section 3,
/// "Compile deltas → pages"). Driving a real on-disk <see cref="BlockManager"/> plus the three
/// folder stores, <see cref="FolderCompiler.Compile"/> must:
/// <list type="bullet">
///   <item>merge the pending delta chain into the pages it touches and rewrite <b>only those</b> COW —
///   an untouched page's <see cref="PageEntry.PageBlockId"/> is byte-identical in the new directory;</item>
///   <item>write a new directory with <see cref="FolderPageDirectory.HeadDeltaBlockId"/> cleared and
///   <see cref="FolderPageDirectory.FolderVersion"/> bumped;</item>
///   <item>report the dead set — replaced page blocks, the consumed delta chain, and the superseded
///   directory version;</item>
///   <item>leave the compiled pages equal to the pre-compile read-path merge.</item>
/// </list>
/// </summary>
public class FolderCompileV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-foldercompile-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x80 + i)).ToArray();

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
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 57);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

    private static V3Id IdOf(int seed)
    {
        var raw = new byte[V3Id.Size];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new V3Id(raw);
    }

    // IdOf only stays distinct while seeds differ mod 256 (raw[i] = seed + i), capping it at 256
    // ids — too few for the ~500-entry scale test. WideId encodes the seed into four bytes so
    // hundreds of adds get genuinely distinct EmailHashedIds.
    private static V3Id WideId(int seed)
    {
        var raw = new byte[V3Id.Size];
        BinaryPrimitives.WriteInt32LittleEndian(raw, seed);
        for (var i = 4; i < raw.Length; i++) raw[i] = (byte)(seed * 31 + i);
        return new V3Id(raw);
    }

    private static ListingRecord WideRecord(int seed, long dateTicks, ListingFlags flags = ListingFlags.None) =>
        new()
        {
            EmailHashedId = WideId(seed),
            ContentBlockId = UlidOf(seed),
            DateTicks = dateTicks,
            Flags = flags,
            MessageSize = 1_000 + seed,
            From = $"sender{seed}@example.com",
            Subject = $"Subject {seed}",
            Preview = $"Preview {seed}",
        };

    private static byte[] UlidOf(int seed)
    {
        var ulid = new byte[UlidGenerator.UlidSize];
        for (var i = 0; i < ulid.Length; i++) ulid[i] = (byte)(seed * 13 + i);
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

    private sealed class Harness : IDisposable
    {
        public readonly RuntimeBlockOffsetMap Map = new();
        public readonly FileStream Stream;
        public readonly BlockManager Manager;
        public readonly EpochDekProvider Provider = MakeProvider();
        public readonly FolderPageStore PageStore;
        public readonly FolderPageDirectoryStore DirStore;
        public readonly FolderDeltaLogStore DeltaStore;
        public readonly FolderCompiler Compiler;

        public Harness(string path)
        {
            Stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            Manager = new BlockManager(Stream, offsetMap: Map);
            PageStore = new FolderPageStore(Manager, Provider, EncryptionPolicy.Default);
            DirStore = new FolderPageDirectoryStore(Manager, Provider, EncryptionPolicy.Default);
            DeltaStore = new FolderDeltaLogStore(Manager, Provider, EncryptionPolicy.Default);
            Compiler = new FolderCompiler(PageStore, DirStore, DeltaStore, Map);
        }

        /// <summary>Walk a folder's delta chain head→root off disk (as the read path does).</summary>
        public List<FolderDeltaLog> WalkChain(byte[] head)
        {
            var chain = new List<FolderDeltaLog>();
            var cursor = head;
            while (true)
            {
                Assert.True(Map.TryGetLocation(cursor, out var loc));
                var read = DeltaStore.ReadDeltaBlock(loc!.Offset);
                Ok(read);
                chain.Add(read.Value);
                if (!read.Value.HasPrevious)
                    break;
                cursor = read.Value.PreviousDeltaBlockId;
            }
            return chain;
        }

        public List<ListingRecord> ReadAllPages(FolderPageDirectory dir)
        {
            var records = new List<ListingRecord>();
            foreach (var entry in dir.PageEntries)
            {
                Assert.True(Map.TryGetLocation(entry.PageBlockId, out var loc));
                var read = PageStore.ReadPage(loc!.Offset);
                Ok(read);
                records.AddRange(read.Value.Records);
            }
            return records;
        }

        public void Dispose()
        {
            Manager.Dispose();
            Stream.Dispose();
            Provider.Dispose();
        }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    // ---- Threshold predicate --------------------------------------------------------------------

    [Fact]
    public void ShouldCompile_TriggersAtThreshold()
    {
        Assert.Equal(500, FolderCompiler.CompileThreshold);
        Assert.False(FolderCompiler.ShouldCompile(0));
        Assert.False(FolderCompiler.ShouldCompile(499));
        Assert.True(FolderCompiler.ShouldCompile(500));
        Assert.True(FolderCompiler.ShouldCompile(750));
    }

    // ---- Affected-page-only rewrite + head reset + version bump ---------------------------------

    [Fact]
    public void Compile_RewritesOnlyAffectedPages_ResetsDeltaHead_BumpsVersion()
    {
        using var h = new Harness(_path);

        // Two pages: A (newest, dates 500/400/300), B (oldest, dates 200/100). Only page B will be
        // touched by the pending deltas, so page A must be carried over verbatim.
        var pageA = FolderPage.FromRecords(new[] { Record(1, 500), Record(2, 400), Record(3, 300) });
        var pageB = FolderPage.FromRecords(new[] { Record(4, 200), Record(5, 100) });
        var locA = h.PageStore.WritePage(pageA);
        var locB = h.PageStore.WritePage(pageB);
        Ok(locA);
        Ok(locB);

        var folderId = UlidOf(900);
        var dir0 = FolderPageDirectory.Create(folderId, new[]
        {
            new PageEntry(locA.Value.BlockId, dateFrom: 300, dateTo: 500, entryCount: pageA.Count),
            new PageEntry(locB.Value.BlockId, dateFrom: 100, dateTo: 200, entryCount: pageB.Count),
        });

        // Pending deltas that touch page B only: delete id5 (@100), flag id4 Read (@200),
        // add id6 @150 (lands in page B's range).
        var append = h.DeltaStore.AppendChained(dir0, new[]
        {
            FolderDeltaEntry.Delete(IdOf(5)),
            FolderDeltaEntry.FlagChange(IdOf(4), ListingFlags.Read),
            FolderDeltaEntry.Add(Record(6, 150)),
        });
        Ok(append);
        var dir1 = append.Value.Directory;
        Assert.True(dir1.HasPendingDelta);

        // Compute the expected read-path merge before compiling, to compare against the pages after.
        var expected = FolderListingMerger.Merge(h.ReadAllPages(dir1), h.WalkChain(dir1.HeadDeltaBlockId));

        var result = h.Compiler.Compile(dir1);
        Ok(result);
        var compiled = result.Value.Directory;

        // Delta head reset and FolderVersion bumped (Create=0, one AppendChained=1, compile=2).
        Assert.False(compiled.HasPendingDelta);
        Assert.Equal(new byte[UlidGenerator.UlidSize], compiled.HeadDeltaBlockId);
        Assert.Equal(dir1.FolderVersion + 1, compiled.FolderVersion);
        Assert.Equal(2UL, compiled.FolderVersion);

        // Page A untouched: still first, same BlockId; page B replaced with a new BlockId.
        Assert.Equal(Hex(locA.Value.BlockId), Hex(compiled.PageEntries[0].PageBlockId));
        Assert.DoesNotContain(
            compiled.PageEntries, e => Hex(e.PageBlockId) == Hex(locB.Value.BlockId));

        // Only one page was rewritten (page B); page A was not written again.
        Assert.Single(result.Value.NewPages);
        Assert.Single(result.Value.DeadPages);
        Assert.Equal(Hex(locB.Value.BlockId), Hex(result.Value.DeadPages[0].BlockId));

        // The compiled pages equal the pre-compile read-path merge (order + contents).
        var compiledRecords = h.ReadAllPages(compiled);
        Assert.Equal(
            expected.Select(r => r.DateTicks).ToArray(),
            compiledRecords.Select(r => r.DateTicks).ToArray());
        Assert.DoesNotContain(compiledRecords, r => r.EmailHashedId == IdOf(5)); // deleted
        Assert.Contains(compiledRecords, r => r.EmailHashedId == IdOf(6) && r.DateTicks == 150); // added
        var id4 = compiledRecords.Single(r => r.EmailHashedId == IdOf(4));
        Assert.Equal(ListingFlags.Read, id4.Flags); // flag applied

        // The re-read directory (stable BlockId) round-trips with no pending delta.
        var reread = h.DirStore.ReadDirectory(result.Value.DirectoryLocation!.Offset);
        Ok(reread);
        Assert.False(reread.Value.HasPendingDelta);
        Assert.Equal(2UL, reread.Value.FolderVersion);
    }

    // ---- Dead-set correctness -------------------------------------------------------------------

    [Fact]
    public void Compile_DeadSet_CountsOldPage_OldDirectory_And_ConsumedDeltaChain()
    {
        using var h = new Harness(_path);

        var page = FolderPage.FromRecords(new[] { Record(1, 300), Record(2, 200) });
        var pageLoc = h.PageStore.WritePage(page);
        Ok(pageLoc);

        var folderId = UlidOf(901);
        var dir0 = FolderPageDirectory.Create(folderId, new[]
        {
            new PageEntry(pageLoc.Value.BlockId, dateFrom: 200, dateTo: 300, entryCount: page.Count),
        });
        // Persist dir0 so it has an on-disk location to be superseded (dead directory version).
        var dir0Loc = h.DirStore.WriteDirectory(dir0);
        Ok(dir0Loc);

        // A two-block delta chain (both consumed by the compile). Both touch the single page.
        var a1 = h.DeltaStore.AppendChained(dir0, new[] { FolderDeltaEntry.Add(Record(3, 250)) });
        Ok(a1);
        var a2 = h.DeltaStore.AppendChained(a1.Value.Directory, new[] { FolderDeltaEntry.Delete(IdOf(2)) });
        Ok(a2);
        var dir = a2.Value.Directory;

        // Remember the two delta block ids before compiling.
        var deltaHead = dir.HeadDeltaBlockId;
        var chain = h.WalkChain(deltaHead);
        Assert.Equal(2, chain.Count);

        var result = h.Compiler.Compile(dir);
        Ok(result);

        // Pending entry count reported: 1 Add + 1 Delete = 2.
        Assert.Equal(2, result.Value.PendingEntryCount);

        // Dead pages: the single old page block.
        Assert.Single(result.Value.DeadPages);
        Assert.Equal(Hex(pageLoc.Value.BlockId), Hex(result.Value.DeadPages[0].BlockId));

        // Dead deltas: exactly the two chain blocks (head + root).
        Assert.Equal(2, result.Value.DeadDeltas.Count);
        var deadDeltaIds = result.Value.DeadDeltas.Select(l => Hex(l.BlockId)).ToHashSet();
        Assert.Contains(Hex(a1.Value.DeltaLocation.BlockId), deadDeltaIds);
        Assert.Contains(Hex(a2.Value.DeltaLocation.BlockId), deadDeltaIds);

        // Dead directory: the superseded version, at dir0's offset and under the folder BlockId.
        Assert.NotNull(result.Value.DeadDirectory);
        Assert.Equal(dir0Loc.Value.Offset, result.Value.DeadDirectory!.Offset);
        Assert.Equal(Hex(folderId), Hex(result.Value.DeadDirectory.BlockId));

        // The new directory is a distinct, later block sharing the stable folder BlockId.
        Assert.Equal(Hex(folderId), Hex(result.Value.DirectoryLocation!.BlockId));
        Assert.True(result.Value.DirectoryLocation.Offset > result.Value.DeadDirectory.Offset);

        // DeadArtifacts aggregates page + 2 deltas + directory = 4.
        Assert.Equal(4, result.Value.DeadArtifacts.Count());
    }

    // ---- Full dead semantics: every replaced page dead, and the new directory dangles nothing -----

    // Story criterion "old pages, directory versions, and consumed delta chain become dead after
    // compile", proven end-to-end with MULTIPLE replaced pages: two pages are each affected by a
    // different delta block, so BOTH old page blocks must go dead (not just one), alongside the
    // superseded directory version and every consumed delta block. The teeth are the converse — the
    // compiled directory must reference NONE of those dead artifacts: no dead page BlockId among its
    // PageEntries, its cleared HeadDeltaBlockId no longer points into the consumed chain, and it is a
    // later block than the dead directory it supersedes. The freshly written pages are live, not dead.
    [Fact]
    public void Compile_DeadSemantics_EveryReplacedPageDead_NewDirectoryReferencesNoDeadArtifact()
    {
        using var h = new Harness(_path);

        // Two pages with disjoint, newest-first date ranges.
        var pageX = FolderPage.FromRecords(new[] { Record(1, 500), Record(2, 400) });
        var pageY = FolderPage.FromRecords(new[] { Record(3, 200), Record(4, 100) });
        var locX = h.PageStore.WritePage(pageX);
        var locY = h.PageStore.WritePage(pageY);
        Ok(locX);
        Ok(locY);

        var folderId = UlidOf(905);
        var dir0 = FolderPageDirectory.Create(folderId, new[]
        {
            new PageEntry(locX.Value.BlockId, dateFrom: 400, dateTo: 500, entryCount: pageX.Count),
            new PageEntry(locY.Value.BlockId, dateFrom: 100, dateTo: 200, entryCount: pageY.Count),
        });
        // Persist dir0 so it has an on-disk location to be superseded (dead directory version).
        var dir0Loc = h.DirStore.WriteDirectory(dir0);
        Ok(dir0Loc);

        // A two-block chain, each block touching a DIFFERENT page so both pages are replaced:
        //   block1 flags id1 (resident on pageX) → pageX affected;
        //   block2 deletes id4 (resident on pageY) → pageY affected.
        var a1 = h.DeltaStore.AppendChained(dir0, new[] { FolderDeltaEntry.FlagChange(IdOf(1), ListingFlags.Read) });
        Ok(a1);
        var a2 = h.DeltaStore.AppendChained(a1.Value.Directory, new[] { FolderDeltaEntry.Delete(IdOf(4)) });
        Ok(a2);
        var dir = a2.Value.Directory;
        Assert.Equal(2, h.WalkChain(dir.HeadDeltaBlockId).Count);

        var result = h.Compiler.Compile(dir);
        Ok(result);
        var compiled = result.Value.Directory;

        // (a) EVERY replaced page block is dead — both pageX and pageY, exactly.
        var deadPageIds = result.Value.DeadPages.Select(l => Hex(l.BlockId)).ToHashSet();
        Assert.Equal(2, deadPageIds.Count);
        Assert.Contains(Hex(locX.Value.BlockId), deadPageIds);
        Assert.Contains(Hex(locY.Value.BlockId), deadPageIds);

        // (c) Every consumed delta chain block is dead — both blocks of the two-block chain.
        var deadDeltaIds = result.Value.DeadDeltas.Select(l => Hex(l.BlockId)).ToHashSet();
        Assert.Equal(2, deadDeltaIds.Count);
        Assert.Contains(Hex(a1.Value.DeltaLocation.BlockId), deadDeltaIds);
        Assert.Contains(Hex(a2.Value.DeltaLocation.BlockId), deadDeltaIds);

        // (b) The superseded directory version is dead (dir0's offset, folder's stable BlockId).
        Assert.NotNull(result.Value.DeadDirectory);
        Assert.Equal(dir0Loc.Value.Offset, result.Value.DeadDirectory!.Offset);
        Assert.Equal(Hex(folderId), Hex(result.Value.DeadDirectory.BlockId));

        // Aggregate: 2 pages + 2 deltas + 1 directory = 5 dead artifacts.
        Assert.Equal(5, result.Value.DeadArtifacts.Count());

        // (d) DEAD SEMANTICS — the compiled directory dangles NONE of the dead artifacts.
        //   * No dead page BlockId appears among the new directory's PageEntries.
        foreach (var entry in compiled.PageEntries)
            Assert.DoesNotContain(Hex(entry.PageBlockId), deadPageIds);
        //   * The delta head is cleared and therefore points into the consumed chain no longer.
        Assert.False(compiled.HasPendingDelta);
        Assert.Equal(new byte[UlidGenerator.UlidSize], compiled.HeadDeltaBlockId);
        Assert.DoesNotContain(Hex(compiled.HeadDeltaBlockId), deadDeltaIds);
        //   * The new directory is a strictly later block than the dead version it supersedes.
        Assert.Equal(Hex(folderId), Hex(result.Value.DirectoryLocation!.BlockId));
        Assert.True(result.Value.DirectoryLocation.Offset > result.Value.DeadDirectory.Offset);

        // The freshly written pages are LIVE, not dead: every new page block is referenced by the new
        // directory and none of them appears in the dead set.
        var newPageIds = result.Value.NewPages.Select(l => Hex(l.BlockId)).ToHashSet();
        Assert.NotEmpty(newPageIds);
        foreach (var id in newPageIds)
            Assert.DoesNotContain(id, deadPageIds);
        Assert.All(compiled.PageEntries, e => Assert.Contains(Hex(e.PageBlockId), newPageIds));
    }

    // ---- No-op compile when nothing is pending --------------------------------------------------

    [Fact]
    public void Compile_NoPendingDelta_IsNoOp()
    {
        using var h = new Harness(_path);

        var page = FolderPage.FromRecords(new[] { Record(1, 100) });
        var pageLoc = h.PageStore.WritePage(page);
        Ok(pageLoc);
        var dir = FolderPageDirectory.Create(UlidOf(902), new[]
        {
            new PageEntry(pageLoc.Value.BlockId, dateFrom: 100, dateTo: 100, entryCount: 1),
        });
        Assert.False(dir.HasPendingDelta);

        var result = h.Compiler.Compile(dir);
        Ok(result);
        Assert.Same(dir, result.Value.Directory);
        Assert.Empty(result.Value.NewPages);
        Assert.Empty(result.Value.DeadPages);
        Assert.Empty(result.Value.DeadDeltas);
        Assert.Null(result.Value.DeadDirectory);
        Assert.Empty(result.Value.DeadArtifacts);
    }

    // ---- Compiling into an initially empty directory --------------------------------------------

    [Fact]
    public void Compile_EmptyDirectory_SeedsPagesFromAdds()
    {
        using var h = new Harness(_path);

        var dir0 = FolderPageDirectory.Create(UlidOf(903), Array.Empty<PageEntry>());
        var append = h.DeltaStore.AppendChained(dir0, new[]
        {
            FolderDeltaEntry.Add(Record(1, 300)),
            FolderDeltaEntry.Add(Record(2, 100)),
            FolderDeltaEntry.Add(Record(3, 200)),
        });
        Ok(append);

        var result = h.Compiler.Compile(append.Value.Directory);
        Ok(result);
        var compiled = result.Value.Directory;

        Assert.False(compiled.HasPendingDelta);
        Assert.Single(compiled.PageEntries);           // one fresh page seeded
        Assert.Single(result.Value.NewPages);
        Assert.Empty(result.Value.DeadPages);          // there was no page to replace

        var records = h.ReadAllPages(compiled);
        Assert.Equal(new long[] { 300, 200, 100 }, records.Select(r => r.DateTicks).ToArray());
    }

    // ---- Splitting an over-full affected page into ~80-record sub-pages --------------------------

    [Fact]
    public void Compile_SplitsOverfullAffectedPage_IntoBoundedPages()
    {
        using var h = new Harness(_path);

        // A single existing page with 40 records (dates 1000..961, newest-first). Ids 0..39 —
        // IdOf(seed) depends only on seed mod 256, so all seeds used here stay distinct mod 256.
        var seed = Enumerable.Range(0, 40).Select(i => Record(i, 1000 - i)).ToArray();
        var page = FolderPage.FromRecords(seed);
        var pageLoc = h.PageStore.WritePage(page);
        Ok(pageLoc);
        var dir0 = FolderPageDirectory.Create(UlidOf(904), new[]
        {
            new PageEntry(pageLoc.Value.BlockId, dateFrom: 961, dateTo: 1000, entryCount: page.Count),
        });

        // Add 120 more records (ids 40..159, all distinct mod 256) inside the page's date range so
        // the merged set is 160 rows > 2 pages.
        var adds = Enumerable.Range(0, 120)
            .Select(i => FolderDeltaEntry.Add(Record(40 + i, 980 - (i % 20))))
            .ToArray();
        var append = h.DeltaStore.AppendChained(dir0, adds);
        Ok(append);

        var result = h.Compiler.Compile(append.Value.Directory);
        Ok(result);
        var compiled = result.Value.Directory;

        // 160 rows split into ceil(160/80) = 2 pages, each within the target size, newest-first.
        Assert.Equal(160, compiled.PageEntries.Sum(e => e.EntryCount));
        Assert.Equal(2, compiled.PageCount);
        Assert.All(compiled.PageEntries, e => Assert.True(e.EntryCount <= FolderPage.TargetRecordsPerPage));
        for (int i = 1; i < compiled.PageCount; i++)
            Assert.True(compiled.PageEntries[i - 1].DateTo >= compiled.PageEntries[i].DateTo);

        var records = h.ReadAllPages(compiled);
        Assert.Equal(160, records.Count);
        for (int i = 1; i < records.Count; i++)
            Assert.True(records[i - 1].DateTicks >= records[i].DateTicks); // globally date-descending
    }

    // ---- At-scale: compile a ~500-entry chain against a multi-page folder -----------------------

    // The story's headline criterion, at the threshold it names: a chain that has reached
    // CompileThreshold (500) pending entries, spread over several delta blocks, compiled against a
    // multi-page folder where the adds route to a single page. The compile must rewrite ONLY that
    // page COW (untouched pages carried over with byte-identical BlockIds), reset the delta head, and
    // bump the version — the same invariants the small-chain tests check, but proven at 500 entries.
    [Fact]
    public void Compile_AtThreshold_500Entries_MultiPageFolder_RewritesOnlyAffectedPage_ResetsHead()
    {
        using var h = new Harness(_path);

        // Three pages with disjoint, newest-first date ranges. FindPageByDate routes a date to the
        // oldest page whose DateTo >= it, so any date <= pageC's DateTo (10_000) lands in pageC and
        // leaves pageA and pageB untouched.
        var pageA = FolderPage.FromRecords(new[] { WideRecord(1, 30_000), WideRecord(2, 29_999), WideRecord(3, 29_998) });
        var pageB = FolderPage.FromRecords(new[] { WideRecord(4, 20_000), WideRecord(5, 19_999) });
        var pageC = FolderPage.FromRecords(new[] { WideRecord(6, 10_000), WideRecord(7, 9_999) });
        var locA = h.PageStore.WritePage(pageA);
        var locB = h.PageStore.WritePage(pageB);
        var locC = h.PageStore.WritePage(pageC);
        Ok(locA);
        Ok(locB);
        Ok(locC);

        var folderId = UlidOf(950);
        var dir = FolderPageDirectory.Create(folderId, new[]
        {
            new PageEntry(locA.Value.BlockId, dateFrom: 29_998, dateTo: 30_000, entryCount: pageA.Count),
            new PageEntry(locB.Value.BlockId, dateFrom: 19_999, dateTo: 20_000, entryCount: pageB.Count),
            new PageEntry(locC.Value.BlockId, dateFrom: 9_999, dateTo: 10_000, entryCount: pageC.Count),
        });

        // Build a chain of exactly CompileThreshold (500) Add entries, all with dates < pageC.DateTo
        // so every one routes into pageC. Spread across 5 delta blocks of 100 to exercise chain walk.
        const int total = FolderCompiler.CompileThreshold; // 500
        Assert.True(FolderCompiler.ShouldCompile(total));   // threshold reached
        const int perBlock = 100;
        for (int b = 0; b < total / perBlock; b++)
        {
            var batch = Enumerable.Range(0, perBlock)
                .Select(i => { int seed = 1_000 + b * perBlock + i; return FolderDeltaEntry.Add(WideRecord(seed, 5_000 + b * perBlock + i)); })
                .ToArray();
            var append = h.DeltaStore.AppendChained(dir, batch);
            Ok(append);
            dir = append.Value.Directory;
        }
        Assert.True(dir.HasPendingDelta);
        Assert.Equal(500, h.WalkChain(dir.HeadDeltaBlockId).Sum(l => l.Count)); // 500 pending entries

        // Expected read-path merge before compiling, to compare the compiled pages against.
        var expected = FolderListingMerger.Merge(h.ReadAllPages(dir), h.WalkChain(dir.HeadDeltaBlockId));

        var result = h.Compiler.Compile(dir);
        Ok(result);
        var compiled = result.Value.Directory;

        // Delta head reset and version bumped by exactly one.
        Assert.False(compiled.HasPendingDelta);
        Assert.Equal(new byte[UlidGenerator.UlidSize], compiled.HeadDeltaBlockId);
        Assert.Equal(dir.FolderVersion + 1, compiled.FolderVersion);

        // All 500 entries were consumed and reported.
        Assert.Equal(500, result.Value.PendingEntryCount);

        // Only pageC was affected: pageA and pageB carried over with byte-identical BlockIds, and the
        // old pageC block is gone from the directory but reported dead exactly once.
        Assert.Equal(Hex(locA.Value.BlockId), Hex(compiled.PageEntries[0].PageBlockId));
        Assert.Equal(Hex(locB.Value.BlockId), Hex(compiled.PageEntries[1].PageBlockId));
        Assert.DoesNotContain(compiled.PageEntries, e => Hex(e.PageBlockId) == Hex(locC.Value.BlockId));
        Assert.Single(result.Value.DeadPages);
        Assert.Equal(Hex(locC.Value.BlockId), Hex(result.Value.DeadPages[0].BlockId));

        // pageC held 2 records; +500 adds = 502 rows, split into ceil(502/80) bounded sub-pages. Only
        // those new sub-pages were written — the two untouched pages were not rewritten.
        int expectedNewPages = (502 + FolderPage.TargetRecordsPerPage - 1) / FolderPage.TargetRecordsPerPage;
        Assert.Equal(expectedNewPages, result.Value.NewPages.Count);
        var newPageIds = result.Value.NewPages.Select(l => Hex(l.BlockId)).ToHashSet();
        Assert.DoesNotContain(Hex(locA.Value.BlockId), newPageIds);
        Assert.DoesNotContain(Hex(locB.Value.BlockId), newPageIds);
        Assert.All(compiled.PageEntries, e => Assert.True(e.EntryCount <= FolderPage.TargetRecordsPerPage));

        // Compiled pages equal the pre-compile read-path merge: 3 + 2 + 502 = 507 rows, date-descending.
        var compiledRecords = h.ReadAllPages(compiled);
        Assert.Equal(507, compiledRecords.Count);
        Assert.Equal(
            expected.Select(r => r.DateTicks).ToArray(),
            compiledRecords.Select(r => r.DateTicks).ToArray());
        Assert.Equal(
            expected.Select(r => Hex(r.EmailHashedId.GetBytes())).ToArray(),
            compiledRecords.Select(r => Hex(r.EmailHashedId.GetBytes())).ToArray());

        // Re-read the persisted directory (stable folder BlockId): no pending delta, version bumped.
        var reread = h.DirStore.ReadDirectory(result.Value.DirectoryLocation!.Offset);
        Ok(reread);
        Assert.False(reread.Value.HasPendingDelta);
        Assert.Equal(compiled.FolderVersion, reread.Value.FolderVersion);
    }
}
