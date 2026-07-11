using System.Text;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="TrigramExtractor"/> and <see cref="Trigram"/> (US-EMDB-91-6,
/// docs/Search.md Phase 1): the shared normalization + sliding-window trigram contract
/// that ingest (task 91-7) and query (task 91-8) both run text through so a query
/// trigram matches an indexed trigram exactly when the characters match. Covers
/// normalization (NFC + case fold), the distinct sliding window, short-input handling,
/// unicode/astral runes, and the fixed 12-byte trigram encoding.
/// </summary>
public class TrigramExtractionTests
{
    // ------------------------------------------------------------- Normalization

    [Fact]
    public void Normalize_lowercases_and_is_case_insensitive()
    {
        Assert.Equal("ryan@biztactix.com.au", TrigramExtractor.Normalize("Ryan@BizTactix.Com.AU"));
    }

    [Fact]
    public void Normalize_returns_empty_for_null_or_empty()
    {
        Assert.Equal(string.Empty, TrigramExtractor.Normalize(null));
        Assert.Equal(string.Empty, TrigramExtractor.Normalize(""));
    }

    [Fact]
    public void Normalize_NFC_folds_decomposed_and_composed_to_the_same_form()
    {
        // "é" composed (U+00E9) vs decomposed "e" + combining acute (U+0065 U+0301).
        string composed = "café@x.com";
        string decomposed = "café@x.com";
        Assert.NotEqual(composed, decomposed); // different code points before normalization

        string nc = TrigramExtractor.Normalize(composed);
        string nd = TrigramExtractor.Normalize(decomposed);
        Assert.Equal(nc, nd); // same after NFC

        // ... and therefore identical trigram sets.
        Assert.Equal(
            TrigramExtractor.Extract(composed).OrderBy(t => t),
            TrigramExtractor.Extract(decomposed).OrderBy(t => t));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        string once = TrigramExtractor.Normalize("Ryan@Éx.COM");
        Assert.Equal(once, TrigramExtractor.Normalize(once));
    }

    // ------------------------------------------------------------- Sliding window

    [Fact]
    public void Extract_produces_the_sliding_window_of_the_whole_string()
    {
        var set = TrigramExtractor.Extract("ryan");
        Assert.Equal(
            new[] { new Trigram('r', 'y', 'a'), new Trigram('y', 'a', 'n') }.OrderBy(t => t),
            set.OrderBy(t => t));
    }

    [Fact]
    public void Extract_spans_the_at_and_dot_boundaries_for_substring_search()
    {
        // A substring query crossing the "@" (e.g. "n@x") must match the indexed address,
        // so the address is trigrammed whole, not split into local-part/domain tokens.
        var set = TrigramExtractor.Extract("a@x.io");
        Assert.Contains(new Trigram('a', '@', 'x'), set);
        Assert.Contains(new Trigram('@', 'x', '.'), set);
        Assert.Contains(new Trigram('x', '.', 'i'), set);
    }

    [Fact]
    public void Extract_returns_distinct_trigrams()
    {
        // "aaaa" has repeated windows "aaa","aaa" → a single distinct trigram.
        var set = TrigramExtractor.Extract("aaaa");
        Assert.Single(set);
        Assert.Contains(new Trigram('a', 'a', 'a'), set);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void Extract_yields_nothing_for_input_shorter_than_three_runes(string? input)
    {
        Assert.Empty(TrigramExtractor.Extract(input));
    }

    [Fact]
    public void Extract_at_exactly_three_runes_yields_one_trigram()
    {
        var set = TrigramExtractor.Extract("abc");
        Assert.Single(set);
        Assert.Contains(new Trigram('a', 'b', 'c'), set);
    }

    // ------------------------------------------------------------- Unicode / runes

    [Fact]
    public void Extract_counts_an_astral_character_as_one_window_position()
    {
        // "😀" is one rune but two UTF-16 chars (a surrogate pair). "a😀b" has 3 runes,
        // so it must yield exactly one trigram over runes, not a broken half-surrogate one.
        var set = TrigramExtractor.Extract("a\U0001F600b");
        Assert.Single(set);
        int emoji = new Rune(0x1F600).Value;
        Assert.Contains(new Trigram('a', emoji, 'b'), set);
    }

    [Fact]
    public void Extract_handles_a_long_string_beyond_the_stackalloc_threshold()
    {
        // >256 runes forces the heap path in ExtractInto; result must still be correct.
        string s = new string('x', 300) + "yz";
        var set = TrigramExtractor.Extract(s);
        Assert.Contains(new Trigram('x', 'x', 'x'), set);
        Assert.Contains(new Trigram('x', 'y', 'z'), set);
    }

    [Fact]
    public void ExtractInto_accumulates_across_fields_and_reports_new_additions()
    {
        var set = new HashSet<Trigram>();
        int a = TrigramExtractor.ExtractInto("abc", set);   // abc
        int b = TrigramExtractor.ExtractInto("bcd", set);   // bcd (abc-overlap "bc.." none shared trigram)
        int again = TrigramExtractor.ExtractInto("abc", set); // all already present

        Assert.Equal(1, a);
        Assert.Equal(1, b);
        Assert.Equal(0, again);
        Assert.Equal(2, set.Count);
    }

    // ------------------------------------------------------------- Trigram codec

    [Fact]
    public void Trigram_round_trips_through_its_12_byte_encoding()
    {
        Assert.Equal(12, Trigram.Size);
        var tri = new Trigram('r', 'y', 'a');
        var bytes = tri.GetBytes();
        Assert.Equal(Trigram.Size, bytes.Length);
        Assert.Equal(tri, Trigram.ReadFrom(bytes));
    }

    [Fact]
    public void Trigram_round_trips_an_astral_rune()
    {
        var tri = new Trigram(new Rune(0x1F600), new Rune('a'), new Rune(0x00E9));
        Assert.Equal(tri, Trigram.ReadFrom(tri.GetBytes()));
    }

    [Fact]
    public void Trigram_ordering_matches_rune_order()
    {
        Assert.True(new Trigram('a', 'a', 'a').CompareTo(new Trigram('a', 'a', 'b')) < 0);
        Assert.True(new Trigram('a', 'b', 'a').CompareTo(new Trigram('a', 'a', 'z')) > 0);
        Assert.Equal(0, new Trigram('a', 'b', 'c').CompareTo(new Trigram('a', 'b', 'c')));
    }

    [Fact]
    public void Trigram_rejects_an_invalid_scalar_value()
    {
        // A lone surrogate (U+D800) is not a valid Unicode scalar.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Trigram(0xD800, 'a', 'b'));
    }

    [Fact]
    public void Trigram_ReadFrom_rejects_a_wrong_width_span()
    {
        Assert.Throws<ArgumentException>(() => Trigram.ReadFrom(new byte[Trigram.Size - 1]));
    }
}
