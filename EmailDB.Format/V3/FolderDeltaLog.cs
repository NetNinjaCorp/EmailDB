using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// One block in a folder's append-only delta chain (BlockType 13, docs/Folder_Listing.md
/// Sections 2-3): a small block buffering <see cref="FolderDeltaEntry"/>s between page rebuilds.
/// Each block links to the previous one via <see cref="PreviousDeltaBlockId"/>, and the folder's
/// <see cref="FolderPageDirectory.HeadDeltaBlockId"/> names the newest block — so the whole pending
/// tail is a linked list walked head-to-root. There is no in-place rewriting: a mutation appends a
/// new block and advances the directory head (docs/Folder_Listing.md Section 2).
///
/// <para><b>Payload layout</b> — a fixed 21-byte header then the packed entries, all integers
/// little-endian:</para>
/// <list type="table">
///   <item><description><c>[0]</c>       FormatVersion — one byte</description></item>
///   <item><description><c>[1..17)</c>   PreviousDeltaBlockId — 16-byte ULID (all-zero = chain root)</description></item>
///   <item><description><c>[17..21)</c>  Entry count — Int32</description></item>
///   <item><description>then <c>count</c> × self-delimiting <see cref="FolderDeltaEntry"/></description></item>
/// </list>
///
/// <para>The packed payload is Zstd-compressed and encrypted under the Default policy before being
/// appended (Add entries carry subjects/senders), mirroring <see cref="FolderPage"/>; this type owns
/// only the packing.</para>
/// </summary>
public sealed class FolderDeltaLog
{
    /// <summary>Payload format version, bumped if the packed layout changes.</summary>
    public const byte FormatVersion = 1;

    private const int PreviousDeltaOffset = sizeof(byte);                             // 1
    private const int EntryCountOffset = PreviousDeltaOffset + UlidGenerator.UlidSize; // 17

    /// <summary>Bytes of fixed header before the first entry: version(1) + prev(16) + count(4).</summary>
    public const int HeaderLength = EntryCountOffset + sizeof(int);                   // 21

    private static readonly byte[] NoPrevious = new byte[UlidGenerator.UlidSize];

    private readonly byte[] _previousDeltaBlockId;
    private readonly FolderDeltaEntry[] _entries;

    private FolderDeltaLog(byte[] previousDeltaBlockId, FolderDeltaEntry[] entries)
    {
        _previousDeltaBlockId = previousDeltaBlockId;
        _entries = entries;
    }

    /// <summary>The previous (older) block in the chain (all-zero ULID = this is the chain root).</summary>
    public byte[] PreviousDeltaBlockId => (byte[])_previousDeltaBlockId.Clone();

    /// <summary>True when a previous block is linked (<see cref="PreviousDeltaBlockId"/> is non-zero).</summary>
    public bool HasPrevious => !IsZeroUlid(_previousDeltaBlockId);

    /// <summary>The buffered mutations, in append order.</summary>
    public IReadOnlyList<FolderDeltaEntry> Entries => _entries;

    /// <summary>Number of entries buffered in this block.</summary>
    public int Count => _entries.Length;

    /// <summary>
    /// Creates a delta block linking to <paramref name="previousDeltaBlockId"/> (pass
    /// <see langword="null"/> or a zero ULID for the chain root). <paramref name="entries"/> are
    /// copied and kept in the given append order.
    /// </summary>
    /// <exception cref="ArgumentException">The previous block id is the wrong size.</exception>
    public static FolderDeltaLog Create(
        IEnumerable<FolderDeltaEntry> entries, byte[]? previousDeltaBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var prev = NormalizePrevious(previousDeltaBlockId);
        return new FolderDeltaLog(prev, entries.ToArray());
    }

    /// <summary>The exact byte length <see cref="PackPayload"/> produces for this block.</summary>
    public int PayloadLength
    {
        get
        {
            int total = HeaderLength;
            foreach (var entry in _entries)
                total += entry.PackedLength;
            return total;
        }
    }

    /// <summary>Packs the block into a new byte array ready for the block pipeline (Zstd → encrypt → append).</summary>
    public byte[] PackPayload()
    {
        var buffer = new byte[PayloadLength];
        var span = buffer.AsSpan();

        span[0] = FormatVersion;
        _previousDeltaBlockId.CopyTo(span.Slice(PreviousDeltaOffset, UlidGenerator.UlidSize));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(EntryCountOffset, sizeof(int)), _entries.Length);

        int offset = HeaderLength;
        foreach (var entry in _entries)
            offset += entry.Pack(span.Slice(offset));
        return buffer;
    }

    /// <summary>
    /// Unpacks a block payload produced by <see cref="PackPayload"/>, validating the version and the
    /// entry count.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is malformed or truncated.</exception>
    public static FolderDeltaLog UnpackPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderLength)
            throw new ArgumentException(
                $"FolderDeltaLog payload needs at least {HeaderLength} header bytes, got {payload.Length}.",
                nameof(payload));

        byte version = payload[0];
        if (version != FormatVersion)
            throw new ArgumentException(
                $"Unsupported FolderDeltaLog format version {version} (expected {FormatVersion}).", nameof(payload));

        var prev = payload.Slice(PreviousDeltaOffset, UlidGenerator.UlidSize).ToArray();
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(EntryCountOffset, sizeof(int)));
        if (count < 0)
            throw new ArgumentException($"FolderDeltaLog entry count {count} is negative.", nameof(payload));

        var entries = new FolderDeltaEntry[count];
        int offset = HeaderLength;
        for (int i = 0; i < count; i++)
        {
            entries[i] = FolderDeltaEntry.Unpack(payload.Slice(offset), out int consumed);
            offset += consumed;
        }

        return new FolderDeltaLog(prev, entries);
    }

    private static byte[] NormalizePrevious(byte[]? previousDeltaBlockId)
    {
        if (previousDeltaBlockId is null)
            return NoPrevious;
        if (previousDeltaBlockId.Length != UlidGenerator.UlidSize)
            throw new ArgumentException(
                $"PreviousDeltaBlockId must be exactly {UlidGenerator.UlidSize} bytes, got {previousDeltaBlockId.Length}.",
                nameof(previousDeltaBlockId));
        return (byte[])previousDeltaBlockId.Clone();
    }

    private static bool IsZeroUlid(byte[] ulid)
    {
        foreach (var b in ulid)
            if (b != 0)
                return false;
        return true;
    }
}
