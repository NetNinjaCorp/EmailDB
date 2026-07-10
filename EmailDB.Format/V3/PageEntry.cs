using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// One row of a <see cref="FolderPageDirectory"/> (BlockType 11, docs/Folder_Listing.md Section 2):
/// a pointer to a <see cref="FolderPage"/> plus the inclusive date range its records span, so the
/// directory can binary-search by date without opening any page.
///
/// <para><b>Packed layout</b> — a fixed 36 bytes, all integers little-endian to match the block
/// on-disk convention:</para>
/// <list type="table">
///   <item><description><c>[0..16)</c>  PageBlockId — 16-byte ULID of the FolderPage (BlockType 12)</description></item>
///   <item><description><c>[16..24)</c> DateFrom — Int64, oldest record's date (UTC ticks) on the page</description></item>
///   <item><description><c>[24..32)</c> DateTo — Int64, newest record's date (UTC ticks) on the page</description></item>
///   <item><description><c>[32..36)</c> EntryCount — Int32, number of listing records on the page</description></item>
/// </list>
///
/// <para>Because pages are date-descending and the directory lists them newest-first,
/// <see cref="DateTo"/> is non-increasing across a directory's entries — the ordering the date
/// binary search relies on.</para>
/// </summary>
public readonly struct PageEntry
{
    /// <summary>Byte length of a packed entry: 16 + 8 + 8 + 4.</summary>
    public const int PackedLength =
        UlidGenerator.UlidSize + sizeof(long) + sizeof(long) + sizeof(int);

    private const int DateFromOffset = UlidGenerator.UlidSize;         // 16
    private const int DateToOffset = DateFromOffset + sizeof(long);    // 24
    private const int EntryCountOffset = DateToOffset + sizeof(long);  // 32

    /// <summary>ULID (16 bytes) of the <see cref="FolderPage"/> this entry indexes.</summary>
    public byte[] PageBlockId { get; }

    /// <summary>Oldest record date on the page, as UTC ticks (the page's minimum <c>DateTicks</c>).</summary>
    public long DateFrom { get; }

    /// <summary>Newest record date on the page, as UTC ticks (the page's maximum <c>DateTicks</c>).</summary>
    public long DateTo { get; }

    /// <summary>Number of listing records on the page.</summary>
    public int EntryCount { get; }

    /// <summary>
    /// Constructs an entry. <paramref name="pageBlockId"/> must be a 16-byte ULID and
    /// <paramref name="dateFrom"/> must not exceed <paramref name="dateTo"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The block id is the wrong size, the range is inverted, or the count is negative.</exception>
    public PageEntry(byte[] pageBlockId, long dateFrom, long dateTo, int entryCount)
    {
        ArgumentNullException.ThrowIfNull(pageBlockId);
        if (pageBlockId.Length != UlidGenerator.UlidSize)
            throw new ArgumentException(
                $"PageBlockId must be exactly {UlidGenerator.UlidSize} bytes, got {pageBlockId.Length}.",
                nameof(pageBlockId));
        if (dateFrom > dateTo)
            throw new ArgumentException(
                $"DateFrom ({dateFrom}) must not exceed DateTo ({dateTo}).", nameof(dateFrom));
        if (entryCount < 0)
            throw new ArgumentException($"EntryCount must not be negative, got {entryCount}.", nameof(entryCount));

        PageBlockId = pageBlockId;
        DateFrom = dateFrom;
        DateTo = dateTo;
        EntryCount = entryCount;
    }

    /// <summary>True when <paramref name="dateTicks"/> falls within this page's inclusive date range.</summary>
    public bool Contains(long dateTicks) => dateTicks >= DateFrom && dateTicks <= DateTo;

    /// <summary>Packs this entry into <paramref name="destination"/>, returning the bytes written (<see cref="PackedLength"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="PackedLength"/>.</exception>
    public int Pack(Span<byte> destination)
    {
        if (destination.Length < PackedLength)
            throw new ArgumentException(
                $"Destination must be at least {PackedLength} bytes, got {destination.Length}.", nameof(destination));

        PageBlockId.CopyTo(destination.Slice(0, UlidGenerator.UlidSize));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(DateFromOffset, sizeof(long)), DateFrom);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(DateToOffset, sizeof(long)), DateTo);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(EntryCountOffset, sizeof(int)), EntryCount);
        return PackedLength;
    }

    /// <summary>Unpacks one entry from the front of <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentException">The buffer is shorter than <see cref="PackedLength"/>.</exception>
    public static PageEntry Unpack(ReadOnlySpan<byte> source)
    {
        if (source.Length < PackedLength)
            throw new ArgumentException(
                $"Page entry needs at least {PackedLength} bytes, got {source.Length}.", nameof(source));

        var pageBlockId = source.Slice(0, UlidGenerator.UlidSize).ToArray();
        long dateFrom = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DateFromOffset, sizeof(long)));
        long dateTo = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DateToOffset, sizeof(long)));
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(EntryCountOffset, sizeof(int)));
        return new PageEntry(pageBlockId, dateFrom, dateTo, entryCount);
    }
}
