using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
// Disambiguate from the test project's own Models.EmailHashedID.
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the Phase 1 address trigram query path (story US-EMDB-91, task US-EMDB-91-8,
/// docs/Search.md Phase 1): <see cref="EmailManager.SearchAddresses"/> and the underlying
/// <see cref="FtsIndex.FindCandidates"/> posting-list intersection. Covers multi-trigram AND
/// intersection, From/To/Cc/All field filtering, Tier 1 verification that drops trigram false
/// positives, short-query fallback, post-delete masking, encrypted-file queries, and reopen-then-query.
/// </summary>
public class AddressTrigramSearchV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-addrsearch-{Guid.NewGuid():N}.emdb");

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
        FolderPageDirectory folder, long ticks, string from,
        string[]? to = null, string[]? cc = null) => new()
    {
        RawContent = Mime(from),
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = from,
        To = to ?? Array.Empty<string>(),
        Cc = cc ?? Array.Empty<string>(),
        Subject = "probe",
        Preview = "preview",
    };

    private static HashSet<V3Id> Ids(AddressSearchResult r) => r.Hits.Select(h => h.EmailId).ToHashSet();

    // --------------------------------------------------------- Intersection (multi-trigram AND)

    [Fact]
    public void Multi_trigram_AND_requires_every_query_trigram_in_one_field()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // "abcdef" holds both trigrams of "abcd" (abc, bcd); "abcxyz" holds abc but not bcd.
        var full = mgr.AddEmail(Request(folder, 300, from: "abcdef@x.com")); Ok(full); folder = full.Value.Folder!;
        var partial = mgr.AddEmail(Request(folder, 200, from: "abcxyz@x.com")); Ok(partial); folder = partial.Value.Folder!;
        var none = mgr.AddEmail(Request(folder, 100, from: "zzzzzz@x.com")); Ok(none); folder = none.Value.Folder!;

        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "abcd");
        Ok(found);
        Assert.True(found.Value.UsedTrigramIndex);
        // Only the email whose From holds ALL trigrams (and the substring) survives.
        Assert.Equal(new HashSet<V3Id> { full.Value.EmailId }, Ids(found.Value));
    }

    [Fact]
    public void Query_returns_every_email_whose_From_contains_the_substring_newest_first()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // Both "ryan@..." and "bryan@..." contain the substring "ryan"; "alice" does not.
        var a = mgr.AddEmail(Request(folder, 100, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 300, from: "bryan@example.com")); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, 200, from: "alice@example.org")); Ok(c); folder = c.Value.Folder!;

        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "ryan");
        Ok(found);
        Assert.Equal(2, found.Value.MatchCount);
        // Ranked newest-first: b@300 then a@100.
        Assert.Equal(new[] { b.Value.EmailId, a.Value.EmailId }, found.Value.Hits.Select(h => h.EmailId).ToArray());
        Assert.All(found.Value.Hits, h => Assert.Equal(folder.FolderId, h.FolderId));
    }

    // --------------------------------------------------------- Tier 1 verification (false positives)

    [Fact]
    public void Tier1_verification_drops_a_trigram_false_positive()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // "abcxbcd" holds trigrams abc and bcd but NOT the contiguous substring "abcd" — a trigram
        // false positive. "qqabcdqq" holds abc, bcd AND "abcd".
        var falsePos = mgr.AddEmail(Request(folder, 200, from: "abcxbcd@x.com")); Ok(falsePos); folder = falsePos.Value.Folder!;
        var real = mgr.AddEmail(Request(folder, 100, from: "qqabcdqq@x.com")); Ok(real); folder = real.Value.Folder!;

        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "abcd");
        Ok(found);
        // Both were trigram candidates, but verification against the actual From keeps only the real match.
        Assert.Equal(2, found.Value.CandidatesExamined);
        Assert.Equal(new HashSet<V3Id> { real.Value.EmailId }, Ids(found.Value));
    }

    [Fact]
    public void From_verification_drops_a_scrambled_trigram_false_positive_and_keeps_the_true_substring()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // Query "abcdef" has trigrams {abc, bcd, cde, def}. "abcdxcdef" holds ALL of them
        // (abc, bcd from the "abcd" run; cde, def from the "cdef" run) yet never contains the
        // contiguous substring "abcdef" — a scrambled/overlap trigram false positive. "zzabcdefzz"
        // holds every trigram AND the substring.
        var falsePos = mgr.AddEmail(Request(folder, 300, from: "abcdxcdef@x.com")); Ok(falsePos); folder = falsePos.Value.Folder!;
        var real = mgr.AddEmail(Request(folder, 200, from: "zzabcdefzz@x.com")); Ok(real); folder = real.Value.Folder!;
        var noTrigrams = mgr.AddEmail(Request(folder, 100, from: "unrelated@x.com")); Ok(noTrigrams); folder = noTrigrams.Value.Folder!;

        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "abcdef");
        Ok(found);
        Assert.True(found.Value.UsedTrigramIndex);
        // Both trigram-holders were candidates; only the contiguous substring survives Tier 1 verification.
        Assert.Equal(2, found.Value.CandidatesExamined);
        Assert.Equal(new HashSet<V3Id> { real.Value.EmailId }, Ids(found.Value));
        Assert.Equal(AddressField.From, found.Value.Hits.Single().MatchedFields);
    }

    [Fact]
    public void An_uppercase_query_matches_a_lowercased_stored_From_after_casefold()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var stored = mgr.AddEmail(Request(folder, 200, from: "ryan@biztactix.com.au")); Ok(stored); folder = stored.Value.Folder!;
        var other = mgr.AddEmail(Request(folder, 100, from: "alice@example.org")); Ok(other); folder = other.Value.Folder!;

        // Query casing differs from the stored address; both are casefolded before trigram
        // extraction and before the contiguous-substring verification, so the match holds.
        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "BIZTACTIX");
        Ok(found);
        Assert.True(found.Value.UsedTrigramIndex);
        Assert.Equal(new HashSet<V3Id> { stored.Value.EmailId }, Ids(found.Value));

        // A mixed-case query verifies identically — verification is normalization-insensitive, not literal.
        var mixed = mgr.SearchAddresses(new[] { folder.FolderId }, "BizTactix");
        Ok(mixed);
        Assert.Equal(new HashSet<V3Id> { stored.Value.EmailId }, Ids(mixed.Value));
    }

    // --------------------------------------------------------- To/Cc: documented unverified contract

    [Fact]
    public void A_To_or_Cc_trigram_false_positive_is_returned_unverified_documented_contract()
    {
        // CONTRACT PIN (docs/Search.md Phase 1, US-EMDB-91): only the From address is persisted at
        // Tier 1, so From candidates are verified against the actual address (contiguous-substring check)
        // while To/Cc candidates have no Tier 1 text to re-verify and are kept at the trigram level.
        // The SAME scrambled false-positive text ("abcdxcdef" for query "abcdef") is therefore DROPPED in
        // From but RETURNED in To/Cc. If a future schema persists To/Cc at Tier 1 and verifies them, this
        // test must be updated deliberately — that is the point of pinning it.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // A trigram false positive parked in To, and the identical text parked in Cc, on emails whose
        // From holds none of the query trigrams (so From is neither a candidate nor a verifier here).
        var inTo = mgr.AddEmail(Request(folder, 300, from: "nomatch1@x.com", to: new[] { "abcdxcdef@to.com" })); Ok(inTo); folder = inTo.Value.Folder!;
        var inCc = mgr.AddEmail(Request(folder, 200, from: "nomatch2@x.com", cc: new[] { "abcdxcdef@cc.com" })); Ok(inCc); folder = inCc.Value.Folder!;
        // The same false positive in From is the control: it must be dropped by Tier 1 verification.
        var inFrom = mgr.AddEmail(Request(folder, 100, from: "abcdxcdef@from.com")); Ok(inFrom); folder = inFrom.Value.Folder!;
        var id = new[] { folder.FolderId };

        // To: the trigram false positive survives (unverified, by current design).
        var to = mgr.SearchAddresses(id, "abcdef", AddressField.To);
        Ok(to);
        Assert.Equal(new HashSet<V3Id> { inTo.Value.EmailId }, Ids(to.Value));
        Assert.Equal(AddressField.To, to.Value.Hits.Single().MatchedFields);

        // Cc: same — kept at trigram level.
        var cc = mgr.SearchAddresses(id, "abcdef", AddressField.Cc);
        Ok(cc);
        Assert.Equal(new HashSet<V3Id> { inCc.Value.EmailId }, Ids(cc.Value));
        Assert.Equal(AddressField.Cc, cc.Value.Hits.Single().MatchedFields);

        // From (the control): the identical trigram false positive IS verified away.
        var from = mgr.SearchAddresses(id, "abcdef", AddressField.From);
        Ok(from);
        Assert.Empty(from.Value.Hits);

        // Searching all fields returns exactly the two unverified To/Cc matches, never the verified-away From.
        var all = mgr.SearchAddresses(id, "abcdef", AddressField.All);
        Ok(all);
        Assert.Equal(new HashSet<V3Id> { inTo.Value.EmailId, inCc.Value.EmailId }, Ids(all.Value));
    }

    // --------------------------------------------------------- Field filtering

    [Fact]
    public void Field_filter_selects_From_To_Cc_and_All()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        // "widget" appears in exactly one field per email.
        var inFrom = mgr.AddEmail(Request(folder, 300, from: "widget@from.com")); Ok(inFrom); folder = inFrom.Value.Folder!;
        var inTo = mgr.AddEmail(Request(folder, 200, from: "nomatch1@x.com", to: new[] { "widget@to.com" })); Ok(inTo); folder = inTo.Value.Folder!;
        var inCc = mgr.AddEmail(Request(folder, 100, from: "nomatch2@x.com", cc: new[] { "widget@cc.com" })); Ok(inCc); folder = inCc.Value.Folder!;

        var id = new[] { folder.FolderId };
        Assert.Equal(new HashSet<V3Id> { inFrom.Value.EmailId }, Ids(mgr.SearchAddresses(id, "widget", AddressField.From).Value));
        Assert.Equal(new HashSet<V3Id> { inTo.Value.EmailId }, Ids(mgr.SearchAddresses(id, "widget", AddressField.To).Value));
        Assert.Equal(new HashSet<V3Id> { inCc.Value.EmailId }, Ids(mgr.SearchAddresses(id, "widget", AddressField.Cc).Value));
        Assert.Equal(
            new HashSet<V3Id> { inFrom.Value.EmailId, inTo.Value.EmailId, inCc.Value.EmailId },
            Ids(mgr.SearchAddresses(id, "widget", AddressField.All).Value));
    }

    // --------------------------------------------------------- Short query fallback

    [Fact]
    public void A_query_shorter_than_a_trigram_falls_back_to_a_Tier1_From_scan()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 200, from: "ab@x.com")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 100, from: "cd@y.com")); Ok(b); folder = b.Value.Folder!;

        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "ab");
        Ok(found);
        Assert.False(found.Value.UsedTrigramIndex); // fell back — "ab" has no trigrams
        Assert.Equal(new HashSet<V3Id> { a.Value.EmailId }, Ids(found.Value));
    }

    // --------------------------------------------------------- Post-delete

    [Fact]
    public void A_deleted_email_no_longer_matches()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        var del = mgr.AddEmail(Request(folder, 200, from: "ryan@x.com")); Ok(del); folder = del.Value.Folder!;
        var keep = mgr.AddEmail(Request(folder, 100, from: "bryan@y.com")); Ok(keep); folder = keep.Value.Folder!;

        var before = mgr.SearchAddresses(new[] { folder.FolderId }, "ryan");
        Ok(before);
        Assert.Equal(2, before.Value.MatchCount);

        var removed = mgr.DeleteEmail(new DeleteEmailRequest
        {
            Folder = folder,
            EmailId = del.Value.EmailId,
            DateTicks = 200,
        });
        Ok(removed);
        folder = removed.Value.Folder!;

        var after = mgr.SearchAddresses(new[] { folder.FolderId }, "ryan");
        Ok(after);
        Assert.Equal(new HashSet<V3Id> { keep.Value.EmailId }, Ids(after.Value));
    }

    // --------------------------------------------------------- Encryption

    [Fact]
    public void Address_search_works_on_an_encrypted_file()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        using var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value;
        Assert.True(mgr.IsEncrypted);

        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 200, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, 100, from: "alice@example.org")); Ok(b); folder = b.Value.Folder!;

        var found = mgr.SearchAddresses(new[] { folder.FolderId }, "biztactix");
        Ok(found);
        Assert.Equal(new HashSet<V3Id> { a.Value.EmailId }, Ids(found.Value));
    }

    // --------------------------------------------------------- Reopen-then-query

    [Fact]
    public void A_committed_index_serves_address_queries_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();
        V3Id target;
        byte[] folderId;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folder = NewFolder();
            var a = mgr.AddEmail(Request(folder, 200, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
            var b = mgr.AddEmail(Request(folder, 100, from: "alice@example.org")); Ok(b); folder = b.Value.Folder!;
            target = a.Value.EmailId;
            folderId = folder.FolderId;
            Ok(mgr.Close()); // commit flushes the FTS segment + registers the root
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            // No AddEmail on this manager: a served hit came from the recovered segment + Tier 1 listing.
            var found = reopened.SearchAddresses(new[] { folderId }, "ryan");
            Ok(found);
            Assert.True(found.Value.UsedTrigramIndex);
            Assert.Equal(new HashSet<V3Id> { target }, Ids(found.Value));
        }
    }

    // --------------------------------------------------------- Encrypted reopen-then-query

    [Fact]
    public void An_encrypted_committed_index_serves_address_queries_after_reopen()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        V3Id target;
        byte[] folderId;
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            var folder = NewFolder();
            var a = mgr.AddEmail(Request(folder, 100, from: "ryan@biztactix.com.au")); Ok(a); folder = a.Value.Folder!;
            target = a.Value.EmailId;
            folderId = folder.FolderId;
            Ok(mgr.Close());
        }

        using (var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            var found = reopened.SearchAddresses(new[] { folderId }, "ryan");
            Ok(found);
            Assert.Equal(new HashSet<V3Id> { target }, Ids(found.Value));
        }
    }

    // --------------------------------------------------------- Guards

    [Fact]
    public void SearchAddresses_rejects_bad_arguments_and_an_unopened_manager()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 100, from: "ryan@x.com")); Ok(a); folder = a.Value.Folder!;
        var id = new[] { folder.FolderId };

        Assert.True(mgr.SearchAddresses(id, "").IsFailure);                               // empty query
        Assert.True(mgr.SearchAddresses(id, "ryan", maxResults: 0).IsFailure);            // bad cap
        Assert.True(mgr.SearchAddresses(id, "ryan", AddressField.None).IsFailure);        // no field
        Assert.True(mgr.SearchAddresses(new byte[]?[] { null }!, "ryan").IsFailure);      // null folder id

        // An unknown folder id does not resolve.
        Assert.True(mgr.SearchAddresses(new[] { new UlidGenerator().Next() }, "ryan").IsFailure);

        using var created = EmailManager.Create(_path + ".2").Value; // Create, never Open
        Assert.True(created.SearchAddresses(new[] { new UlidGenerator().Next() }, "ryan").IsFailure);
        created.Dispose();
        File.Delete(_path + ".2");
    }

    [Fact]
    public void An_empty_folder_set_verifies_nothing()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();
        var a = mgr.AddEmail(Request(folder, 100, from: "ryan@x.com")); Ok(a);

        // No folders to load Tier 1 rows from ⇒ no candidate can be verified/ranked ⇒ empty result.
        var found = mgr.SearchAddresses(Array.Empty<byte[]>(), "ryan");
        Ok(found);
        Assert.Empty(found.Value.Hits);
    }
}
