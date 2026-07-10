namespace EmailDB.Format.V3;

/// <summary>
/// Compiles a folder's pending <see cref="FolderDeltaLog"/> chain back into its
/// <see cref="FolderPage"/>s (docs/Folder_Listing.md Section 3, "Compile deltas → pages"). Where
/// <see cref="FolderDeltaLogStore.AppendChained"/> keeps adds cheap by buffering them in an
/// append-only chain and <see cref="FolderListingMerger"/> overlays that chain on the read path,
/// this type is the write-back: at the ~500-entry threshold (or on close) it merges the chain into
/// the pages it touches, rewrites <b>only those pages</b> copy-on-write, and appends a new directory
/// version with its <see cref="FolderPageDirectory.HeadDeltaBlockId"/> cleared and
/// <see cref="FolderPageDirectory.FolderVersion"/> bumped.
///
/// <para><b>Affected pages only.</b> A page is affected when a delta touches a record in it: a
/// Delete or FlagChange of an id resident on the page, a re-Add of a resident id, or an Add whose
/// date routes into the page's range (<see cref="FolderPageDirectory.FindPageByDate"/>). Untouched
/// pages keep their existing block — their <see cref="PageEntry"/> is carried into the new directory
/// verbatim, so their <see cref="PageEntry.PageBlockId"/> is byte-identical across the compile. An
/// affected page is re-merged with just the deltas routed to it and repacked, splitting into
/// ~<see cref="FolderPage.TargetRecordsPerPage"/>-record sub-pages so pages stay bounded; a page
/// whose rows are all deleted vanishes.</para>
///
/// <para><b>Append-only / COW.</b> Nothing is rewritten in place. The old page blocks that were
/// replaced, the superseded directory version, and every consumed delta block are simply no longer
/// referenced by the new directory — they are the compile's <b>dead set</b>, reported in
/// <see cref="FolderCompileResult"/> for later compaction (mirroring the v3 COW engine's notion of a
/// block going dead once nothing references it). This type performs the page/directory writes; the
/// caller owns durability (a <see cref="BlockManager.Flush"/> and, eventually, a checkpoint that
/// folds the dead bytes into its accounting).</para>
/// </summary>
public sealed class FolderCompiler
{
    /// <summary>
    /// Pending-entry count at which a folder should be compiled (docs/Folder_Listing.md Section 3).
    /// The automatic trigger is wired by the folder lifecycle; callers/tests consult
    /// <see cref="ShouldCompile"/> to decide.
    /// </summary>
    public const int CompileThreshold = 500;

    private readonly FolderPageStore _pageStore;
    private readonly FolderPageDirectoryStore _directoryStore;
    private readonly FolderDeltaLogStore _deltaStore;
    private readonly IBlockIdResolver _resolver;

