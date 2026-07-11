using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// One posting in an <see cref="FtsPostingList"/>: the identity of an email whose
/// indexed addresses contain the list's trigram, plus the bitwise-OR of the
/// <see cref="AddressField"/>s in which the trigram appeared for that email
/// (docs/Search.md Phase 1, "postings distinguish the field").
/// </summary>
/// <param name="Email">The matching email's content identity (Tier 1 verification key).</param>
/// <param name="Fields">Non-empty set of fields the trigram appeared in for this email.</param>
public readonly record struct FtsPosting(EmailHashedID Email, AddressField Fields);

/// <summary>
/// One trigram-intersection candidate from <see cref="FtsIndex.FindCandidates"/> (task 91-8): an email
/// whose addresses hold every query trigram within a single requested field, tagged with the fields in
/// which the whole query trigram set co-occurs. A candidate is a <i>necessary</i> match only — the
/// query path still verifies the actual Tier 1 address text contains the query substring before
/// returning it, so trigram false positives are dropped.
/// </summary>
/// <param name="Email">The candidate email's content identity (the Tier 1 verification key).</param>
/// <param name="Fields">Requested fields in which every query trigram co-occurs (a subset of the query's field filter).</param>
public readonly record struct FtsCandidate(EmailHashedID Email, AddressField Fields);

/// <summary>
/// In-memory model of an FTSPostingList block (BlockType 16, docs/Search.md Phase 1):
/// the sorted list of emails that contain one trigram in their addresses. Posting lists
/// are the leaves of the trigram index — the query path (task 91-8) reads one per query
/// trigram and intersects them (a k-way merge over the sorted <see cref="EmailHashedID"/>
/// keys), so <b>entries are sorted strictly ascending by <see cref="FtsPosting.Email"/>
/// with no duplicate email</b>. Always encrypted (spec Section 9.5) — a trigram plus its
/// posting list reverses to the indexed addresses.
///
/// <para>Layout (serialized by <see cref="FtsPostingListSerializer"/>, little-endian per
/// spec Section 4):</para>
/// <code>
///   Trigram    (12)                     — the trigram this list is the posting list for
///   EntryCount (uint32, 4)
///   Entries[]  ({ EmailHashedID (32) + Fields (byte, 1) } × EntryCount)  — asc by Email
/// </code>
/// </summary>
public sealed class FtsPostingList
{
    /// <summary>The trigram whose occurrences this list records.</summary>
    public required Trigram Trigram { get; init; }

    /// <summary>Postings, strictly ascending by <see cref="FtsPosting.Email"/>, each with a non-empty field set.</summary>
    public required IReadOnlyList<FtsPosting> Postings { get; init; }

    /// <summary>Number of emails in the list.</summary>
    public int Count => Postings.Count;

    /// <summary>
    /// Builds a posting list from an arbitrary-order set of (email, fields) pairs,
    /// sorting by email and OR-merging the fields of any email that appears more than
    /// once so the result satisfies the strictly-ascending, non-duplicate invariant.
    /// A posting with an empty field set is rejected.
    /// </summary>
    /// <exception cref="ArgumentException">A posting has <see cref="AddressField.None"/> fields.</exception>
    public static FtsPostingList FromPostings(Trigram trigram, IEnumerable<FtsPosting> postings)
    {
        ArgumentNullException.ThrowIfNull(postings);
        var merged = new SortedDictionary<EmailHashedID, AddressField>();
        foreach (var p in postings)
        {
            if (p.Fields == AddressField.None)
                throw new ArgumentException(
                    $"Posting for email {p.Email} has no fields set; every posting must name at least one field.",
                    nameof(postings));
            merged[p.Email] = merged.TryGetValue(p.Email, out var existing)
                ? existing | p.Fields
                : p.Fields;
        }
        var list = new FtsPosting[merged.Count];
        int i = 0;
        foreach (var kv in merged)
            list[i++] = new FtsPosting(kv.Key, kv.Value);
        return new FtsPostingList { Trigram = trigram, Postings = list };
    }
}

