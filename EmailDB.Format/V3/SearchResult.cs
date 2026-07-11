namespace EmailDB.Format.V3;

/// <summary>
/// One ranked hit from the query planner (<see cref="EmailManager.Search"/>): the matched email's identity,
/// the folder its Tier 1 row was read from, the row itself, and which phase produced the match. Because the
/// planner dedupes by <see cref="EmailId"/>, a message listed in several scoped folders yields a single hit;
/// <see cref="FolderId"/> reports the deterministically-chosen folder (the lexicographically smallest folder
/// ULID among the folders it was found in at the winning date/id).
/// </summary>
public sealed class SearchHit
{
    /// <summary>The matched email's content identity.</summary>
    public required EmailHashedID EmailId { get; init; }

    /// <summary>The 16-byte ULID of the folder this hit's Tier 1 row was read from.</summary>
    public required byte[] FolderId { get; init; }

    /// <summary>The matched Tier 1 listing row (the same record a folder listing would return).</summary>
    public required ListingRecord Record { get; init; }

    /// <summary>Which single phase produced this hit (<see cref="SearchPhase.Fts"/>, <see cref="SearchPhase.Scan"/>, or <see cref="SearchPhase.DateIndex"/>).</summary>
    public required SearchPhase MatchedPhase { get; init; }
}

/// <summary>
/// The outcome of the query planner (<see cref="EmailManager.Search"/>, story US-EMDB-94): the ranked,
/// deduped hits plus the plan diagnostics that make the routing decision observable — which phases ran,
/// whether the trigram index or a short-query fallback served an address query, and whether the Date index
/// pre-filtered or degraded to an in-memory date filter. Hits are in the house canonical order: date
/// descending, ties broken by <see cref="EmailHashedID"/> ascending.
/// </summary>
public sealed class SearchResult
{
    /// <summary>The ranked, deduped matches, newest-first (ties broken by <see cref="EmailHashedID"/> ascending).</summary>
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    /// <summary>The phases the plan actually executed for this query — the observable routing decision.</summary>
    public required SearchPhase PhasesExecuted { get; init; }

    /// <summary>
    /// True when an address query was split into trigrams and served from the FTS posting-list index; false
    /// when it fell back to a Tier 1 From scan (short query) or the query never touched the FTS phase.
    /// </summary>
    public bool UsedTrigramIndex { get; init; }

    /// <summary>
    /// True when a date-bounded query pre-filtered its candidates through the Date B+-tree index; false when
    /// no date bound was given, or the Date index was absent and the planner degraded to an in-memory
    /// <see cref="ListingRecord.DateTicks"/> filter.
    /// </summary>
    public bool UsedDateIndex { get; init; }

    /// <summary>
    /// True when a date bound was requested but the Date index was absent, so the range was enforced by an
    /// in-memory filter over the candidate rows rather than an index pre-filter (graceful degradation).
    /// </summary>
    public bool DateFilterDegraded { get; init; }

    /// <summary>Trigram candidates examined by the FTS phase before Tier 1 verification (0 when it did not run).</summary>
    public int CandidatesExamined { get; init; }

    /// <summary>Tier 1 listing rows the scan phase examined (0 when it did not run).</summary>
    public int RecordsScanned { get; init; }

    /// <summary>Folders a Bloom filter let a whole-mailbox scan skip without a page scan (0 unless the scan phase ran).</summary>
    public int FoldersSkipped { get; init; }

    /// <summary>Number of matching emails (<c>Hits.Count</c>).</summary>
    public int MatchCount => Hits.Count;
}