    /// <summary>
    /// Wraps the three folder stores and a BlockId resolver (spec Section 7 — runtime map, then the
    /// durable index) used to turn the directory's page/delta BlockId pointers into file offsets to
    /// read. None of the stores are owned.
    /// </summary>
    public FolderCompiler(
        FolderPageStore pageStore,
        FolderPageDirectoryStore directoryStore,
        FolderDeltaLogStore deltaStore,
        IBlockIdResolver resolver)
    {
        _pageStore = pageStore ?? throw new ArgumentNullException(nameof(pageStore));
        _directoryStore = directoryStore ?? throw new ArgumentNullException(nameof(directoryStore));
        _deltaStore = deltaStore ?? throw new ArgumentNullException(nameof(deltaStore));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Whether a folder with <paramref name="pendingCount"/> buffered delta entries has reached the
    /// <see cref="CompileThreshold"/> and should be compiled. Wiring the automatic trigger belongs to
    /// the folder lifecycle; this is the predicate it (and tests) call.
    /// </summary>
    public static bool ShouldCompile(int pendingCount) => pendingCount >= CompileThreshold;

    /// <summary>
    /// Merges <paramref name="directory"/>'s pending delta chain into its pages and writes the
    /// compiled result: the affected pages rewritten COW, a new directory version with the delta head
    /// cleared and the folder version bumped, and the dead set of superseded artifacts. Returns a
    /// no-op-shaped success (the same directory, empty dead set) when there is nothing pending.
    /// </summary>
    public Result<FolderCompileResult> Compile(FolderPageDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!directory.HasPendingDelta)
            return Result<FolderCompileResult>.Success(
                new FolderCompileResult(
                    directory, DirectoryLocation: null, PendingEntryCount: 0,
                    NewPages: Array.Empty<BlockLocation>(),
                    DeadPages: Array.Empty<BlockLocation>(),
                    DeadDeltas: Array.Empty<BlockLocation>(),
                    DeadDirectory: null));

        // Resolve the current (soon-to-be-superseded) directory version up front — it shares the
        // folder's stable BlockId with the version we are about to write, so we must capture its
        // location before the rewrite makes the resolver point at the new one.
        BlockLocation? deadDirectory = Resolve(directory.FolderId);

        // ---- 1. Read every page (records + its BlockId), newest-first. --------------------------
        var pageRecords = new List<IReadOnlyList<ListingRecord>>(directory.PageCount);
        foreach (var entry in directory.PageEntries)
        {
            var loc = Resolve(entry.PageBlockId);
            if (loc is null)
                return Result<FolderCompileResult>.Failure(
                    $"Compile could not resolve FolderPage {Hex(entry.PageBlockId)} to an offset.");
            var read = _pageStore.ReadPage(loc.Offset);
            if (read.IsFailure)
                return Result<FolderCompileResult>.Failure(read.Error);
            pageRecords.Add(read.Value.Records);
        }

        // ---- 2. Walk the pending chain head→root; each block is consumed (dead) by the compile. --
        var deadDeltas = new List<BlockLocation>();
        var chainHeadToRoot = new List<FolderDeltaLog>();
        var cursor = directory.HeadDeltaBlockId;
        while (true)
        {
            var loc = Resolve(cursor);
            if (loc is null)
                return Result<FolderCompileResult>.Failure(
                    $"Compile could not resolve FolderDeltaLog {Hex(cursor)} to an offset.");
            var read = _deltaStore.ReadDeltaBlock(loc.Offset);
            if (read.IsFailure)
                return Result<FolderCompileResult>.Failure(read.Error);

            deadDeltas.Add(loc);
            chainHeadToRoot.Add(read.Value);
            if (!read.Value.HasPrevious)
                break;
            cursor = read.Value.PreviousDeltaBlockId;
        }
        var entries = FolderListingMerger.FlattenChronological(chainHeadToRoot); // oldest first

        // ---- 3. Map every page-resident id to its page index. -----------------------------------
        var idToPage = new Dictionary<EmailHashedID, int>();
        for (int i = 0; i < pageRecords.Count; i++)
            foreach (var record in pageRecords[i])
                idToPage[record.EmailHashedId] = i;

        // ---- 4. Route each delta entry to the page it affects (chronological order preserved). --
        // A Delete/FlagChange of an id on no page and never Added in the chain is a no-op (the
        // merger ignores it), so it marks no page affected.
        var perPage = new Dictionary<int, List<FolderDeltaEntry>>();
        var newPageEntries = new List<FolderDeltaEntry>(); // Adds for a currently empty directory
        foreach (var entry in entries)
        {
            if (!idToPage.TryGetValue(entry.EmailHashedId, out int index))
            {
                if (entry.Op != FolderDeltaOp.Add)
                    continue; // Delete/FlagChange of an unknown id — nothing to compile.

                index = directory.FindPageByDate(entry.Record!.DateTicks);
                if (index < 0)
                {
                    newPageEntries.Add(entry); // no pages yet — this Add seeds a fresh page.
                    continue;
                }
                // Bind the id to that page so later chain ops for it route to the same page.
                idToPage[entry.EmailHashedId] = index;
            }

            if (!perPage.TryGetValue(index, out var list))
                perPage[index] = list = new List<FolderDeltaEntry>();
            list.Add(entry);
        }

        // ---- 5. Rebuild the page set: affected pages repacked COW, untouched pages carried over. -
        var newDirEntries = new List<PageEntry>(directory.PageCount);
        var newPages = new List<BlockLocation>();
        var deadPages = new List<BlockLocation>();

        for (int i = 0; i < directory.PageCount; i++)
        {
            if (!perPage.TryGetValue(i, out var pageDeltas))
            {
                newDirEntries.Add(directory.PageEntries[i]); // untouched — same block, verbatim entry.
                continue;
            }

            // This page is affected: its old block is replaced.
            deadPages.Add(Resolve(directory.PageEntries[i].PageBlockId)!);

            var merged = FolderListingMerger.Merge(pageRecords[i], pageDeltas);
            var write = WriteSplitPages(merged, newDirEntries, newPages);
            if (write.IsFailure)
                return Result<FolderCompileResult>.Failure(write.Error);
        }

        // Adds that seed pages for a previously-empty directory append after the (empty) page list.
        if (newPageEntries.Count > 0)
        {
            var merged = FolderListingMerger.Merge(Array.Empty<ListingRecord>(), newPageEntries);
            var write = WriteSplitPages(merged, newDirEntries, newPages);
            if (write.IsFailure)
                return Result<FolderCompileResult>.Failure(write.Error);
        }

        // ---- 6. Write the new directory: delta head cleared, FolderVersion bumped. ---------------
        FolderPageDirectory compiled;
        try
        {
            compiled = directory.Rewrite(newDirEntries, headDeltaBlockId: null);
        }
        catch (ArgumentException ex)
        {
            return Result<FolderCompileResult>.Failure(
                $"Compile produced a directory that is not newest-first: {ex.Message}");
        }

        var writtenDir = _directoryStore.WriteDirectory(compiled);
        if (writtenDir.IsFailure)
            return Result<FolderCompileResult>.Failure(writtenDir.Error);

        return Result<FolderCompileResult>.Success(
            new FolderCompileResult(
                compiled,
                DirectoryLocation: writtenDir.Value,
                PendingEntryCount: entries.Count,
                NewPages: newPages,
                DeadPages: deadPages,
                DeadDeltas: deadDeltas,
                DeadDirectory: deadDirectory));
    }

