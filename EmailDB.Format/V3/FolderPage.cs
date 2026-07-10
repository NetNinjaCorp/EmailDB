using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// A Tier 1 folder page (BlockType 12, docs/Folder_Listing.md Section 2): a run of packed
/// <see cref="ListingRecord"/>s in strict date-descending order (newest first), ~80 records per
/// page (~35 KB before compression). Browsing a folder reads the directory then one page, so a
/// page must be self-contained and cheap to unpack.
///
/// <para><b>Payload layout.</b> A 5-byte header — a format-version byte then an Int32 (LE) record
/// count — followed by the packed records back to back. Each record is self-delimiting (its string
/// fields are length-prefixed; see <see cref="ListingRecord"/>), so the count plus sequential
/// unpacking is all that is needed to read them.</para>
///
/// <para>The packed payload is Zstd-compressed and encrypted under the Default policy before being
/// appended as a BlockType 12 block; this type owns only the packing.</para>
/// </summary>
public sealed class FolderPage
{
    /// <summary>Target number of listing records per page (docs/Folder_Listing.md Section 2).</summary>
    public const int TargetRecordsPerPage = 80;

    /// <summary>Payload format version, bumped if the packed layout changes.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Bytes of fixed page header before the first record: version(1) + count(4).</summary>
    public const int HeaderLength = sizeof(byte) + sizeof(int);

    private readonly List<ListingRecord> _records;

    private FolderPage(List<ListingRecord> records) => _records = records;

    /// <summary>The page's records, newest-first (date-descending).</summary>
    public IReadOnlyList<ListingRecord> Records => _records;

    /// <summary>Number of records on the page.</summary>
    public int Count => _records.Count;

    /// <summary>
    /// Builds a page from <paramref name="records"/>, sorting them date-descending (newest first).
    /// Ties on <see cref="ListingRecord.DateTicks"/> are broken by EmailHashedID for a stable,
    /// deterministic order.
    /// </summary>
    public static FolderPage FromRecords(IEnumerable<ListingRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ordered = new List<ListingRecord>(records);
        ordered.Sort(CompareDateDescending);
        return new FolderPage(ordered);
    }

    /// <summary>
    /// Wraps records that are <b>already</b> date-descending, without re-sorting; validates the
    /// ordering and throws if it is violated. Used by <see cref="UnpackPayload"/> on read.
    /// </summary>
    /// <exception cref="ArgumentException">The records are not date-descending.</exception>
    public static FolderPage FromSortedRecords(IEnumerable<ListingRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = new List<ListingRecord>(records);
        for (int i = 1; i < list.Count; i++)
            if (CompareDateDescending(list[i - 1], list[i]) > 0)
                throw new ArgumentException(
                    $"FolderPage records must be date-descending; record {i} is newer than record {i - 1}.",
                    nameof(records));
        return new FolderPage(list);
    }

    /// <summary>True when the records are in non-increasing date order.</summary>
    public bool IsDateDescending()
    {
        for (int i = 1; i < _records.Count; i++)
            if (_records[i - 1].DateTicks < _records[i].DateTicks)
                return false;
        return true;
    }

    /// <summary>The exact byte length <see cref="PackPayload"/> produces for this page.</summary>
    public int PayloadLength
    {
        get
        {
            int total = HeaderLength;
            foreach (var record in _records)
                total += record.PackedLength;
            return total;
        }
    }

    /// <summary>
    /// Packs the page into a new byte array ready for the block pipeline (Zstd → encrypt → append).
    /// </summary>
    public byte[] PackPayload()
    {
        var buffer = new byte[PayloadLength];
        var span = buffer.AsSpan();
        span[0] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(sizeof(byte), sizeof(int)), _records.Count);

        int offset = HeaderLength;
        foreach (var record in _records)
            offset += record.Pack(span.Slice(offset));
        return buffer;
    }

    /// <summary>
    /// Unpacks a page payload produced by <see cref="PackPayload"/>, validating the version, the
    /// record count, and the date-descending ordering.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is malformed, truncated, or mis-ordered.</exception>
    public static FolderPage UnpackPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
            throw new ArgumentException(
                $"FolderPage payload needs at least {HeaderLength} header bytes, got {payload.Length}.", nameof(payload));

        byte version = payload[0];
        if (version != FormatVersion)
            throw new ArgumentException(
                $"Unsupported FolderPage format version {version} (expected {FormatVersion}).", nameof(payload));

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(sizeof(byte), sizeof(int)));
        if (count < 0)
            throw new ArgumentException($"FolderPage record count {count} is negative.", nameof(payload));

        var records = new List<ListingRecord>(count);
        int offset = HeaderLength;
        for (int i = 0; i < count; i++)
        {
            var record = ListingRecord.Unpack(payload.Slice(offset), out int consumed);
            records.Add(record);
            offset += consumed;
        }

        return FromSortedRecords(records);
    }

    private static int CompareDateDescending(ListingRecord a, ListingRecord b)
    {
        int byDate = b.DateTicks.CompareTo(a.DateTicks); // newest first
        return byDate != 0 ? byDate : a.EmailHashedId.CompareTo(b.EmailHashedId);
    }
}
