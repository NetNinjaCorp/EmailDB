using System.Buffers.Binary;
using System.Text;

namespace EmailDB.Format.V3;

/// <summary>
/// A single Tier 1 folder-listing record (docs/Folder_Listing.md Section 2) — the ~400-byte packed
/// row that a folder browser renders without touching Tier 2 or Tier 3. Records are packed
/// date-descending into a <see cref="FolderPage"/> (BlockType 12).
///
/// <para><b>Packed layout.</b> A fixed 68-byte prefix followed by three length-prefixed UTF-8
/// strings; all integers little-endian to match the block on-disk convention
/// (<see cref="BlockSerializer"/>):</para>
/// <list type="table">
///   <item><description><c>[0..32)</c>  EmailHashedID — 32 raw digest bytes (see <see cref="EmailHashedID.WriteTo"/>)</description></item>
///   <item><description><c>[32..48)</c> ContentBlockId — 16-byte ULID of the Tier 3 EmailContent block</description></item>
///   <item><description><c>[48..56)</c> DateTicks — Int64, message date as UTC ticks</description></item>
///   <item><description><c>[56..60)</c> Flags — UInt32 (<see cref="ListingFlags"/>)</description></item>
///   <item><description><c>[60..68)</c> MessageSize — Int64, raw MIME size in bytes</description></item>
///   <item><description><c>From</c>, <c>Subject</c>, <c>Preview</c> — each a UInt16 byte-length prefix then that many UTF-8 bytes</description></item>
/// </list>
///
/// <para>Every field is regenerable from the Tier 2 <c>EmailMetadata</c> block, so a lost page can
/// be rebuilt without reading Tier 3 (docs/Folder_Listing.md Section 4).</para>
/// </summary>
public sealed class ListingRecord
{
    /// <summary>Byte length of the fixed prefix: 32 + 16 + 8 + 4 + 8.</summary>
    public const int FixedPrefixLength =
        EmailHashedID.Size + UlidGenerator.UlidSize + sizeof(long) + sizeof(uint) + sizeof(long);

    private const int DateTicksOffset = EmailHashedID.Size + UlidGenerator.UlidSize;   // 48
    private const int FlagsOffset = DateTicksOffset + sizeof(long);                    // 56
    private const int MessageSizeOffset = FlagsOffset + sizeof(uint);                  // 60

    /// <summary>Maximum UTF-8 byte length of any one packed string (the UInt16 prefix cap).</summary>
    public const int MaxStringByteLength = ushort.MaxValue;

    /// <summary>SHA3-256 content identity of the email (the PrimaryEmail index key).</summary>
    public EmailHashedID EmailHashedId { get; init; }

    /// <summary>ULID (16 bytes) of the Tier 3 <c>EmailContent</c> block this listing points at.</summary>
    public required byte[] ContentBlockId { get; init; }

    /// <summary>Message send date as UTC ticks (0 when the source had no parseable date).</summary>
    public long DateTicks { get; init; }

    /// <summary>Listing state flags (read/flagged/answered/draft).</summary>
    public ListingFlags Flags { get; init; }

    /// <summary>Raw MIME message size in bytes.</summary>
    public long MessageSize { get; init; }

    /// <summary>Decoded From display value.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Decoded Subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>~200-char plain-text body preview (Tier 2's Preview field).</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>The exact number of bytes <see cref="Pack"/> writes for this record.</summary>
    public int PackedLength =>
        FixedPrefixLength
        + sizeof(ushort) + Utf8ByteCount(From)
        + sizeof(ushort) + Utf8ByteCount(Subject)
        + sizeof(ushort) + Utf8ByteCount(Preview);

    /// <summary>
    /// Packs this record into <paramref name="destination"/> and returns the number of bytes written
    /// (equal to <see cref="PackedLength"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short, or a field is malformed.</exception>
    public int Pack(Span<byte> destination)
    {
        if (ContentBlockId is null || ContentBlockId.Length != UlidGenerator.UlidSize)
            throw new ArgumentException(
                $"ContentBlockId must be exactly {UlidGenerator.UlidSize} bytes.", nameof(ContentBlockId));

        int total = PackedLength;
        if (destination.Length < total)
            throw new ArgumentException(
                $"Destination must be at least {total} bytes, got {destination.Length}.", nameof(destination));

        EmailHashedId.WriteTo(destination.Slice(0, EmailHashedID.Size));
        ContentBlockId.CopyTo(destination.Slice(EmailHashedID.Size, UlidGenerator.UlidSize));
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(DateTicksOffset, sizeof(long)), DateTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(FlagsOffset, sizeof(uint)), (uint)Flags);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(MessageSizeOffset, sizeof(long)), MessageSize);

        int offset = FixedPrefixLength;
        offset += PackString(destination, offset, From);
        offset += PackString(destination, offset, Subject);
        offset += PackString(destination, offset, Preview);
        return offset;
    }

    /// <summary>Packs this record into a right-sized new array.</summary>
    public byte[] Pack()
    {
        var buffer = new byte[PackedLength];
        Pack(buffer);
        return buffer;
    }

    /// <summary>
    /// Unpacks one record from the front of <paramref name="source"/>, returning the record and, via
    /// <paramref name="bytesConsumed"/>, how many bytes it occupied (so a page can read the next one).
    /// </summary>
    /// <exception cref="ArgumentException">The buffer is truncated or a length prefix overruns it.</exception>
    public static ListingRecord Unpack(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        if (source.Length < FixedPrefixLength)
            throw new ArgumentException(
                $"Listing record needs at least {FixedPrefixLength} bytes, got {source.Length}.", nameof(source));

        var id = new EmailHashedID(source.Slice(0, EmailHashedID.Size));
        var contentBlockId = source.Slice(EmailHashedID.Size, UlidGenerator.UlidSize).ToArray();
        long dateTicks = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(DateTicksOffset, sizeof(long)));
        var flags = (ListingFlags)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FlagsOffset, sizeof(uint)));
        long messageSize = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(MessageSizeOffset, sizeof(long)));

        int offset = FixedPrefixLength;
        string from = UnpackString(source, ref offset);
        string subject = UnpackString(source, ref offset);
        string preview = UnpackString(source, ref offset);

        bytesConsumed = offset;
        return new ListingRecord
        {
            EmailHashedId = id,
            ContentBlockId = contentBlockId,
            DateTicks = dateTicks,
            Flags = flags,
            MessageSize = messageSize,
            From = from,
            Subject = subject,
            Preview = preview,
        };
    }

    private static int PackString(Span<byte> destination, int offset, string value)
    {
        int byteCount = Utf8ByteCount(value);
        if (byteCount > MaxStringByteLength)
            throw new ArgumentException(
                $"Packed string is {byteCount} UTF-8 bytes, exceeding the {MaxStringByteLength}-byte limit.");

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, sizeof(ushort)), (ushort)byteCount);
        Encoding.UTF8.GetBytes(value, destination.Slice(offset + sizeof(ushort), byteCount));
        return sizeof(ushort) + byteCount;
    }

    private static string UnpackString(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset + sizeof(ushort) > source.Length)
            throw new ArgumentException("Listing record truncated before a string length prefix.", nameof(source));

        int length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        if (offset + length > source.Length)
            throw new ArgumentException(
                $"Listing record string length {length} overruns the buffer.", nameof(source));

        string value = Encoding.UTF8.GetString(source.Slice(offset, length));
        offset += length;
        return value;
    }

    private static int Utf8ByteCount(string value) =>
        string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
}
