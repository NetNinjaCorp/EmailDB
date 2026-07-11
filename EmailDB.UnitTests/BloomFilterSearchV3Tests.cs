using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Integration tests for the per-folder Bloom filters wired into the v3 <see cref="EmailManager"/>
/// (US-EMDB-97-5, docs/Search.md Phase 5): <see cref="EmailManager.SearchMailbox"/> consults each folder's
/// filter and skips folders that cannot match; the skip is delta-safe (a folder with pending deltas is
/// always scanned); <see cref="EmailManager.RebuildFolderBloomFilter"/> rebuilds a folder's filter from its
/// compiled pages; and the catalog is registered under IndexKind 4, encrypted, and recovered on reopen.
/// </summary>
public class BloomFilterSearchV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"emaildb-bloomsearch-{Guid.NewGuid():N}.emdb");
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(string subject, string body) =>
        Encoding.UTF8.GetBytes($"From: s@example.com\r\nSubject: {subject}\r\n\r\n{body}");

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

    /// <summary>Writes a folder from compiled records (one page) plus an optional pending delta chain; returns its id.</summary>
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

    // --------------------------------------------------------- Skip behavior + correctness guard

    [Fact]
    public void SearchMailbox_skips_a_folder_whose_filter_eliminates_the_query_and_scans_the_matching_one()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var apples = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", "Apple Harvest", "orchard notes") });
        var bananas = BuildFolder(mgr, new[] { Rec(400, "b@corp.com", "Banana Bread", "recipe notes") });
        Ok(mgr.RebuildFolderBloomFilter(apples));
        Ok(mgr.RebuildFolderBloomFilter(bananas));

        // "apple" lives only in the apples folder: the bananas folder is eliminated and skipped.
        var found = mgr.SearchMailbox(new[] { apples, bananas }, "apple");
        Ok(found);
        Assert.Equal(1, found.Value.MatchCount);
        Assert.Equal(apples, found.Value.Hits[0].FolderId);
        Assert.Equal(1, found.Value.FoldersSkipped);   // bananas skipped
        Assert.Equal(1, found.Value.RecordsScanned);   // only the apples row scanned

        // A matching folder is never skipped: searching "banana" scans bananas and skips apples.
        var found2 = mgr.SearchMailbox(new[] { apples, bananas }, "banana");
        Ok(found2);
        Assert.Equal(1, found2.Value.MatchCount);
        Assert.Equal(bananas, found2.Value.Hits[0].FolderId);
        Assert.Equal(1, found2.Value.FoldersSkipped);
    }

    [Fact]
    public void Skipping_never_changes_the_result_versus_an_unfiltered_scan()
    {
        // Correctness guard: skipped folders truly cannot match, so filtered results equal a full scan.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var f1 = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", "Widget Alpha", "in one") });
        var f2 = BuildFolder(mgr, new[] { Rec(400, "b@corp.com", "Gadget Beta", "in two") });
        var f3 = BuildFolder(mgr, new[] { Rec(300, "c@corp.com", "Widget Gamma", "in three") });

        // Baseline WITHOUT filters (none rebuilt yet): every folder scanned, no skips.
        var baseline = mgr.SearchMailbox(new[] { f1, f2, f3 }, "widget");
        Ok(baseline);
        Assert.Equal(0, baseline.Value.FoldersSkipped);
        Assert.Equal(2, baseline.Value.MatchCount);

        // Now build filters and re-run: f2 (no "widget") is skipped, but the hit set is identical.
        Ok(mgr.RebuildFolderBloomFilter(f1));
        Ok(mgr.RebuildFolderBloomFilter(f2));
        Ok(mgr.RebuildFolderBloomFilter(f3));
        var filtered = mgr.SearchMailbox(new[] { f1, f2, f3 }, "widget");
        Ok(filtered);
        Assert.Equal(1, filtered.Value.FoldersSkipped);
        Assert.Equal(
            baseline.Value.Hits.Select(h => (h.FolderId, h.Record.DateTicks)),
            filtered.Value.Hits.Select(h => (h.FolderId, h.Record.DateTicks)));
    }

    [Fact]
    public void A_folder_whose_filter_reports_a_false_positive_is_scanned_but_yields_no_wrong_hit()
    {
        // (b) A "might match" verdict only means SCAN — it can be a Bloom false positive at the folder level:
        // every 3-char window of the query is present (spread across fields), yet no field contains the query
        // as a contiguous substring. The folder must be scanned (never skipped) and the scan simply finds
        // nothing — a wasted scan, never a wrong result. Contrast with a folder whose windows genuinely miss.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // fp: Subject "abcd" (windows ABC,BCD) + Preview "bcde" (windows BCD,CDE) together hold every window of
        // "abcde" (ABC,BCD,CDE) — so the filter cannot eliminate "abcde", but no field actually contains it.
        var fp = BuildFolder(mgr, new[] { Rec(500, "z@corp.com", "abcd", "bcde") });
        // hit: a real substring match for "abcde".
        var hit = BuildFolder(mgr, new[] { Rec(400, "y@corp.com", "see abcde now", "body") });
        // miss: no window of "abcde" — the filter eliminates it and the folder is skipped.
        var miss = BuildFolder(mgr, new[] { Rec(300, "x@corp.com", "zzzqqq", "nothing here") });
        Ok(mgr.RebuildFolderBloomFilter(fp));
        Ok(mgr.RebuildFolderBloomFilter(hit));
        Ok(mgr.RebuildFolderBloomFilter(miss));

        var found = mgr.SearchMailbox(new[] { fp, hit, miss }, "abcde");
        Ok(found);
        Assert.Equal(1, found.Value.FoldersSkipped);          // only 'miss' is eliminated
        Assert.Equal(2, found.Value.RecordsScanned);          // 'fp' (false positive) + 'hit' are both scanned
        Assert.Equal(1, found.Value.MatchCount);              // the false positive produced NO hit
        Assert.Equal(hit, found.Value.Hits[0].FolderId);      // the only real match

        // Sanity: the fp folder really does report "might match" on its own (so its scan was not a fluke).
        Assert.True(mgr.Bloom!.MightMatch(fp, "abcde", ReadDirectory(mgr, fp).FolderVersion, hasPendingDelta: false));
    }

    // --------------------------------------------------------- Stale-filter safety (version drift)

    [Fact]
    public void A_stale_filter_built_for_an_older_version_never_wrongly_skips_after_the_folder_changed()
    {
        // (d) A filter is stamped with the FolderVersion it was built at. If the folder later changes (a
        // compile bumps FolderVersion) and the filter is NOT rebuilt, SearchMailbox feeds the LIVE version to
        // MightMatch, the covered-version guard fires, and the folder is scanned — so a change the stale
        // filter never saw is still found and an absent token the stale filter would have eliminated is not
        // wrongly skipped. Rebuilding at the live version restores skipping.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var apple = Rec(500, "a@corp.com", "Apple Page", "compiled");
        var folder = BuildFolder(mgr, new[] { apple });
        var v0 = ReadDirectory(mgr, folder).FolderVersion;
        Ok(mgr.RebuildFolderBloomFilter(folder)); // filter covers "apple" only, stamped at v0

        // Change the folder AFTER the filter was built: append "banana" then compile so the directory advances
        // to a new version with NO pending delta (isolating version drift from the pending-delta guard).
        var banana = Rec(600, "b@corp.com", "Banana Fresh", "added after the filter was built");
        var appended = mgr.FolderDeltas!.AppendChained(ReadDirectory(mgr, folder), new[] { FolderDeltaEntry.Add(banana) });
        Ok(appended);
        Ok(mgr.FolderDirectory!.WriteDirectory(appended.Value.Directory));
        var compiler = new FolderCompiler(mgr.Folders!, mgr.FolderDirectory!, mgr.FolderDeltas!, mgr.Resolver!);
        var compiled = compiler.Compile(ReadDirectory(mgr, folder));
        Ok(compiled);
        Assert.False(compiled.Value.Directory.HasPendingDelta); // no pending delta ⇒ (c) does not apply
        var vLive = ReadDirectory(mgr, folder).FolderVersion;
        Assert.NotEqual(v0, vLive);                             // the folder's version advanced past the filter's

        // The stale filter (covers "apple" @ v0) does NOT wrongly skip: the new "banana" content is found...
        var bananaSearch = mgr.SearchMailbox(new[] { folder }, "banana");
        Ok(bananaSearch);
        Assert.Equal(0, bananaSearch.Value.FoldersSkipped);
        Assert.Equal(1, bananaSearch.Value.MatchCount);
        Assert.Equal(banana.EmailHashedId, bananaSearch.Value.Hits[0].Record.EmailHashedId);

        // ...and a token the stale filter WOULD have eliminated is not skipped either (fail-closed on drift).
        var absentStale = mgr.SearchMailbox(new[] { folder }, "xylophone-zzz");
        Ok(absentStale);
        Assert.Equal(0, absentStale.Value.FoldersSkipped);

        // Rebuild at the live version: skipping is restored, proving the version guard — not something else —
        // was suppressing the skip above.
        Ok(mgr.RebuildFolderBloomFilter(folder));
        var absentFresh = mgr.SearchMailbox(new[] { folder }, "xylophone-zzz");
        Ok(absentFresh);
        Assert.Equal(1, absentFresh.Value.FoldersSkipped);
    }

    // --------------------------------------------------------- No bloom index built yet

    [Fact]
    public void With_no_filters_built_SearchMailbox_degrades_to_scanning_every_folder()
    {
        // (e) A file opened before any filter was rebuilt has an empty Bloom index, so no folder can be
        // eliminated: MightMatch returns true for every folder and SearchMailbox scans them all. The results
        // are still correct — degrading to a full scan only forfeits the optimization, never correctness.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var apples = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", "Apple One", "orchard") });
        var bananas = BuildFolder(mgr, new[] { Rec(400, "b@corp.com", "Banana Two", "recipe") });
        Assert.Equal(0, mgr.Bloom!.FolderCount); // no filter was ever built

        var found = mgr.SearchMailbox(new[] { apples, bananas }, "apple");
        Ok(found);
        Assert.Equal(0, found.Value.FoldersSkipped); // nothing to consult ⇒ scan everything
        Assert.Equal(2, found.Value.RecordsScanned); // both folders scanned though only one can match
        Assert.Equal(1, found.Value.MatchCount);
        Assert.Equal(apples, found.Value.Hits[0].FolderId);
    }

    // --------------------------------------------------------- Delta safety

    [Fact]
    public void A_folder_with_pending_deltas_is_never_skipped_even_when_its_pages_cannot_match()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Compiled page holds "apple"; a pending delta adds "banana". The filter is built over pages ONLY.
        var appleRec = Rec(500, "a@corp.com", "Apple Page", "compiled");
        var bananaRec = Rec(600, "b@corp.com", "Banana Delta", "pending, not compiled");
        var folder = BuildFolder(mgr, new[] { appleRec }, new[] { FolderDeltaEntry.Add(bananaRec) });
        Ok(mgr.RebuildFolderBloomFilter(folder)); // covers "apple" only, stamped at the pending version

        // "banana" is not in the pages-only filter, but the folder has a pending delta ⇒ it must be SCANNED,
        // and the pending "banana" row is found. A wrong skip here would lose the delta row.
        var found = mgr.SearchMailbox(new[] { folder }, "banana");
        Ok(found);
        Assert.Equal(0, found.Value.FoldersSkipped);
        Assert.Equal(1, found.Value.MatchCount);
        Assert.Equal(bananaRec.EmailHashedId, found.Value.Hits[0].Record.EmailHashedId);

        // Even a genuinely-absent query does not skip a folder that has pending deltas.
        var absent = mgr.SearchMailbox(new[] { folder }, "xylophone-zzz");
        Ok(absent);
        Assert.Equal(0, absent.Value.FoldersSkipped);
    }

    // --------------------------------------------------------- Rebuild with page compile

    [Fact]
    public void Rebuilding_after_a_page_compile_covers_the_formerly_pending_rows_and_enables_skipping()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Compiled page "apple" + pending delta Add "banana".
        var appleRec = Rec(500, "a@corp.com", "Apple Page", "compiled");
        var bananaRec = Rec(600, "b@corp.com", "Banana Delta", "pending");
        var folder = BuildFolder(mgr, new[] { appleRec }, new[] { FolderDeltaEntry.Add(bananaRec) });

        // Compile the pending delta into the pages (docs/Folder_Listing.md Section 3), then rebuild the filter
        // from the now-compiled pages — the "filters rebuilt with page compile" criterion.
        var compiler = new FolderCompiler(mgr.Folders!, mgr.FolderDirectory!, mgr.FolderDeltas!, mgr.Resolver!);
        var beforeDir = ReadDirectory(mgr, folder);
        var compiled = compiler.Compile(beforeDir);
        Ok(compiled);
        Assert.False(compiled.Value.Directory.HasPendingDelta); // deltas folded into pages

        Ok(mgr.RebuildFolderBloomFilter(folder));

        // The rebuilt filter now covers "banana"; with no pending delta the folder participates in skipping.
        var hitBanana = mgr.SearchMailbox(new[] { folder }, "banana");
        Ok(hitBanana);
        Assert.Equal(0, hitBanana.Value.FoldersSkipped);       // "banana" present ⇒ scanned
        Assert.Equal(1, hitBanana.Value.MatchCount);

        var absent = mgr.SearchMailbox(new[] { folder }, "xylophone-zzz");
        Ok(absent);
        Assert.Equal(1, absent.Value.FoldersSkipped);          // now skippable — no pending delta, absent token
        Assert.Equal(0, absent.Value.MatchCount);
    }

    [Fact]
    public void Rebuilding_at_the_compile_threshold_advances_the_covered_version_and_covers_the_compiled_rows()
    {
        // (a) "Filters rebuilt with page compile", proven at the threshold the story names: a folder whose
        // pending delta chain has reached FolderCompiler.CompileThreshold (500) entries is compiled — the
        // page-compile step — and the filter rebuilt over the now-compiled pages. The rebuilt filter's
        // CoveredFolderVersion must advance to the compiled directory version (not stay at the pre-compile
        // version), a formerly-pending token must become queryable through the gate, and an absent token must
        // become skippable now that no pending delta forces a scan.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // One compiled page ("apple"); the filter over it is stamped at the folder's initial version.
        var folder = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", "Apple Page", "compiled") });
        Ok(mgr.RebuildFolderBloomFilter(folder));
        var vInitial = mgr.Bloom!.GetFilter(folder)!.CoveredFolderVersion;

        // Accumulate exactly CompileThreshold pending Add deltas (spread over several delta blocks to walk the
        // chain), one carrying a distinctive token no page holds yet. All dates route into the single page.
        const string compiledToken = "zebrafish";
        const int total = FolderCompiler.CompileThreshold; // 500
        const int perBlock = 100;
        var dir = ReadDirectory(mgr, folder);
        for (int b = 0; b < total / perBlock; b++)
        {
            var batch = Enumerable.Range(0, perBlock)
                .Select(i =>
                {
                    int n = b * perBlock + i;
                    var subject = n == 250 ? $"{compiledToken} arrival" : $"Filler {n}";
                    return FolderDeltaEntry.Add(Rec(100 + n, $"u{n}@corp.com", subject, $"pending {n}"));
                })
                .ToArray();
            var appended = mgr.FolderDeltas!.AppendChained(dir, batch);
            Ok(appended);
            Ok(mgr.FolderDirectory!.WriteDirectory(appended.Value.Directory));
            dir = appended.Value.Directory;
        }
        Assert.True(dir.HasPendingDelta);
        Assert.True(FolderCompiler.ShouldCompile(total)); // the chain has reached the compile threshold

        // The page-compile step folds the 500 pending rows into the pages and bumps the folder version.
        var compiler = new FolderCompiler(mgr.Folders!, mgr.FolderDirectory!, mgr.FolderDeltas!, mgr.Resolver!);
        var compiled = compiler.Compile(dir);
        Ok(compiled);
        Assert.False(compiled.Value.Directory.HasPendingDelta);
        var vCompiled = ReadDirectory(mgr, folder).FolderVersion;
        Assert.NotEqual(vInitial, vCompiled); // the directory version advanced past the filter's

        // Rebuild WITH the page compile: the filter now covers the compiled rows at the advanced version.
        Ok(mgr.RebuildFolderBloomFilter(folder));
        Assert.Equal(vCompiled, mgr.Bloom!.GetFilter(folder)!.CoveredFolderVersion);

        // The formerly-pending token is now covered ⇒ the folder is scanned and the row is found...
        var hit = mgr.SearchMailbox(new[] { folder }, compiledToken);
        Ok(hit);
        Assert.Equal(0, hit.Value.FoldersSkipped);
        Assert.Equal(1, hit.Value.MatchCount);

        // ...and an absent token is now skippable: no pending delta remains and the filter covers the live version.
        var absent = mgr.SearchMailbox(new[] { folder }, "xylophone-zzz");
        Ok(absent);
        Assert.Equal(1, absent.Value.FoldersSkipped);
        Assert.Equal(0, absent.Value.MatchCount);
    }

    private static FolderPageDirectory ReadDirectory(EmailManager mgr, byte[] folderId)
    {
        Assert.True(mgr.Resolver!.TryGetLocation(folderId, out var loc) && loc is not null);
        var dir = mgr.FolderDirectory!.ReadDirectory(loc!.Offset);
        Ok(dir);
        return dir.Value;
    }

    // --------------------------------------------------------- Checkpoint registration + reopen

    [Fact]
    public void Committed_filters_register_under_IndexKind_4_and_are_recovered_on_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();
        byte[] apples, bananas;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            apples = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", "Apple Harvest", "orchard") });
            bananas = BuildFolder(mgr, new[] { Rec(400, "b@corp.com", "Banana Bread", "recipe") });
            Ok(mgr.RebuildFolderBloomFilter(apples));
            Ok(mgr.RebuildFolderBloomFilter(bananas));
            Ok(mgr.Close()); // commit: flush the catalog + register under IndexKind 4.
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            // Registered under IndexKind 4 and pointing at a real BloomFilter block (type 18).
            var bloom = reopened.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Bloom);
            var block = reopened.BlockManager.Read(bloom.Root.Offset);
            Ok(block);
            Assert.Equal(BlockType.BloomFilter, block.Value.Header.Type);
            Assert.Equal(2, reopened.Bloom!.FolderCount);

            // The recovered filters still gate the scan without any rebuild on this manager.
            var found = reopened.SearchMailbox(new[] { apples, bananas }, "apple");
            Ok(found);
            Assert.Equal(1, found.Value.MatchCount);
            Assert.Equal(1, found.Value.FoldersSkipped);
        }
    }

    // --------------------------------------------------------- Encryption

    [Fact]
    public void The_bloom_catalog_lands_encrypted_on_disk_and_recovers_on_reopen()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        byte[] folder;
        const string rareToken = "zqxjwv-marker";
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            Assert.True(mgr.IsEncrypted);
            folder = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", $"Subject {rareToken}", "body") });
            Ok(mgr.RebuildFolderBloomFilter(folder));
            Ok(mgr.Close());
        }

        using (var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            var bloom = reopened.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Bloom);
            var block = reopened.BlockManager.Read(bloom.Root.Offset);
            Ok(block);
            Assert.Equal(BlockType.BloomFilter, block.Value.Header.Type);
            Assert.True(block.Value.Header.IsEncrypted); // filter bits are always encrypted (spec 9.5)

            // The recovered, decrypted filter still gates the scan correctly.
            Assert.False(reopened.Bloom!.MightMatch(folder, "xylophone-absent", ReadDirectory(reopened, folder).FolderVersion, hasPendingDelta: false));
        }

        // Whole-file: the indexed token never appears in the clear at rest.
        var raw = File.ReadAllBytes(_path);
        Assert.True(raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(rareToken)) < 0,
            "The encrypted file leaked an indexed token from the Bloom catalog.");
    }

    // A deliberately rare token folded into the folder page subject: we scan the raw file for it, and
    // a covered folder id (16-byte ULID, copied verbatim into the catalog payload by the serializer) is
    // the catalog marker we scan the on-disk type-18 block for. In a plaintext catalog the folder id
    // appears; in ciphertext it must not, so its absence proves the payload is genuinely encrypted.
    private const string RareToken = "zqxjwv-marker";

    /// <summary>
    /// Story acceptance criterion — "Filters always encrypted" (mirrors the FTS criterion, US-EMDB-91-2):
    /// for an encrypted file created under each <see cref="EncryptionPolicy"/> the v3 stack defines
    /// (<see cref="EncryptionPolicy.Default"/> and <see cref="EncryptionPolicy.Full"/>), a real ingest +
    /// commit writes the <see cref="BlockType.BloomFilter"/> catalog (type 18) to disk encrypted: header
    /// <c>Encrypted</c> flag set, active key epoch stamped, and the ciphertext payload leaks neither a
    /// covered folder id (a catalog marker) nor the indexed token a plaintext catalog would expose
    /// (spec Section 9.5, <see cref="EncryptionPolicySet.RequiresEncryption"/> true for type 18).
    /// </summary>
    [Theory]
    [InlineData(EncryptionPolicy.Default)]
    [InlineData(EncryptionPolicy.Full)]
    public void The_bloom_catalog_lands_encrypted_on_disk_under_every_policy(EncryptionPolicy policy)
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = pw,
            KdfParameters = FastParams,
            EncryptionPolicy = policy,
        }).Value.Dispose();

        byte[] folder;
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            Assert.True(mgr.IsEncrypted);
            folder = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", $"Subject {RareToken}", "body") });
            Ok(mgr.RebuildFolderBloomFilter(folder));
            Ok(mgr.Close()); // commit: flush the catalog + register under IndexKind 4.
        }

        using (var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            ushort activeEpoch = reopened.EncryptionProvider!.ActiveEpoch;
            var bloom = reopened.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Bloom);
            var block = reopened.BlockManager.Read(bloom.Root.Offset);
            Ok(block);
            Assert.Equal(BlockType.BloomFilter, block.Value.Header.Type);

            Assert.True(block.Value.Header.IsEncrypted,
                $"the Bloom catalog must be encrypted at rest under {policy}.");
            Assert.Equal(activeEpoch, block.Value.Header.KeyEpoch); // active key epoch stamped
            // A plaintext catalog carries the covered folder id verbatim; the ciphertext must not.
            Assert.False(Contains(block.Value.Payload, folder),
                $"the Bloom catalog ciphertext leaked a covered folder id under {policy}.");
            Assert.False(Contains(block.Value.Payload, Encoding.UTF8.GetBytes(RareToken)),
                $"the Bloom catalog ciphertext leaked the indexed token under {policy}.");

            // And decryption on reopen recovers a filter that still gates the scan.
            Assert.False(reopened.Bloom!.MightMatch(folder, "xylophone-absent",
                ReadDirectory(reopened, folder).FolderVersion, hasPendingDelta: false));
        }

        // Whole-file (manager closed so the handle is free): the indexed token never appears in the clear.
        var raw = File.ReadAllBytes(_path);
        Assert.False(Contains(raw, Encoding.UTF8.GetBytes(RareToken)),
            $"the raw encrypted file leaked an indexed token under {policy}.");
    }

    /// <summary>
    /// Negative control for the "always encrypted" rule: docs/Search.md says the Bloom catalog is always
    /// encrypted under every policy, but a plaintext file has no keys — so, like every other block, the
    /// type-18 catalog is written in the clear (the <see cref="BloomFilterStore"/> "no provider ⇒ plaintext"
    /// contract, mirroring <see cref="FtsBlockStore"/>). A covered folder id therefore sits verbatim in the
    /// catalog payload, confirming genuine plaintext.
    /// </summary>
    [Fact]
    public void The_bloom_catalog_is_plaintext_on_an_unencrypted_file()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] folder;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            Assert.False(mgr.IsEncrypted);
            folder = BuildFolder(mgr, new[] { Rec(500, "a@corp.com", $"Subject {RareToken}", "body") });
            Ok(mgr.RebuildFolderBloomFilter(folder));
            Ok(mgr.Close());
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            var bloom = reopened.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Bloom);
            var block = reopened.BlockManager.Read(bloom.Root.Offset);
            Ok(block);
            Assert.Equal(BlockType.BloomFilter, block.Value.Header.Type);
            Assert.False(block.Value.Header.IsEncrypted,
                "the Bloom catalog should be plaintext on an unencrypted file.");
            // The covered folder id sits in the clear in the catalog payload — proof it is truly plaintext.
            Assert.True(Contains(block.Value.Payload, folder),
                "the plaintext Bloom catalog should carry the covered folder id verbatim.");
        }
    }

    /// <summary>True if <paramref name="needle"/> occurs as a contiguous byte run in <paramref name="haystack"/>.</summary>
    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;
}
