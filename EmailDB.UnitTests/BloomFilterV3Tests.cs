using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Pure-unit tests for the per-folder Bloom filter primitives (US-EMDB-97-5, docs/Search.md Phase 5):
/// <see cref="BloomFilter"/> sizing/membership, the Tier 1 <see cref="ListingBloomTokens"/> tokenization
/// and its soundness against the <see cref="ListingScanMatcher"/> keyword scan, and the catalog
/// serialization round-trip. No file/manager — the block-wired maintenance and search-skip behavior have
/// their own suites (<see cref="FolderBloomIndexV3Tests"/>, BloomFilterSearchV3Tests).
/// </summary>
public class BloomFilterUnitV3Tests
{
    // --------------------------------------------------------- Sizing (criterion: ~1% FP)

    // The exact standard-formula sizing across folder sizes from tiny to well over 10K distinct tokens
    // (criterion a): m = ceil(-n·ln p / (ln 2)²), k = round((m/n)·ln 2) at p = 0.01. ~9.6 bits/token and
    // k = 7 are the hallmark of a 1%-sized filter, so this both pins the formula and the realized rate shape.
    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(1000)]
    [InlineData(10_000)]
    [InlineData(250_000)]
    public void Optimal_matches_the_standard_bloom_formula_for_one_percent(long n)
    {
        const double p = 0.01;
        var (bits, hashes) = BloomFilter.Optimal(n, p);

        double ln2 = Math.Log(2);
        double expectedM = Math.Ceiling(-n * Math.Log(p) / (ln2 * ln2));
        int expectedBits = (int)Math.Min(expectedM, int.MaxValue - 64);
        int expectedHashes = (int)Math.Round((double)expectedBits / n * ln2);

        Assert.Equal(expectedBits, bits);
        Assert.Equal(expectedHashes, hashes);
        Assert.InRange((double)bits / n, 9.0, 10.0); // ≈9.6 bits/token is the signature of p≈1%
        Assert.InRange(hashes, 6, 8);                // k rounds to 7 at 1%
    }

    [Fact]
    public void Optimal_handles_the_degenerate_zero_token_folder_without_dividing_by_zero()
    {
        // A folder with no Tier-1 tokens must not divide by n = 0; it yields the minimal all-zero shape.
        var (bits, hashes) = BloomFilter.Optimal(0, 0.01);
        Assert.Equal(1, bits);
        Assert.Equal(1, hashes);
    }

    [Fact]
    public void Optimal_rejects_a_negative_count_and_an_out_of_range_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.Optimal(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.Optimal(10, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.Optimal(10, 1.0));
    }

    [Fact]
    public void Measured_false_positive_rate_is_near_the_one_percent_target_with_no_false_negatives()
    {
        const int n = 4000;
        var rng = new Random(12345);
        var members = new HashSet<ulong>();
        while (members.Count < n)
            members.Add((ulong)rng.NextInt64());

        var filter = BloomFilter.Build(members, 0.01);

        // No false negatives: every member must test present.
        foreach (var m in members)
            Assert.True(filter.MightContainHash(m));

        // False-positive rate over non-members near the 1% target (double hashing runs a touch high; the
        // sizing is designed for ~1%, so a 2.5% ceiling is a safe non-flaky bound).
        int probes = 40000, falsePositives = 0;
        for (int i = 0; i < probes; i++)
        {
            ulong candidate = (ulong)rng.NextInt64();
            if (members.Contains(candidate)) continue;
            if (filter.MightContainHash(candidate)) falsePositives++;
        }
        double fp = (double)falsePositives / probes;
        Assert.InRange(fp, 0.0, 0.025);
    }

    [Fact]
    public void An_empty_filter_eliminates_every_token()
    {
        var filter = BloomFilter.Build(Array.Empty<ulong>());
        Assert.Equal(0, filter.TokenCount);
        Assert.False(filter.MightContainHash(1));
        Assert.False(filter.MightContainHash(ulong.MaxValue));
    }

    // The end-to-end criterion (b/c): build a filter from a realistic folder's Tier-1 tokens using the REAL
    // tokenizer over generated From/Subject/Preview fields, then probe with a large set of known-absent 3-char
    // windows produced by that same tokenizer and assert the measured false-positive rate is near 1%. Run for a
    // tiny folder AND a folder well past 10K distinct tokens so the ~1% bound holds at both extremes.
    [Theory]
    [InlineData(80)]      // tiny folder — a handful of short emails
    [InlineData(12_000)]  // large folder — 12K+ distinct Tier-1 tokens
    public void Measured_fp_over_real_tokenizer_tokens_stays_near_one_percent(int targetDistinctTokens)
    {
        var rng = new Random(9876 + targetDistinctTokens);

        // Accumulate DISTINCT window hashes straight from the production tokenizer over generated records.
        var members = new HashSet<ulong>();
        while (members.Count < targetDistinctTokens)
            ListingBloomTokens.AddRecordTokens(RandomRecord(rng), members);

        var filter = BloomFilter.Build(members, 0.01);

        // No false negatives: every token the tokenizer actually produced must test present.
        foreach (var m in members)
            Assert.True(filter.MightContainHash(m));

        // Probe with ~20K known-absent 3-char windows hashed by the SAME tokenizer (QueryTokenHashes), drawn
        // from a broad printable-ASCII alphabet so almost every window is genuinely absent from the folder.
        const int probes = 20_000;
        int measured = 0, falsePositives = 0;
        while (measured < probes)
        {
            ulong hash = ListingBloomTokens.QueryTokenHashes(RandomProbeWindow(rng)).Single();
            if (members.Contains(hash)) continue; // a real token — not an absent probe
            measured++;
            if (filter.MightContainHash(hash)) falsePositives++;
        }

        // At a true 1% rate, 20K probes expect ~200 FPs (σ≈14); a 2% ceiling is a non-flaky bound.
        double rate = (double)falsePositives / probes;
        Assert.InRange(rate, 0.0, 0.02);
    }

    private static readonly char[] WordChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    private static string RandomWord(Random rng, int min, int max)
    {
        int len = rng.Next(min, max + 1);
        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = WordChars[rng.Next(WordChars.Length)];
        return new string(chars);
    }

    private static ListingRecord RandomRecord(Random rng) => new()
    {
        EmailHashedId = default,
        ContentBlockId = new byte[16],
        DateTicks = rng.NextInt64(),
        Flags = ListingFlags.None,
        MessageSize = 1,
        From = $"{RandomWord(rng, 3, 8)}@{RandomWord(rng, 3, 8)}.com",
        Subject = string.Join(' ', Enumerable.Range(0, rng.Next(2, 6)).Select(_ => RandomWord(rng, 3, 9))),
        Preview = string.Join(' ', Enumerable.Range(0, rng.Next(4, 12)).Select(_ => RandomWord(rng, 3, 9))),
    };

    private static string RandomProbeWindow(Random rng)
    {
        var chars = new char[ListingBloomTokens.WindowLength];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = (char)rng.Next(33, 127); // printable ASCII — huge absent universe
        return new string(chars);
    }

    // --------------------------------------------------------- Serialization

    [Fact]
    public void Filter_round_trips_through_serialize_deserialize()
    {
        var members = new HashSet<ulong> { 1, 2, 3, 100, 9999, ulong.MaxValue };
        var filter = BloomFilter.Build(members, 0.01);

        var bytes = filter.Serialize();
        Assert.Equal(filter.SerializedLength, bytes.Length);

        var back = BloomFilter.Deserialize(bytes, out int consumed);
        Assert.True(back.IsSuccess, back.Error);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(filter.BitCount, back.Value.BitCount);
        Assert.Equal(filter.HashCount, back.Value.HashCount);
        Assert.Equal(filter.TokenCount, back.Value.TokenCount);
        foreach (var m in members)
            Assert.True(back.Value.MightContainHash(m));
    }

    [Fact]
    public void Deserialize_rejects_a_truncated_or_inconsistent_payload()
    {
        var bytes = BloomFilter.Build(new ulong[] { 1, 2, 3 }).Serialize();
        Assert.True(BloomFilter.Deserialize(bytes.AsSpan(0, BloomFilter.HeaderSize - 1), out _).IsFailure);
        // Corrupt the declared byte length so it no longer matches the bit count.
        var corrupt = (byte[])bytes.Clone();
        corrupt[16] ^= 0xFF;
        Assert.True(BloomFilter.Deserialize(corrupt, out _).IsFailure);
    }
}

