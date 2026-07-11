using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the folder-scoped listing scan search (story US-EMDB-92, task US-EMDB-92-4,
/// docs/Search.md Phase 2): <see cref="EmailManager.SearchFolder"/> and its whole-mailbox fallback
/// <see cref="EmailManager.SearchMailbox"/>, plus the pure <see cref="ListingScanMatcher"/>.
///
/// <para>This is the implementation task's own basic coverage — folder-scoped match, case handling,
/// and empty results; the three story acceptance criteria (50K warm timing, pending-delta inclusion,
/// whole-mailbox fallback) get dedicated sibling test tasks (US-EMDB-92-1/2/3).</para>
/// </summary>
public class ListingScanSearchV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-scansearch-{Guid.NewGuid():N}.emdb");

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

    private static AddEmailRequest Request(
        FolderPageDirectory folder, byte[] mime, long ticks,
        string from, string subject, string preview) => new()
        {
            RawContent = mime,
            Folder = folder,
            MetadataPayload = Encoding.UTF8.GetBytes("tier2-metadata-payload"),
            DateTicks = ticks,
            Flags = ListingFlags.None,
            From = from,
            Subject = subject,
            Preview = preview,
        };

    private static ListingRecord Rec(long date, string from, string subject, string preview) => new()
    {
        EmailHashedId = V3Id.ComputeFromRawContent(Mime(subject, $"{from}|{preview}|{date}")),
        ContentBlockId = new UlidGenerator().Next(),
        DateTicks = date,
        Flags = ListingFlags.None,
        MessageSize = 100,
        From = from,
        Subject = subject,
        Preview = preview,
    };

    /// <summary>
    /// Builds a folder directly from compiled <paramref name="records"/> (one page) plus an optional pending
    /// <paramref name="deltas"/> chain, persisting the directory under a fresh folder id and returning that id.
    /// Lets the whole-mailbox tests place an exact record — same id in two folders, or a date tie — that the
    /// content-deduped <see cref="EmailManager.AddEmail"/> path cannot express.
    /// </summary>
    private static byte[] BuildFolder(
        EmailManager mgr, IReadOnlyList<ListingRecord> records, IReadOnlyList<FolderDeltaEntry>? deltas = null)
    {
        var page = FolderPage.FromRecords(records);
        var pageLoc = mgr.Folders!.WritePage(page);
        Ok(pageLoc);
        var folderId = new UlidGenerator().Next();
        var dir = FolderPageDirectory.Create(
            folderId,
            new[]
            {
                new PageEntry(
                    pageLoc.Value.BlockId,
                    dateFrom: records.Min(r => r.DateTicks),
                    dateTo: records.Max(r => r.DateTicks),
                    entryCount: page.Count),
            });
        if (deltas is { Count: > 0 })
        {
            var appended = mgr.FolderDeltas!.AppendChained(dir, deltas);
            Ok(appended);
            Ok(mgr.FolderDirectory!.WriteDirectory(appended.Value.Directory));
        }
        else
        {
            Ok(mgr.FolderDirectory!.WriteDirectory(dir));
        }
        return folderId;
    }

    // ------------------------------------------------------------------ Pure matcher

    [Fact]
    public void Matcher_is_case_insensitive_across_the_selected_fields()
    {
        var record = Rec(100, from: "Alice <alice@Example.com>", subject: "Quarterly Report", preview: "See the ATTACHED numbers");

        // Case-insensitive substring hits in each of the three fields.
        Assert.True(ListingScanMatcher.Matches(record, "quarterly", ListingSearchField.All));
        Assert.True(ListingScanMatcher.Matches(record, "ALICE", ListingSearchField.All));
        Assert.True(ListingScanMatcher.Matches(record, "attached", ListingSearchField.All));

        // A term that is present only in a non-selected field does not match.
        Assert.True(ListingScanMatcher.Matches(record, "alice", ListingSearchField.From));
        Assert.False(ListingScanMatcher.Matches(record, "alice", ListingSearchField.Subject));
        Assert.False(ListingScanMatcher.Matches(record, "alice", ListingSearchField.Preview));

        // A term present nowhere never matches; an empty field set never matches.
        Assert.False(ListingScanMatcher.Matches(record, "nonexistent", ListingSearchField.All));
        Assert.False(ListingScanMatcher.Matches(record, "quarterly", ListingSearchField.None));
    }

    // ------------------------------------------------------------------ Folder-scoped scan

    [Fact]
    public void SearchFolder_matches_subject_from_and_preview_case_insensitively_newest_first()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // Three matching emails (each hits a different field) plus one non-matching, distinct dates.
        var a = mgr.AddEmail(Request(folder, Mime("Invoice #100", "b"), 400, "billing@corp.com", "Invoice #100", "amount due")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, Mime("Hello", "c"), 300, "invoice-bot@corp.com", "Hello", "greetings")); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, Mime("Notes", "d"), 200, "someone@corp.com", "Notes", "your INVOICE is ready")); Ok(c); folder = c.Value.Folder!;
        var d = mgr.AddEmail(Request(folder, Mime("Lunch", "e"), 100, "friend@corp.com", "Lunch", "wanna eat")); Ok(d); folder = d.Value.Folder!;

        var found = mgr.SearchFolder(folder.FolderId, "invoice");
        Ok(found);
        Assert.False(found.Value.WholeMailbox);
        Assert.Equal(4, found.Value.RecordsScanned);
        Assert.Equal(3, found.Value.MatchCount);

        // Newest-first ordering preserved; every hit is tagged with the scanned folder.
        Assert.Equal(new long[] { 400, 300, 200 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
        Assert.All(found.Value.Hits, h => Assert.Equal(folder.FolderId, h.FolderId));
        Assert.DoesNotContain(found.Value.Hits, h => h.Record.EmailHashedId == d.Value.EmailId);
    }

    [Fact]
    public void SearchFolder_field_selection_narrows_the_match()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // "target" appears only in the From address of this email.
        var a = mgr.AddEmail(Request(folder, Mime("Subj", "x"), 100, "target@corp.com", "Subj", "nothing here")); Ok(a); folder = a.Value.Folder!;

        Assert.Equal(1, mgr.SearchFolder(folder.FolderId, "target", ListingSearchField.From).Value.MatchCount);
        Assert.Equal(0, mgr.SearchFolder(folder.FolderId, "target", ListingSearchField.Subject | ListingSearchField.Preview).Value.MatchCount);
    }

    [Fact]
    public void SearchFolder_with_no_match_is_a_clean_empty_result()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, Mime("Hello", "x"), 100, "a@corp.com", "Hello", "world")); Ok(a); folder = a.Value.Folder!;

        var found = mgr.SearchFolder(folder.FolderId, "zzz-no-such-term");
        Ok(found);
        Assert.Empty(found.Value.Hits);
        Assert.Equal(0, found.Value.MatchCount);
        Assert.Equal(1, found.Value.RecordsScanned);
    }

    [Fact]
    public void SearchFolder_respects_maxResults_cap_keeping_the_newest()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        for (int i = 0; i < 5; i++)
        {
            var r = mgr.AddEmail(Request(folder, Mime($"Report {i}", $"body {i}"), (i + 1) * 100, "x@corp.com", $"Report {i}", "quarterly report"));
            Ok(r);
            folder = r.Value.Folder!;
        }

        var found = mgr.SearchFolder(folder.FolderId, "report", ListingSearchField.All, maxResults: 2);
        Ok(found);
        Assert.Equal(2, found.Value.MatchCount);
        // The two newest (500, 400) are kept.
        Assert.Equal(new long[] { 500, 400 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
    }

    [Fact]
    public void SearchFolder_includes_pending_delta_rows_over_a_compiled_page()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // A compiled page with one matching row, plus a pending-delta Add with another match.
        var pageRec = Rec(500, "page@corp.com", "Compiled Widget", "in the page");
        var page = FolderPage.FromRecords(new[] { pageRec });
        var pageLoc = mgr.Folders!.WritePage(page);
        Ok(pageLoc);
        var folderId = new UlidGenerator().Next();
        var dir = FolderPageDirectory.Create(
            folderId,
            new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 500, dateTo: 500, entryCount: page.Count) });

        var deltaRec = Rec(600, "delta@corp.com", "Pending Widget", "not compiled yet");
        var appended = mgr.FolderDeltas!.AppendChained(dir, new[] { FolderDeltaEntry.Add(deltaRec) });
        Ok(appended);
        Ok(mgr.FolderDirectory!.WriteDirectory(appended.Value.Directory));

        var found = mgr.SearchFolder(folderId, "widget");
        Ok(found);
        Assert.Equal(2, found.Value.MatchCount);
        // Pending delta row (600) is newest and precedes the compiled page row (500).
        Assert.Equal(new long[] { 600, 500 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
    }

    [Fact]
    public void SearchFolder_excludes_a_compiled_row_masked_by_a_pending_delete()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Two matching rows compiled into a page; a pending Delete (the source side of a move, or a plain
        // delete) masks one. The masked row must no longer match in this folder even though the page still
        // physically carries it — the delta wins over the compiled listing.
        var kept = Rec(500, "keep@corp.com", "Widget Kept", "still here");
        var removed = Rec(400, "gone@corp.com", "Widget Removed", "moved away");
        var page = FolderPage.FromRecords(new[] { kept, removed });
        var pageLoc = mgr.Folders!.WritePage(page);
        Ok(pageLoc);
        var folderId = new UlidGenerator().Next();
        var dir = FolderPageDirectory.Create(
            folderId,
            new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 400, dateTo: 500, entryCount: page.Count) });

        var appended = mgr.FolderDeltas!.AppendChained(dir, new[] { FolderDeltaEntry.Delete(removed.EmailHashedId) });
        Ok(appended);
        Ok(mgr.FolderDirectory!.WriteDirectory(appended.Value.Directory));

        var found = mgr.SearchFolder(folderId, "widget");
        Ok(found);
        // Only the surviving row matches; the deleted/moved-out row is gone from this folder's results.
        Assert.Equal(1, found.Value.MatchCount);
        Assert.Equal(new long[] { 500 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
        Assert.DoesNotContain(found.Value.Hits, h => h.Record.EmailHashedId == removed.EmailHashedId);
    }

    [Fact]
    public void SearchFolder_flag_change_delta_updates_flags_without_duplicating_the_match()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // A single matching row compiled into a page, with a pending FlagChange marking it Read. The scan
        // must return exactly one hit carrying the new flags — a FlagChange overrides the row in place, it
        // does not append a second, duplicate listing record.
        var rec = Rec(500, "flag@corp.com", "Widget Flagged", "mark me read");
        Assert.Equal(ListingFlags.None, rec.Flags);
        var page = FolderPage.FromRecords(new[] { rec });
        var pageLoc = mgr.Folders!.WritePage(page);
        Ok(pageLoc);
        var folderId = new UlidGenerator().Next();
        var dir = FolderPageDirectory.Create(
            folderId,
            new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 500, dateTo: 500, entryCount: page.Count) });

        var appended = mgr.FolderDeltas!.AppendChained(
            dir, new[] { FolderDeltaEntry.FlagChange(rec.EmailHashedId, ListingFlags.Read) });
        Ok(appended);
        Ok(mgr.FolderDirectory!.WriteDirectory(appended.Value.Directory));

        var found = mgr.SearchFolder(folderId, "widget");
        Ok(found);
        // Exactly one hit — no duplicate from the flag delta — and it reflects the updated flags.
        Assert.Equal(1, found.Value.MatchCount);
        Assert.Equal(rec.EmailHashedId, found.Value.Hits[0].Record.EmailHashedId);
        Assert.Equal(ListingFlags.Read, found.Value.Hits[0].Record.Flags);
    }

    // ------------------------------------------------------------------ Whole-mailbox fallback

    [Fact]
    public void SearchMailbox_scans_every_folder_and_merges_newest_first()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var f1 = NewFolder();
        var a = mgr.AddEmail(Request(f1, Mime("Alpha", "x"), 100, "x@corp.com", "Alpha", "project apollo update")); Ok(a); f1 = a.Value.Folder!;
        var b = mgr.AddEmail(Request(f1, Mime("Beta", "y"), 300, "y@corp.com", "Beta", "no keyword")); Ok(b); f1 = b.Value.Folder!;

        var f2 = NewFolder();
        var c = mgr.AddEmail(Request(f2, Mime("Gamma", "z"), 200, "z@corp.com", "Gamma", "APOLLO mission notes")); Ok(c); f2 = c.Value.Folder!;

        var found = mgr.SearchMailbox(new[] { f1.FolderId, f2.FolderId }, "apollo");
        Ok(found);
        Assert.True(found.Value.WholeMailbox);
        Assert.Equal(3, found.Value.RecordsScanned);
        Assert.Equal(2, found.Value.MatchCount);

        // Cross-folder merge is date-descending: f2's @200 comes after f1's @100? No — 200 > 100, so newest-first is [200,100].
        Assert.Equal(new long[] { 200, 100 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
        // Each hit reports its own folder.
        Assert.Equal(f2.FolderId, found.Value.Hits[0].FolderId);
        Assert.Equal(f1.FolderId, found.Value.Hits[1].FolderId);
    }

    [Fact]
    public void SearchMailbox_with_no_folders_is_a_clean_empty_result()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var found = mgr.SearchMailbox(Array.Empty<byte[]>(), "anything");
        Ok(found);
        Assert.True(found.Value.WholeMailbox);
        Assert.Empty(found.Value.Hits);
        Assert.Equal(0, found.Value.RecordsScanned);
    }

    [Fact]
    public void SearchMailbox_applies_maxResults_cap_globally_across_folders_not_per_folder()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Two folders, each with three matching emails; the six matches interleave by date across folders
        // (f1: 100/300/500, f2: 200/400/600). A per-folder cap of 2 would keep 2 from each (four rows); the
        // correct global cap keeps only the two newest overall — 600 (f2) then 500 (f1).
        var f1 = NewFolder();
        foreach (var ticks in new long[] { 100, 300, 500 })
        {
            var r = mgr.AddEmail(Request(f1, Mime($"S{ticks}", "b"), ticks, "x@corp.com", $"S{ticks}", "widget row"));
            Ok(r); f1 = r.Value.Folder!;
        }
        var f2 = NewFolder();
        foreach (var ticks in new long[] { 200, 400, 600 })
        {
            var r = mgr.AddEmail(Request(f2, Mime($"S{ticks}", "b"), ticks, "y@corp.com", $"S{ticks}", "widget row"));
            Ok(r); f2 = r.Value.Folder!;
        }

        var found = mgr.SearchMailbox(new[] { f1.FolderId, f2.FolderId }, "widget", ListingSearchField.All, maxResults: 2);
        Ok(found);
        Assert.True(found.Value.WholeMailbox);
        Assert.Equal(6, found.Value.RecordsScanned);           // every folder scanned unbounded before the cap
        Assert.Equal(2, found.Value.MatchCount);               // cap is global, not 2-per-folder
        Assert.Equal(new long[] { 600, 500 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
        Assert.Equal(f2.FolderId, found.Value.Hits[0].FolderId);
        Assert.Equal(f1.FolderId, found.Value.Hits[1].FolderId);
    }

    [Fact]
    public void SearchMailbox_returns_a_hit_per_folder_for_a_message_listed_in_two_folders()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // The same message (identical EmailHashedID) is listed in two folders — the dual-listing/move state
        // AddEmail cannot express because it dedupes globally by content id. A whole-mailbox scan must surface
        // it once per folder, each hit tagged with the folder it was found in.
        var shared = Rec(500, "shared@corp.com", "Shared Widget", "one message, two folders");
        var f1 = BuildFolder(mgr, new[] { shared });
        var f2 = BuildFolder(mgr, new[] { shared });

        var found = mgr.SearchMailbox(new[] { f1, f2 }, "widget");
        Ok(found);
        Assert.Equal(2, found.Value.RecordsScanned);
        Assert.Equal(2, found.Value.MatchCount);
        Assert.All(found.Value.Hits, h => Assert.Equal(shared.EmailHashedId, h.Record.EmailHashedId));
        Assert.Equal(
            new[] { f1, f2 }.OrderBy(x => x, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).ToArray(),
            found.Value.Hits.Select(h => h.FolderId)
                .OrderBy(x => x, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).ToArray());
    }

    [Fact]
    public void SearchMailbox_breaks_date_ties_by_EmailHashedID_across_folders()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Two distinct messages share an identical DateTicks but live in different folders. The cross-folder
        // merge must order the tie deterministically by EmailHashedID ascending, matching the folder-scoped
        // and page-level canonical order.
        var f1 = NewFolder();
        var a = mgr.AddEmail(Request(f1, Mime("Tie A", "aaa"), 500, "a@corp.com", "Tie A", "widget tie")); Ok(a); f1 = a.Value.Folder!;
        var f2 = NewFolder();
        var c = mgr.AddEmail(Request(f2, Mime("Tie C", "ccc"), 500, "c@corp.com", "Tie C", "widget tie")); Ok(c); f2 = c.Value.Folder!;

        var found = mgr.SearchMailbox(new[] { f1.FolderId, f2.FolderId }, "widget");
        Ok(found);
        Assert.Equal(2, found.Value.MatchCount);
        Assert.All(found.Value.Hits, h => Assert.Equal(500, h.Record.DateTicks));

        // Expected order is EmailHashedID ascending on the date tie — independent of the folder scan order.
        var expected = new[] { a.Value.EmailId, c.Value.EmailId }.OrderBy(id => id).ToArray();
        Assert.Equal(expected, found.Value.Hits.Select(h => h.Record.EmailHashedId).ToArray());
    }

    [Fact]
    public void SearchMailbox_honors_pending_deltas_inside_each_folder()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // f1 carries a compiled match plus a pending-delta Add match; f2 carries a compiled match and a pending
        // Delete that masks a second compiled row. The mailbox scan must apply each folder's delta chain before
        // the cross-folder merge: the pending Add appears, the masked row does not.
        var f1Compiled = Rec(500, "c1@corp.com", "Widget Compiled One", "in the page");
        var f1Delta = Rec(600, "d1@corp.com", "Widget Pending One", "not compiled yet");
        var f1 = BuildFolder(mgr, new[] { f1Compiled }, new[] { FolderDeltaEntry.Add(f1Delta) });

        var f2Kept = Rec(400, "c2@corp.com", "Widget Compiled Two", "still here");
        var f2Masked = Rec(300, "m2@corp.com", "Widget Masked Two", "moved away");
        var f2 = BuildFolder(mgr, new[] { f2Kept, f2Masked }, new[] { FolderDeltaEntry.Delete(f2Masked.EmailHashedId) });

        var found = mgr.SearchMailbox(new[] { f1, f2 }, "widget");
        Ok(found);
        // Pending Add (600) + f1 compiled (500) + f2 kept (400); the masked f2 row is gone.
        Assert.Equal(3, found.Value.MatchCount);
        Assert.Equal(new long[] { 600, 500, 400 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
        Assert.Equal(f1, found.Value.Hits[0].FolderId);        // pending Add surfaced from its own folder
        Assert.DoesNotContain(found.Value.Hits, h => h.Record.EmailHashedId == f2Masked.EmailHashedId);
    }

    // ------------------------------------------------------------------ Guards

    [Fact]
    public void Search_rejects_empty_query_bad_cap_and_unopened_manager()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, Mime("Hi", "x"), 100, "a@corp.com", "Hi", "there")); Ok(a); folder = a.Value.Folder!;

        Assert.True(mgr.SearchFolder(folder.FolderId, "").IsFailure);
        Assert.True(mgr.SearchFolder(folder.FolderId, "hi", maxResults: 0).IsFailure);
        Assert.True(mgr.SearchMailbox(new[] { folder.FolderId }, "").IsFailure);
        // An unknown folder id does not resolve.
        Assert.True(mgr.SearchFolder(new UlidGenerator().Next(), "hi").IsFailure);

        using var created = EmailManager.Create(_path + ".2").Value; // Create, never Open
        Assert.True(created.SearchFolder(new UlidGenerator().Next(), "hi").IsFailure);
        created.Dispose();
        File.Delete(_path + ".2");
    }
}
