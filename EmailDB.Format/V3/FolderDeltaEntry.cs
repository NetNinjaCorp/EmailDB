using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// One buffered folder mutation in a <see cref="FolderDeltaLog"/> block (BlockType 13,
/// docs/Folder_Listing.md Sections 2-3). An entry is the append-only unit that keeps adds cheap:
/// a listing change is recorded here and later compiled into a <see cref="FolderPage"/> in bulk
/// (US-EMDB-82-8) rather than rewriting a page in place.
///
/// <para><b>Op payloads.</b> Each op carries only what a later merge/compile needs:</para>
/// <list type="table">
///   <item><description><see cref="FolderDeltaOp.Add"/> — the full <see cref="ListingRecord"/>
///   (which itself carries the EmailHashedID and ContentBlockId), so the row can be packed into a
///   page without re-reading Tier 2.</description></item>
///   <item><description><see cref="FolderDeltaOp.Delete"/> — just the <see cref="EmailHashedId"/>
///   of the row to drop.</description></item>
///   <item><description><see cref="FolderDeltaOp.FlagChange"/> — the <see cref="EmailHashedId"/>
///   plus the new <see cref="Flags"/>.</description></item>
/// </list>
///
/// <para>A folder move touches no content: it is a <see cref="Delete"/> in the source folder's log
/// and an <see cref="Add"/> in the destination's, both referencing the same ContentBlockId
/// (docs/Folder_Listing.md Section 3, "Move/delete").</para>
///
/// <para><b>Packed layout</b> — a one-byte op then the op-specific body, all integers
/// little-endian:</para>
/// <list type="table">
///   <item><description><c>[0]</c>       Op — one byte (<see cref="FolderDeltaOp"/>)</description></item>
///   <item><description>Add — the self-delimiting <see cref="ListingRecord"/> packing</description></item>
///   <item><description>Delete — 32-byte EmailHashedID</description></item>
///   <item><description>FlagChange — 32-byte EmailHashedID then Flags (UInt32)</description></item>
/// </list>
/// </summary>
public sealed class FolderDeltaEntry
{
    private const int OpLength = sizeof(byte);

    private readonly ListingRecord? _record;

    private FolderDeltaEntry(FolderDeltaOp op, EmailHashedID id, ListingRecord? record, ListingFlags flags)
    {
        Op = op;
        EmailHashedId = id;
        _record = record;
        Flags = flags;
    }

    /// <summary>The mutation kind (Add/Delete/FlagChange).</summary>
    public FolderDeltaOp Op { get; }

    /// <summary>Content identity of the affected email (from the record for an Add).</summary>
    public EmailHashedID EmailHashedId { get; }

    /// <summary>The listing row to add; non-null only for <see cref="FolderDeltaOp.Add"/>.</summary>
    public ListingRecord? Record => _record;

    /// <summary>The new listing flags; meaningful only for <see cref="FolderDeltaOp.FlagChange"/>.</summary>
    public ListingFlags Flags { get; }

    /// <summary>
    /// An Add entry buffering <paramref name="record"/> for a later page compile. The entry's
    /// <see cref="EmailHashedId"/> is taken from the record.
    /// </summary>
    public static FolderDeltaEntry Add(ListingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new FolderDeltaEntry(FolderDeltaOp.Add, record.EmailHashedId, record, record.Flags);
    }

    /// <summary>A Delete entry dropping the row identified by <paramref name="emailHashedId"/>.</summary>
    public static FolderDeltaEntry Delete(EmailHashedID emailHashedId) =>
        new(FolderDeltaOp.Delete, emailHashedId, record: null, ListingFlags.None);

    /// <summary>A FlagChange entry setting <paramref name="flags"/> on the row identified by
    /// <paramref name="emailHashedId"/>.</summary>
    public static FolderDeltaEntry FlagChange(EmailHashedID emailHashedId, ListingFlags flags) =>
        new(FolderDeltaOp.FlagChange, emailHashedId, record: null, flags);

    /// <summary>The exact number of bytes <see cref="Pack"/> writes for this entry.</summary>
    public int PackedLength => Op switch
    {
        FolderDeltaOp.Add => OpLength + _record!.PackedLength,
        FolderDeltaOp.Delete => OpLength + EmailHashedID.Size,
        FolderDeltaOp.FlagChange => OpLength + EmailHashedID.Size + sizeof(uint),
        _ => throw new InvalidOperationException($"Unknown delta op {Op}."),
    };

    /// <summary>
    /// Packs this entry into <paramref name="destination"/> and returns the bytes written
    /// (equal to <see cref="PackedLength"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public int Pack(Span<byte> destination)
    {
        int total = PackedLength;
        if (destination.Length < total)
            throw new ArgumentException(
                $"Destination must be at least {total} bytes, got {destination.Length}.", nameof(destination));

        destination[0] = (byte)Op;
        var body = destination.Slice(OpLength);
        switch (Op)
        {
            case FolderDeltaOp.Add:
                _record!.Pack(body);
                break;
            case FolderDeltaOp.Delete:
                EmailHashedId.WriteTo(body.Slice(0, EmailHashedID.Size));
                break;
            case FolderDeltaOp.FlagChange:
                EmailHashedId.WriteTo(body.Slice(0, EmailHashedID.Size));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    body.Slice(EmailHashedID.Size, sizeof(uint)), (uint)Flags);
                break;
            default:
                throw new InvalidOperationException($"Unknown delta op {Op}.");
        }
        return total;
    }

    /// <summary>
    /// Unpacks one entry from the front of <paramref name="source"/>, returning the entry and, via
    /// <paramref name="bytesConsumed"/>, how many bytes it occupied (so a block can read the next one).
    /// </summary>
    /// <exception cref="ArgumentException">The buffer is empty, truncated, or carries an unknown op.</exception>
    public static FolderDeltaEntry Unpack(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        if (source.Length < OpLength)
            throw new ArgumentException("Delta entry needs at least one op byte.", nameof(source));

        var op = (FolderDeltaOp)source[0];
        var body = source.Slice(OpLength);
        switch (op)
        {
            case FolderDeltaOp.Add:
            {
                var record = ListingRecord.Unpack(body, out int consumed);
                bytesConsumed = OpLength + consumed;
                return Add(record);
            }
            case FolderDeltaOp.Delete:
            {
                if (body.Length < EmailHashedID.Size)
                    throw new ArgumentException("Delete delta entry truncated before its EmailHashedID.", nameof(source));
                var id = new EmailHashedID(body.Slice(0, EmailHashedID.Size));
                bytesConsumed = OpLength + EmailHashedID.Size;
                return Delete(id);
            }
            case FolderDeltaOp.FlagChange:
            {
                if (body.Length < EmailHashedID.Size + sizeof(uint))
                    throw new ArgumentException("FlagChange delta entry truncated.", nameof(source));
                var id = new EmailHashedID(body.Slice(0, EmailHashedID.Size));
                var flags = (ListingFlags)BinaryPrimitives.ReadUInt32LittleEndian(
                    body.Slice(EmailHashedID.Size, sizeof(uint)));
                bytesConsumed = OpLength + EmailHashedID.Size + sizeof(uint);
                return FlagChange(id, flags);
            }
            default:
                throw new ArgumentException($"Unknown delta op byte {source[0]}.", nameof(source));
        }
    }
}
