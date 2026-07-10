using EmailDB.Format;
using EmailDB.Format.Protobuf.V3;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the Tier-2 page regeneration routine (US-EMDB-83-4, story US-EMDB-83,
/// docs/Folder_Listing.md Section 4). A folder whose Tier 1 pages are lost or corrupt is a recovery
/// event, not data loss: <see cref="FolderPageRegenerator"/> rebuilds the pages and directory from
/// <see cref="EmailMetadata"/> (Tier 2) alone. The three story acceptance criteria:
/// <list type="bullet">
///   <item>Regeneration reads only Tier 2 blocks, never Tier 3 (<see cref="EmailContent"/>).</item>
///   <item>Rebuilt pages match the originals except that flags reset to defaults.</item>
///   <item>The regenerated directory carries a bumped <see cref="FolderPageDirectory.FolderVersion"/>.</item>
/// </list>
/// </summary>
public class FolderPageRegeneratorV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-regen-{Guid.NewGuid():N}.emdb");

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

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static EpochDekProvider MakeProvider()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 13);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

    private static V3Id IdOf(byte seed)
    {
        var raw = new byte[V3Id.Size];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new V3Id(raw);
    }

    private static byte[] UlidOf(byte seed)
    {
        var ulid = new byte[UlidGenerator.UlidSize];
        for (var i = 0; i < ulid.Length; i++) ulid[i] = (byte)(seed * 3 + i);
        return ulid;
    }

    // A Tier 2 metadata block built directly (no MIME parse needed): every field the listing needs.
    private static EmailMetadata Meta(byte seed, long dateTicks) =>
        new()
        {
            EmailHashedId = IdOf(seed).GetBytes(),
            ContentBlockId = UlidOf(seed),
            DateTicks = dateTicks,
            MessageSize = 10_000 + seed,
            From = $"User {seed} <u{seed}@example.com>",
            Subject = $"Subject line for message {seed}",
            Preview = $"Preview body text for message {seed}.",
        };

    private sealed record Harness(
        BlockManager Manager,
        EmailBlockStore EmailStore,
        FolderPageStore PageStore,
        FolderPageDirectoryStore DirectoryStore,
        FolderPageRegenerator Regenerator);

    private Harness NewHarness(FileStream stream, EpochDekProvider provider)
    {
        var manager = new BlockManager(stream, ownsStream: false);
        var emailStore = new EmailBlockStore(manager, provider, EncryptionPolicy.Default);
        var pageStore = new FolderPageStore(manager, provider, EncryptionPolicy.Default);
        var dirStore = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);
        var regen = new FolderPageRegenerator(emailStore, pageStore, dirStore);
        return new Harness(manager, emailStore, pageStore, dirStore, regen);
    }

    // Writes `count` Tier 2 metadata blocks (out of date order) plus a Tier 3 content block for each,
    // and returns the metadata offsets, the content offsets, and the source metadata by id.
    private (List<long> MetaOffsets, List<long> ContentOffsets, Dictionary<V3Id, EmailMetadata> ById)
        SeedFolder(Harness h, int count)
    {
        var metaOffsets = new List<long>();
        var contentOffsets = new List<long>();
        var byId = new Dictionary<V3Id, EmailMetadata>();

        // Deliberately non-monotonic dates so the regenerator must sort, not just copy input order.
        for (int i = 0; i < count; i++)
        {
            byte seed = (byte)(i + 1);
            long date = 600_000_000_000_000_000L + ((i * 7919) % 1000) * 1_000_000_000L;
            var meta = Meta(seed, date);

            var wMeta = h.EmailStore.WriteMetadata(meta);
            Ok(wMeta);
            metaOffsets.Add(wMeta.Value.Offset);

            var content = new EmailContent
            {
                EmailHashedId = meta.EmailHashedId,
                RawContent = new byte[] { seed, 0xAA, 0xBB, 0xCC },
            };
            var wContent = h.EmailStore.WriteContent(content);
            Ok(wContent);
            contentOffsets.Add(wContent.Value.Offset);

            byId[new V3Id(meta.EmailHashedId)] = meta;
        }

        return (metaOffsets, contentOffsets, byId);
    }

    // ---- AC1: reads only Tier 2, never Tier 3 -------------------------------

    [Fact]
    public void Regenerate_ReadsTier2Only_RebuildsFromMetadataOffsets()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        var (metaOffsets, _, byId) = SeedFolder(h, 5);
        var prior = FolderPageDirectory.Create(UlidOf(1), Array.Empty<PageEntry>(), folderVersion: 0);

        var result = h.Regenerator.Regenerate(prior, metaOffsets);
        Ok(result);

        // Every rebuilt record's content identity traces back to a seeded Tier 2 block.
        var rebuilt = ReadAllRecords(h, result.Value.NewPages);
        Assert.Equal(byId.Count, rebuilt.Count);
        foreach (var record in rebuilt)
            Assert.True(byId.ContainsKey(record.EmailHashedId));
    }

    [Fact]
    public void Regenerate_RejectsTier3Offsets_NeverConsumingContentAsMetadata()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        var (_, contentOffsets, _) = SeedFolder(h, 3);

        // Feeding the regenerator Tier 3 (EmailContent) offsets must fail: it only ever asks the
        // store for EmailMetadata, and that read rejects a non-Tier-2 block rather than reading it.
        var result = h.Regenerator.Regenerate(UlidOf(1), contentOffsets);
        Assert.True(result.IsFailure);
        Assert.Contains("EmailMetadata", result.Error);
    }

    [Fact]
    public void Regenerate_NeverSeeksToATier3BlockOffset_ProvenByObservedReads()
    {
        using var provider = MakeProvider();
        using var stream = new RecordingFileStream(_path);
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        // Intermix Tier 3 (EmailContent, type 7) and Tier 2 (EmailMetadata, type 10) blocks in the
        // same file: SeedFolder writes them interleaved (meta, content, meta, content, ...).
        var (metaOffsets, contentOffsets, _) = SeedFolder(h, 6);

        // Flush the seed writes, snapshot EOF, then observe only the I/O the regeneration itself
        // issues. Each BlockManager.Read seeks to the block's absolute offset before reading it
        // (BlockManager.Read → stream.Seek(offset)); each append seeks to EOF (offset >= file length),
        // strictly beyond every seeded block. So the set of offsets seeked during regeneration IS the
        // set of block offsets it touched, and any seek below the pre-regen EOF is a block read.
        var flushed = mgr.Flush();
        Assert.True(flushed.IsSuccess, flushed.IsFailure ? flushed.Error : null);
        long eofBeforeRegen = stream.Length;
        stream.SeekOffsets.Clear();

        var prior = FolderPageDirectory.Create(UlidOf(7), Array.Empty<PageEntry>(), folderVersion: 0);
        var result = h.Regenerator.Regenerate(prior, metaOffsets);
        Ok(result);

        var metaSet = metaOffsets.ToHashSet();
        var contentSet = contentOffsets.ToHashSet();

        // (1) Not one Tier 3 (EmailContent) block offset is ever touched during regeneration.
        foreach (var contentOffset in contentOffsets)
            Assert.DoesNotContain(contentOffset, stream.SeekOffsets);

        // (2) Every read (any seek below the pre-regen EOF) landed on a Tier 2 metadata block — never
        // on a content block or any other non-member offset.
        foreach (var seeked in stream.SeekOffsets)
        {
            if (seeked < eofBeforeRegen)
                Assert.True(metaSet.Contains(seeked),
                    $"Regeneration read offset {seeked} (below pre-regen EOF {eofBeforeRegen}) that is not a " +
                    $"Tier-2 metadata member. Tier-3 content offsets were {string.Join(", ", contentSet)}.");
        }

        // (3) Every Tier 2 member was read exactly once — the listing was genuinely rebuilt from
        // Tier 2, so this is not a vacuous "read nothing" pass.
        foreach (var metaOffset in metaOffsets)
            Assert.Equal(1, stream.SeekOffsets.Count(o => o == metaOffset));
    }

    // ---- AC2: rebuilt pages match originals except flags reset to defaults ---

    [Fact]
    public void Regenerate_RebuiltRecordsMatchOriginals_ExceptFlagsReset()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        var (metaOffsets, _, byId) = SeedFolder(h, 12);
        var prior = FolderPageDirectory.Create(UlidOf(2), Array.Empty<PageEntry>(), folderVersion: 3);

        var result = h.Regenerator.Regenerate(prior, metaOffsets);
        Ok(result);

        var rebuilt = ReadAllRecords(h, result.Value.NewPages);
        Assert.Equal(byId.Count, rebuilt.Count);

        foreach (var record in rebuilt)
        {
            // The "original" listing as it existed before corruption carried live flags; here we
            // compare against that original with flags set, and require every field to match except
            // the flags, which regeneration resets to None.
            var meta = byId[record.EmailHashedId];
            var original = ListingRecordBuilder.FromMetadata(
                meta, ListingFlags.Read | ListingFlags.Flagged | ListingFlags.Answered);

            Assert.Equal(ListingFlags.None, record.Flags);              // flags reset to defaults
            Assert.NotEqual(original.Flags, record.Flags);              // and genuinely differ

            Assert.Equal(original.EmailHashedId, record.EmailHashedId); // every other field matches
            Assert.Equal(original.ContentBlockId, record.ContentBlockId);
            Assert.Equal(original.DateTicks, record.DateTicks);
            Assert.Equal(original.MessageSize, record.MessageSize);
            Assert.Equal(original.From, record.From);
            Assert.Equal(original.Subject, record.Subject);
            Assert.Equal(original.Preview, record.Preview);
        }
    }

    [Fact]
    public void Regenerate_RebuiltPagesMatchOriginalFolderPageByPage_ExceptFlagsReset()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        // A genuine multi-page folder: 175 members → ceil(175 / 80) = 3 pages.
        int count = FolderPage.TargetRecordsPerPage * 2 + 15;
        var (metaOffsets, _, byId) = SeedFolder(h, count);
        var folderId = UlidOf(2);

        // Build the ORIGINAL healthy folder the normal way — original listing records carrying LIVE
        // flags, packed into date-descending ~80-record pages via FolderPageStore and indexed by a
        // directory. This is the on-disk layout that exists before the pages are lost.
        var (originalDir, originalPageLocs, originalOrdered) =
            BuildOriginalFolder(h, folderId, byId, folderVersion: 7);
        Assert.Equal(3, originalDir.PageCount);
        Assert.Equal(count, originalOrdered.Count);
        Assert.All(originalOrdered, r => Assert.NotEqual(ListingFlags.None, r.Flags)); // genuinely live

        // The pages are lost; the directory survives. Regenerate from Tier 2 metadata alone.
        var result = h.Regenerator.Regenerate(originalDir, metaOffsets);
        Ok(result);
        var rebuiltDir = result.Value.Directory;

        // (1) Same page partitioning: page count, per-page record count, and per-page inclusive date
        // range are byte-for-byte the same as the original — the recovery reproduces the exact layout.
        Assert.Equal(originalDir.PageCount, rebuiltDir.PageCount);
        for (int p = 0; p < originalDir.PageCount; p++)
        {
            Assert.Equal(originalDir.PageEntries[p].EntryCount, rebuiltDir.PageEntries[p].EntryCount);
            Assert.Equal(originalDir.PageEntries[p].DateFrom, rebuiltDir.PageEntries[p].DateFrom);
            Assert.Equal(originalDir.PageEntries[p].DateTo, rebuiltDir.PageEntries[p].DateTo);
        }

        // (2) Record-by-record, page-by-page (pages read newest-first, concatenated in order): every
        // field equal to the original EXCEPT Flags, which regeneration resets to None everywhere.
        var originalRecords = ReadAllRecords(h, originalPageLocs);
        var rebuiltRecords = ReadAllRecords(h, result.Value.NewPages);
        Assert.Equal(count, originalRecords.Count);
        Assert.Equal(count, rebuiltRecords.Count);

        for (int k = 0; k < originalRecords.Count; k++)
        {
            var original = originalRecords[k];
            var rebuilt = rebuiltRecords[k];

            Assert.Equal(ListingFlags.None, rebuilt.Flags);   // flags reset to the recovery default
            Assert.NotEqual(original.Flags, rebuilt.Flags);   // and the original genuinely differed

            Assert.Equal(original.EmailHashedId, rebuilt.EmailHashedId); // every other field matches
            Assert.Equal(original.ContentBlockId, rebuilt.ContentBlockId);
            Assert.Equal(original.DateTicks, rebuilt.DateTicks);
            Assert.Equal(original.MessageSize, rebuilt.MessageSize);
            Assert.Equal(original.From, rebuilt.From);
            Assert.Equal(original.Subject, rebuilt.Subject);
            Assert.Equal(original.Preview, rebuilt.Preview);
        }
    }

    [Fact]
    public void Regenerate_RepacksIntoDateDescendingPages_SplittingAtTargetSize()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        // More than two pages' worth so splitting and cross-page ordering are exercised.
        int count = FolderPage.TargetRecordsPerPage * 2 + 15;
        var (metaOffsets, _, byId) = SeedFolder(h, count);
        var prior = FolderPageDirectory.Create(UlidOf(3), Array.Empty<PageEntry>(), folderVersion: 0);

        var result = h.Regenerator.Regenerate(prior, metaOffsets);
        Ok(result);

        var dir = result.Value.Directory;
        Assert.Equal(3, dir.PageCount);                          // ceil(175 / 80) = 3 pages
        Assert.Equal(count, result.Value.RecordCount);

        // Directory PageEntries are newest-first, and their DateTo is non-increasing.
        for (int i = 1; i < dir.PageCount; i++)
            Assert.True(dir.PageEntries[i - 1].DateTo >= dir.PageEntries[i].DateTo);

        // Concatenating pages newest-first yields one globally date-descending, complete sequence.
        var all = ReadAllRecords(h, result.Value.NewPages);
        Assert.Equal(count, all.Count);
        for (int i = 1; i < all.Count; i++)
            Assert.True(all[i - 1].DateTicks >= all[i].DateTicks);
        Assert.Equal(byId.Count, all.Select(r => r.EmailHashedId).Distinct().Count());
    }

    [Fact]
    public void Regenerate_EmptyMembership_ProducesEmptyDirectory()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        var prior = FolderPageDirectory.Create(UlidOf(4), Array.Empty<PageEntry>(), folderVersion: 1);
        var result = h.Regenerator.Regenerate(prior, Array.Empty<long>());
        Ok(result);

        Assert.Equal(0, result.Value.Directory.PageCount);
        Assert.Empty(result.Value.NewPages);
        Assert.Equal(2UL, result.Value.Directory.FolderVersion); // still a rewrite → bumped
    }

    // ---- AC3: regenerated directory carries a bumped FolderVersion ----------

    [Fact]
    public void Regenerate_SurvivingDirectory_BumpsFolderVersion_ClearsDeltaHead_KeepsFolderId()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        var (metaOffsets, _, _) = SeedFolder(h, 6);

        // The surviving directory: version 41, with a pending delta head, pointing at (now lost) pages.
        var prior = FolderPageDirectory.Create(
            folderId: UlidOf(5),
            pageEntries: new[] { new PageEntry(UlidOf(50), 100, 200, 80) },
            headDeltaBlockId: UlidOf(60),
            folderVersion: 41);

        var result = h.Regenerator.Regenerate(prior, metaOffsets);
        Ok(result);

        // Read the fresh directory back through the encrypted store.
        var read = h.DirectoryStore.ReadDirectory(result.Value.DirectoryLocation.Offset);
        Ok(read);
        Assert.Equal(42UL, read.Value.FolderVersion);            // bumped by exactly one
        Assert.Equal(prior.FolderId, read.Value.FolderId);       // same folder
        Assert.False(read.Value.HasPendingDelta);                // delta head cleared
        Assert.Equal(prior.FolderId, result.Value.DirectoryLocation.BlockId); // stable BlockId
    }

    [Fact]
    public void Regenerate_FreshStart_SeedsDirectoryAtGivenVersion()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        var h = NewHarness(stream, provider);
        using var mgr = h.Manager;

        var (metaOffsets, _, _) = SeedFolder(h, 4);

        // Directory itself was lost: no prior to rewrite. Default starts at version 0.
        var fresh = h.Regenerator.Regenerate(UlidOf(6), metaOffsets);
        Ok(fresh);
        Assert.Equal(0UL, fresh.Value.Directory.FolderVersion);
        Assert.Equal(UlidOf(6), fresh.Value.Directory.FolderId);

        // A caller that knows the last-known version can seed from it so replication still advances.
        var seeded = h.Regenerator.Regenerate(UlidOf(6), metaOffsets, startingFolderVersion: 9);
        Ok(seeded);
        Assert.Equal(9UL, seeded.Value.Directory.FolderVersion);
    }

    // ---- helpers ------------------------------------------------------------

    // A non-default flag set, rotated per record so every original record carries LIVE flags — a
    // regeneration that clears them is then observable on each record, not just some.
    private static ListingFlags LiveFlags(int i) => (i % 3) switch
    {
        0 => ListingFlags.Read | ListingFlags.Flagged,
        1 => ListingFlags.Answered,
        _ => ListingFlags.Read | ListingFlags.Draft,
    };

    // Builds the ORIGINAL healthy folder as a normal folder-build would: project each member's Tier 2
    // metadata to a listing record WITH live flags, sort them globally date-descending, split into
    // TargetRecordsPerPage pages written through FolderPageStore, and index them with a directory.
    // Returns the directory, the written page locations (newest-first), and the ordered records.
    private (FolderPageDirectory Dir, List<BlockLocation> PageLocs, List<ListingRecord> Ordered)
        BuildOriginalFolder(
            Harness h, byte[] folderId, Dictionary<V3Id, EmailMetadata> byId, ulong folderVersion)
    {
        var originals = new List<ListingRecord>();
        int i = 0;
        foreach (var meta in byId.Values)
            originals.Add(ListingRecordBuilder.FromMetadata(meta, LiveFlags(i++)));

        // The same canonical partitioning regeneration uses: global date-descending order, then
        // contiguous TargetRecordsPerPage slices. Identical membership ⇒ identical page boundaries.
        var sorted = FolderPage.FromRecords(originals).Records;

        var pageLocs = new List<BlockLocation>();
        var entries = new List<PageEntry>();
        for (int start = 0; start < sorted.Count; start += FolderPage.TargetRecordsPerPage)
        {
            int length = Math.Min(FolderPage.TargetRecordsPerPage, sorted.Count - start);
            var chunk = new List<ListingRecord>(length);
            for (int j = 0; j < length; j++)
                chunk.Add(sorted[start + j]);

            var written = h.PageStore.WritePage(FolderPage.FromSortedRecords(chunk));
            Ok(written);
            pageLocs.Add(written.Value);
            entries.Add(new PageEntry(
                written.Value.BlockId,
                dateFrom: chunk[^1].DateTicks,
                dateTo: chunk[0].DateTicks,
                entryCount: chunk.Count));
        }

        var dir = FolderPageDirectory.Create(folderId, entries, folderVersion: folderVersion);
        return (dir, pageLocs, sorted.ToList());
    }

    // A FileStream that records every absolute (SeekOrigin.Begin) seek the block manager issues.
    // BlockManager reads and appends both seek from Begin to a target offset, so this captures the
    // exact set of block offsets touched — reads land on the target block, appends land at EOF.
    private sealed class RecordingFileStream : FileStream
    {
        public RecordingFileStream(string path)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite) { }

        public readonly List<long> SeekOffsets = new();

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin)
                SeekOffsets.Add(offset);
            return base.Seek(offset, origin);
        }
    }

    // The regenerator returns the freshly written page locations in the same newest-first order as
    // the directory's PageEntries, so reading them in order reconstructs the whole folder listing.
    private static List<ListingRecord> ReadAllRecords(Harness h, IReadOnlyList<BlockLocation> pages)
    {
        var all = new List<ListingRecord>();
        foreach (var page in pages)
        {
            var read = h.PageStore.ReadPage(page.Offset);
            Ok(read);
            all.AddRange(read.Value.Records);
        }
        return all;
    }
}
