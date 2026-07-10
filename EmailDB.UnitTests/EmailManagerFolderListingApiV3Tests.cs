using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the folder listing API (story US-EMDB-87, task US-EMDB-87-6):
/// <see cref="EmailManager.ListFolder"/> and its date-jump variant
/// <see cref="EmailManager.ListFolderFromDate"/> (docs/Folder_Listing.md Section 3, "List a page").
///
/// <para>The DoD is asserted end-to-end against a real on-disk file: emails added through the write
/// pipeline become a pending <c>FolderDeltaLog</c> chain, and <see cref="EmailManager.ListFolder"/>
/// returns a <b>stable date-descending slice for any offset within the folder</b> with those pending
/// deltas (and the effects of move/delete/flag operations) merged over the compiled pages. The
/// date-jump variant seeks to a date at record granularity.</para>
/// </summary>
public class EmailManagerFolderListingApiV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-folderlist-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(string subject, string body) =>
        Encoding.UTF8.GetBytes(
            $"From: sender@example.com\r\nTo: rcpt@example.com\r\nSubject: {subject}\r\n\r\n{body}");

    private static FolderPageDirectory NewFolder() =>
        FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

    private static AddEmailRequest Request(FolderPageDirectory folder, byte[] mime, long ticks) => new()
    {
        RawContent = mime,
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2-metadata-payload"),
        DateTicks = ticks,
        Flags = ListingFlags.None,
        From = "sender@example.com",
        Subject = "probe",
        Preview = "preview body text",
    };

    /// <summary>
    /// Adds <paramref name="dates"/> emails (one per tick value) to a fresh folder built purely from the
    /// pending delta chain (AddEmail appends an Add delta per email, no page compiled), threading the
    /// COW-advanced directory across the batch. Returns the final directory (its FolderId is the listing key).
    /// </summary>
    private static FolderPageDirectory AddAll(EmailManager mgr, IEnumerable<long> dates, out List<long> added)
    {
        var folder = NewFolder();
        added = new List<long>();
        int i = 0;
        foreach (var ticks in dates)
        {
            var r = mgr.AddEmail(Request(folder, Mime($"s{i}", $"body number {i} @ {ticks}"), ticks));
            Ok(r);
            folder = r.Value.Folder!;
            added.Add(ticks);
            i++;
        }
        return folder;
    }

    // --------------------------------------------------------- Stable date-descending slices

    [Fact]
    public void ListFolder_returns_a_stable_date_descending_page_for_any_offset()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Add in a deliberately shuffled date order; the listing must come back newest-first regardless.
        var dates = new long[] { 500, 100, 900, 300, 700, 200, 800, 400, 600, 1000 };
        var folder = AddAll(mgr, dates, out _);
        var folderId = folder.FolderId;

        long[] expectedDesc = dates.OrderByDescending(d => d).ToArray(); // 1000..100

        // Whole folder in one page: exactly the descending order, total = 10, no more.
        var all = mgr.ListFolder(folderId, pageOffset: 0, pageSize: 100);
        Ok(all);
        Assert.Equal(10, all.Value.TotalCount);
        Assert.Equal(expectedDesc, all.Value.Records.Select(r => r.DateTicks).ToArray());
        Assert.False(all.Value.HasMore);
        Assert.Equal(0, all.Value.Offset);

        // Every offset returns the matching contiguous window of the SAME descending order (stability).
        const int size = 3;
        for (int offset = 0; offset <= 12; offset++)
        {
            var page = mgr.ListFolder(folderId, offset, size);
            Ok(page);
            int start = Math.Min(offset, expectedDesc.Length);
            int take = Math.Min(size, expectedDesc.Length - start);
            Assert.Equal(expectedDesc.Skip(start).Take(take).ToArray(),
                page.Value.Records.Select(r => r.DateTicks).ToArray());
            Assert.Equal(start, page.Value.Offset);
            Assert.Equal(10, page.Value.TotalCount);
            Assert.Equal(start + take < expectedDesc.Length, page.Value.HasMore);
        }

        // Reading the same offset twice yields identical rows (stable across reads).
        var first = mgr.ListFolder(folderId, 4, size);
        var again = mgr.ListFolder(folderId, 4, size);
        Ok(first);
        Ok(again);
        Assert.Equal(
            first.Value.Records.Select(r => r.EmailHashedId).ToArray(),
            again.Value.Records.Select(r => r.EmailHashedId).ToArray());
    }

    [Fact]
    public void ListFolder_offset_past_the_end_is_a_clean_empty_slice()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = AddAll(mgr, new long[] { 10, 20, 30 }, out _);
        var page = mgr.ListFolder(folder.FolderId, pageOffset: 99, pageSize: 10);
        Ok(page);
        Assert.Empty(page.Value.Records);
        Assert.Equal(3, page.Value.TotalCount);
        Assert.Equal(3, page.Value.Offset); // clamped
        Assert.False(page.Value.HasMore);
    }

    // --------------------------------------------------------- Deltas merged (delete + flag)

    [Fact]
    public void ListFolder_merges_pending_delete_and_flag_change_deltas()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Add three emails; capture the middle one's id to delete and the newest to re-flag.
        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, Mime("a", "oldest"), 100)); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, Mime("b", "middle"), 200)); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, Mime("c", "newest"), 300)); Ok(c); folder = c.Value.Folder!;

        // Delete the middle email; flag the newest as Flagged | Read. Both are pending deltas.
        var del = mgr.DeleteEmail(new DeleteEmailRequest { Folder = folder, EmailId = b.Value.EmailId, DateTicks = 200 });
        Ok(del);
        folder = del.Value.Folder;
        var newFlags = ListingFlags.Flagged | ListingFlags.Read;
        var flag = mgr.ChangeFlags(folder, c.Value.EmailId, newFlags);
        Ok(flag);
        folder = flag.Value.Folder;

        var listing = mgr.ListFolder(folder.FolderId, 0, 100);
        Ok(listing);

        // The deleted middle email is gone; the remaining two are still date-descending.
        Assert.Equal(2, listing.Value.TotalCount);
        Assert.Equal(new long[] { 300, 100 }, listing.Value.Records.Select(r => r.DateTicks).ToArray());
        Assert.DoesNotContain(listing.Value.Records, r => r.EmailHashedId == b.Value.EmailId);

        // The newest email carries the merged flags.
        var newest = listing.Value.Records.Single(r => r.EmailHashedId == c.Value.EmailId);
        Assert.Equal(newFlags, newest.Flags);
    }

    [Fact]
    public void ListFolder_shows_a_moved_email_arriving_in_the_target_folder()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        byte[] mime = Mime("move", "moves to another folder");
        long ticks = 424242;
        var source = NewFolder();
        var added = mgr.AddEmail(Request(source, mime, ticks));
        Ok(added);
        source = added.Value.Folder!;
        var target = NewFolder();

        var record = new ListingRecord
        {
            EmailHashedId = added.Value.EmailId,
            ContentBlockId = added.Value.ContentBlockId!,
            DateTicks = ticks,
            Flags = ListingFlags.None,
            MessageSize = mime.Length,
            From = "sender@example.com",
            Subject = "probe",
            Preview = "preview body text",
        };
        var move = mgr.MoveEmail(new MoveEmailRequest { SourceFolder = source, TargetFolder = target, Record = record });
        Ok(move);

        // Source listing no longer contains the email; target listing shows exactly it.
        var src = mgr.ListFolder(move.Value.SourceFolder.FolderId, 0, 100);
        Ok(src);
        Assert.DoesNotContain(src.Value.Records, r => r.EmailHashedId == added.Value.EmailId);

        var dst = mgr.ListFolder(move.Value.TargetFolder.FolderId, 0, 100);
        Ok(dst);
        var arrived = Assert.Single(dst.Value.Records);
        Assert.Equal(added.Value.EmailId, arrived.EmailHashedId);
        Assert.Equal(added.Value.ContentBlockId, arrived.ContentBlockId);
    }

    [Fact]
    public void Move_of_a_committed_email_is_listed_in_both_folders_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] mime = Mime("movecommit", "added in one session, moved in the next, listed in a third");
        long ticks = 777000;
        FolderPageDirectory source;
        V3Id id;
        byte[] contentBlockId;

        // Session 1: add the email and commit it (Close writes the final Checkpoint).
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
            Ok(added);
            source = added.Value.Folder!;
            id = added.Value.EmailId;
            contentBlockId = added.Value.ContentBlockId!;
            Assert.True(mgr.Close().IsSuccess);
        }

        // Session 2: the email predates this session, so the move's deltas chain over committed
        // state. The move must be visible through the public listing API immediately.
        byte[] srcId, tgtId;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var record = new ListingRecord
            {
                EmailHashedId = id,
                ContentBlockId = contentBlockId,
                DateTicks = ticks,
                Flags = ListingFlags.None,
                MessageSize = mime.Length,
                From = "sender@example.com",
                Subject = "probe",
                Preview = "preview body text",
            };
            var move = mgr.MoveEmail(new MoveEmailRequest
            {
                SourceFolder = source,
                TargetFolder = NewFolder(),
                Record = record,
            });
            Ok(move);
            srcId = move.Value.SourceFolder.FolderId;
            tgtId = move.Value.TargetFolder.FolderId;

            var src = mgr.ListFolder(srcId, 0, 100);
            Ok(src);
            Assert.Empty(src.Value.Records);
            var dst = mgr.ListFolder(tgtId, 0, 100);
            Ok(dst);
            Assert.Equal(id, Assert.Single(dst.Value.Records).EmailHashedId);
            Assert.True(mgr.Close().IsSuccess);
        }

        // Session 3: the committed move is durable — both listings agree after reopen, the row still
        // names the ORIGINAL content block, and GetEmail round-trips the identical raw bytes.
        using var reopened = EmailManager.Open(_path).Value;
        var srcAgain = reopened.ListFolder(srcId, 0, 100);
        Ok(srcAgain);
        Assert.Empty(srcAgain.Value.Records);
        Assert.Equal(0, srcAgain.Value.TotalCount);

        var dstAgain = reopened.ListFolder(tgtId, 0, 100);
        Ok(dstAgain);
        var row = Assert.Single(dstAgain.Value.Records);
        Assert.Equal(id, row.EmailHashedId);
        Assert.Equal(contentBlockId, row.ContentBlockId);

        var read = reopened.GetEmail(id);
        Ok(read);
        Assert.True(read.Value.Found);
        Assert.Equal(mime, read.Value.Content);
    }

    // --------------------------------------------------------- Compiled pages + deltas merged

    [Fact]
    public void ListFolder_merges_pending_deltas_over_compiled_pages()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Build a folder with a compiled page directly through the manager's stores, then chain a delta.
        var pageStore = mgr.Folders!;
        var dirStore = mgr.FolderDirectory!;
        var deltaStore = mgr.FolderDeltas!;

        ListingRecord Rec(int seed, long date) => new()
        {
            EmailHashedId = V3Id.ComputeFromRawContent(Mime($"p{seed}", $"page row {seed}")),
            ContentBlockId = new UlidGenerator().Next(),
            DateTicks = date,
            Flags = ListingFlags.None,
            MessageSize = 100 + seed,
            From = $"p{seed}@example.com",
            Subject = $"Page {seed}",
            Preview = $"preview {seed}",
        };

        var pageRecs = new[] { Rec(1, 500), Rec(2, 400), Rec(3, 300) };
        var page = FolderPage.FromRecords(pageRecs);
        var pageLoc = pageStore.WritePage(page);
        Ok(pageLoc);

        var folderId = new UlidGenerator().Next();
        var dir0 = FolderPageDirectory.Create(
            folderId,
            new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 300, dateTo: 500, entryCount: page.Count) });

        // A pending delta: add a row @450 (lands between the page rows) and delete the @400 row.
        var add = Rec(9, 450);
        var appended = deltaStore.AppendChained(dir0, new[]
        {
            FolderDeltaEntry.Add(add),
            FolderDeltaEntry.Delete(pageRecs[1].EmailHashedId), // delete the @400 row
        });
        Ok(appended);
        Ok(dirStore.WriteDirectory(appended.Value.Directory));

        var listing = mgr.ListFolder(folderId, 0, 100);
        Ok(listing);

        // Effective: 500, 450 (added), 300 — the @400 page row is masked by the pending delete.
        Assert.Equal(new long[] { 500, 450, 300 }, listing.Value.Records.Select(r => r.DateTicks).ToArray());
        Assert.Contains(listing.Value.Records, r => r.EmailHashedId == add.EmailHashedId);
        Assert.DoesNotContain(listing.Value.Records, r => r.EmailHashedId == pageRecs[1].EmailHashedId);
    }

    /// <summary>
    /// Task US-EMDB-87-4 (story acceptance criterion): for a folder large enough to span <b>multiple</b>
    /// compiled <see cref="FolderPage"/>s plus a multi-block pending delta chain, every offset — swept
    /// across all page/slice boundaries at several page sizes — returns a strictly date-descending,
    /// non-overlapping, complete tiling of the folder's effective listing. Slices are stable across
    /// repeated reads and identical after the file is closed and reopened.
    /// </summary>
    [Fact]
    public void ListFolder_tiles_multiple_compiled_pages_plus_deltas_across_every_offset_and_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        int seed = 0;
        var byDate = new Dictionary<long, ListingRecord>();
        ListingRecord Rec(long date, ListingFlags flags = ListingFlags.None)
        {
            int s = seed++;
            var rec = new ListingRecord
            {
                EmailHashedId = V3Id.ComputeFromRawContent(Mime($"m{s}", $"row {s} @ {date}")),
                ContentBlockId = new UlidGenerator().Next(),
                DateTicks = date,
                Flags = flags,
                MessageSize = 100 + s,
                From = $"m{s}@example.com",
                Subject = $"Subject {s}",
                Preview = $"preview {s}",
            };
            byDate[date] = rec;
            return rec;
        }

        byte[] folderId;
        var flagged = ListingFlags.Flagged | ListingFlags.Read;

        // ---- Build the folder: THREE compiled pages (disjoint, newest-first) + a THREE-block delta chain.
        using (var mgr = EmailManager.Open(_path).Value)
        {
            // Distinct dates throughout, so the expected order is fully determined by date (no tie-break
            // ambiguity) yet the merge still has to interleave delta rows between compiled-page rows.
            var p1 = new[] { Rec(1000), Rec(900), Rec(800), Rec(700) }; // newest page
            var p2 = new[] { Rec(600), Rec(500), Rec(400), Rec(300) };
            var p3 = new[] { Rec(250), Rec(200), Rec(150), Rec(100) };  // oldest page

            PageEntry Compile(ListingRecord[] recs)
            {
                var page = FolderPage.FromRecords(recs);
                var loc = mgr.Folders!.WritePage(page);
                Ok(loc);
                return new PageEntry(
                    loc.Value.BlockId,
                    dateFrom: recs.Min(r => r.DateTicks),
                    dateTo: recs.Max(r => r.DateTicks),
                    entryCount: page.Count);
            }

            folderId = new UlidGenerator().Next();
            var dir = FolderPageDirectory.Create(folderId, new[] { Compile(p1), Compile(p2), Compile(p3) });

            // Rows added only through the pending delta chain — they land BETWEEN compiled-page rows and
            // even outside every page's range (1100 above all, 50 below all), so the merge must interleave.
            var a1100 = Rec(1100);
            var a850 = Rec(850);
            var a550 = Rec(550);
            var a125 = Rec(125);
            var a50 = Rec(50);

            FolderPageDirectory Chain(FolderPageDirectory cur, params FolderDeltaEntry[] entries)
            {
                var r = mgr.FolderDeltas!.AppendChained(cur, entries);
                Ok(r);
                return r.Value.Directory;
            }

            // Three chained blocks (chronological, root-first): the walk must reduce the whole chain.
            dir = Chain(dir,
                FolderDeltaEntry.Add(a850),
                FolderDeltaEntry.Add(a550),
                FolderDeltaEntry.Delete(byDate[700].EmailHashedId));      // mask a compiled-page row
            dir = Chain(dir,
                FolderDeltaEntry.Add(a1100),
                FolderDeltaEntry.Delete(byDate[300].EmailHashedId),       // mask a compiled-page row
                FolderDeltaEntry.FlagChange(byDate[900].EmailHashedId, flagged)); // re-flag a compiled row
            dir = Chain(dir,
                FolderDeltaEntry.Add(a125),
                FolderDeltaEntry.Add(a50));
            Ok(mgr.FolderDirectory!.WriteDirectory(dir));

            // ---- Expected effective listing: page dates minus the two deletes, plus the five added rows,
            // with the @900 row carrying the merged flags. All distinct ⇒ pure date-descending order.
            var deleted = new HashSet<long> { 700, 300 };
            var expected = byDate.Keys
                .Where(d => !deleted.Contains(d))
                .OrderByDescending(d => d)
                .Select(d => (Date: d, Id: (V3Id)byDate[d].EmailHashedId,
                              Flags: d == 900 ? flagged : byDate[d].Flags))
                .ToList();
            Assert.Equal(15, expected.Count); // 12 page rows - 2 deletes + 5 adds

            AssertStableTiling(mgr, folderId, expected);

            // The merged flags on the compiled @900 row are visible through the paged API.
            var whole = mgr.ListFolder(folderId, 0, 1000);
            Ok(whole);
            Assert.Equal(flagged, whole.Value.Records.Single(r => r.DateTicks == 900).Flags);

            Assert.True(mgr.Close().IsSuccess);

            // ---- After reopen, the identical tiling holds (durable, offset-stable).
            using var reopened = EmailManager.Open(_path).Value;
            AssertStableTiling(reopened, folderId, expected);
            var wholeAgain = reopened.ListFolder(folderId, 0, 1000);
            Ok(wholeAgain);
            Assert.Equal(flagged, wholeAgain.Value.Records.Single(r => r.DateTicks == 900).Flags);
        }
    }

    /// <summary>
    /// Asserts that <see cref="EmailManager.ListFolder"/> returns a strictly date-descending, non-overlapping,
    /// complete tiling of <paramref name="expected"/> (the whole effective listing, newest-first) for every
    /// offset at several page sizes, that offsets clamp cleanly past the end, and that repeated reads of the
    /// same offset are byte-for-byte stable.
    /// </summary>
    private static void AssertStableTiling(
        EmailManager mgr, byte[] folderId, IReadOnlyList<(long Date, V3Id Id, ListingFlags Flags)> expected)
    {
        long[] expDates = expected.Select(e => e.Date).ToArray();
        V3Id[] expIds = expected.Select(e => e.Id).ToArray();

        // Whole folder in one page: exact descending order, strictly decreasing, ids/flags all match.
        var all = mgr.ListFolder(folderId, 0, 1000);
        Ok(all);
        Assert.Equal(expected.Count, all.Value.TotalCount);
        Assert.Equal(expDates, all.Value.Records.Select(r => r.DateTicks).ToArray());
        Assert.Equal(expIds, all.Value.Records.Select(r => (V3Id)r.EmailHashedId).ToArray());
        for (int i = 1; i < all.Value.Records.Count; i++)
            Assert.True(all.Value.Records[i - 1].DateTicks > all.Value.Records[i].DateTicks,
                "listing must be strictly date-descending");
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Flags, all.Value.Records[i].Flags);

        // Sweep every offset (well past the end) at several page sizes, including sizes that straddle the
        // compiled-page boundaries. Each slice is the matching window of the SAME order; page-aligned slices
        // reassemble the whole listing with no gaps and no overlaps.
        foreach (int size in new[] { 1, 2, 3, 4, 5, 7, expected.Count, 1000 })
        {
            for (int offset = 0; offset <= expected.Count + 2; offset++)
            {
                var page = mgr.ListFolder(folderId, offset, size);
                Ok(page);
                int start = Math.Min(offset, expected.Count);
                int take = Math.Min(size, expected.Count - start);
                Assert.Equal(start, page.Value.Offset);            // clamped offset
                Assert.Equal(expected.Count, page.Value.TotalCount);
                Assert.Equal(expDates.Skip(start).Take(take).ToArray(),
                    page.Value.Records.Select(r => r.DateTicks).ToArray());
                Assert.Equal(expIds.Skip(start).Take(take).ToArray(),
                    page.Value.Records.Select(r => (V3Id)r.EmailHashedId).ToArray());
                Assert.Equal(start + take < expected.Count, page.Value.HasMore);
            }

            // Page-aligned tiling: concatenating successive slices reproduces the whole listing exactly
            // (proves non-overlapping + complete: any overlap or gap would break this equality).
            var reconstructed = new List<V3Id>();
            for (int offset = 0; offset < expected.Count; offset += size)
            {
                var page = mgr.ListFolder(folderId, offset, size);
                Ok(page);
                reconstructed.AddRange(page.Value.Records.Select(r => (V3Id)r.EmailHashedId));
            }
            Assert.Equal(expIds, reconstructed.ToArray());
        }

        // Stable across repeated reads: the same offset returns identical rows both times.
        var first = mgr.ListFolder(folderId, 6, 4);
        var again = mgr.ListFolder(folderId, 6, 4);
        Ok(first);
        Ok(again);
        Assert.Equal(
            first.Value.Records.Select(r => (V3Id)r.EmailHashedId).ToArray(),
            again.Value.Records.Select(r => (V3Id)r.EmailHashedId).ToArray());
    }

    [Fact]
    public void Move_out_of_a_compiled_page_masks_the_row_without_touching_the_page()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // A real email through the write pipeline (so GetEmail can prove the content survives) ...
        byte[] mime = Mime("compiledmove", "row lives in a compiled page, then moves out of it");
        long ticks = 400;
        var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
        Ok(added);

        var movedRec = new ListingRecord
        {
            EmailHashedId = added.Value.EmailId,
            ContentBlockId = added.Value.ContentBlockId!,
            DateTicks = ticks,
            Flags = ListingFlags.None,
            MessageSize = mime.Length,
            From = "sender@example.com",
            Subject = "probe",
            Preview = "preview body text",
        };
        var keeperRec = new ListingRecord
        {
            EmailHashedId = V3Id.ComputeFromRawContent(Mime("keeper", "stays in the source page")),
            ContentBlockId = new UlidGenerator().Next(),
            DateTicks = 500,
            Flags = ListingFlags.None,
            MessageSize = 123,
            From = "keeper@example.com",
            Subject = "Keeper",
            Preview = "keeper preview",
        };

        // ... whose listing row sits in a COMPILED FolderPage of the source folder (no pending delta).
        var page = FolderPage.FromRecords(new[] { keeperRec, movedRec });
        var pageLoc = mgr.Folders!.WritePage(page);
        Ok(pageLoc);
        var source = FolderPageDirectory.Create(
            new UlidGenerator().Next(),
            new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 400, dateTo: 500, entryCount: page.Count) });
        Ok(mgr.FolderDirectory!.WriteDirectory(source));

        var pageBytesBefore = mgr.BlockManager.Read(pageLoc.Value.Offset);
        Ok(pageBytesBefore);
        byte[] rawPageBefore = (byte[])pageBytesBefore.Value.Payload.Clone();

        var move = mgr.MoveEmail(new MoveEmailRequest
        {
            SourceFolder = source,
            TargetFolder = NewFolder(),
            Record = movedRec,
        });
        Ok(move);

        // Source listing: the Delete delta masks the compiled page row; the keeper is untouched.
        var src = mgr.ListFolder(move.Value.SourceFolder.FolderId, 0, 100);
        Ok(src);
        var kept = Assert.Single(src.Value.Records);
        Assert.Equal(keeperRec.EmailHashedId, kept.EmailHashedId);

        // Target listing: exactly the moved row, still naming the original content block.
        var dst = mgr.ListFolder(move.Value.TargetFolder.FolderId, 0, 100);
        Ok(dst);
        var arrived = Assert.Single(dst.Value.Records);
        Assert.Equal(added.Value.EmailId, arrived.EmailHashedId);
        Assert.Equal(added.Value.ContentBlockId, arrived.ContentBlockId);

        // The compiled page block itself was NOT rewritten (byte-identical at the same offset), and
        // the moved email's content still reads back through GetEmail.
        var pageBytesAfter = mgr.BlockManager.Read(pageLoc.Value.Offset);
        Ok(pageBytesAfter);
        Assert.Equal(rawPageBefore, pageBytesAfter.Value.Payload);
        var read = mgr.GetEmail(added.Value.EmailId);
        Ok(read);
        Assert.True(read.Value.Found);
        Assert.Equal(mime, read.Value.Content);
    }

    // --------------------------------------------------------- Flag change visible in next listing read

    /// <summary>
    /// Acceptance criterion for story US-EMDB-87: a flag change is visible in the very next listing read.
    /// Here the re-flagged row lives in a <b>committed compiled <see cref="FolderPage"/></b> (no pending
    /// delta), so <see cref="EmailManager.ChangeFlags"/> chains a <c>FlagChange</c> delta over the compiled
    /// page; the next <see cref="EmailManager.ListFolder"/> read must overlay the new flags on the compiled
    /// row without rewriting the page block. (The same-session, pending-Add case is covered by
    /// <see cref="ListFolder_merges_pending_delete_and_flag_change_deltas"/>.)
    /// </summary>
    [Fact]
    public void ChangeFlags_on_a_committed_compiled_row_is_visible_in_the_next_listing()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // A listing row sitting in a COMPILED FolderPage (no pending delta), flags initially None.
        var row = new ListingRecord
        {
            EmailHashedId = V3Id.ComputeFromRawContent(Mime("flagcompiled", "row lives in a compiled page")),
            ContentBlockId = new UlidGenerator().Next(),
            DateTicks = 500,
            Flags = ListingFlags.None,
            MessageSize = 222,
            From = "flag@example.com",
            Subject = "Flag",
            Preview = "flag preview",
        };
        var page = FolderPage.FromRecords(new[] { row });
        var pageLoc = mgr.Folders!.WritePage(page);
        Ok(pageLoc);
        var folder = FolderPageDirectory.Create(
            new UlidGenerator().Next(),
            new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 500, dateTo: 500, entryCount: page.Count) });
        Ok(mgr.FolderDirectory!.WriteDirectory(folder));
        var folderId = folder.FolderId;

        // Baseline: the compiled row lists with its original (None) flags.
        var before = mgr.ListFolder(folderId, 0, 100);
        Ok(before);
        Assert.Equal(ListingFlags.None, Assert.Single(before.Value.Records).Flags);

        // Capture the compiled page's exact on-disk bytes to prove the flag change does not rewrite it.
        var pageBytesBefore = mgr.BlockManager.Read(pageLoc.Value.Offset);
        Ok(pageBytesBefore);
        byte[] rawPageBefore = (byte[])pageBytesBefore.Value.Payload.Clone();

        // ChangeFlags chains a FlagChange delta over the committed compiled page.
        var newFlags = ListingFlags.Flagged | ListingFlags.Read;
        var changed = mgr.ChangeFlags(folder, row.EmailHashedId, newFlags);
        Ok(changed);

        // The VERY NEXT listing read reflects the new flags, merged over the compiled page row.
        var after = mgr.ListFolder(folderId, 0, 100);
        Ok(after);
        var reflagged = Assert.Single(after.Value.Records);
        Assert.Equal(row.EmailHashedId, reflagged.EmailHashedId);
        Assert.Equal(newFlags, reflagged.Flags);

        // The compiled page block itself was NOT rewritten (byte-identical at the same offset).
        var pageBytesAfter = mgr.BlockManager.Read(pageLoc.Value.Offset);
        Ok(pageBytesAfter);
        Assert.Equal(rawPageBefore, pageBytesAfter.Value.Payload);
    }

    /// <summary>
    /// Acceptance criterion for story US-EMDB-87 across a checkpoint boundary: a flag change made in a
    /// later session (over a committed row from an earlier session) is visible in that session's next
    /// listing read, and the committed FlagChange delta remains visible after the file is closed and
    /// reopened.
    /// </summary>
    [Fact]
    public void ChangeFlags_is_visible_in_the_listing_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] folderId;
        V3Id id;
        long ticks = 858000;
        var newFlags = ListingFlags.Flagged | ListingFlags.Read | ListingFlags.Answered;

        // Session 1: add an email (flags None) and commit it (Close writes the final Checkpoint).
        FolderPageDirectory folder;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("flagreopen", "flagged in a later session"), ticks));
            Ok(added);
            folder = added.Value.Folder!;
            folderId = folder.FolderId;
            id = added.Value.EmailId;

            var baseline = mgr.ListFolder(folderId, 0, 100);
            Ok(baseline);
            Assert.Equal(ListingFlags.None, Assert.Single(baseline.Value.Records).Flags);
            Assert.True(mgr.Close().IsSuccess);
        }

        // Session 2: the row predates this session (committed), so ChangeFlags chains over committed
        // state. The very next listing read reflects the new flags.
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var changed = mgr.ChangeFlags(folder, id, newFlags);
            Ok(changed);

            var after = mgr.ListFolder(folderId, 0, 100);
            Ok(after);
            Assert.Equal(newFlags, Assert.Single(after.Value.Records).Flags);
            Assert.True(mgr.Close().IsSuccess);
        }

        // Session 3: the committed flag change is durable — the listing still reflects the new flags.
        using var reopened = EmailManager.Open(_path).Value;
        var listing = reopened.ListFolder(folderId, 0, 100);
        Ok(listing);
        var reflagged = Assert.Single(listing.Value.Records);
        Assert.Equal(id, reflagged.EmailHashedId);
        Assert.Equal(newFlags, reflagged.Flags);
    }

    // --------------------------------------------------------- Date-jump variant

    [Fact]
    public void ListFolderFromDate_seeks_to_the_newest_row_at_or_before_the_date()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Dates 100,200,...,1000 → descending 1000..100 at offsets 0..9.
        var folder = AddAll(mgr, Enumerable.Range(1, 10).Select(i => (long)(i * 100)), out _);
        var folderId = folder.FolderId;

        // Exact hit at 700 → first row with date <= 700 is 700 itself (offset 3 in 1000,900,800,700,...).
        var at700 = mgr.ListFolderFromDate(folderId, 700, pageSize: 4);
        Ok(at700);
        Assert.Equal(3, at700.Value.Offset);
        Assert.Equal(new long[] { 700, 600, 500, 400 }, at700.Value.Records.Select(r => r.DateTicks).ToArray());

        // Between two dates: 650 → first row <= 650 is 600 (offset 4).
        var at650 = mgr.ListFolderFromDate(folderId, 650, pageSize: 2);
        Ok(at650);
        Assert.Equal(4, at650.Value.Offset);
        Assert.Equal(new long[] { 600, 500 }, at650.Value.Records.Select(r => r.DateTicks).ToArray());

        // Newer than everything → starts at the top.
        var atTop = mgr.ListFolderFromDate(folderId, 99999, pageSize: 2);
        Ok(atTop);
        Assert.Equal(0, atTop.Value.Offset);
        Assert.Equal(new long[] { 1000, 900 }, atTop.Value.Records.Select(r => r.DateTicks).ToArray());

        // Older than everything → empty tail past the end.
        var atBottom = mgr.ListFolderFromDate(folderId, 1, pageSize: 5);
        Ok(atBottom);
        Assert.Equal(10, atBottom.Value.Offset);
        Assert.Empty(atBottom.Value.Records);
    }

    // --------------------------------------------------------- Reopen + guards

    [Fact]
    public void ListFolder_reads_a_committed_folder_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] folderId;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folder = AddAll(mgr, new long[] { 30, 10, 20 }, out _);
            folderId = folder.FolderId;
            Assert.True(mgr.Close().IsSuccess);
        }

        using var reopened = EmailManager.Open(_path).Value;
        var listing = reopened.ListFolder(folderId, 0, 100);
        Ok(listing);
        Assert.Equal(new long[] { 30, 20, 10 }, listing.Value.Records.Select(r => r.DateTicks).ToArray());
    }

    [Fact]
    public void ListFolder_on_an_unopened_manager_or_unknown_folder_fails()
    {
        using var created = EmailManager.Create(_path).Value; // Create, never Open
        Assert.True(created.ListFolder(new UlidGenerator().Next(), 0).IsFailure);
        created.Dispose();

        using var mgr = EmailManager.Open(_path).Value;
        // A folder id that was never written this session does not resolve.
        Assert.True(mgr.ListFolder(new UlidGenerator().Next(), 0).IsFailure);
        // Bad arguments.
        var folder = AddAll(mgr, new long[] { 1 }, out _);
        Assert.True(mgr.ListFolder(folder.FolderId, -1).IsFailure);
        Assert.True(mgr.ListFolder(folder.FolderId, 0, pageSize: 0).IsFailure);
    }
}
