using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// Rebuilds a folder's Tier 1 structures (its <see cref="FolderPage"/>s and
/// <see cref="FolderPageDirectory"/>) from Tier 2 <see cref="EmailMetadata"/> alone — the recovery
/// path of docs/Folder_Listing.md Section 4. When a folder's pages are lost or corrupt this is a
/// recovery event, not data loss: every packed listing field (From, Subject, Date, size, Preview,
/// content-block link, EmailHashedID) is derivable from Tier 2, so the listing is regenerated
/// <b>without ever reading Tier 3</b> (<see cref="EmailContent"/>).
///
/// <para><b>Tier 2 only.</b> The regenerator reads exclusively through
/// <see cref="EmailBlockStore.ReadMetadata"/> (BlockType 10). It has no path to Tier 3 — it never
/// calls <see cref="EmailBlockStore.ReadContent"/> — and <c>ReadMetadata</c> rejects any offset that
/// is not a metadata block, so pointing it at a Tier 3 block fails rather than reading it. This is
/// the "regeneration reads only Tier 2 blocks, never Tier 3" contract.</para>
///
/// <para><b>Flags reset to defaults.</b> Listing flags (read/flagged/answered/draft) are folder
/// state, not content, and are not carried in Tier 2, so every rebuilt record is created with
/// <see cref="ListingFlags.None"/> — the recovery reset value. A rebuilt page therefore matches the
/// original in every field except that its flags are cleared.</para>
///
/// <para><b>Bumped FolderVersion.</b> When the prior directory survives (only the pages were lost),
/// pass it as <paramref name="priorDirectory"/>: the fresh directory is a COW
/// <see cref="FolderPageDirectory.Rewrite"/> of it, so its <see cref="FolderPageDirectory.FolderVersion"/>
/// is bumped by one and the pending delta head is cleared — replication (Section 5) sees the folder
/// as changed and re-pulls it. When the directory itself was lost, use the folder-id overload to
/// start a fresh directory.</para>
///
/// <para><b>Membership is an input.</b> How the folder's members are enumerated durably
/// (FolderTree, delta history, or a full EmailMetadata sweep — Section 4 step 1) is a separate
/// concern; this routine accepts the resolved set of member <see cref="EmailMetadata"/> block offsets
/// and rebuilds from them. The stores are not owned; the caller scopes and flushes them.</para>
/// </summary>
public sealed class FolderPageRegenerator
{
    private readonly EmailBlockStore _metadataStore;
    private readonly FolderPageStore _pageStore;
    private readonly FolderPageDirectoryStore _directoryStore;

    /// <summary>
    /// Wraps the Tier 2 email store (read side) and the Tier 1 page/directory stores (write side).
    /// None are owned.
    /// </summary>
    public FolderPageRegenerator(
        EmailBlockStore metadataStore,
        FolderPageStore pageStore,
        FolderPageDirectoryStore directoryStore)
    {
        _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        _pageStore = pageStore ?? throw new ArgumentNullException(nameof(pageStore));
        _directoryStore = directoryStore ?? throw new ArgumentNullException(nameof(directoryStore));
    }

    /// <summary>
    /// Regenerates a folder whose <b>directory survived</b> but whose pages were lost or corrupt:
    /// reads each member's Tier 2 metadata, rebuilds its listing record (flags reset to defaults),
    /// repacks date-descending pages, and writes a fresh directory as a COW rewrite of
    /// <paramref name="priorDirectory"/> — same <see cref="FolderPageDirectory.FolderId"/>,
    /// <see cref="FolderPageDirectory.FolderVersion"/> bumped by one, delta head cleared.
    /// </summary>
    public Result<FolderRegenerateResult> Regenerate(
        FolderPageDirectory priorDirectory, IEnumerable<long> metadataOffsets)
    {
        ArgumentNullException.ThrowIfNull(priorDirectory);
        ArgumentNullException.ThrowIfNull(metadataOffsets);

        return RegenerateCore(
            metadataOffsets,
            entries =>
            {
                try
                {
                    return Result<FolderPageDirectory>.Success(
                        priorDirectory.Rewrite(entries, headDeltaBlockId: null));
                }
                catch (OverflowException)
                {
                    return Result<FolderPageDirectory>.Failure(
                        "Regeneration cannot bump FolderVersion: the prior directory is at ulong.MaxValue.");
                }
            });
    }

