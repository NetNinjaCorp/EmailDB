namespace EmailDB.Format.V3;

/// <summary>
/// The maintenance + query side of the per-folder Bloom filters (docs/Search.md Phase 5) — the Bloom
/// counterpart of <see cref="FtsIndex"/>. It holds every folder's <see cref="FolderBloomFilter"/> in
/// memory, is rebuilt for a folder at page-compile time (<see cref="RebuildFolder"/>), is consulted before
/// a multi-folder scan (<see cref="MightMatch"/>), flushes the whole set to a single
/// <see cref="BloomFilterCatalog"/> block registered in the Checkpoint under
/// <see cref="BTreeIndexKind.Bloom"/> (IndexKind 4), and reopens from that block
/// (<see cref="Reconstruct"/>).
///
/// <para><b>Rebuildable derived data.</b> The filters take part in no crash-recovery guarantee: a filter is
/// only ever an optimization gate over the authoritative Tier 1 page scan, so an out-of-date or missing
/// filter can never cause a wrong result — at worst a folder is scanned that a fresh filter would have
/// skipped (the <see cref="FolderBloomFilter.CoveredFolderVersion"/> guard makes a stale filter fail
/// closed, i.e. it forces a scan). Not thread-safe.</para>
/// </summary>
public sealed class FolderBloomIndex
{
    // Folder id (hex) → its filter. Hex-string key gives value equality over the 16-byte id.
    private readonly Dictionary<string, FolderBloomFilter> _filters = new(StringComparer.Ordinal);

    private ulong _nextCatalogSequence;
    private bool _dirty;
    private BlockLocation? _committedCatalogLocation;

    /// <summary>Creates an empty index (a fresh file, or one the Checkpoint names no Bloom catalog for).</summary>
    public FolderBloomIndex() { }

    private FolderBloomIndex(ulong nextCatalogSequence, BlockLocation committedCatalogLocation)
    {
        _nextCatalogSequence = nextCatalogSequence;
        _committedCatalogLocation = committedCatalogLocation;
    }

    /// <summary>Number of folders with a filter.</summary>
    public int FolderCount => _filters.Count;

    /// <summary>True when a rebuild since the last flush has yet to be written to a catalog block.</summary>
    public bool IsDirty => _dirty;

    /// <summary>The location of the currently-committed catalog block, or null when never flushed.</summary>
    public BlockLocation? CommittedCatalogLocation => _committedCatalogLocation;

    // ------------------------------------------------------------- Build / rebuild

    /// <summary>
    /// (Re)builds a folder's filter from its <b>compiled page</b> <paramref name="records"/> (never the
    /// pending delta chain), stamping it with <paramref name="coveredFolderVersion"/> — the folder's
    /// directory version at the compile that produced those pages. Replaces any prior filter for the folder
    /// and marks the index dirty so the next flush persists it. This is the "rebuilt with page compile"
    /// hook of the story: the caller runs it right after a <see cref="FolderCompiler.Compile"/> (when the
    /// folder has no pending delta) so the filter covers the folder's whole effective listing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="folderId"/> is not 16 bytes.</exception>
    public void RebuildFolder(byte[] folderId, ulong coveredFolderVersion, IEnumerable<ListingRecord> records)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        var filter = FolderBloomFilter.Build(folderId, coveredFolderVersion, records);
        _filters[Key(folderId)] = filter;
        _dirty = true;
    }

    /// <summary>Removes a folder's filter (e.g. a deleted folder); marks dirty when one was present.</summary>
    public bool RemoveFolder(byte[] folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        if (!_filters.Remove(Key(folderId)))
            return false;
        _dirty = true;
        return true;
    }

    /// <summary>The folder's filter, or null when none is registered.</summary>
    public FolderBloomFilter? GetFilter(byte[] folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return _filters.TryGetValue(Key(folderId), out var f) ? f : null;
    }

    // ------------------------------------------------------------- Query

    /// <summary>
    /// Whether folder <paramref name="folderId"/> might match <paramref name="query"/> given its live
    /// <paramref name="currentFolderVersion"/> and <paramref name="hasPendingDelta"/> state. Returns true
    /// (folder must be scanned) when no filter is registered, when the filter no longer covers the live
    /// directory (version drift or pending delta), or when the filter cannot eliminate the query; returns
    /// false — the authoritative "cannot match, safe to skip" — only when a covering filter eliminates it.
    /// </summary>
    public bool MightMatch(byte[] folderId, string query, ulong currentFolderVersion, bool hasPendingDelta)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(query);
        var filter = GetFilter(folderId);
        if (filter is null)
            return true; // No filter ⇒ cannot eliminate; scan.
        return filter.MightMatch(query, currentFolderVersion, hasPendingDelta);
    }

    // ------------------------------------------------------------- Flush

    /// <summary>
    /// Writes the whole per-folder filter set as one fresh <see cref="BloomFilterCatalog"/> block and
    /// returns its location for the Checkpoint's IndexKind-4 registration. When the index is unchanged
    /// since the last flush the existing catalog location is returned unwritten (so a commit still
    /// re-registers it); when the index is empty and was never flushed, null is returned (nothing to
    /// register). On success the dirty flag clears.
    /// </summary>
    public Result<BlockLocation?> Flush(BloomFilterStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!_dirty)
            return Result<BlockLocation?>.Success(_committedCatalogLocation);

        var catalog = BloomFilterCatalog.Create(_nextCatalogSequence, _filters.Values);
        var written = store.AppendCatalog(catalog);
        if (written.IsFailure)
            return Result<BlockLocation?>.Failure($"Bloom flush failed writing the catalog block: {written.Error}");

        _nextCatalogSequence++;
        _committedCatalogLocation = written.Value;
        _dirty = false;
        return Result<BlockLocation?>.Success(_committedCatalogLocation);
    }

    // ------------------------------------------------------------- Reconstruction

    /// <summary>
    /// Rebuilds the in-memory index from the catalog block a Checkpoint named under IndexKind 4: reads the
    /// <see cref="BloomFilterCatalog"/> at <paramref name="bloomRoot"/> and loads every folder's filter. A
    /// null pointer is an empty index.
    /// </summary>
    public static Result<FolderBloomIndex> Reconstruct(BloomFilterStore store, ResolvedRoot? bloomRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (bloomRoot is null)
            return Result<FolderBloomIndex>.Success(new FolderBloomIndex());

        var read = store.ReadCatalog(bloomRoot.Offset);
        if (read.IsFailure)
            return Result<FolderBloomIndex>.Failure($"Bloom reopen failed reading the catalog: {read.Error}");
        var catalog = read.Value;

        var location = new BlockLocation
        {
            BlockId = (byte[])bloomRoot.BlockId.Clone(),
            Offset = bloomRoot.Offset,
            TotalBlockLength = bloomRoot.TotalBlockLength,
        };
        var index = new FolderBloomIndex(catalog.CatalogSequence + 1, location);
        foreach (var filter in catalog.Filters)
            index._filters[Key(filter.FolderId)] = filter;
        return Result<FolderBloomIndex>.Success(index);
    }

    private static string Key(byte[] folderId) => Convert.ToHexString(folderId);
}
