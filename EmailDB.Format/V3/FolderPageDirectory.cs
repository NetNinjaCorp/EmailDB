using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// The per-folder page index (BlockType 11, docs/Folder_Listing.md Section 2): one block per folder
/// that names the folder's <see cref="FolderPage"/>s in newest-first order, carries the
/// <see cref="FolderVersion"/> replication counter, and points at the head of the pending
/// <c>FolderDeltaLog</c> chain. Browsing a folder reads this directory (1 block, usually cached),
/// then one page, then the short delta chain — the "at most 3 block reads" path.
///
/// <para><b>Stable identity.</b> The folder's ULID (<see cref="FolderId"/>) doubles as the
/// directory block's own BlockId, so every COW rewrite re-appends under the same BlockId as a new
/// version (docs/Folder_Listing.md Section 2). The directory is immutable in memory; a mutation
/// produces a new instance via <see cref="Rewrite"/> with <see cref="FolderVersion"/> incremented,
/// which the store then appends.</para>
///
/// <para><b>Payload layout</b> — a fixed 45-byte header then the packed <see cref="PageEntry"/>s,
/// all integers little-endian:</para>
/// <list type="table">
///   <item><description><c>[0]</c>       FormatVersion — one byte</description></item>
///   <item><description><c>[1..17)</c>   FolderId — 16-byte ULID</description></item>
///   <item><description><c>[17..25)</c>  FolderVersion — UInt64 monotonic counter</description></item>
///   <item><description><c>[25..41)</c>  HeadDeltaBlockId — 16-byte ULID (all-zero = none pending)</description></item>
///   <item><description><c>[41..45)</c>  PageEntry count — Int32</description></item>
///   <item><description>then <c>count</c> × <see cref="PageEntry"/> (36 bytes each)</description></item>
/// </list>
/// </summary>
public sealed class FolderPageDirectory
{
    /// <summary>Payload format version, bumped if the packed layout changes.</summary>
    public const byte FormatVersion = 1;

    private const int FolderIdOffset = sizeof(byte);                            // 1
    private const int FolderVersionOffset = FolderIdOffset + UlidGenerator.UlidSize;   // 17
    private const int HeadDeltaOffset = FolderVersionOffset + sizeof(ulong);    // 25
    private const int PageCountOffset = HeadDeltaOffset + UlidGenerator.UlidSize;      // 41

    /// <summary>Bytes of fixed header before the first page entry.</summary>
    public const int HeaderLength = PageCountOffset + sizeof(int);             // 45

    private static readonly byte[] NoDeltaHead = new byte[UlidGenerator.UlidSize];

    private readonly byte[] _folderId;
    private readonly byte[] _headDeltaBlockId;
    private readonly PageEntry[] _pageEntries;

    private FolderPageDirectory(
        byte[] folderId, ulong folderVersion, byte[] headDeltaBlockId, PageEntry[] pageEntries)
    {
        _folderId = folderId;
        FolderVersion = folderVersion;
        _headDeltaBlockId = headDeltaBlockId;
        _pageEntries = pageEntries;
    }

    /// <summary>The folder's ULID; also the directory block's stable BlockId across rewrites.</summary>
    public byte[] FolderId => (byte[])_folderId.Clone();

    /// <summary>Monotonic counter incremented on every directory rewrite; the sync replication unit.</summary>
    public ulong FolderVersion { get; }

    /// <summary>Newest <c>FolderDeltaLog</c> block (all-zero ULID = none pending).</summary>
    public byte[] HeadDeltaBlockId => (byte[])_headDeltaBlockId.Clone();

    /// <summary>True when no delta block is pending (<see cref="HeadDeltaBlockId"/> is the zero ULID).</summary>
    public bool HasPendingDelta => !IsZeroUlid(_headDeltaBlockId);

    /// <summary>The folder's pages, newest-first (matching page 0 = newest emails).</summary>
    public IReadOnlyList<PageEntry> PageEntries => _pageEntries;

