namespace EmailDB.Format.V3;

/// <summary>
/// The phases a <see cref="EmailManager.Search"/> plan can execute (docs/Search.md "Query planning"
/// matrix). Reported back on <see cref="SearchResult.PhasesExecuted"/> as a flag set so a caller — and a
/// unit test — can assert which route actually served a query rather than inferring it from the hits.
/// </summary>
[Flags]
public enum SearchPhase
{
    /// <summary>No phase ran (an empty scope, or a query that produced no candidates).</summary>
    None = 0,

    /// <summary>Phase 1 — the address trigram FTS index (<see cref="EmailManager.SearchAddresses"/>).</summary>
    Fts = 1 << 0,

    /// <summary>Phase 2 — the Tier 1 listing-page keyword scan (<see cref="EmailManager.SearchFolder"/>/<see cref="EmailManager.SearchMailbox"/>).</summary>
    Scan = 1 << 1,

    /// <summary>Phase 3 — the Date B+-tree pre-filter (<see cref="EmailManager.DateIndex"/> range query).</summary>
    DateIndex = 1 << 2,
}

/// <summary>
/// The query spec handed to the one <see cref="EmailManager.Search"/> entry point (story US-EMDB-94): the
/// planner classifies its shape per the docs/Search.md planning matrix and routes across the phases —
/// address-shaped to the trigram FTS index, date-bounded through the Date index pre-filter, and the
/// keyword/free-text baseline to the listing scan — merging the results into one ranked list.
///
/// <para>Shape classification (the matrix, docs/Search.md "Query planning"):</para>
/// <list type="bullet">
///   <item><b>Address-shaped</b> — <see cref="AddressFields"/> is set, or <see cref="Text"/> contains an
///   <c>@</c>: routed to the FTS index (Phase 1).</item>
///   <item><b>Keyword / free text</b> — any other non-empty <see cref="Text"/>: routed to the listing scan
///   (Phase 2), which the Bloom filters (Phase 5) gate for the whole-mailbox case.</item>
///   <item><b>Date-bounded</b> — <see cref="FromTicks"/>/<see cref="ToTicks"/> present: the Date index
///   (Phase 3) pre-filters the candidate set the text phase refines; a date-only query (no text) lists the
///   scoped folders filtered to the range.</item>
/// </list>
/// </summary>
public sealed class SearchQuery
{
    /// <summary>
    /// The keyword or address substring to search. May be null/empty for a date-only query (which lists the
    /// scoped folders filtered to <see cref="FromTicks"/>..<see cref="ToTicks"/>). Text containing an
    /// <c>@</c> is treated as address-shaped and routed to the FTS index.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// An explicit address-field hint. When set to a real field (From/To/Cc/All), the query is address-shaped
    /// regardless of whether <see cref="Text"/> holds an <c>@</c>, and is routed to the FTS index over exactly
    /// these fields. Null (the default) leaves the shape to be inferred from <see cref="Text"/>.
    /// </summary>
    public AddressField? AddressFields { get; init; }

    /// <summary>Inclusive lower date bound in UTC ticks, or null for no lower bound (treated as 0).</summary>
    public long? FromTicks { get; init; }

    /// <summary>Inclusive upper date bound in UTC ticks, or null for no upper bound (treated as <see cref="long.MaxValue"/>).</summary>
    public long? ToTicks { get; init; }

    /// <summary>
    /// The folder scope: the 16-byte folder ULIDs whose Tier 1 listings the search reads (a single folder is
    /// a folder-scoped search; several are a whole-mailbox search). Required and non-empty for any search that
    /// must resolve Tier 1 rows.
    /// </summary>
    public required IReadOnlyList<byte[]> Folders { get; init; }

    /// <summary>Which Tier 1 fields a keyword scan matches (defaults to Subject, From, and Preview).</summary>
    public ListingSearchField ScanFields { get; init; } = ListingSearchField.All;

    /// <summary>Cap on the number of ranked hits returned, newest-first (defaults to unbounded).</summary>
    public int MaxResults { get; init; } = int.MaxValue;
}
