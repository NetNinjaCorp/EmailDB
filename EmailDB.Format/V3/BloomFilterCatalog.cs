using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// The Bloom-filter catalog (docs/Search.md Phase 5): the single BlockType-18 block that carries every
/// folder's <see cref="FolderBloomFilter"/> and is registered in the Checkpoint's generic secondary-index
/// table under <see cref="BTreeIndexKind.Bloom"/> (IndexKind 4), so the whole per-folder filter set reopens
/// from one authoritative pointer — exactly as the <see cref="FtsSearchRoot"/> reopens the FTS index from
/// its IndexKind-3 entry and the <see cref="DateIndex"/> from its IndexKind-2 entry.
///
/// <para>docs/Search.md envisions "one BloomFilter block per folder" named by a catalog; this v3
/// implementation packs the per-folder filters INLINE into the one catalog block instead of writing a
/// block per folder. The filters are small, rebuildable derived data that take part in no crash-recovery
/// guarantee, so one block is simpler and still satisfies the story's criteria (sized ~1% FP, consulted
/// before scans, always encrypted, rebuilt at compile, registered in the Checkpoint). Splitting into a
/// block per folder is a future refinement this layout does not preclude.</para>
///
/// <para><b>Layout</b> (serialized by <see cref="BloomFilterCatalogSerializer"/>, little-endian per spec
/// Section 4):</para>
/// <code>
///   CatalogSequence (uint64, 8)   — monotonic version (advances every rebuild)
///   FolderCount     (uint32, 4)
///   Folders[]       — each: FolderId (16) ‖ CoveredFolderVersion (uint64, 8)
///                            ‖ FilterLength (int32, 4) ‖ Filter (FilterLength bytes)
/// </code>
/// </summary>
public sealed class BloomFilterCatalog
{
    /// <summary>Monotonic version of the catalog; advances each time it is flushed with a change.</summary>
    public required ulong CatalogSequence { get; init; }

    /// <summary>The per-folder filters, in ascending folder-id order for determinism.</summary>
    public required IReadOnlyList<FolderBloomFilter> Filters { get; init; }

    /// <summary>Number of folders the catalog carries a filter for.</summary>
    public int FolderCount => Filters.Count;

    /// <summary>Creates a catalog naming <paramref name="filters"/>, sorted ascending by folder id.</summary>
    public static BloomFilterCatalog Create(ulong catalogSequence, IEnumerable<FolderBloomFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var list = new List<FolderBloomFilter>(filters);
        list.Sort(static (a, b) => a.FolderId.AsSpan().SequenceCompareTo(b.FolderId));
        return new BloomFilterCatalog { CatalogSequence = catalogSequence, Filters = list };
    }

    /// <summary>
    /// Builds the Checkpoint secondary-index entry that registers this catalog under
    /// <see cref="BTreeIndexKind.Bloom"/> (IndexKind 4). The caller supplies the catalog block's ULID and
    /// the file offset it was appended at (a verified hint); recovery reads this entry back to reopen the
    /// filter set — mirrors <see cref="FtsSearchRoot.ToCheckpointSecondaryIndex"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="catalogBlockId"/> is not 16 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="catalogOffset"/> is negative.</exception>
    public static CheckpointSecondaryIndex ToCheckpointSecondaryIndex(byte[] catalogBlockId, long catalogOffset) =>
        CheckpointSecondaryIndex.Create(BTreeIndexKind.Bloom, catalogBlockId, catalogOffset);
}

/// <summary>Serializes the variable-length <see cref="BloomFilterCatalog"/> payload (BlockType 18).</summary>
public static class BloomFilterCatalogSerializer
{
    private const int FolderIdSize = UlidGenerator.UlidSize; // 16
    private const int FixedPrefixSize = 8 + 4;               // CatalogSequence + FolderCount
    private const int PerFolderFixedSize = FolderIdSize + 8 + 4; // id + version + filter length prefix

    /// <exception cref="ArgumentException">A folder id has the wrong width.</exception>
    public static byte[] Serialize(BloomFilterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        int total = FixedPrefixSize;
        foreach (var f in catalog.Filters)
        {
            if (f.FolderId is null || f.FolderId.Length != FolderIdSize)
                throw new ArgumentException($"A folder id must be exactly {FolderIdSize} bytes.", nameof(catalog));
            total += PerFolderFixedSize + f.Filter.SerializedLength;
        }

        var buffer = new byte[total];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(0, 8), catalog.CatalogSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), (uint)catalog.Filters.Count);

        int cursor = FixedPrefixSize;
        foreach (var f in catalog.Filters)
        {
            f.FolderId.CopyTo(span.Slice(cursor, FolderIdSize));
            cursor += FolderIdSize;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(cursor, 8), f.CoveredFolderVersion);
            cursor += 8;
            int filterLen = f.Filter.SerializedLength;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor, 4), filterLen);
            cursor += 4;
            f.Filter.WriteTo(span.Slice(cursor, filterLen));
            cursor += filterLen;
        }
        return buffer;
    }

    public static Result<BloomFilterCatalog> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPrefixSize)
            return Result<BloomFilterCatalog>.Failure(
                $"Bloom catalog payload must be at least {FixedPrefixSize} bytes, got {payload.Length}.");

        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8));
        uint folderCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));

        var filters = new List<FolderBloomFilter>((int)Math.Min(folderCount, 1024));
        int cursor = FixedPrefixSize;
        for (uint i = 0; i < folderCount; i++)
        {
            if (cursor + PerFolderFixedSize > payload.Length)
                return Result<BloomFilterCatalog>.Failure(
                    $"Bloom catalog truncated reading folder {i} of {folderCount}.");

            var folderId = payload.Slice(cursor, FolderIdSize).ToArray();
            cursor += FolderIdSize;
            ulong coveredVersion = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor, 8));
            cursor += 8;
            int filterLen = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            if (filterLen < 0 || cursor + filterLen > payload.Length)
                return Result<BloomFilterCatalog>.Failure(
                    $"Bloom catalog folder {i} declares a filter length {filterLen} that overruns the buffer.");

            var filter = BloomFilter.Deserialize(payload.Slice(cursor, filterLen), out int consumed);
            if (filter.IsFailure)
                return Result<BloomFilterCatalog>.Failure($"Bloom catalog folder {i}: {filter.Error}");
            if (consumed != filterLen)
                return Result<BloomFilterCatalog>.Failure(
                    $"Bloom catalog folder {i}: filter consumed {consumed} bytes, expected {filterLen}.");
            cursor += filterLen;

            filters.Add(new FolderBloomFilter
            {
                FolderId = folderId,
                CoveredFolderVersion = coveredVersion,
                Filter = filter.Value,
            });
        }

        return Result<BloomFilterCatalog>.Success(new BloomFilterCatalog
        {
            CatalogSequence = sequence,
            Filters = filters,
        });
    }
}
