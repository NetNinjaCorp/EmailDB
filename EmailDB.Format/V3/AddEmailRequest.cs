namespace EmailDB.Format.V3;

/// <summary>
/// The input to <see cref="EmailManager.AddEmail"/> (story US-EMDB-85): one email to persist
/// end-to-end. It carries the raw MIME the identity is hashed from, the Tier 2 metadata payload,
/// the Tier 1 listing fields, and the target folder directory the new listing row is appended to.
///
/// <para>The <see cref="RawContent"/> is the exact RFC 5322 octet sequence stored verbatim in the
/// Tier 3 <c>EmailContent</c> block and hashed (SHA3-256) into the <see cref="EmailHashedID"/> that
/// keys the PrimaryEmail index and drives dedupe (spec Sections 5-6). <see cref="MetadataPayload"/>
/// is the opaque Tier 2 <c>EmailMetadata</c> block body (its schema is a later story); the listing
/// fields (<see cref="From"/>, <see cref="Subject"/>, <see cref="Preview"/>, <see cref="DateTicks"/>,
/// <see cref="Flags"/>) become the folder <see cref="ListingRecord"/> (docs/Folder_Listing.md
/// Section 2).</para>
/// </summary>
public sealed class AddEmailRequest
{
    /// <summary>The raw RFC 5322 MIME bytes: stored verbatim in Tier 3 and hashed into the EmailHashedID.</summary>
    public required ReadOnlyMemory<byte> RawContent { get; init; }

    /// <summary>The target folder's current <see cref="FolderPageDirectory"/> — the delta append chains onto its head.</summary>
    public required FolderPageDirectory Folder { get; init; }

    /// <summary>The opaque Tier 2 <c>EmailMetadata</c> block payload (schema defined by a later story; may be empty).</summary>
    public ReadOnlyMemory<byte> MetadataPayload { get; init; }

    /// <summary>Message send date as UTC ticks (0 when the source had no parseable date); must be non-negative.</summary>
    public long DateTicks { get; init; }

    /// <summary>Listing state flags stamped into the Tier 1 row (read/flagged/answered/draft).</summary>
    public ListingFlags Flags { get; init; }

    /// <summary>Decoded From display value for the listing row (and the From address indexed by the FTS trigram index).</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// The email's To addresses, indexed by the Phase 1 FTS trigram index (docs/Search.md, US-EMDB-91).
    /// Not stored in the Tier 1 listing row; supplied only to feed address search. Empty when unknown.
    /// </summary>
    public IReadOnlyList<string> To { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The email's Cc addresses, indexed by the Phase 1 FTS trigram index (docs/Search.md, US-EMDB-91).
    /// Not stored in the Tier 1 listing row; supplied only to feed address search. Empty when unknown.
    /// </summary>
    public IReadOnlyList<string> Cc { get; init; } = Array.Empty<string>();

    /// <summary>Decoded Subject for the listing row.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>~200-char plain-text body preview for the listing row.</summary>
    public string Preview { get; init; } = string.Empty;
}

/// <summary>
/// The outcome of <see cref="EmailManager.AddEmail"/>. On a fresh insert it names the blocks and
/// index/log entries the pipeline produced; on a duplicate it reports <see cref="IsDuplicate"/> with
/// the already-present <see cref="EmailId"/> and no new writes (spec Sections 5-6: content-addressed
/// dedupe means the same MIME is never stored twice).
/// </summary>
public sealed class AddEmailResult
{
    /// <summary>True when the email's content was already present (detected via <see cref="EmailHashedID"/>) and nothing was written.</summary>
    public required bool IsDuplicate { get; init; }

    /// <summary>The email's canonical content identity (set whether or not it was a duplicate).</summary>
    public required EmailHashedID EmailId { get; init; }

    /// <summary>The 16-byte ULID of the Tier 3 <c>EmailContent</c> block written; null on a duplicate.</summary>
    public byte[]? ContentBlockId { get; init; }

    /// <summary>The 16-byte ULID of the Tier 2 <c>EmailMetadata</c> block written; null on a duplicate.</summary>
    public byte[]? MetadataBlockId { get; init; }

    /// <summary>The WAL block sequence the insert was logged under; null on a duplicate.</summary>
    public ulong? WalSequence { get; init; }

    /// <summary>The 16-byte ULID of the appended <c>FolderDeltaLog</c> block (the folder's new delta head); null on a duplicate.</summary>
    public byte[]? DeltaBlockId { get; init; }

    /// <summary>
    /// The folder directory advanced by the delta append (its <see cref="FolderPageDirectory.HeadDeltaBlockId"/>
    /// now points at the new delta block and <see cref="FolderPageDirectory.FolderVersion"/> is incremented),
    /// already persisted; on a duplicate this is the unchanged input directory.
    /// </summary>
    public FolderPageDirectory? Folder { get; init; }
}