    /// <summary>
    /// Splits a merged, date-descending record set into ~<see cref="FolderPage.TargetRecordsPerPage"/>
    /// pages, writes each COW, and appends the resulting <see cref="PageEntry"/>/location. An empty
    /// set writes nothing (the page vanished — all its rows were deleted).
    /// </summary>
    private Result WriteSplitPages(
        IReadOnlyList<ListingRecord> merged, List<PageEntry> dirEntries, List<BlockLocation> newPages)
    {
        for (int start = 0; start < merged.Count; start += FolderPage.TargetRecordsPerPage)
        {
            int length = Math.Min(FolderPage.TargetRecordsPerPage, merged.Count - start);
            var chunk = new List<ListingRecord>(length);
            for (int j = 0; j < length; j++)
                chunk.Add(merged[start + j]);

            // merged is already date-descending; a contiguous slice stays sorted, so newest = first.
            var page = FolderPage.FromSortedRecords(chunk);
            var written = _pageStore.WritePage(page);
            if (written.IsFailure)
                return Result.Failure(written.Error);

            newPages.Add(written.Value);
            dirEntries.Add(new PageEntry(
                written.Value.BlockId,
                dateFrom: chunk[^1].DateTicks, // oldest on the slice
                dateTo: chunk[0].DateTicks,    // newest on the slice
                entryCount: chunk.Count));
        }
        return Result.Success();
    }

    private BlockLocation? Resolve(ReadOnlySpan<byte> blockId) =>
        _resolver.TryGetLocation(blockId, out var location) ? location : null;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}

/// <summary>
/// Outcome of <see cref="FolderCompiler.Compile"/>: the new compiled <see cref="FolderPageDirectory"/>
/// (delta head cleared, <see cref="FolderPageDirectory.FolderVersion"/> bumped) and where it was
/// written, plus the compile's <b>dead set</b> — artifacts the new directory no longer references and
/// that later compaction can reclaim (docs/Folder_Listing.md Section 3):
/// <list type="bullet">
///   <item><see cref="DeadPages"/> — old page blocks replaced by the repacked ones;</item>
///   <item><see cref="DeadDeltas"/> — the consumed delta chain blocks;</item>
///   <item><see cref="DeadDirectory"/> — the superseded directory version (same BlockId, prior offset).</item>
/// </list>
/// <see cref="NewPages"/> are the freshly written page blocks the new directory now points at. On a
/// no-op compile (nothing pending) every list is empty and <see cref="Directory"/> is the input.
/// </summary>
public sealed record FolderCompileResult(
    FolderPageDirectory Directory,
    BlockLocation? DirectoryLocation,
    int PendingEntryCount,
    IReadOnlyList<BlockLocation> NewPages,
    IReadOnlyList<BlockLocation> DeadPages,
    IReadOnlyList<BlockLocation> DeadDeltas,
    BlockLocation? DeadDirectory)
{
    /// <summary>Every block the compile rendered dead: replaced pages, consumed deltas, and the old directory version.</summary>
    public IEnumerable<BlockLocation> DeadArtifacts
    {
        get
        {
            foreach (var page in DeadPages) yield return page;
            foreach (var delta in DeadDeltas) yield return delta;
            if (DeadDirectory is not null) yield return DeadDirectory;
        }
    }
}
