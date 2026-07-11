using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>One term-dictionary entry: a trigram and the ULID of its posting-list block.</summary>
/// <param name="Trigram">The indexed trigram.</param>
/// <param name="PostingListBlockId">16-byte ULID of the <see cref="FtsPostingList"/> block for this trigram.</param>
public readonly record struct FtsTermEntry(Trigram Trigram, byte[] PostingListBlockId);

/// <summary>
/// In-memory model of an FTSTermDictionary block (BlockType 15, docs/Search.md Phase 1):
/// maps each trigram present in a segment to the block holding that trigram's posting
/// list. It is the segment's index — the query path (task 91-8) binary-searches it for a
/// query trigram, so <b>entries are sorted strictly ascending by <see cref="Trigram"/>
/// with no duplicate trigram</b>. Always encrypted (spec Section 9.5) — the set of
/// trigrams alone leaks the indexed text.
///
/// <para>Layout (serialized by <see cref="FtsTermDictionarySerializer"/>, little-endian
/// per spec Section 4):</para>
/// <code>
///   EntryCount (uint32, 4)
///   Entries[]  ({ Trigram (12) + PostingListBlockId (16) } × EntryCount)  — asc by Trigram
/// </code>
/// </summary>
public sealed class FtsTermDictionary
{
    /// <summary>Number of raw bytes in a block ULID.</summary>
    public const int BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>Entries, strictly ascending by <see cref="FtsTermEntry.Trigram"/>.</summary>
    public required IReadOnlyList<FtsTermEntry> Entries { get; init; }

    /// <summary>Number of distinct trigrams in the segment.</summary>
    public int Count => Entries.Count;

    /// <summary>
    /// Builds a dictionary from a trigram → posting-list-block-id map, sorting by trigram
    /// so the strictly-ascending invariant holds. Validates every block id width.
    /// </summary>
    /// <exception cref="ArgumentException">A posting-list block id is not <see cref="BlockIdSize"/> bytes.</exception>
    public static FtsTermDictionary FromMap(IEnumerable<KeyValuePair<Trigram, byte[]>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var sorted = new SortedDictionary<Trigram, byte[]>();
        foreach (var kv in map)
        {
            if (kv.Value is null || kv.Value.Length != BlockIdSize)
                throw new ArgumentException(
                    $"Posting-list block id for trigram '{kv.Key}' must be exactly {BlockIdSize} bytes.", nameof(map));
            sorted[kv.Key] = (byte[])kv.Value.Clone();
        }
        var entries = new FtsTermEntry[sorted.Count];
        int i = 0;
        foreach (var kv in sorted)
            entries[i++] = new FtsTermEntry(kv.Key, kv.Value);
        return new FtsTermDictionary { Entries = entries };
    }
}

/// <summary>
/// Serializes the <see cref="FtsTermDictionary"/> payload (BlockType 15), following the
/// <see cref="CleanupSerializer"/> idiom: <see cref="Deserialize"/> bounds-checks the
/// declared count against the payload length and enforces strictly-ascending trigram
/// order and block-id width before trusting the block, so a corrupt dictionary fails
/// rather than mis-routing the query path to a wrong posting block.
/// </summary>
public static class FtsTermDictionarySerializer
{
    /// <summary>Bytes of one entry: Trigram (12) + PostingListBlockId (16).</summary>
    public const int EntrySize = Trigram.Size + FtsTermDictionary.BlockIdSize;

    /// <summary>Bytes of the fixed prefix: EntryCount (4).</summary>
    public const int FixedPrefixSize = sizeof(uint);

    /// <summary>Serialized length of a dictionary with <paramref name="entryCount"/> entries.</summary>
    public static int PayloadSize(int entryCount) => FixedPrefixSize + entryCount * EntrySize;

    /// <exception cref="ArgumentException">Entries are null, out-of-order, duplicated, or have a bad block-id width.</exception>
    public static byte[] Serialize(FtsTermDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        IReadOnlyList<FtsTermEntry> entries = dictionary.Entries
            ?? throw new ArgumentException($"{nameof(FtsTermDictionary.Entries)} must not be null.", nameof(dictionary));

        var buffer = new byte[PayloadSize(entries.Count)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), (uint)entries.Count);

        int cursor = FixedPrefixSize;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.PostingListBlockId is null || e.PostingListBlockId.Length != FtsTermDictionary.BlockIdSize)
                throw new ArgumentException(
                    $"{nameof(FtsTermDictionary.Entries)}[{i}].PostingListBlockId must be exactly " +
                    $"{FtsTermDictionary.BlockIdSize} bytes.", nameof(dictionary));
            if (i > 0 && e.Trigram.CompareTo(entries[i - 1].Trigram) <= 0)
                throw new ArgumentException(
                    $"{nameof(FtsTermDictionary.Entries)} must be strictly ascending by Trigram; " +
                    $"entry [{i}] is not greater than [{i - 1}].", nameof(dictionary));

            var entry = span.Slice(cursor, EntrySize);
            e.Trigram.WriteTo(entry.Slice(0, Trigram.Size));
            e.PostingListBlockId.CopyTo(entry.Slice(Trigram.Size, FtsTermDictionary.BlockIdSize));
            cursor += EntrySize;
        }
        return buffer;
    }

    public static Result<FtsTermDictionary> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPrefixSize)
            return Result<FtsTermDictionary>.Failure(
                $"FTS term dictionary payload must be at least {FixedPrefixSize} bytes, got {payload.Length}.");

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        long expected = (long)FixedPrefixSize + (long)entryCount * EntrySize;
        if (payload.Length != expected)
            return Result<FtsTermDictionary>.Failure(
                $"FTS term dictionary length {payload.Length} does not match declared EntryCount {entryCount} " +
                $"(expected {expected} bytes; truncated, oversized, or corrupt).");

        var entries = new FtsTermEntry[entryCount];
        int cursor = FixedPrefixSize;
        Trigram previous = default;
        for (int i = 0; i < entryCount; i++)
        {
            var entry = payload.Slice(cursor, EntrySize);
            var trigram = Trigram.ReadFrom(entry.Slice(0, Trigram.Size));
            if (i > 0 && trigram.CompareTo(previous) <= 0)
                return Result<FtsTermDictionary>.Failure(
                    $"FTS term dictionary entry [{i}] is not strictly greater than the previous trigram " +
                    "(unsorted or duplicate; corrupt payload).");
            var blockId = entry.Slice(Trigram.Size, FtsTermDictionary.BlockIdSize).ToArray();
            entries[i] = new FtsTermEntry(trigram, blockId);
            previous = trigram;
            cursor += EntrySize;
        }

        return Result<FtsTermDictionary>.Success(new FtsTermDictionary { Entries = entries });
    }
}