    /// <summary>Number of pages this directory indexes.</summary>
    public int PageCount => _pageEntries.Length;

    /// <summary>
    /// Creates a directory. <paramref name="pageEntries"/> are copied and must be in newest-first
    /// order (non-increasing <see cref="PageEntry.DateTo"/>), the order the date binary search relies
    /// on. Pass <see langword="null"/> or a zero ULID for <paramref name="headDeltaBlockId"/> when no
    /// delta is pending.
    /// </summary>
    /// <exception cref="ArgumentException">A block id is the wrong size or the entries are not newest-first.</exception>
    public static FolderPageDirectory Create(
        byte[] folderId,
        IEnumerable<PageEntry> pageEntries,
        byte[]? headDeltaBlockId = null,
        ulong folderVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(pageEntries);
        if (folderId.Length != UlidGenerator.UlidSize)
            throw new ArgumentException(
                $"FolderId must be exactly {UlidGenerator.UlidSize} bytes, got {folderId.Length}.", nameof(folderId));

        var head = NormalizeDeltaHead(headDeltaBlockId);
        var entries = pageEntries.ToArray();
        ValidateNewestFirst(entries);
        return new FolderPageDirectory((byte[])folderId.Clone(), folderVersion, head, entries);
    }

    /// <summary>
    /// Copy-on-write rewrite: returns a new directory for the same folder with
    /// <see cref="FolderVersion"/> incremented by one and the given page set and delta head. This is
    /// the only way <see cref="FolderVersion"/> advances — an add, a page compaction, or a delta-head
    /// change all go through here so every rewrite bumps the counter (docs/Folder_Listing.md
    /// Section 3). Overflowing the 64-bit counter throws.
    /// </summary>
    /// <exception cref="ArgumentException">A block id is the wrong size or the entries are not newest-first.</exception>
    /// <exception cref="OverflowException"><see cref="FolderVersion"/> is at <see cref="ulong.MaxValue"/>.</exception>
    public FolderPageDirectory Rewrite(
        IEnumerable<PageEntry> pageEntries, byte[]? headDeltaBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(pageEntries);
        checked { _ = FolderVersion + 1; }

        var head = NormalizeDeltaHead(headDeltaBlockId);
        var entries = pageEntries.ToArray();
        ValidateNewestFirst(entries);
        return new FolderPageDirectory(_folderId, FolderVersion + 1, head, entries);
    }

