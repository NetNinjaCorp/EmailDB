namespace EmailDB.Format.V3;

/// <summary>
/// One slice of a folder's effective listing returned by <see cref="EmailManager.ListFolder"/> and
/// <see cref="EmailManager.ListFolderFromDate"/> (story US-EMDB-87, docs/Folder_Listing.md Section 3).
/// The <see cref="Records"/> are date-descending (newest first, ties broken by EmailHashedID — the
/// canonical <see cref="FolderPage"/> order) with the folder's pending <c>FolderDeltaLog</c> chain
/// already merged over its compiled pages: pending Adds inserted in date order, Deletes masking rows,
/// and FlagChanges overriding flags. The slice is a contiguous window <c>[Offset, Offset + Records.Count)</c>
/// of the whole effective listing, so any two reads of an unchanged folder at the same offset return the
/// same rows in the same order.
/// </summary>
public sealed class FolderListingPage
{
    /// <summary>The listing rows in this slice, date-descending (newest first).</summary>
    public required IReadOnlyList<ListingRecord> Records { get; init; }

    /// <summary>Zero-based record index into the effective listing at which this slice begins (clamped to <see cref="TotalCount"/>).</summary>
    public required int Offset { get; init; }

    /// <summary>Total number of rows in the folder's effective listing after the pending deltas are merged.</summary>
    public required int TotalCount { get; init; }

    /// <summary>The <see cref="FolderPageDirectory.FolderVersion"/> the slice was read from (the replication/version stamp).</summary>
    public required ulong FolderVersion { get; init; }

    /// <summary>True when rows exist beyond this slice (a further offset would return more).</summary>
    public bool HasMore => Offset + Records.Count < TotalCount;

    /// <summary>
    /// Cuts the contiguous window <c>[offset, offset + size)</c> out of the already-merged, date-descending
    /// <paramref name="listing"/>. <paramref name="offset"/> past the end yields an empty slice reporting the
    /// clamped offset (stable "beyond the folder" read); a short tail returns however many rows remain.
    /// </summary>
    public static FolderListingPage Slice(
        IReadOnlyList<ListingRecord> listing, int offset, int size, ulong folderVersion)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        int total = listing.Count;
        int start = Math.Min(offset, total);
        int take = Math.Min(size, total - start);
        var slice = new ListingRecord[take];
        for (int i = 0; i < take; i++)
            slice[i] = listing[start + i];

        return new FolderListingPage
        {
            Records = slice,
            Offset = start,
            TotalCount = total,
            FolderVersion = folderVersion,
        };
    }
}
