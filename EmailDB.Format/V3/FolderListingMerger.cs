namespace EmailDB.Format.V3;

/// <summary>
/// In-memory merge of a folder's pending <see cref="FolderDeltaLog"/> chain over its
/// <see cref="FolderPage"/> records (docs/Folder_Listing.md Sections 2-3), producing the effective
/// listing a browser sees: pending Adds inserted date-descending, Deletes masking page rows, and
/// FlagChanges overriding flags. This is the read-path merge (US-EMDB-82-7): it performs no I/O and
/// rewrites no page — the caller reads and decrypts the page(s) and walks the delta chain, then hands
/// the records and entries here. Compiling the merge back into pages is a separate concern
/// (US-EMDB-82-8).
///
/// <para><b>Precedence.</b> Deltas are applied in chronological (append) order — chain root block
/// first, head block last, each block's entries in append order — so a later entry wins over an
/// earlier one for the same <see cref="EmailHashedID"/>: a re-Add replaces a prior row, a Delete masks
/// it, and a following Add un-masks it. A <see cref="FolderDeltaOp.FlagChange"/> only overrides an id
/// that is currently present; a FlagChange for a masked (deleted) or unknown id is a no-op, since there
/// is no row to flag.</para>
///
/// <para>The merged rows are returned in the same canonical order pages use — date-descending, ties
/// broken by EmailHashedID (see <see cref="FolderPage.FromRecords"/>) — so the read-path view orders
/// rows identically to the pages a later compile will write.</para>
/// </summary>
public static class FolderListingMerger
{
    /// <summary>
    /// Merges <paramref name="deltaChainHeadToRoot"/> — the blocks as walked from the directory's
    /// <see cref="FolderPageDirectory.HeadDeltaBlockId"/> back to the chain root, i.e. newest block
    /// first — over <paramref name="pageRecords"/>, returning the effective listing newest-first.
    /// </summary>
    public static IReadOnlyList<ListingRecord> Merge(
        IEnumerable<ListingRecord> pageRecords,
        IEnumerable<FolderDeltaLog> deltaChainHeadToRoot)
    {
        ArgumentNullException.ThrowIfNull(pageRecords);
        ArgumentNullException.ThrowIfNull(deltaChainHeadToRoot);
        return Merge(pageRecords, FlattenChronological(deltaChainHeadToRoot));
    }

    /// <summary>
    /// Merges pre-flattened delta <paramref name="entriesOldestFirst"/> (chronological apply order,
    /// oldest first) over <paramref name="pageRecords"/>, returning the effective listing newest-first.
    /// </summary>
    public static IReadOnlyList<ListingRecord> Merge(
        IEnumerable<ListingRecord> pageRecords,
        IEnumerable<FolderDeltaEntry> entriesOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(pageRecords);
        ArgumentNullException.ThrowIfNull(entriesOldestFirst);

        // Effective row per email id; assigning into the dictionary makes last-write-wins natural, so
        // applying entries oldest-first yields the newest-wins precedence the chain implies.
        var effective = new Dictionary<EmailHashedID, ListingRecord>();
        foreach (var record in pageRecords)
        {
            ArgumentNullException.ThrowIfNull(record);
            effective[record.EmailHashedId] = record;
        }

        foreach (var entry in entriesOldestFirst)
        {
            ArgumentNullException.ThrowIfNull(entry);
            switch (entry.Op)
            {
                case FolderDeltaOp.Add:
                    // Add carries the full row; a re-Add (or move-back) overrides an existing one.
                    effective[entry.EmailHashedId] = entry.Record!;
                    break;
                case FolderDeltaOp.Delete:
                    // Mask the row; a later Add for the same id can bring it back.
                    effective.Remove(entry.EmailHashedId);
                    break;
                case FolderDeltaOp.FlagChange:
                    // Override flags only when a row is present; ignore for masked/unknown ids.
                    if (effective.TryGetValue(entry.EmailHashedId, out var current))
                        effective[entry.EmailHashedId] = WithFlags(current, entry.Flags);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown delta op {entry.Op}.");
            }
        }

        // Reuse the canonical page ordering (date-descending, tie-break by EmailHashedID).
        return FolderPage.FromRecords(effective.Values).Records;
    }

    /// <summary>
    /// Flattens a head-to-root delta chain into chronological apply order (oldest entry first): the
    /// blocks reversed to root-first, each block's entries kept in their append order. Feed this to the
    /// entry overload of <see cref="Merge(IEnumerable{ListingRecord}, IEnumerable{FolderDeltaEntry})"/>.
    /// </summary>
    public static IReadOnlyList<FolderDeltaEntry> FlattenChronological(
        IEnumerable<FolderDeltaLog> deltaChainHeadToRoot)
    {
        ArgumentNullException.ThrowIfNull(deltaChainHeadToRoot);
        var blocks = deltaChainHeadToRoot as IReadOnlyList<FolderDeltaLog> ?? deltaChainHeadToRoot.ToArray();

        var entries = new List<FolderDeltaEntry>();
        for (int i = blocks.Count - 1; i >= 0; i--) // root (oldest) first, head (newest) last
        {
            var block = blocks[i];
            ArgumentNullException.ThrowIfNull(block);
            entries.AddRange(block.Entries);
        }
        return entries;
    }

    /// <summary>Returns a copy of <paramref name="record"/> with <see cref="ListingRecord.Flags"/> replaced.</summary>
    private static ListingRecord WithFlags(ListingRecord record, ListingFlags flags) =>
        new()
        {
            EmailHashedId = record.EmailHashedId,
            ContentBlockId = record.ContentBlockId,
            DateTicks = record.DateTicks,
            Flags = flags,
            MessageSize = record.MessageSize,
            From = record.From,
            Subject = record.Subject,
            Preview = record.Preview,
        };
}
