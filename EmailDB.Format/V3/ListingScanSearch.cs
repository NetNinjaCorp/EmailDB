namespace EmailDB.Format.V3;

/// <summary>
/// Which Tier 1 <see cref="ListingRecord"/> fields a listing-page scan search
/// (docs/Search.md Phase 2) matches against. The three fields are the only text a
/// <see cref="FolderPage"/> row carries, so a Phase 2 scan needs no extra index — it
/// works day one and is the accuracy backstop other phases confirm candidates against.
/// </summary>
[Flags]
public enum ListingSearchField
{
    /// <summary>Match no field (a query that can never hit).</summary>
    None = 0,

    /// <summary>Match the record's <see cref="ListingRecord.Subject"/>.</summary>
    Subject = 1,

    /// <summary>Match the record's <see cref="ListingRecord.From"/> display value.</summary>
    From = 2,

    /// <summary>Match the record's <see cref="ListingRecord.Preview"/> (~200-char body preview).</summary>
    Preview = 4,

    /// <summary>Match any of Subject, From, or Preview (the default).</summary>
    All = Subject | From | Preview,
}

/// <summary>
/// One matching row from a listing scan (docs/Search.md Phase 2), carrying the folder it was
/// found in alongside the matched <see cref="ListingRecord"/>. Folder-scoped searches report a
/// single folder across every hit; a whole-mailbox scan reports the actual folder of each hit.
/// </summary>
public sealed class ListingScanHit
{
    /// <summary>The 16-byte ULID of the folder this row was scanned from.</summary>
    public required byte[] FolderId { get; init; }

    /// <summary>The matched Tier 1 listing row (the same record a folder listing would return).</summary>
    public required ListingRecord Record { get; init; }
}

/// <summary>
/// The outcome of a listing-page scan search (docs/Search.md Phase 2): the matching rows in
/// canonical date-descending order, how many rows were examined, and whether the scan spanned the
/// whole mailbox (the fallback) or a single folder. Because the scanned listing is the effective
/// listing — compiled pages with the pending <see cref="FolderDeltaLog"/> chain merged over them —
/// matches naturally include pending, not-yet-compiled delta rows.
/// </summary>
public sealed class ListingScanResult
{
    /// <summary>The matching rows, newest-first (ties broken by <see cref="EmailHashedID"/>).</summary>
    public required IReadOnlyList<ListingScanHit> Hits { get; init; }

    /// <summary>Total listing rows examined across every scanned folder (matches + misses).</summary>
    public required int RecordsScanned { get; init; }

    /// <summary>True when the scan spanned every supplied folder (whole-mailbox fallback); false for a single folder.</summary>
    public required bool WholeMailbox { get; init; }

    /// <summary>
    /// How many folders a Bloom filter (docs/Search.md Phase 5) let the whole-mailbox scan skip without a
    /// page scan — a covering filter proved they could not match. Always 0 for a folder-scoped
    /// <see cref="EmailManager.SearchFolder"/> and for a mailbox scan with no usable filters.
    /// </summary>
    public int FoldersSkipped { get; init; }

    /// <summary>Number of matching rows (<c>Hits.Count</c>).</summary>
    public int MatchCount => Hits.Count;
}

/// <summary>
/// The pure matcher behind the listing-page scan (docs/Search.md Phase 2): a case-insensitive
/// substring test of a <see cref="ListingRecord"/>'s selected Tier 1 fields against a keyword. Kept
/// I/O-free and static so the match semantics are unit-testable without a file, exactly as
/// <see cref="FolderListingMerger"/> keeps the read-path merge pure.
/// </summary>
public static class ListingScanMatcher
{
    /// <summary>
    /// True when <paramref name="query"/> occurs (case-insensitively, ordinal) as a substring of any
    /// of the <paramref name="fields"/> the record carries. An empty <paramref name="fields"/> set
    /// never matches. <paramref name="query"/> must be non-empty (the caller validates it).
    /// </summary>
    public static bool Matches(ListingRecord record, string query, ListingSearchField fields)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(query);

        if ((fields & ListingSearchField.Subject) != 0 && Contains(record.Subject, query))
            return true;
        if ((fields & ListingSearchField.From) != 0 && Contains(record.From, query))
            return true;
        if ((fields & ListingSearchField.Preview) != 0 && Contains(record.Preview, query))
            return true;
        return false;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
