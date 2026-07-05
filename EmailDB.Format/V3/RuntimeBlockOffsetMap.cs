using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace EmailDB.Format.V3;

/// <summary>
/// Runtime BlockId-to-offset map (EmailDB_FileFormat_Spec.md Section 7): a
/// concurrent in-memory map of every block appended this session that is not
/// yet in the durable BlockLocationIndex (pre-checkpoint blocks). The append
/// path (<see cref="BlockManager.Append"/>) notifies it via
/// <see cref="OnBlockAppended"/>; readers resolve BlockIds against it first,
/// then the BlockLocationIndex, then (disaster only) a full scan.
///
/// Duplicate BlockIds are legitimate — new versions of Metadata/KeyStore/
/// directory blocks reuse a logical identity — and resolve
/// last-position-wins: the entry at the greater file offset supersedes,
/// regardless of notification arrival order.
///
/// Only the location (offset + total length) is retained, matching what the
/// BlockLocationIndex stores per Section 7; the header itself is not kept.
/// At a Checkpoint, <see cref="SnapshotOrderedByOffset"/> supplies the batch
/// of entries to insert into the BlockLocationIndex and <see cref="Clear"/>
/// drops them once the Checkpoint block is durable.
/// </summary>
public sealed class RuntimeBlockOffsetMap : IBlockOffsetMap, IBlockIdResolver
{
    private readonly ConcurrentDictionary<UlidKey, Location> _entries = new();

    /// <summary>Offset + total length of one appended block (BlockId lives in the key).</summary>
    private readonly record struct Location(long Offset, long TotalBlockLength);

    /// <summary>Number of distinct BlockIds currently tracked.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Records an appended block. Thread-safe. When the BlockId is already
    /// tracked, the entry at the greater file offset wins (last position
    /// supersedes), so concurrent or out-of-order notifications converge on
    /// the latest version.
    /// </summary>
    /// <param name="header">Header of the appended block; only its BlockId is used.</param>
    /// <param name="offset">File offset of the block's first header byte.</param>
    /// <param name="totalBlockLength">Entire on-disk block size including the footer.</param>
    public void OnBlockAppended(BlockHeader header, long offset, long totalBlockLength)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            totalBlockLength, BlockSerializer.FixedOverhead);

        var key = UlidKey.FromBlockId(header.BlockId);
        var candidate = new Location(offset, totalBlockLength);
        _entries.AddOrUpdate(
            key,
            candidate,
            // Last position wins: keep whichever entry sits later in the file.
            // (The update callback may race and rerun; it is a pure comparison.)
            (_, existing) => candidate.Offset >= existing.Offset ? candidate : existing);
    }

    /// <summary>
    /// Resolves a BlockId to its latest location (last-position-wins).
    /// Thread-safe. Returns false when the block was not appended this
    /// session — the caller then falls back to the BlockLocationIndex
    /// (resolution precedence, spec Section 7).
    /// </summary>
    /// <param name="blockId">ULID as 16 raw bytes, big-endian binary layout.</param>
    /// <param name="location">The block's latest location when found; null otherwise.</param>
    public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
    {
        if (_entries.TryGetValue(UlidKey.FromBlockId(blockId), out var entry))
        {
            location = new BlockLocation
            {
                BlockId = blockId.ToArray(),
                Offset = entry.Offset,
                TotalBlockLength = entry.TotalBlockLength,
            };
            return true;
        }

        location = null;
        return false;
    }

    /// <summary>
    /// Point-in-time snapshot of all tracked blocks ordered by file offset —
    /// the batch a Checkpoint inserts into the BlockLocationIndex
    /// (spec Section 7). Entries recorded concurrently with the snapshot may
    /// or may not be included.
    /// </summary>
    public IReadOnlyList<BlockLocation> SnapshotOrderedByOffset() =>
        _entries
            .Select(pair => new BlockLocation
            {
                BlockId = pair.Key.ToBlockId(),
                Offset = pair.Value.Offset,
                TotalBlockLength = pair.Value.TotalBlockLength,
            })
            .OrderBy(location => location.Offset)
            .ToArray();

    /// <summary>
    /// Forgets all tracked blocks. Called after a Checkpoint has durably
    /// batch-inserted them into the BlockLocationIndex, at which point the
    /// index serves lookups for them.
    /// </summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// A 16-byte ULID as a value-equatable dictionary key (two big-endian
    /// halves), so lookups need no per-call allocation or custom comparer.
    /// </summary>
    private readonly record struct UlidKey(ulong High, ulong Low)
    {
        public static UlidKey FromBlockId(ReadOnlySpan<byte> blockId)
        {
            if (blockId.Length != UlidGenerator.UlidSize)
                throw new ArgumentException(
                    $"BlockId must be {UlidGenerator.UlidSize} bytes, got {blockId.Length}.",
                    nameof(blockId));

            return new UlidKey(
                BinaryPrimitives.ReadUInt64BigEndian(blockId),
                BinaryPrimitives.ReadUInt64BigEndian(blockId[8..]));
        }

        public byte[] ToBlockId()
        {
            var bytes = new byte[UlidGenerator.UlidSize];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), Low);
            return bytes;
        }
    }
}
