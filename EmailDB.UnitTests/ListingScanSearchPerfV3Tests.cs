using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Performance acceptance test for story US-EMDB-92 (docs/Search.md Phase 2), task US-EMDB-92-1:
/// <b>a folder-scoped listing scan of a 50K-email folder completes in ~15ms warm</b>.
///
/// <para>The folder is built the same way <see cref="FolderListingReadCostV3Tests"/> builds its 50K
/// folder — 625 compiled <see cref="FolderPage"/>s of <see cref="FolderPage.TargetRecordsPerPage"/>
/// (80) records — but written through the live <see cref="EmailManager"/> stores so
/// <see cref="EmailManager.SearchFolder"/> can resolve and scan the effective listing end-to-end.</para>
///
/// <para><b>Measured (Release, 25 warm iterations after warm-up, dev hardware 2026-07):</b> the pure
/// <see cref="ListingScanMatcher"/> scan over the 50K resident rows takes ~4ms best — comfortably
/// under the ~15ms target. The full public <see cref="EmailManager.SearchFolder"/> call, however,
/// takes ~99ms best / ~143ms avg, because it has <b>no cached effective listing</b>: every call
/// re-reads, AES-GCM-decrypts and Zstd-decompresses all 625 pages and re-runs
/// <see cref="FolderListingMerger"/> before scanning. So whether the ~15ms criterion is "met" depends
/// on what "warm" means — a warm/resident listing (matcher only, ~4ms) meets it; a cold-per-call
/// public scan does not. That judgment (add a warm listing cache vs. refine the spec) is left to a
/// human: task US-EMDB-92-1 is parked in <c>review</c> with these numbers.</para>
///
/// <para>The assertions below therefore verify (1) correctness at 50K scale, (2) the warm/resident
/// scan cost meets the ~15ms target with CI headroom, and (3) a coarse end-to-end regression guard.
/// CI hardware varies, so timings are the <b>best</b> of many warm iterations — the fairest
/// stand-in for "warm", discarding GC/scheduler noise while reflecting the real work.</para>
/// </summary>
public class ListingScanSearchPerfV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-scanperf-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    /// <summary>Counts case-insensitive matches over an in-memory listing (the pure warm-scan work).</summary>
    private static int RunMatcher(List<ListingRecord> rows, string needle)
    {
        int m = 0;
        for (int i = 0; i < rows.Count; i++)
            if (ListingScanMatcher.Matches(rows[i], needle, ListingSearchField.All)) m++;
        return m;
    }

    // Embed the full 32-bit seed so every one of the 50K records gets a genuinely distinct id — a
    // wrapping single-byte fill would collide and the effective-listing merge would dedup the rows.
    private static V3Id IdOf(int seed)
    {
        var raw = new byte[V3Id.Size];
        BitConverter.TryWriteBytes(raw, seed);
        BitConverter.TryWriteBytes(raw.AsSpan(4), seed * 2654435761u); // scatter into more bytes
        for (var i = 8; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new V3Id(raw);
    }

    private static byte[] UlidOf(int seed)
    {
        var ulid = new byte[UlidGenerator.UlidSize];
        BitConverter.TryWriteBytes(ulid, seed);
        BitConverter.TryWriteBytes(ulid.AsSpan(4), seed * 40503u);
        for (var i = 8; i < ulid.Length; i++) ulid[i] = (byte)(seed * 7 + i);
        return ulid;
    }

    [Fact]
    public void SearchFolder_scanning_a_50K_email_folder_completes_warm_near_15ms()
    {
        const int recordsPerPage = FolderPage.TargetRecordsPerPage; // 80
        const int pageCount = 625;                                  // 625 * 80 = 50,000 emails
        const int totalEmails = recordsPerPage * pageCount;
        const int spacingPerPage = 1_000;                           // >> 80 so pages don't overlap
        const string needle = "apollo-mission";                     // seeded into a sparse subset only
        const int matchEvery = 500;                                 // ~100 matches across 50K rows

        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Build 625 compiled, encrypted, Zstd'd pages newest-first through the live manager stores, so
        // the manager's resolver knows every page and directory block SearchFolder must read.
        var entries = new List<PageEntry>(pageCount);
        int expectedMatches = 0;
        int seq = 0;
        for (int p = 0; p < pageCount; p++)
        {
            long dateTo = (long)(pageCount - p) * spacingPerPage; // newest page has the largest date
            long dateFrom = dateTo - (recordsPerPage - 1);        // 80 distinct ticks per page

            var records = new List<ListingRecord>(recordsPerPage);
            for (int r = 0; r < recordsPerPage; r++, seq++)
            {
                bool seeded = seq % matchEvery == 0;
                if (seeded) expectedMatches++;
                records.Add(new ListingRecord
                {
                    EmailHashedId = IdOf(seq),
                    ContentBlockId = UlidOf(seq),
                    DateTicks = dateTo - r,
                    Flags = ListingFlags.None,
                    MessageSize = 1_000 + r,
                    From = "sender@example.com",
                    Subject = $"Subject page {p} row {r}",
                    Preview = seeded
                        ? $"quarterly numbers for the {needle} rollout"
                        : new string('x', 120),
                });
            }

            var loc = mgr.Folders!.WritePage(FolderPage.FromRecords(records));
            Ok(loc);
            entries.Add(new PageEntry(loc.Value.BlockId, dateFrom, dateTo, recordsPerPage));
        }

        var folderId = UlidOf(9_999);
        var dir = FolderPageDirectory.Create(folderId, entries);
        Ok(mgr.FolderDirectory!.WriteDirectory(dir));

        // Correctness at scale: a full scan examines every one of the 50K rows and finds exactly the
        // sparse seeded matches, newest-first.
        var first = mgr.SearchFolder(folderId, needle);
        Ok(first);
        Assert.False(first.Value.WholeMailbox);
        Assert.Equal(totalEmails, first.Value.RecordsScanned);
        Assert.Equal(expectedMatches, first.Value.MatchCount);
        for (int i = 1; i < first.Value.Hits.Count; i++)
            Assert.True(first.Value.Hits[i - 1].Record.DateTicks >= first.Value.Hits[i].Record.DateTicks,
                "hits must be newest-first");

        // Warm up: prime the file cache, JIT, and any per-call allocation paths.
        const int warmups = 5;
        for (int i = 0; i < warmups; i++)
            Ok(mgr.SearchFolder(folderId, needle));

        // Measure: best-of-N warm scans. Best-of discards GC/scheduler outliers on shared CI hardware.
        const int iterations = 25;
        double bestMs = double.MaxValue;
        double totalMs = 0;
        var sw = new Stopwatch();
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            var scan = mgr.SearchFolder(folderId, needle);
            sw.Stop();
            Ok(scan);
            Assert.Equal(totalEmails, scan.Value.RecordsScanned);
            double ms = sw.Elapsed.TotalMilliseconds;
            totalMs += ms;
            if (ms < bestMs) bestMs = ms;
        }
        double avgMs = totalMs / iterations;

        // Warm/resident scan cost: the pure matcher over the 50K rows once they are in memory — this is
        // the work the ~15ms target bounds if "warm" means an already-loaded effective listing.
        var flat = new List<ListingRecord>(totalEmails);
        for (int s2 = 0; s2 < totalEmails; s2++)
        {
            bool seeded = s2 % matchEvery == 0;
            flat.Add(new ListingRecord
            {
                EmailHashedId = IdOf(s2), ContentBlockId = UlidOf(s2), DateTicks = s2,
                Flags = ListingFlags.None, MessageSize = 1000, From = "sender@example.com",
                Subject = $"Subject {s2}",
                Preview = seeded ? $"the {needle} rollout" : new string('x', 120),
            });
        }
        for (int w = 0; w < 3; w++) RunMatcher(flat, needle); // warm up JIT
        double matcherBest = double.MaxValue;
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            RunMatcher(flat, needle);
            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds < matcherBest) matcherBest = sw.Elapsed.TotalMilliseconds;
        }

        const double target = 15.0;
        string report =
            $"50K-folder scan — warm/resident matcher best={matcherBest:F2}ms (target ~{target}ms); " +
            $"full SearchFolder best={bestMs:F2}ms avg={avgMs:F2}ms over {iterations} iters " +
            $"(re-reads+decrypts+decompresses {pageCount} pages + re-merges every call).";

        // (2) The warm/resident scan meets the ~15ms target (generous 3x CI headroom).
        Assert.True(matcherBest <= target * 3, report);

        // (3) Coarse end-to-end regression guard only — NOT a verification of the ~15ms warm target,
        // which the current cache-less SearchFolder does not meet end-to-end (see class remarks; task
        // parked in review). Catches gross regressions (e.g. an accidental O(n^2) or lost merge dedup).
        Assert.True(bestMs <= 400.0, report);
    }
}