/// <summary>
/// Soundness of the Tier 1 tokenization (<see cref="ListingBloomTokens"/>) against the Phase 2 keyword
/// scan: the core correctness guard of the story — a folder is skipped only when it truly cannot match.
/// </summary>
public class BloomFilterTokenizerV3Tests
{
    private static ListingRecord Rec(string from, string subject, string preview) => new()
    {
        EmailHashedId = default,
        ContentBlockId = new byte[16],
        DateTicks = 1,
        Flags = ListingFlags.None,
        MessageSize = 1,
        From = from,
        Subject = subject,
        Preview = preview,
    };

    private static BloomFilter FilterOver(params ListingRecord[] records)
    {
        var tokens = new HashSet<ulong>();
        foreach (var r in records)
            ListingBloomTokens.AddRecordTokens(r, tokens);
        return BloomFilter.Build(tokens);
    }

    [Fact]
    public void Every_substring_of_a_field_tests_present_so_a_real_match_is_never_eliminated()
    {
        var record = Rec("Alice <alice@Example.com>", "Quarterly Report", "See the ATTACHED numbers");
        var filter = FilterOver(record);

        // For each field, every case-variant substring of length >= 3 that the scan could match on MUST
        // survive the filter (MightMatch true) — otherwise the folder would be wrongly skipped.
        foreach (var field in new[] { record.From, record.Subject, record.Preview })
            for (int i = 0; i < field.Length; i++)
                for (int len = 3; i + len <= field.Length; len++)
                {
                    string sub = field.Substring(i, len);
                    Assert.True(ListingBloomTokens.MightMatch(filter, sub),
                        $"Filter wrongly eliminated the present substring '{sub}'.");
                    Assert.True(ListingBloomTokens.MightMatch(filter, sub.ToUpperInvariant()));
                    Assert.True(ListingBloomTokens.MightMatch(filter, sub.ToLowerInvariant()));
                }
    }

