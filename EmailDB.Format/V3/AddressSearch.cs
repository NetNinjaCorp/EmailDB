namespace EmailDB.Format.V3;

/// <summary>
/// One matching email from an address trigram search (docs/Search.md Phase 1, task US-EMDB-91-8):
/// the Tier 1 <see cref="ListingRecord"/> that was verified to contain the query substring, the folder
/// it was found in, and which address field(s) the query matched.
///
/// <para>Address search is mailbox-wide over an email's identity — its addresses are the same wherever
/// it is listed — so an email yields a single hit even when it is listed in several of the searched
/// folders; <see cref="FolderId"/> reports the first folder the email was found in.</para>
/// </summary>
public sealed class AddressSearchHit
{
    /// <summary>The matched email's content identity.</summary>
    public required EmailHashedID EmailId { get; init; }

    /// <summary>The 16-byte ULID of the folder the email's Tier 1 listing row was read from.</summary>
    public required byte[] FolderId { get; init; }

    /// <summary>The matched Tier 1 listing row (the same record a folder listing would return).</summary>
    public required ListingRecord Record { get; init; }

    /// <summary>
    /// Which address field(s) the query matched. From matches are verified against the Tier 1 listing
    /// row's <see cref="ListingRecord.From"/> address; To/Cc matches are trigram-level (their address
    /// text is not persisted at Tier 1, so it cannot be re-verified after ingest).
    /// </summary>
    public required AddressField MatchedFields { get; init; }
}

/// <summary>
/// The outcome of <see cref="EmailManager.SearchAddresses"/> (docs/Search.md Phase 1): the matching
/// emails in canonical date-descending order (ties broken by <see cref="EmailHashedID"/>), whether the
/// trigram index served the query or a short-query Tier 1 scan fell back, and how many trigram
/// candidates were examined before Tier 1 verification.
/// </summary>
public sealed class AddressSearchResult
{
    /// <summary>The matching emails, newest-first (ties broken by <see cref="EmailHashedID"/> ascending).</summary>
    public required IReadOnlyList<AddressSearchHit> Hits { get; init; }

    /// <summary>
    /// True when the query was split into trigrams and served from the posting-list index; false when
    /// the query was shorter than a trigram and fell back to a Tier 1 From-address substring scan.
    /// </summary>
    public required bool UsedTrigramIndex { get; init; }

    /// <summary>
    /// Trigram candidates examined before Tier 1 verification (the trigram-index path), or the number of
    /// Tier 1 rows scanned (the short-query fallback path). Diagnostic — a measure of the work the
    /// verification/scan stage did.
    /// </summary>
    public required int CandidatesExamined { get; init; }

    /// <summary>Number of matching emails (<c>Hits.Count</c>).</summary>
    public int MatchCount => Hits.Count;
}
