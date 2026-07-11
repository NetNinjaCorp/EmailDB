namespace EmailDB.Format.V3;

/// <summary>
/// The Tier 1 tokenization that feeds the per-folder Bloom filters (docs/Search.md Phase 5) and the
/// matching query-side tokenization — the two <b>must</b> agree so the filter is a sound skip test for
/// the <see cref="ListingScanMatcher"/> keyword scan (docs/Search.md Phase 2).
///
/// <para><b>Tokens are 3-character windows.</b> Each of a <see cref="ListingRecord"/>'s three searchable
/// Tier 1 fields — <see cref="ListingRecord.Subject"/>, <see cref="ListingRecord.From"/>,
/// <see cref="ListingRecord.Preview"/> — is case-folded per UTF-16 code unit (<see cref="char.ToUpperInvariant(char)"/>,
/// mirroring the ordinal case-insensitive folding <see cref="ListingScanMatcher"/> uses) and windowed into
/// every contiguous 3-char substring; each window is hashed to a 64-bit value and added to the folder's
/// filter. Three-character windows are the smallest unit that keeps the skip test <b>sound</b>: because the
/// scan matches a query as a case-insensitive substring, if the query (≥3 chars) is a substring of a field
/// then every 3-char window of the folded query is a 3-char substring of the folded field — hence in the
/// filter. So if ANY query window tests absent, no record in the folder can contain the query and the folder
/// is safely skipped; a query <b>shorter</b> than 3 chars produces no window to test and can never be
/// eliminated (<see cref="MightMatch"/> returns true), so its folder is always scanned.</para>
///
/// <para>All fields go into one filter. A filter built over every field is a superset of any single field's
/// tokens, so it stays sound for a field-restricted query (Subject-only, etc.): a window absent from the
/// all-fields filter is absent from every field, so it still cannot match — a field restriction only ever
/// adds false positives (an extra scan), never a wrong skip.</para>
/// </summary>
public static class ListingBloomTokens
{
    /// <summary>The window length in UTF-16 code units — the minimum for a sound substring skip test.</summary>
    public const int WindowLength = 3;

    /// <summary>
    /// Adds the 64-bit hash of every 3-char case-folded window of the record's Subject, From, and Preview
    /// fields to <paramref name="tokenHashes"/>. A field shorter than <see cref="WindowLength"/> contributes
    /// nothing. The set folds duplicate windows so the caller's filter is sized over DISTINCT tokens.
    /// </summary>
    public static void AddRecordTokens(ListingRecord record, ISet<ulong> tokenHashes)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(tokenHashes);
        AddWindows(record.Subject, tokenHashes);
        AddWindows(record.From, tokenHashes);
        AddWindows(record.Preview, tokenHashes);
    }

    /// <summary>
    /// The distinct 64-bit window hashes of <paramref name="query"/>. Empty when the query is shorter than
    /// <see cref="WindowLength"/> (nothing to test) — the caller treats that as "cannot eliminate".
    /// </summary>
    public static IReadOnlyCollection<ulong> QueryTokenHashes(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var set = new HashSet<ulong>();
        AddWindows(query, set);
        return set;
    }

    /// <summary>
    /// Whether <paramref name="query"/> might match some record in the folder the <paramref name="filter"/>
    /// covers: true when the query is too short to tokenize (cannot be eliminated) or every query window
    /// tests present; false only when a window is DEFINITELY absent — an authoritative "cannot match" that
    /// lets the caller skip the folder's page scan.
    /// </summary>
    public static bool MightMatch(BloomFilter filter, string query)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length < WindowLength)
            return true; // No window to test — the scan phase must decide.

        foreach (var hash in QueryTokenHashes(query))
            if (!filter.MightContainHash(hash))
                return false; // A window is certainly absent ⇒ no record can contain the query substring.
        return true;
    }

    private static void AddWindows(string? text, ISet<ulong> destination)
    {
        if (string.IsNullOrEmpty(text) || text.Length < WindowLength)
            return;
        for (int i = 0; i + WindowLength <= text.Length; i++)
        {
            char a = char.ToUpperInvariant(text[i]);
            char b = char.ToUpperInvariant(text[i + 1]);
            char c = char.ToUpperInvariant(text[i + 2]);
            destination.Add(HashWindow(a, b, c));
        }
    }

    /// <summary>FNV-1a 64-bit hash of a folded 3-char window (little-endian byte order of the three chars).</summary>
    private static ulong HashWindow(char a, char b, char c)
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        ulong h = FnvOffset;
        h = (h ^ (byte)(a & 0xFF)) * FnvPrime;
        h = (h ^ (byte)(a >> 8)) * FnvPrime;
        h = (h ^ (byte)(b & 0xFF)) * FnvPrime;
        h = (h ^ (byte)(b >> 8)) * FnvPrime;
        h = (h ^ (byte)(c & 0xFF)) * FnvPrime;
        h = (h ^ (byte)(c >> 8)) * FnvPrime;
        return h;
    }
}