    [Fact]
    public void Whenever_the_scan_matches_the_filter_says_might_match()
    {
        // The invariant the SearchMailbox skip relies on: ListingScanMatcher.Matches ⇒ MightMatch.
        var record = Rec("bob@corp.com", "Widget Launch Plan", "the quarterly numbers are attached");
        var filter = FilterOver(record);

        string[] queries =
        {
            "widget", "WIDGET", "launch", "quarterly", "attach", "bob@", "corp.com", "the ", "numbers",
            "zzz-absent", "xylophone", "qqq", "###",
        };
        foreach (var q in queries)
            if (ListingScanMatcher.Matches(record, q, ListingSearchField.All))
                Assert.True(ListingBloomTokens.MightMatch(filter, q),
                    $"'{q}' matches the record but the filter eliminated it.");
    }

    [Fact]
    public void An_absent_token_is_eliminated_and_a_short_query_is_never_eliminated()
    {
        var filter = FilterOver(Rec("bob@corp.com", "Widget", "hello world"));

        // A three-plus-char token that appears nowhere is eliminated (this is the whole point of the filter).
        Assert.False(ListingBloomTokens.MightMatch(filter, "xylophone-zzz"));

        // A query shorter than the 3-char window cannot be tested, so the filter must never eliminate it.
        Assert.True(ListingBloomTokens.MightMatch(filter, "wi"));
        Assert.True(ListingBloomTokens.MightMatch(filter, "z"));
        Assert.True(ListingBloomTokens.MightMatch(filter, ""));
    }

    [Fact]
    public void A_filter_over_all_fields_stays_sound_for_a_single_field_query()
    {
        // "target" is only in From; the filter is built over all fields. A window absent from the all-fields
        // filter is absent from every field, so a From-only or Subject-only scan is still soundly gated.
        var record = Rec("target@corp.com", "unrelated subject", "unrelated preview");
        var filter = FilterOver(record);

        Assert.True(ListingScanMatcher.Matches(record, "target", ListingSearchField.From));
        Assert.True(ListingBloomTokens.MightMatch(filter, "target"));   // must not be eliminated
        Assert.False(ListingBloomTokens.MightMatch(filter, "zzz-nope")); // genuinely absent ⇒ eliminated
    }
}

/// <summary>Catalog serialization round-trip (US-EMDB-97-5): the type-18 block payload.</summary>
public class BloomFilterCatalogSerializationV3Tests
{
    private static FolderBloomFilter Folder(byte seed, ulong version, params ulong[] tokens) => new()
    {
        FolderId = Enumerable.Range(0, 16).Select(i => (byte)(seed + i)).ToArray(),
        CoveredFolderVersion = version,
        Filter = BloomFilter.Build(tokens),
    };

    [Fact]
    public void Catalog_with_several_folders_round_trips()
    {
        var catalog = BloomFilterCatalog.Create(7, new[]
        {
            Folder(0x30, 12, 1, 2, 3),
            Folder(0x10, 99, 100, 200),
            Folder(0x20, 3),
        });

        var bytes = BloomFilterCatalogSerializer.Serialize(catalog);
        var back = BloomFilterCatalogSerializer.Deserialize(bytes);
        Assert.True(back.IsSuccess, back.Error);
        Assert.Equal(7UL, back.Value.CatalogSequence);
        Assert.Equal(3, back.Value.FolderCount);

        // Create sorts ascending by folder id: 0x10, 0x20, 0x30.
        Assert.Equal(0x10, back.Value.Filters[0].FolderId[0]);
        Assert.Equal(0x20, back.Value.Filters[1].FolderId[0]);
        Assert.Equal(0x30, back.Value.Filters[2].FolderId[0]);
        Assert.Equal(99UL, back.Value.Filters[0].CoveredFolderVersion);
        Assert.True(back.Value.Filters[0].Filter.MightContainHash(100));
        Assert.True(back.Value.Filters[2].Filter.MightContainHash(3));
    }

    [Fact]
    public void An_empty_catalog_round_trips()
    {
        var catalog = BloomFilterCatalog.Create(0, Array.Empty<FolderBloomFilter>());
        var back = BloomFilterCatalogSerializer.Deserialize(BloomFilterCatalogSerializer.Serialize(catalog));
        Assert.True(back.IsSuccess, back.Error);
        Assert.Equal(0, back.Value.FolderCount);
    }

    [Fact]
    public void Deserialize_rejects_a_truncated_catalog()
    {
        var bytes = BloomFilterCatalogSerializer.Serialize(BloomFilterCatalog.Create(1, new[] { Folder(1, 1, 5) }));
        Assert.True(BloomFilterCatalogSerializer.Deserialize(bytes.AsSpan(0, bytes.Length - 4)).IsFailure);
    }
}
