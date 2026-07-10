namespace EmailDB.Format.V3;

/// <summary>
/// The input to <see cref="EmailManager.MoveEmail"/> (story US-EMDB-87): move one email's folder
/// membership from <see cref="SourceFolder"/> to <see cref="TargetFolder"/> without touching any
/// Tier 2/3 content block. A move is purely a folder-listing change — a <c>Delete</c> delta in the
/// source folder's log and an <c>Add</c> delta (of the same <see cref="Record"/>, referencing the
/// same ContentBlockId) in the destination's — so the email appears in both folders' listings during
/// replication and in the destination's afterward (docs/Folder_Listing.md Section 3, "Move/delete").
///
/// <para>The caller supplies the <see cref="ListingRecord"/> it is moving (it already has it from the
/// source folder's listing): its EmailHashedID identifies the row to drop from the source, and the
/// record itself is re-added to the target so the destination page can be compiled without re-reading
/// Tier 2.</para>
/// </summary>
public sealed class MoveEmailRequest
{
    /// <summary>The folder the email is leaving; a <c>Delete</c> delta drops the row here.</summary>
    public required FolderPageDirectory SourceFolder { get; init; }

    /// <summary>The folder the email is entering; an <c>Add</c> delta of <see cref="Record"/> is appended here.</summary>
    public required FolderPageDirectory TargetFolder { get; init; }

    /// <summary>The listing row being moved (same ContentBlockId in both folders); its EmailHashedID keys the source delete.</summary>
    public required ListingRecord Record { get; init; }
}

/// <summary>
/// The outcome of <see cref="EmailManager.MoveEmail"/>: the two COW-advanced, already-persisted folder
/// directories. Each has its <see cref="FolderPageDirectory.HeadDeltaBlockId"/> pointing at the newly
/// appended delta block and its <see cref="FolderPageDirectory.FolderVersion"/> incremented; no page or
/// content block was rewritten.
/// </summary>
public sealed class MoveEmailResult
{
    /// <summary>The source folder directory after the <c>Delete</c> delta append (persisted).</summary>
    public required FolderPageDirectory SourceFolder { get; init; }

    /// <summary>The target folder directory after the <c>Add</c> delta append (persisted).</summary>
    public required FolderPageDirectory TargetFolder { get; init; }

    /// <summary>ULID of the appended <c>FolderDeltaLog</c> block in the source folder (its new delta head).</summary>
    public required byte[] SourceDeltaBlockId { get; init; }

    /// <summary>ULID of the appended <c>FolderDeltaLog</c> block in the target folder (its new delta head).</summary>
    public required byte[] TargetDeltaBlockId { get; init; }
}

/// <summary>
/// The input to <see cref="EmailManager.DeleteEmail"/> (story US-EMDB-87): remove one email
/// completely — its folder listing row, its index entries, and its on-disk bytes accounted dead.
///
/// <para>The <see cref="EmailId"/> keys both the source <c>Delete</c> delta and the PrimaryEmail index
/// delete; <see cref="DateTicks"/> is the email's timestamp, needed to form the composite
/// <c>DateTicks ‖ ContentBlockId</c> key removed from the Date index (the index has no reverse lookup
/// by BlockId). Both come straight from the <see cref="ListingRecord"/> the caller is deleting.</para>
/// </summary>
public sealed class DeleteEmailRequest
{
    /// <summary>The folder the email is being removed from; a <c>Delete</c> delta drops the row here.</summary>
    public required FolderPageDirectory Folder { get; init; }

    /// <summary>The content identity to delete: keys the folder delete, the PrimaryEmail delete, and the dead-byte accounting.</summary>
    public required EmailHashedID EmailId { get; init; }

    /// <summary>The email's timestamp in ticks (from its listing row); with the ContentBlockId it forms the Date index key removed.</summary>
    public required long DateTicks { get; init; }
}

/// <summary>
/// The outcome of <see cref="EmailManager.DeleteEmail"/>: the COW-advanced, already-persisted folder
/// directory plus the retired blocks accounted dead. On a delete of an email the index did not know
/// (already deleted, or never added) <see cref="WasPresent"/> is false and nothing but a possible
/// folder delta is written.
/// </summary>
public sealed class DeleteEmailResult
{
    /// <summary>True when the email was present in the PrimaryEmail index and its full removal ran; false for an unknown identity.</summary>
    public required bool WasPresent { get; init; }

    /// <summary>The folder directory after the <c>Delete</c> delta append (persisted); the unchanged input directory when the identity was unknown.</summary>
    public required FolderPageDirectory Folder { get; init; }

    /// <summary>ULID of the appended <c>FolderDeltaLog</c> block (the folder's new delta head); null when the identity was unknown.</summary>
    public byte[]? DeltaBlockId { get; init; }

    /// <summary>True when the Date index entry (DateTicks ‖ ContentBlockId) existed and was removed.</summary>
    public bool DateEntryRemoved { get; init; }

    /// <summary>On-disk bytes moved from live to dead by this delete (the Tier 3 content + Tier 2 metadata blocks); 0 for an unknown identity.</summary>
    public long DeadBytes { get; init; }
}

/// <summary>
/// The outcome of <see cref="EmailManager.ChangeFlags"/>: the COW-advanced, already-persisted folder
/// directory whose delta head now carries a <c>FlagChange</c> row for the email. No page or content
/// block is rewritten — the new flags become visible when the next listing read merges the delta chain
/// (docs/Folder_Listing.md Section 3).
/// </summary>
public sealed class ChangeFlagsResult
{
    /// <summary>The folder directory after the <c>FlagChange</c> delta append (persisted).</summary>
    public required FolderPageDirectory Folder { get; init; }

    /// <summary>ULID of the appended <c>FolderDeltaLog</c> block (the folder's new delta head).</summary>
    public required byte[] DeltaBlockId { get; init; }
}