/// <summary>
/// Serializes the <see cref="FtsPostingList"/> payload (BlockType 16). Follows the
/// <see cref="CleanupSerializer"/> idiom: <see cref="Serialize"/> throws on invariant
/// violations (a caller-side programming error) while <see cref="Deserialize"/>
/// bounds-checks the declared entry count against the payload length before reading a
/// body byte and enforces the strictly-ascending / non-empty-fields invariants, so a
/// truncated or corrupt block fails rather than yielding an unsorted list that would
/// break the query-path merge.
/// </summary>
public static class FtsPostingListSerializer
{
    /// <summary>Bytes of one posting entry: EmailHashedID (32) + Fields (1).</summary>
    public const int EntrySize = EmailHashedID.Size + 1;

    /// <summary>Bytes of the fixed prefix: Trigram (12) + EntryCount (4).</summary>
    public const int FixedPrefixSize = Trigram.Size + sizeof(uint);

    /// <summary>Serialized length of a posting list with <paramref name="entryCount"/> entries.</summary>
    public static int PayloadSize(int entryCount) => FixedPrefixSize + entryCount * EntrySize;

    /// <exception cref="ArgumentException">Postings are null, out-of-order, duplicated, or have empty fields.</exception>
    public static byte[] Serialize(FtsPostingList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        IReadOnlyList<FtsPosting> postings = list.Postings
            ?? throw new ArgumentException($"{nameof(FtsPostingList.Postings)} must not be null.", nameof(list));

        var buffer = new byte[PayloadSize(postings.Count)];
        var span = buffer.AsSpan();
        list.Trigram.WriteTo(span.Slice(0, Trigram.Size));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(Trigram.Size, 4), (uint)postings.Count);

        int cursor = FixedPrefixSize;
        for (int i = 0; i < postings.Count; i++)
        {
            var p = postings[i];
            if (p.Fields == AddressField.None)
                throw new ArgumentException(
                    $"{nameof(FtsPostingList.Postings)}[{i}] has no fields set.", nameof(list));
            if (i > 0 && p.Email.CompareTo(postings[i - 1].Email) <= 0)
                throw new ArgumentException(
                    $"{nameof(FtsPostingList.Postings)} must be strictly ascending by Email; " +
                    $"entry [{i}] is not greater than [{i - 1}].", nameof(list));

            var entry = span.Slice(cursor, EntrySize);
            p.Email.WriteTo(entry.Slice(0, EmailHashedID.Size));
            entry[EmailHashedID.Size] = (byte)p.Fields;
            cursor += EntrySize;
        }
        return buffer;
    }

    public static Result<FtsPostingList> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPrefixSize)
            return Result<FtsPostingList>.Failure(
                $"FTS posting list payload must be at least {FixedPrefixSize} bytes, got {payload.Length}.");

        var trigram = Trigram.ReadFrom(payload.Slice(0, Trigram.Size));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(Trigram.Size, 4));
        long expected = (long)FixedPrefixSize + (long)entryCount * EntrySize;
        if (payload.Length != expected)
            return Result<FtsPostingList>.Failure(
                $"FTS posting list length {payload.Length} does not match declared EntryCount {entryCount} " +
                $"(expected {expected} bytes; truncated, oversized, or corrupt).");

        var postings = new FtsPosting[entryCount];
        int cursor = FixedPrefixSize;
        EmailHashedID previous = default;
        for (int i = 0; i < entryCount; i++)
        {
            var entry = payload.Slice(cursor, EntrySize);
            var email = new EmailHashedID(entry.Slice(0, EmailHashedID.Size));
            var fields = (AddressField)entry[EmailHashedID.Size];
            if (fields == AddressField.None)
                return Result<FtsPostingList>.Failure(
                    $"FTS posting list entry [{i}] has no fields set (corrupt payload).");
            if (i > 0 && email.CompareTo(previous) <= 0)
                return Result<FtsPostingList>.Failure(
                    $"FTS posting list entry [{i}] is not strictly greater than the previous email " +
                    "(unsorted or duplicate; corrupt payload).");
            postings[i] = new FtsPosting(email, fields);
            previous = email;
            cursor += EntrySize;
        }

        return Result<FtsPostingList>.Success(new FtsPostingList { Trigram = trigram, Postings = postings });
    }
}
