using System.Globalization;
using System.Text;

namespace EmailDB.Format.V3;

/// <summary>
/// Extracts the sliding-window trigrams of an address string for the Phase 1 FTS
/// index (docs/Search.md). This is the shared normalization + tokenization contract
/// between ingest (task 91-7 indexes an email's addresses) and query (task 91-8
/// trigrams the search text): both MUST run text through the same
/// <see cref="Normalize"/> then <see cref="Extract"/> so a query trigram matches an
/// indexed trigram exactly when the underlying characters match.
///
/// <para><b>Normalization (<see cref="Normalize"/>).</b> Unicode NFC composition then
/// invariant lowercase. NFC folds the many byte spellings of a composed character
/// (e.g. "é" as one code point vs. "e" + combining acute) to one canonical form, and
/// case folding makes the index case-insensitive — "Ryan@X" and "ryan@x" index and
/// query identically. Address strings are indexed whole (local part, "@", domain, and
/// dots included) so a substring query like "n@bi" spans the boundary and still
/// matches.</para>
///
/// <para><b>Trigrams (<see cref="Extract"/>).</b> A sliding window of three Unicode
/// runes over the normalized text: "ryan" → {rya, yan}. Runes (not chars) are the
/// window unit, so an astral character counts as one position, not a surrogate pair.
/// The result is the DISTINCT set — a document cares only whether a trigram is
/// present, and a repeated trigram (e.g. "aaaa" → only "aaa") must not create
/// duplicate postings. <b>Short input</b> (fewer than three runes) yields the empty
/// set: two characters cannot form a trigram, so "ab", "a", and "" index nothing.
/// A query shorter than three characters therefore has no trigrams to intersect and
/// the query path must fall back to a scan — a query-planner concern (task 91-8), not
/// an extraction one.</para>
/// </summary>
public static class TrigramExtractor
{
    /// <summary>The minimum number of runes an input needs to yield at least one trigram.</summary>
    public const int MinLength = Trigram.RuneCount;

    /// <summary>
    /// Canonicalizes address text before trigram extraction: Unicode NFC composition
    /// followed by invariant-culture lowercasing. Returns the empty string for null or
    /// empty input. Idempotent — normalizing already-normalized text is a no-op.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        // NFC first so case folding sees composed characters, then lowercase.
        string composed = text.IsNormalized(NormalizationForm.FormC)
            ? text
            : text.Normalize(NormalizationForm.FormC);
        return composed.ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes <paramref name="text"/> (<see cref="Normalize"/>) and returns the
    /// DISTINCT set of its sliding-window trigrams. Empty, null, or fewer-than-three-rune
    /// input yields an empty set.
    /// </summary>
    public static IReadOnlySet<Trigram> Extract(string? text)
    {
        var set = new HashSet<Trigram>();
        ExtractInto(text, set);
        return set;
    }

    /// <summary>
    /// Normalizes <paramref name="text"/> and adds its distinct trigrams to
    /// <paramref name="destination"/> (accumulating across several fields of one email
    /// without an intermediate allocation). Returns the number of trigrams newly added.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    public static int ExtractInto(string? text, ISet<Trigram> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string normalized = Normalize(text);
        if (normalized.Length < MinLength)
            return 0;

        // Decode the whole string to runes once, then slide a size-3 window.
        // Runes-not-chars means surrogate pairs occupy a single window slot.
        Span<int> runes = normalized.Length <= 256
            ? stackalloc int[normalized.Length]
            : new int[normalized.Length];
        int n = 0;
        foreach (Rune rune in normalized.EnumerateRunes())
            runes[n++] = rune.Value;

        if (n < MinLength)
            return 0;

        int added = 0;
        for (int i = 0; i + Trigram.RuneCount <= n; i++)
        {
            var tri = new Trigram(runes[i], runes[i + 1], runes[i + 2]);
            if (destination.Add(tri))
                added++;
        }
        return added;
    }
}