    /// <summary>
    /// Regenerates a folder whose <b>directory was also lost</b>: as above, but seeds a fresh
    /// directory for <paramref name="folderId"/> starting at <paramref name="startingFolderVersion"/>
    /// (default 0) with no pending delta head. Use this fresh-start form when no prior directory
    /// version survives to rewrite.
    /// </summary>
    public Result<FolderRegenerateResult> Regenerate(
        byte[] folderId, IEnumerable<long> metadataOffsets, ulong startingFolderVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(metadataOffsets);

        return RegenerateCore(
            metadataOffsets,
            entries =>
            {
                try
                {
                    return Result<FolderPageDirectory>.Success(
                        FolderPageDirectory.Create(
                            folderId, entries, headDeltaBlockId: null, folderVersion: startingFolderVersion));
                }
                catch (ArgumentException ex)
                {
                    return Result<FolderPageDirectory>.Failure(ex.Message);
                }
            });
    }

    private Result<FolderRegenerateResult> RegenerateCore(
        IEnumerable<long> metadataOffsets,
        Func<IReadOnlyList<PageEntry>, Result<FolderPageDirectory>> buildDirectory)
    {
        // ---- 1. Read every member's Tier 2 metadata (BlockType 10) and rebuild its listing record.
        // ReadMetadata is the only read path here, so Tier 3 is never touched; a non-metadata offset
        // fails the type check rather than being read as content.
        var records = new List<ListingRecord>();
        foreach (var offset in metadataOffsets)
        {
            var read = _metadataStore.ReadMetadata(offset);
            if (read.IsFailure)
                return Result<FolderRegenerateResult>.Failure(
                    $"Regeneration could not read EmailMetadata at offset {offset}: {read.Error}");

            // Flags default to ListingFlags.None — folder state is not in Tier 2 (Section 4).
            records.Add(ListingRecordBuilder.FromMetadata(read.Value));
        }

        // ---- 2. Sort globally date-descending, then repack into ~TargetRecordsPerPage pages. ------
        var sorted = FolderPage.FromRecords(records).Records; // canonical date-desc (tie: EmailHashedID)

        var newDirEntries = new List<PageEntry>();
        var newPages = new List<BlockLocation>();
        for (int start = 0; start < sorted.Count; start += FolderPage.TargetRecordsPerPage)
        {
            int length = Math.Min(FolderPage.TargetRecordsPerPage, sorted.Count - start);
            var chunk = new List<ListingRecord>(length);
            for (int j = 0; j < length; j++)
                chunk.Add(sorted[start + j]);

            // A contiguous slice of the globally sorted set is itself date-descending.
            var written = _pageStore.WritePage(FolderPage.FromSortedRecords(chunk));
            if (written.IsFailure)
                return Result<FolderRegenerateResult>.Failure(written.Error);

            newPages.Add(written.Value);
            newDirEntries.Add(new PageEntry(
                written.Value.BlockId,
                dateFrom: chunk[^1].DateTicks, // oldest on the slice
                dateTo: chunk[0].DateTicks,    // newest on the slice
                entryCount: chunk.Count));
        }

        // ---- 3. Write the fresh directory (bumped FolderVersion / fresh start, delta head cleared).
        var directory = buildDirectory(newDirEntries);
        if (directory.IsFailure)
            return Result<FolderRegenerateResult>.Failure(directory.Error);

        var writtenDir = _directoryStore.WriteDirectory(directory.Value);
        if (writtenDir.IsFailure)
            return Result<FolderRegenerateResult>.Failure(writtenDir.Error);

        return Result<FolderRegenerateResult>.Success(
            new FolderRegenerateResult(
                directory.Value, writtenDir.Value, newPages, records.Count));
    }
}

/// <summary>
/// Outcome of <see cref="FolderPageRegenerator.Regenerate(FolderPageDirectory, IEnumerable{long})"/>:
/// the freshly written <see cref="FolderPageDirectory"/> (FolderVersion bumped, delta head cleared),
/// where it was appended, the newly written <see cref="FolderPage"/> locations it points at, and how
/// many listing records were rebuilt from Tier 2.
/// </summary>
public sealed record FolderRegenerateResult(
    FolderPageDirectory Directory,
    BlockLocation DirectoryLocation,
    IReadOnlyList<BlockLocation> NewPages,
    int RecordCount);
