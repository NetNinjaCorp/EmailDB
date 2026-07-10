using System;
using System.Buffers.Binary;
using DZen.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// Canonical content identity for an email (EmailDB_FileFormat_Spec.md Sections 5-6,
/// docs/Folder_Listing.md, docs/Sync.md).
///
/// An <see cref="EmailHashedID"/> is the <b>SHA3-256 (FIPS-202) digest of an email's
/// canonical content bytes</b>. It is the key of the PrimaryEmail B+-tree
/// (<see cref="BTreeIndexKind.PrimaryEmail"/>): EmailHashedID (32) → BlockId (16). Two
/// ingests of the same message produce the same ID, so an insert into the primary index
/// is idempotent and content-addressed — this is what makes EmailContent blocks
/// conflict-free during sync/merge.
///
/// <para><b>Canonical content = the raw MIME bytes.</b> The hash input is the exact,
/// unmodified RFC 5322 message octet sequence that is stored (before compression and
/// before encryption) in the Tier 3 EmailContent block (BlockType 7). No re-serialization,
/// header reordering, whitespace folding, charset transcoding, or line-ending
/// normalization is performed: the identity is a hash of bytes, never of a parsed object.
/// Hashing the stored bytes verbatim is what makes the ID deterministic and stable across
/// sessions, machines, and runtime versions — there is no parser or serializer in the
/// hash path that could drift.</para>
///
/// <para><b>Stable serialization rules (normative):</b>
/// <list type="number">
/// <item>The digest input is the raw MIME byte sequence exactly as received/stored, with
///   no transformation of any kind.</item>
/// <item>The digest algorithm is SHA3-256 (Keccak, FIPS-202 padding), output length 32
///   bytes.</item>
/// <item>The 32 output bytes are stored in the natural big-endian digest order. Byte 0 of
///   the digest is the most significant byte of the identity, so byte-wise lexicographic
///   comparison of two IDs (as the B+-tree compares keys) equals
///   <see cref="CompareTo(EmailHashedID)"/>.</item>
/// <item>The on-disk / on-wire representation is always these 32 raw bytes
///   (<see cref="WriteTo"/> / <see cref="GetBytes"/>). Textual forms
///   (<see cref="ToString"/>) are for logging/debugging only and never feed the hash.</item>
/// </list>
/// </para>
/// </summary>
public readonly struct EmailHashedID : IEquatable<EmailHashedID>, IComparable<EmailHashedID>
{
    /// <summary>Size of an EmailHashedID in bytes (SHA3-256 output width).</summary>
    public const int Size = 32;

    // The 32 digest bytes held as four big-endian 64-bit words. Storing big-endian
    // preserves digest byte order exactly and makes unsigned word comparison equal to
    // byte-wise lexicographic comparison of the raw digest.
    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;

    /// <summary>The all-zero identity, used as a sentinel for "not computed".</summary>
    public static readonly EmailHashedID Empty = default;

    /// <summary>True when this is the all-zero sentinel identity.</summary>
    public bool IsEmpty => (_w0 | _w1 | _w2 | _w3) == 0;

    private EmailHashedID(ulong w0, ulong w1, ulong w2, ulong w3)
    {
        _w0 = w0;
        _w1 = w1;
        _w2 = w2;
        _w3 = w3;
    }

    /// <summary>
    /// Wraps 32 pre-computed digest bytes (e.g. read from a B+-tree key) without hashing.
    /// </summary>
    /// <exception cref="ArgumentException">The span is not exactly 32 bytes.</exception>
    public EmailHashedID(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Size)
            throw new ArgumentException(
                $"EmailHashedID must be exactly {Size} bytes, got {digest.Length}.", nameof(digest));

        _w0 = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(0, 8));
        _w1 = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(8, 8));
        _w2 = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(16, 8));
        _w3 = BinaryPrimitives.ReadUInt64BigEndian(digest.Slice(24, 8));
    }

    /// <summary>
    /// Computes the canonical identity of an email from its raw MIME content bytes.
    /// This is the one and only content-hash entry point; see the type documentation for
    /// the stable serialization rules that govern the input.
    /// </summary>
    /// <param name="rawMimeContent">
    /// The exact RFC 5322 message octets that are (or will be) stored in the Tier 3
    /// EmailContent block, before compression and encryption.
    /// </param>
    public static EmailHashedID ComputeFromRawContent(ReadOnlySpan<byte> rawMimeContent)
    {
        Span<byte> digest = stackalloc byte[Size];
        using (var sha3 = SHA3.Create())
        {
            // DZen SHA3.Create() defaults to SHA3-256 (32-byte FIPS-202 digest).
            byte[] hash = sha3.ComputeHash(rawMimeContent.ToArray());
            if (hash.Length != Size)
                throw new InvalidOperationException(
                    $"Expected a {Size}-byte SHA3-256 digest, got {hash.Length} bytes.");
            hash.CopyTo(digest);
        }

        return new EmailHashedID(digest);
    }

    /// <summary>Writes the 32 digest bytes into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException">Destination is shorter than 32 bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException(
                $"Destination must be at least {Size} bytes, got {destination.Length}.",
                nameof(destination));

        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(0, 8), _w0);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), _w1);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16, 8), _w2);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24, 8), _w3);
    }

    /// <summary>Returns the 32 digest bytes as a new array (the canonical B+-tree key).</summary>
    public byte[] GetBytes()
    {
        var bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Lowercase hex form of the digest, for logging/debugging only.</summary>
    public override string ToString() => Convert.ToHexStringLower(GetBytes());

    /// <summary>Parses a 64-character hex string produced by <see cref="ToString"/>.</summary>
    public static EmailHashedID FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var bytes = Convert.FromHexString(hex);
        return new EmailHashedID(bytes);
    }

    public bool Equals(EmailHashedID other) =>
        _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    public override bool Equals(object? obj) => obj is EmailHashedID other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);

    /// <summary>
    /// Orders IDs identically to byte-wise lexicographic comparison of their 32-byte
    /// digests, matching how the B+-tree compares PrimaryEmail keys.
    /// </summary>
    public int CompareTo(EmailHashedID other)
    {
        int c = _w0.CompareTo(other._w0);
        if (c != 0) return c;
        c = _w1.CompareTo(other._w1);
        if (c != 0) return c;
        c = _w2.CompareTo(other._w2);
        if (c != 0) return c;
        return _w3.CompareTo(other._w3);
    }

    public static bool operator ==(EmailHashedID left, EmailHashedID right) => left.Equals(right);
    public static bool operator !=(EmailHashedID left, EmailHashedID right) => !left.Equals(right);

    // ------------------------------------------------------------------- Dedupe

    /// <summary>
    /// Dedupe lookup helper. Computes the canonical identity of <paramref name="rawMimeContent"/>
    /// and asks <paramref name="primaryIndexContains"/> whether the PrimaryEmail index already
    /// holds it, so callers can skip re-storing an EmailContent block that is already present.
    ///
    /// Callers wire <paramref name="primaryIndexContains"/> to the PrimaryEmail B+-tree, e.g.
    /// <c>id =&gt; tree.TryGet(root, id.AsKey(buf)).Value.Found</c>. Keeping the index access as a
    /// delegate lets the identity type stay independent of any particular index implementation.
    /// </summary>
    /// <param name="rawMimeContent">Raw MIME bytes of the candidate email.</param>
    /// <param name="primaryIndexContains">Predicate: does the primary index already contain this ID?</param>
    /// <param name="id">The computed identity (set whether or not a duplicate was found).</param>
    /// <returns><c>true</c> if the email is already stored (a duplicate); otherwise <c>false</c>.</returns>
    public static bool IsDuplicate(
        ReadOnlySpan<byte> rawMimeContent,
        Func<EmailHashedID, bool> primaryIndexContains,
        out EmailHashedID id)
    {
        ArgumentNullException.ThrowIfNull(primaryIndexContains);
        id = ComputeFromRawContent(rawMimeContent);
        return primaryIndexContains(id);
    }
}