    /// <summary>
    /// Binary-searches the newest-first page ranges for the page a date-jump should read to land at
    /// <paramref name="dateTicks"/> (docs/Folder_Listing.md Section 3, "Jumping to a date"): the page
    /// whose inclusive range contains the date, or — when the date falls in a gap between pages, or
    /// outside the folder's overall span — the nearest page holding emails at least as new as it,
    /// clamped to the oldest page. Returns -1 when the directory has no pages.
    /// </summary>
    public int FindPageByDate(long dateTicks)
    {
        int count = _pageEntries.Length;
        if (count == 0)
            return -1;

        // DateTo is non-increasing across entries (newest-first). Find the last index whose
        // DateTo >= dateTicks — the oldest page that still holds an email at least as new as the
        // target. That page either contains the date or sits immediately newer than a gap.
        int lo = 0, hi = count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_pageEntries[mid].DateTo >= dateTicks)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // result == -1 means the date is newer than every page → the newest page (index 0).
        // Otherwise clamp to the oldest page for dates older than the whole folder.
        return result < 0 ? 0 : result;
    }

    /// <summary>
    /// Binary-searches for the page whose inclusive date range <em>contains</em>
    /// <paramref name="dateTicks"/>. Returns <see langword="true"/> and the page index on a hit;
    /// <see langword="false"/> when the date falls in a gap or outside the folder's span.
    /// </summary>
    public bool TryFindContainingPage(long dateTicks, out int index)
    {
        index = FindPageByDate(dateTicks);
        if (index < 0 || !_pageEntries[index].Contains(dateTicks))
        {
            index = -1;
            return false;
        }
        return true;
    }

    /// <summary>The exact byte length <see cref="PackPayload"/> produces for this directory.</summary>
    public int PayloadLength => HeaderLength + _pageEntries.Length * PageEntry.PackedLength;

    /// <summary>Packs the directory into a new byte array ready for the block pipeline (Zstd → encrypt → append).</summary>
    public byte[] PackPayload()
    {
        var buffer = new byte[PayloadLength];
        var span = buffer.AsSpan();

        span[0] = FormatVersion;
        _folderId.CopyTo(span.Slice(FolderIdOffset, UlidGenerator.UlidSize));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(FolderVersionOffset, sizeof(ulong)), FolderVersion);
        _headDeltaBlockId.CopyTo(span.Slice(HeadDeltaOffset, UlidGenerator.UlidSize));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(PageCountOffset, sizeof(int)), _pageEntries.Length);

        int offset = HeaderLength;
        foreach (var entry in _pageEntries)
            offset += entry.Pack(span.Slice(offset));
        return buffer;
    }

    /// <summary>
    /// Unpacks a directory payload produced by <see cref="PackPayload"/>, validating the version,
    /// the entry count, and the newest-first ordering.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is malformed, truncated, or mis-ordered.</exception>
    public static FolderPageDirectory UnpackPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
            throw new ArgumentException(
                $"FolderPageDirectory payload needs at least {HeaderLength} header bytes, got {payload.Length}.",
                nameof(payload));

        byte version = payload[0];
        if (version != FormatVersion)
            throw new ArgumentException(
                $"Unsupported FolderPageDirectory format version {version} (expected {FormatVersion}).", nameof(payload));

        var folderId = payload.Slice(FolderIdOffset, UlidGenerator.UlidSize).ToArray();
        ulong folderVersion = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(FolderVersionOffset, sizeof(ulong)));
        var headDelta = payload.Slice(HeadDeltaOffset, UlidGenerator.UlidSize).ToArray();
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(PageCountOffset, sizeof(int)));
        if (count < 0)
            throw new ArgumentException($"FolderPageDirectory page count {count} is negative.", nameof(payload));

        int expected = HeaderLength + count * PageEntry.PackedLength;
        if (payload.Length < expected)
            throw new ArgumentException(
                $"FolderPageDirectory payload is {payload.Length} bytes, need {expected} for {count} entries.",
                nameof(payload));

        var entries = new PageEntry[count];
        int offset = HeaderLength;
        for (int i = 0; i < count; i++)
        {
            entries[i] = PageEntry.Unpack(payload.Slice(offset, PageEntry.PackedLength));
            offset += PageEntry.PackedLength;
        }

        ValidateNewestFirst(entries);
        return new FolderPageDirectory(folderId, folderVersion, headDelta, entries);
    }

    private static byte[] NormalizeDeltaHead(byte[]? headDeltaBlockId)
    {
        if (headDeltaBlockId is null)
            return NoDeltaHead;
        if (headDeltaBlockId.Length != UlidGenerator.UlidSize)
            throw new ArgumentException(
                $"HeadDeltaBlockId must be exactly {UlidGenerator.UlidSize} bytes, got {headDeltaBlockId.Length}.",
                nameof(headDeltaBlockId));
        return (byte[])headDeltaBlockId.Clone();
    }

    private static void ValidateNewestFirst(PageEntry[] entries)
    {
        for (int i = 1; i < entries.Length; i++)
            if (entries[i - 1].DateTo < entries[i].DateTo)
                throw new ArgumentException(
                    $"PageEntries must be newest-first (non-increasing DateTo); entry {i} is newer than entry {i - 1}.",
                    nameof(entries));
    }

    private static bool IsZeroUlid(byte[] ulid)
    {
        foreach (var b in ulid)
            if (b != 0)
                return false;
        return true;
    }
}
