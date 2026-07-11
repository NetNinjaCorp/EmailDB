namespace EmailDB.Format.V3;

/// <summary>
/// One folder's Bloom filter (docs/Search.md Phase 5): the <see cref="BloomFilter"/> over the Tier 1
/// tokens of the folder's <b>compiled pages</b>, stamped with the folder id it covers and the
/// <see cref="FolderPageDirectory.FolderVersion"/> it was built at.
///
/// <para><b>Covered version = the delta-safety guard.</b> The filter reflects only the compiled page rows
/// as of <see cref="CoveredFolderVersion"/>. Any add/delete/flag appends a delta and bumps the directory's
/// FolderVersion, and a page compile bumps it again; so the filter may be consulted to skip a folder only
/// when the folder's <i>current</i> directory is byte-for-byte the version it was built at
/// (<see cref="CoveredFolderVersion"/> equals the live version) AND that version has no pending delta. Those
/// two conditions together mean the compiled pages the filter tokenized ARE the folder's whole effective
/// listing, so a filter miss is authoritative. Pending deltas are never covered — a folder with pending
/// deltas is always scanned (docs/Search.md Phase 5).</para>
/// </summary>
public sealed class FolderBloomFilter
{
    /// <summary>The 16-byte ULID of the folder this filter covers (its directory block's stable BlockId).</summary>
    public required byte[] FolderId { get; init; }

    /// <summary>The <see cref="FolderPageDirectory.FolderVersion"/> the compiled pages were tokenized at.</summary>
    public required ulong CoveredFolderVersion { get; init; }

    /// <summary>The probabilistic filter over the covered pages' Tier 1 tokens.</summary>
    public required BloomFilter Filter { get; init; }

    /// <summary>
    /// Builds a folder filter from its compiled-page <paramref name="records"/> (pages only — never the
    /// pending delta chain), sized for <paramref name="falsePositiveRate"/> (default ~1%), stamped with
    /// <paramref name="coveredFolderVersion"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="folderId"/> is not 16 bytes.</exception>
    public static FolderBloomFilter Build(
        byte[] folderId, ulong coveredFolderVersion, IEnumerable<ListingRecord> records,
        double falsePositiveRate = BloomFilter.DefaultFalsePositiveRate)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(records);
        if (folderId.Length != UlidGenerator.UlidSize)
            throw new ArgumentException(
                $"FolderId must be exactly {UlidGenerator.UlidSize} bytes, got {folderId.Length}.", nameof(folderId));

        var tokens = new HashSet<ulong>();
        foreach (var record in records)
            ListingBloomTokens.AddRecordTokens(record, tokens);

        return new FolderBloomFilter
        {
            FolderId = (byte[])folderId.Clone(),
            CoveredFolderVersion = coveredFolderVersion,
            Filter = BloomFilter.Build(tokens, falsePositiveRate),
        };
    }

    /// <summary>
    /// Whether <paramref name="query"/> might match this folder given its <paramref name="currentFolderVersion"/>
    /// and <paramref name="hasPendingDelta"/> state. Returns false — an authoritative "cannot match, safe to
    /// skip" — only when the filter still covers the live directory (same version, no pending delta) AND the
    /// filter eliminates the query. Any version drift or pending delta forces a scan (returns true).
    /// </summary>
    public bool MightMatch(string query, ulong currentFolderVersion, bool hasPendingDelta)
    {
        if (hasPendingDelta || currentFolderVersion != CoveredFolderVersion)
            return true; // Filter no longer covers the folder's full effective listing — must scan.
        return ListingBloomTokens.MightMatch(Filter, query);
    }
}
