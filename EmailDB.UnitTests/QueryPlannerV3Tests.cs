using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the one query-planner entry point (story US-EMDB-94, task US-EMDB-94-5,
/// docs/Search.md "Query planning"): <see cref="EmailManager.Search"/> classifies a
/// <see cref="SearchQuery"/> by shape and routes it across the phases — address-shaped to the trigram FTS
/// index (Phase 1), date-bounded through the Date index pre-filter (Phase 3), and keyword/free text to the
/// listing scan (Phase 2) — then merges + dedupes into one ranked list.
///
/// <para>Covers: address-shaped routing (both the <c>@</c> heuristic and the field hint), keyword routing
/// to scan, the Date index pre-filter, date-only queries, merge/dedupe across folders with stable ordering,
/// graceful degradation when the FTS or Date index is absent, and encrypted-file operation.</para>
/// </summary>
public class QueryPlannerV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-planner-{Guid.NewGuid():N}.emdb");

    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static int _seq;
    private static byte[] Mime(string tag) =>
        Encoding.UTF8.GetBytes($"From: x\r\nSubject: {tag}\r\n\r\nbody-{tag}-{Interlocked.Increment(ref _seq)}");

    private static FolderPageDirectory NewFolder() =>
        FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

    private static AddEmailRequest Request(
        FolderPageDirectory folder, long ticks, string from, string subject = "probe", string preview = "preview",
        string[]? to = null, string[]? cc = null) => new()
    {
        RawContent = Mime($"{from}-{subject}-{ticks}"),
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = from,
        To = to ?? Array.Empty<string>(),
        Cc = cc ?? Array.Empty<string>(),
        Subject = subject,
        Preview = preview,
    };

    /// <summary>
    /// An <see cref="AddEmailRequest"/> with an explicit <paramref name="raw"/> body, so the resulting
    /// <see cref="EmailHashedID"/> (= <c>ComputeFromRawContent(raw)</c>) can be pinned to a known value. The
    /// consistency-oracle tests reuse the same <paramref name="raw"/> bytes to build the identical email both
    /// through AddEmail (index populated) and through <see cref="BuildFolder"/> (index absent), so hits line up
    /// by identity across the two builds.
    /// </summary>
    private static AddEmailRequest RawRequest(
        FolderPageDirectory folder, byte[] raw, long ticks, string from, string subject) => new()
    {
        RawContent = raw,
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = from,
        Subject = subject,
        Preview = "p",
    };

    private static ListingRecord Rec(V3Id id, long date, string from, string subject, string preview) => new()
    {
        EmailHashedId = id,
        ContentBlockId = new UlidGenerator().Next(),
        DateTicks = date,
        Flags = ListingFlags.None,
        MessageSize = 100,
        From = from,
        Subject = subject,
        Preview = preview,
    };

    /// <summary>
    /// Builds a folder directly from compiled <paramref name="records"/> (one page), bypassing the AddEmail
    /// pipeline entirely — so the FTS and Date indexes stay empty. Lets the merge/dedupe and index-absent
    /// degradation tests place exact records (same id in two folders, exact dates) and force the absent-index
    /// routes the content-deduped <see cref="EmailManager.AddEmail"/> path cannot express.
    /// </summary>
    private static byte[] BuildFolder(EmailManager mgr, IReadOnlyList<ListingRecord> records)
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
        Ok(mgr.FolderDirectory!.WriteDirectory(dir));
        return folderId;
    }

    private static int CompareUnsigned(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    // --------------------------------------------------------- Address-shaped routing (Phase 1)

    [Fact]
    public void An_address_query_with_an_at_sign_routes_to_the_trigram_index()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 300, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 200, from: "alice@example.org")); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, 100, from: "bob@other.net")); Ok(c); folder = c.Value.Folder!;

        var found = mgr.Search(new SearchQuery { Text = "ryan@bizt", Folders = new[] { folder.FolderId } });
        Ok(found);
        Assert.Equal(SearchPhase.Fts, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedTrigramIndex);
        Assert.Equal(new[] { a.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
        Assert.All(found.Value.Hits, h => Assert.Equal(SearchPhase.Fts, h.MatchedPhase));
    }

    [Fact]
    public void An_address_field_hint_forces_the_trigram_route_without_an_at_sign()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 200, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 100, from: "alice@example.org")); Ok(b); folder = b.Value.Folder!;

        // "biztactix" has no '@', so only the explicit field hint makes it address-shaped.
        var found = mgr.Search(new SearchQuery
        {
            Text = "biztactix",
            AddressFields = AddressField.From,
            Folders = new[] { folder.FolderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Fts, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedTrigramIndex);
        Assert.Equal(new[] { a.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void A_plain_keyword_routes_to_the_listing_scan()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 200, from: "a@x.com", subject: "Quarterly Invoice")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 100, from: "b@x.com", subject: "Lunch plans")); Ok(b); folder = b.Value.Folder!;

        var found = mgr.Search(new SearchQuery { Text = "invoice", Folders = new[] { folder.FolderId } });
        Ok(found);
        Assert.Equal(SearchPhase.Scan, found.Value.PhasesExecuted);
        Assert.False(found.Value.UsedTrigramIndex);
        Assert.Equal(new[] { a.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void An_address_query_returns_the_FTS_answer_not_a_subject_scan_hit()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // The address lives in this email's From — the trigram (address-only) route must return it.
        var addr = mgr.AddEmail(Request(folder, 200, from: "ryan@biztactix.com.au", subject: "probe")); Ok(addr); folder = addr.Value.Folder!;
        // A decoy whose From is unrelated but whose Subject *contains the same address substring*. A full-text
        // listing scan (Subject|From|Preview) would match it; the FTS route indexes addresses only, so it must not.
        var decoy = mgr.AddEmail(Request(folder, 100, from: "carol@nowhere.net", subject: "meeting with ryan@biztactix.com.au")); Ok(decoy); folder = decoy.Value.Folder!;
        var ids = new[] { folder.FolderId };

        // Divergence control: a Subject scan of the same text *does* hit the decoy — so the two phases genuinely
        // disagree on this seed. The planner routing '@' → FTS is what makes the address query ignore the decoy.
        var subjectScan = mgr.SearchFolder(folder.FolderId, "ryan@bizt", ListingSearchField.Subject);
        Ok(subjectScan);
        Assert.Contains(decoy.Value.EmailId, subjectScan.Value.Hits.Select(h => h.Record.EmailHashedId));

        var found = mgr.Search(new SearchQuery { Text = "ryan@bizt", Folders = ids });
        Ok(found);
        Assert.Equal(SearchPhase.Fts, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedTrigramIndex);
        // Only the address-From email — the subject-only decoy is absent because FTS never scans Subject.
        Assert.Equal(new[] { addr.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
        Assert.DoesNotContain(decoy.Value.EmailId, found.Value.Hits.Select(h => h.EmailId));
        Assert.All(found.Value.Hits, h => Assert.Equal(SearchPhase.Fts, h.MatchedPhase));
    }

    [Fact]
    public void Address_routing_holds_on_an_encrypted_file_after_reopen()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        V3Id target;
        byte[] folderId;
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            var folder = NewFolder();
            var a = mgr.AddEmail(Request(folder, 200, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
            var b = mgr.AddEmail(Request(folder, 100, from: "alice@example.org")); Ok(b); folder = b.Value.Folder!;
            target = a.Value.EmailId;
            folderId = folder.FolderId;
            Ok(mgr.Close()); // commit flushes the FTS segment + registers the committed root
        }

        using var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value;
        Assert.True(reopened.IsEncrypted);
        // No AddEmail on THIS manager: the FTS index was reconstructed from the committed root on open.
        Assert.NotNull(reopened.Fts!.CommittedSearchRootLocation);
        Assert.Equal(2, reopened.Fts!.IndexedEmailCount);

        var found = reopened.Search(new SearchQuery { Text = "ryan@bizt", Folders = new[] { folderId } });
        Ok(found);
        // Routing decision survives the reopen: still FTS, still the trigram index (not a degraded From scan).
        Assert.Equal(SearchPhase.Fts, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedTrigramIndex);
        Assert.Equal(new[] { target }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    // --------------------------------------------------------- Date pre-filter (Phase 3)

    [Fact]
    public void A_date_bounded_keyword_query_pre_filters_via_the_date_index()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // Three emails all matching "report", at distinct dates.
        var a = mgr.AddEmail(Request(folder, 300, from: "a@x.com", subject: "Report A")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 200, from: "b@x.com", subject: "Report B")); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, 100, from: "c@x.com", subject: "Report C")); Ok(c); folder = c.Value.Folder!;

        // Range [150, 250] keeps only the date-200 email.
        var found = mgr.Search(new SearchQuery
        {
            Text = "report",
            FromTicks = 150,
            ToTicks = 250,
            Folders = new[] { folder.FolderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Scan | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedDateIndex);
        Assert.False(found.Value.DateFilterDegraded);
        Assert.Equal(new[] { b.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void The_date_range_is_inclusive_on_both_endpoints()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // Four emails straddling the range endpoints: 100 (below), 200 (== FromTicks),
        // 300 (== ToTicks), 400 (above). All match the keyword so only the date bound decides.
        var below = mgr.AddEmail(Request(folder, 100, from: "a@x.com", subject: "Report below")); Ok(below); folder = below.Value.Folder!;
        var lo = mgr.AddEmail(Request(folder, 200, from: "b@x.com", subject: "Report lo")); Ok(lo); folder = lo.Value.Folder!;
        var hi = mgr.AddEmail(Request(folder, 300, from: "c@x.com", subject: "Report hi")); Ok(hi); folder = hi.Value.Folder!;
        var above = mgr.AddEmail(Request(folder, 400, from: "d@x.com", subject: "Report above")); Ok(above); folder = above.Value.Folder!;

        // Range [200, 300]: both endpoints land exactly on an email — both must be kept, the
        // just-outside 100 and 400 dropped. Pins the [fromTicks, toTicks] inclusive semantics.
        var found = mgr.Search(new SearchQuery
        {
            Text = "report",
            FromTicks = 200,
            ToTicks = 300,
            Folders = new[] { folder.FolderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Scan | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedDateIndex);
        Assert.False(found.Value.DateFilterDegraded);
        // Newest-first: the ToTicks-boundary email (300) then the FromTicks-boundary email (200).
        Assert.Equal(new[] { hi.Value.EmailId, lo.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void A_date_bound_intersects_with_the_text_phase_keeping_only_in_range_matches()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // The only email that is BOTH in the [150,250] range AND matches "invoice".
        var target = mgr.AddEmail(Request(folder, 200, from: "a@x.com", subject: "Quarterly Invoice")); Ok(target); folder = target.Value.Folder!;
        // In range, but the text phase does not match it (subject has no "invoice") — must be dropped.
        var inRangeNoText = mgr.AddEmail(Request(folder, 220, from: "b@x.com", subject: "Lunch plans")); Ok(inRangeNoText); folder = inRangeNoText.Value.Folder!;
        // Matches the text, but out of range (below) — the date pre-filter must drop it.
        var textOutOfRange = mgr.AddEmail(Request(folder, 50, from: "c@x.com", subject: "Old Invoice")); Ok(textOutOfRange); folder = textOutOfRange.Value.Folder!;

        var found = mgr.Search(new SearchQuery
        {
            Text = "invoice",
            FromTicks = 150,
            ToTicks = 250,
            Folders = new[] { folder.FolderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Scan | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedDateIndex);
        Assert.False(found.Value.DateFilterDegraded);
        // Exactly the intersection: only the in-range text match survives both phases.
        Assert.Equal(new[] { target.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
        Assert.DoesNotContain(inRangeNoText.Value.EmailId, found.Value.Hits.Select(h => h.EmailId));
        Assert.DoesNotContain(textOutOfRange.Value.EmailId, found.Value.Hits.Select(h => h.EmailId));
    }

    [Fact]
    public void A_date_bounded_query_agrees_with_the_unbounded_query_filtered_in_memory()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // A spread of matching emails across the date line; the range [175, 425] straddles several.
        long[] dates = { 100, 200, 300, 400, 500 };
        foreach (var d in dates)
        {
            var r = mgr.AddEmail(Request(folder, d, from: $"x{d}@x.com", subject: "Report"));
            Ok(r);
            folder = r.Value.Folder!;
        }
        const long from = 175, to = 425;

        // Oracle: run the SAME text query unbounded, then filter its hits in memory by the range.
        var unbounded = mgr.Search(new SearchQuery { Text = "report", Folders = new[] { folder.FolderId } });
        Ok(unbounded);
        Assert.False(unbounded.Value.UsedDateIndex);
        var expected = unbounded.Value.Hits
            .Where(h => h.Record.DateTicks >= from && h.Record.DateTicks <= to)
            .Select(h => h.EmailId)
            .ToArray();

        // The index-backed date pre-filter must produce exactly the same ranked set.
        var bounded = mgr.Search(new SearchQuery { Text = "report", FromTicks = from, ToTicks = to, Folders = new[] { folder.FolderId } });
        Ok(bounded);
        Assert.True(bounded.Value.UsedDateIndex);
        Assert.False(bounded.Value.DateFilterDegraded);
        Assert.Equal(expected, bounded.Value.Hits.Select(h => h.EmailId).ToArray());
        // Sanity: the range genuinely trims the candidate set (200,300,400 survive from 100..500).
        Assert.Equal(3, bounded.Value.MatchCount);
    }

    [Fact]
    public void The_date_pre_filter_holds_on_an_encrypted_file_after_reopen()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        V3Id target;
        byte[] folderId;
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            var folder = NewFolder();
            var a = mgr.AddEmail(Request(folder, 300, from: "a@x.com", subject: "Report")); Ok(a); folder = a.Value.Folder!;
            var b = mgr.AddEmail(Request(folder, 100, from: "b@x.com", subject: "Report")); Ok(b); folder = b.Value.Folder!;
            target = a.Value.EmailId;
            folderId = folder.FolderId;
            Ok(mgr.Close()); // commit registers the Date index root in the Checkpoint's secondary-index table
        }

        using var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value;
        Assert.True(reopened.IsEncrypted);
        // No AddEmail on THIS manager: the Date index was recovered from the committed checkpoint on open.
        Assert.NotNull(reopened.DateIndex!.Root);

        // Range [250, 350] keeps only the date-300 email — served by the recovered index, not a degraded filter.
        var found = reopened.Search(new SearchQuery
        {
            Text = "report",
            FromTicks = 250,
            ToTicks = 350,
            Folders = new[] { folderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Scan | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedDateIndex);
        Assert.False(found.Value.DateFilterDegraded);
        Assert.Equal(new[] { target }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void A_date_only_query_lists_the_scoped_folder_filtered_to_the_range()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 500, from: "a@x.com")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 400, from: "b@x.com")); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, 100, from: "c@x.com")); Ok(c); folder = c.Value.Folder!;

        // No text — a pure date query. Range [300, long.Max) keeps 500 and 400, newest-first.
        var found = mgr.Search(new SearchQuery { FromTicks = 300, Folders = new[] { folder.FolderId } });
        Ok(found);
        Assert.Equal(SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedDateIndex);
        Assert.Equal(new[] { a.Value.EmailId, b.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    // --------------------------------------------------------- Merge / dedupe / ordering

    [Fact]
    public void A_message_in_two_scoped_folders_dedupes_to_one_hit_on_the_smallest_folder_id()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // The SAME record (same identity, same content block) listed in two folders — the AddEmail path cannot
        // express this (content dedupe), so the records are placed directly.
        var id = V3Id.ComputeFromRawContent(Mime("dup"));
        var rec = Rec(id, 200, from: "sender@corp.com", subject: "Shared Widget", preview: "p");
        var f1 = BuildFolder(mgr, new[] { rec });
        var f2 = BuildFolder(mgr, new[] { rec });

        var found = mgr.Search(new SearchQuery { Text = "widget", Folders = new[] { f1, f2 } });
        Ok(found);
        Assert.Equal(SearchPhase.Scan, found.Value.PhasesExecuted);
        // SearchMailbox would return one hit per folder; the planner dedupes by identity to a single hit.
        Assert.Equal(1, found.Value.MatchCount);
        // Attributed to the lexicographically smallest of the two folder ids (deterministic).
        var expected = CompareUnsigned(f1, f2) <= 0 ? f1 : f2;
        Assert.Equal(expected, found.Value.Hits.Single().FolderId);
    }

    [Fact]
    public void Hits_are_ordered_date_desc_then_by_id_on_a_tie()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Two emails share a date (tie), one is newer. Distinct identities.
        var tieA = V3Id.ComputeFromRawContent(Mime("tieA"));
        var tieB = V3Id.ComputeFromRawContent(Mime("tieB"));
        var newer = V3Id.ComputeFromRawContent(Mime("newer"));
        var recNewer = Rec(newer, 300, "x@x.com", "Widget newer", "p");
        var recTieA = Rec(tieA, 200, "x@x.com", "Widget tieA", "p");
        var recTieB = Rec(tieB, 200, "x@x.com", "Widget tieB", "p");
        var folder = BuildFolder(mgr, new[] { recNewer, recTieA, recTieB });

        var found = mgr.Search(new SearchQuery { Text = "widget", Folders = new[] { folder } });
        Ok(found);
        Assert.Equal(3, found.Value.MatchCount);
        // Newest first; the date-200 tie breaks by EmailHashedID ascending.
        var expectedTieOrder = tieA.CompareTo(tieB) < 0 ? new[] { tieA, tieB } : new[] { tieB, tieA };
        Assert.Equal(new[] { newer, expectedTieOrder[0], expectedTieOrder[1] },
            found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void MaxResults_caps_keeping_the_newest()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        for (int i = 0; i < 5; i++)
        {
            var r = mgr.AddEmail(Request(folder, (i + 1) * 100, from: $"x{i}@x.com", subject: "Report"));
            Ok(r);
            folder = r.Value.Folder!;
        }

        var found = mgr.Search(new SearchQuery { Text = "report", MaxResults = 2, Folders = new[] { folder.FolderId } });
        Ok(found);
        Assert.Equal(2, found.Value.MatchCount);
        Assert.Equal(new long[] { 500, 400 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
    }

    [Fact]
    public void The_cross_folder_merge_is_ordered_date_desc_id_asc_and_is_identical_across_repeated_runs()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // A mixed multi-folder result set: three folders whose matching rows interleave on the date line, with a
        // date-200 tie that straddles two different folders (recT200a in f1, recT200b in f2). The merge must
        // impose the ONE house order across all three folders — date desc, ties by EmailHashedID ascending —
        // regardless of which folder a row came from, and regardless of per-folder listing order.
        var w500 = Rec(V3Id.ComputeFromRawContent(Mime("w500")), 500, "x@x.com", "Widget 500", "p");
        var t200a = Rec(V3Id.ComputeFromRawContent(Mime("t200a")), 200, "x@x.com", "Widget t200a", "p");
        var w400 = Rec(V3Id.ComputeFromRawContent(Mime("w400")), 400, "x@x.com", "Widget 400", "p");
        var t200b = Rec(V3Id.ComputeFromRawContent(Mime("t200b")), 200, "x@x.com", "Widget t200b", "p");
        var w300 = Rec(V3Id.ComputeFromRawContent(Mime("w300")), 300, "x@x.com", "Widget 300", "p");
        var w100 = Rec(V3Id.ComputeFromRawContent(Mime("w100")), 100, "x@x.com", "Widget 100", "p");
        var f1 = BuildFolder(mgr, new[] { w500, t200a });
        var f2 = BuildFolder(mgr, new[] { w400, t200b });
        var f3 = BuildFolder(mgr, new[] { w300, w100 });

        // The date-200 tie breaks by EmailHashedID ascending — even though the two rows live in different folders.
        var tie = t200a.EmailHashedId.CompareTo(t200b.EmailHashedId) < 0
            ? new[] { t200a.EmailHashedId, t200b.EmailHashedId }
            : new[] { t200b.EmailHashedId, t200a.EmailHashedId };
        var expected = new[]
        {
            w500.EmailHashedId, w400.EmailHashedId, w300.EmailHashedId, tie[0], tie[1], w100.EmailHashedId,
        };

        var query = new SearchQuery { Text = "widget", Folders = new[] { f1, f2, f3 } };
        var first = mgr.Search(query);
        Ok(first);
        Assert.Equal(SearchPhase.Scan, first.Value.PhasesExecuted);
        Assert.Equal(6, first.Value.MatchCount);
        Assert.Equal(expected, first.Value.Hits.Select(h => h.EmailId).ToArray());

        // Deterministic: re-running the same query over the same state yields the byte-for-byte same ordering.
        for (int run = 0; run < 3; run++)
        {
            var again = mgr.Search(query);
            Ok(again);
            Assert.Equal(expected, again.Value.Hits.Select(h => h.EmailId).ToArray());
        }
    }

    [Fact]
    public void MaxResults_caps_after_the_global_merge_not_with_a_per_folder_quota()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // The three globally-newest rows (600/500/400) all live in ONE folder; the other two folders hold only
        // older rows. A per-folder or round-robin quota would surface an older row from f2/f3; the global merge
        // must instead keep exactly the three newest — proving the cap is applied AFTER the cross-folder merge.
        var n600 = Rec(V3Id.ComputeFromRawContent(Mime("n600")), 600, "x@x.com", "Widget 600", "p");
        var n500 = Rec(V3Id.ComputeFromRawContent(Mime("n500")), 500, "x@x.com", "Widget 500", "p");
        var n400 = Rec(V3Id.ComputeFromRawContent(Mime("n400")), 400, "x@x.com", "Widget 400", "p");
        var m300 = Rec(V3Id.ComputeFromRawContent(Mime("m300")), 300, "x@x.com", "Widget 300", "p");
        var m200 = Rec(V3Id.ComputeFromRawContent(Mime("m200")), 200, "x@x.com", "Widget 200", "p");
        var o100 = Rec(V3Id.ComputeFromRawContent(Mime("o100")), 100, "x@x.com", "Widget 100", "p");
        var newest = BuildFolder(mgr, new[] { n600, n500, n400 });
        var mid = BuildFolder(mgr, new[] { m300, m200 });
        var old = BuildFolder(mgr, new[] { o100 });

        var found = mgr.Search(new SearchQuery
        {
            Text = "widget",
            MaxResults = 3,
            Folders = new[] { newest, mid, old },
        });
        Ok(found);
        Assert.Equal(3, found.Value.MatchCount);
        // Globally newest three — all from the one folder; no older row from mid/old leaked in via a quota.
        Assert.Equal(new long[] { 600, 500, 400 }, found.Value.Hits.Select(h => h.Record.DateTicks).ToArray());
        Assert.All(found.Value.Hits, h => Assert.Equal(newest, h.FolderId));
    }

    [Fact]
    public void Ordering_and_dedupe_invariants_hold_when_multiple_phases_execute()
    {
        // Candidate generation is single-phase (address → FTS, xor keyword → scan, xor date-only → listing); a
        // date bound then intersects that candidate set as a second phase. So a genuinely multi-phase plan is an
        // address query WITH a date bound (Fts | DateIndex). This pins that the merge/dedupe/order invariants —
        // one hit per identity, date-desc then id-asc — still hold when two phases combine, across folders.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // folderA and folderB each hold a date-200 match (the cross-folder tie) plus one boundary row.
        var folderA = NewFolder();
        var newest = mgr.AddEmail(Request(folderA, 300, from: "ryan@biztactix.com.au")); Ok(newest); folderA = newest.Value.Folder!;
        var tieA = mgr.AddEmail(Request(folderA, 200, from: "ryan@biztfoo.com")); Ok(tieA); folderA = tieA.Value.Folder!;

        var folderB = NewFolder();
        var tieB = mgr.AddEmail(Request(folderB, 200, from: "ryan@biztbar.net")); Ok(tieB); folderB = tieB.Value.Folder!;
        var tooOld = mgr.AddEmail(Request(folderB, 100, from: "ryan@biztactix.org")); Ok(tooOld); folderB = tooOld.Value.Folder!;

        // Address-shaped ('@') + date bound [150, 350]: FTS serves candidates, the Date index pre-filters them,
        // dropping the date-100 match. Both phases must appear in PhasesExecuted.
        var found = mgr.Search(new SearchQuery
        {
            Text = "ryan@bizt",
            FromTicks = 150,
            ToTicks = 350,
            Folders = new[] { folderA.FolderId, folderB.FolderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Fts | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedTrigramIndex);
        Assert.True(found.Value.UsedDateIndex);
        Assert.False(found.Value.DateFilterDegraded);

        // Ordering across the two folders and two phases: newest (300) first, then the cross-folder date-200 tie
        // by EmailHashedID ascending. The out-of-range date-100 match is gone.
        var tie = tieA.Value.EmailId.CompareTo(tieB.Value.EmailId) < 0
            ? new[] { tieA.Value.EmailId, tieB.Value.EmailId }
            : new[] { tieB.Value.EmailId, tieA.Value.EmailId };
        Assert.Equal(new[] { newest.Value.EmailId, tie[0], tie[1] },
            found.Value.Hits.Select(h => h.EmailId).ToArray());
        Assert.DoesNotContain(tooOld.Value.EmailId, found.Value.Hits.Select(h => h.EmailId));
        // Every surviving hit was produced by the FTS phase (the date phase is a filter, not a candidate source).
        Assert.All(found.Value.Hits, h => Assert.Equal(SearchPhase.Fts, h.MatchedPhase));
    }

    // --------------------------------------------------------- Graceful degradation

    [Fact]
    public void An_address_query_degrades_to_a_From_scan_when_the_FTS_index_is_absent()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Records placed directly: the FTS index is never populated (no AddEmail), so the address route has no
        // index to serve it and must degrade to a Tier 1 From scan rather than erroring.
        Assert.Equal(0, mgr.Fts!.IndexedEmailCount);
        var match = Rec(V3Id.ComputeFromRawContent(Mime("m")), 200, from: "ryan@biztactix.com.au", subject: "s", preview: "p");
        var other = Rec(V3Id.ComputeFromRawContent(Mime("o")), 100, from: "alice@example.org", subject: "s", preview: "p");
        var folder = BuildFolder(mgr, new[] { match, other });

        var found = mgr.Search(new SearchQuery { Text = "ryan@bizt", Folders = new[] { folder } });
        Ok(found);
        // Degraded: the scan phase served the address-shaped query; the FTS phase never ran.
        Assert.Equal(SearchPhase.Scan, found.Value.PhasesExecuted);
        Assert.False(found.Value.UsedTrigramIndex);
        Assert.Equal(new[] { match.EmailHashedId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void A_date_bound_degrades_to_an_in_memory_filter_when_the_date_index_is_absent()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Records placed directly: the Date index is never populated, so a date bound cannot pre-filter via the
        // index and must fall back to an in-memory DateTicks filter — the bound still holds.
        Assert.Null(mgr.DateIndex!.Root);
        var inRange = Rec(V3Id.ComputeFromRawContent(Mime("in")), 200, "a@x.com", "Widget in", "p");
        var outRange = Rec(V3Id.ComputeFromRawContent(Mime("out")), 500, "a@x.com", "Widget out", "p");
        var folder = BuildFolder(mgr, new[] { inRange, outRange });

        var found = mgr.Search(new SearchQuery
        {
            Text = "widget",
            FromTicks = 100,
            ToTicks = 300,
            Folders = new[] { folder },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Scan | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.False(found.Value.UsedDateIndex);
        Assert.True(found.Value.DateFilterDegraded);
        Assert.Equal(new[] { inRange.EmailHashedId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    [Fact]
    public void The_degraded_From_scan_returns_the_same_hits_as_the_indexed_FTS_answer()
    {
        // Consistency oracle for the FTS-absent path: the SAME three emails built two ways —
        // (1) through AddEmail so the trigram FTS index is populated, (2) placed directly so the FTS index
        // stays empty — must answer the identical address query with the byte-identical ranked hit set. The
        // fallback From scan is a true equivalent of the index, not an approximation; only the observable
        // route (UsedTrigramIndex) differs, and neither build errors.
        var r1 = Mime("orc-fts-match-newest"); var id1 = V3Id.ComputeFromRawContent(r1);
        var r2 = Mime("orc-fts-match-older");  var id2 = V3Id.ComputeFromRawContent(r2);
        var r3 = Mime("orc-fts-nomatch");      var id3 = V3Id.ComputeFromRawContent(r3);
        var expected = new[] { id1, id2 }; // "ryan@bizt" hits id1 (date 300) then id2 (200); alice id3 dropped.

        // (1) Indexed build — AddEmail populates the trigram FTS index; the address route serves it from FTS.
        V3Id[] indexedHits;
        {
            EmailManager.Create(_path).Value.Dispose();
            using var mgr = EmailManager.Open(_path).Value;
            var folder = NewFolder();
            var a = mgr.AddEmail(RawRequest(folder, r1, 300, "ryan@biztactix.com.au", "s")); Ok(a); folder = a.Value.Folder!;
            var b = mgr.AddEmail(RawRequest(folder, r2, 200, "ryan@biztfoo.org", "s")); Ok(b); folder = b.Value.Folder!;
            var c = mgr.AddEmail(RawRequest(folder, r3, 100, "alice@example.org", "s")); Ok(c); folder = c.Value.Folder!;
            Assert.Equal(3, mgr.Fts!.IndexedEmailCount);
            // Same raw bytes → AddEmail-assigned ids equal the pinned ids the degraded build will place.
            Assert.Equal(new[] { id1, id2, id3 }, new[] { a.Value.EmailId, b.Value.EmailId, c.Value.EmailId });

            var found = mgr.Search(new SearchQuery { Text = "ryan@bizt", Folders = new[] { folder.FolderId } });
            Ok(found); // never errors — a Result success, not a failure/throw.
            Assert.Equal(SearchPhase.Fts, found.Value.PhasesExecuted);
            Assert.True(found.Value.UsedTrigramIndex);
            indexedHits = found.Value.Hits.Select(h => h.EmailId).ToArray();
            Assert.Equal(expected, indexedHits);
        }

        // (2) Degraded build — records placed directly, the FTS index never populated → the From scan serves it.
        var degradedPath = _path + ".deg";
        try
        {
            EmailManager.Create(degradedPath).Value.Dispose();
            using var mgr = EmailManager.Open(degradedPath).Value;
            Assert.Equal(0, mgr.Fts!.IndexedEmailCount);
            var folder = BuildFolder(mgr, new[]
            {
                Rec(id1, 300, "ryan@biztactix.com.au", "s", "p"),
                Rec(id2, 200, "ryan@biztfoo.org", "s", "p"),
                Rec(id3, 100, "alice@example.org", "s", "p"),
            });

            var found = mgr.Search(new SearchQuery { Text = "ryan@bizt", Folders = new[] { folder } });
            Ok(found); // degradation never errors.
            Assert.Equal(SearchPhase.Scan, found.Value.PhasesExecuted);
            Assert.False(found.Value.UsedTrigramIndex);
            var degradedHits = found.Value.Hits.Select(h => h.EmailId).ToArray();
            Assert.Equal(expected, degradedHits);
            // The oracle: identical ranked hits whether the index was present or absent.
            Assert.Equal(indexedHits, degradedHits);
        }
        finally { if (File.Exists(degradedPath)) File.Delete(degradedPath); }
    }

    [Fact]
    public void The_degraded_date_filter_returns_the_same_hits_as_the_indexed_date_pre_filter()
    {
        // Consistency oracle for the date-index-absent path: the SAME five emails built two ways — (1) through
        // AddEmail so the Date B+-tree index is populated, (2) placed directly so it stays empty — must answer
        // the identical date-bounded keyword query with the byte-identical in-range ranked hit set. The
        // in-memory DateTicks fallback is a true equivalent of the index pre-filter; only UsedDateIndex /
        // DateFilterDegraded differ, and neither build errors.
        var raws = new[] { Mime("orc-d-100"), Mime("orc-d-200"), Mime("orc-d-300"), Mime("orc-d-400"), Mime("orc-d-500") };
        var ids = raws.Select(r => V3Id.ComputeFromRawContent(r)).ToArray();
        long[] dates = { 100, 200, 300, 400, 500 };
        const long from = 175, to = 425;                 // keeps 200, 300, 400
        var expected = new[] { ids[3], ids[2], ids[1] }; // date desc: 400, 300, 200

        // (1) Indexed build — AddEmail populates the Date index; the bound pre-filters through it.
        V3Id[] indexedHits;
        {
            EmailManager.Create(_path).Value.Dispose();
            using var mgr = EmailManager.Open(_path).Value;
            var folder = NewFolder();
            var got = new List<V3Id>();
            for (int i = 0; i < raws.Length; i++)
            {
                var r = mgr.AddEmail(RawRequest(folder, raws[i], dates[i], "a@x.com", "Report"));
                Ok(r); folder = r.Value.Folder!;
                got.Add(r.Value.EmailId);
            }
            Assert.Equal(ids, got.ToArray()); // pinned ids match the degraded build's placed ids.
            Assert.NotNull(mgr.DateIndex!.Root);

            var found = mgr.Search(new SearchQuery { Text = "report", FromTicks = from, ToTicks = to, Folders = new[] { folder.FolderId } });
            Ok(found); // never errors.
            Assert.True(found.Value.UsedDateIndex);
            Assert.False(found.Value.DateFilterDegraded);
            indexedHits = found.Value.Hits.Select(h => h.EmailId).ToArray();
            Assert.Equal(expected, indexedHits);
        }

        // (2) Degraded build — records placed directly, the Date index never populated → in-memory filter.
        var degradedPath = _path + ".deg";
        try
        {
            EmailManager.Create(degradedPath).Value.Dispose();
            using var mgr = EmailManager.Open(degradedPath).Value;
            Assert.Null(mgr.DateIndex!.Root);
            var recs = new List<ListingRecord>();
            for (int i = 0; i < ids.Length; i++)
                recs.Add(Rec(ids[i], dates[i], "a@x.com", "Report", "p"));
            var folder = BuildFolder(mgr, recs);

            var found = mgr.Search(new SearchQuery { Text = "report", FromTicks = from, ToTicks = to, Folders = new[] { folder } });
            Ok(found); // degradation never errors.
            Assert.False(found.Value.UsedDateIndex);
            Assert.True(found.Value.DateFilterDegraded);
            var degradedHits = found.Value.Hits.Select(h => h.EmailId).ToArray();
            Assert.Equal(expected, degradedHits);
            // The oracle: identical ranked in-range hits whether the index was present or absent.
            Assert.Equal(indexedHits, degradedHits);
        }
        finally { if (File.Exists(degradedPath)) File.Delete(degradedPath); }
    }

    // The Bloom-filter leg of "degrades gracefully when an index is absent" — a whole-mailbox scan with no
    // per-folder Bloom filters built falls back to scanning every folder rather than erroring — is carried by
    // BloomFilterSearchV3Tests.With_no_filters_built_SearchMailbox_degrades_to_scanning_every_folder.

    // --------------------------------------------------------- Encryption

    [Fact]
    public void The_planner_operates_on_an_encrypted_file()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        using var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value;
        Assert.True(mgr.IsEncrypted);

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 300, from: "ryan@biztactix.com.au", subject: "Report")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 200, from: "alice@example.org", subject: "Report")); Ok(b); folder = b.Value.Folder!;

        // Address-shaped + date-bounded: exercises the FTS route and the Date pre-filter on an encrypted file.
        var found = mgr.Search(new SearchQuery
        {
            Text = "ryan@bizt",
            FromTicks = 250,
            ToTicks = 350,
            Folders = new[] { folder.FolderId },
        });
        Ok(found);
        Assert.Equal(SearchPhase.Fts | SearchPhase.DateIndex, found.Value.PhasesExecuted);
        Assert.True(found.Value.UsedDateIndex);
        Assert.Equal(new[] { a.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
    }

    // --------------------------------------------------------- Guards

    [Fact]
    public void Search_rejects_bad_arguments_and_an_unopened_manager()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 100, from: "ryan@x.com")); Ok(a); folder = a.Value.Folder!;
        var ids = new[] { folder.FolderId };

        Assert.True(mgr.Search(new SearchQuery { Folders = ids }).IsFailure);                       // no text, no date
        Assert.True(mgr.Search(new SearchQuery { Text = "x", MaxResults = 0, Folders = ids }).IsFailure); // bad cap
        Assert.True(mgr.Search(new SearchQuery { Text = "x", FromTicks = 300, ToTicks = 100, Folders = ids }).IsFailure); // inverted range
        Assert.True(mgr.Search(new SearchQuery { Text = "x", Folders = new byte[]?[] { null }! }).IsFailure); // null folder id

        using var created = EmailManager.Create(_path + ".2").Value; // Create, never Open
        Assert.True(created.Search(new SearchQuery { Text = "x", Folders = new[] { new UlidGenerator().Next() } }).IsFailure);
        created.Dispose();
        File.Delete(_path + ".2");
    }
}
