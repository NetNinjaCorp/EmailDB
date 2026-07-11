using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// A classic bit-array Bloom filter (docs/Search.md Phase 5) — the probabilistic
/// existence test behind the per-folder search filters (BlockType 18). It answers "is
/// this token DEFINITELY absent?" with certainty and "is it maybe present?" with a
/// bounded false-positive rate, and it has <b>no false negatives</b>: a token that was
/// added always tests present. That one-sidedness is what makes it a sound skip test —
/// a negative membership result means the token was never added, so a folder whose
/// filter reports every query token absent cannot possibly match and may be skipped
/// without a page scan.
///
/// <para><b>Sizing.</b> <see cref="Build"/> sizes the bit count <c>m</c> and hash count
/// <c>k</c> from the number of distinct tokens <c>n</c> and a target false-positive rate
/// <c>p</c> using the standard optima <c>m = ceil(-n·ln p / (ln 2)²)</c> and
/// <c>k = round((m/n)·ln 2)</c> (for the default <c>p = 0.01</c> that is ≈9.6 bits/token
/// and <c>k = 7</c>). An empty token set yields a minimal all-zero filter that reports
/// every token absent.</para>
///
/// <para><b>Hashing.</b> The <c>k</c> bit positions for a token are derived from its
/// single 64-bit hash by enhanced double hashing — <c>pos_i = (h1 + i·h2) mod m</c> with
/// <c>h1</c>/<c>h2</c> the low/high halves (h2 forced odd) — so a token needs only one
/// hash computed by the caller (<see cref="ListingBloomTokens"/>). Not thread-safe for
/// concurrent mutation; immutable once built.</para>
/// </summary>
public sealed class BloomFilter
{
    private const double Ln2 = 0.6931471805599453;
    private const double Ln2Squared = Ln2 * Ln2;

    /// <summary>The default target false-positive rate (docs/Search.md Phase 5 — "~1%").</summary>
    public const double DefaultFalsePositiveRate = 0.01;

    private readonly byte[] _bits;

    private BloomFilter(int bitCount, int hashCount, long tokenCount, byte[] bits)
    {
        BitCount = bitCount;
        HashCount = hashCount;
        TokenCount = tokenCount;
        _bits = bits;
    }

    /// <summary>Number of bits in the filter (<c>m</c>); always at least 1.</summary>
    public int BitCount { get; }

    /// <summary>Number of hash probes per token (<c>k</c>); always at least 1.</summary>
    public int HashCount { get; }

    /// <summary>Distinct tokens the filter was built over (<c>n</c>) — informational (sizing input).</summary>
    public long TokenCount { get; }

    /// <summary>Number of whole bytes backing the bit array (<c>ceil(m/8)</c>).</summary>
    public int ByteLength => _bits.Length;

    /// <summary>
    /// Computes the space-optimal bit count <c>m</c> and hash count <c>k</c> for
    /// <paramref name="distinctTokenCount"/> tokens at target false-positive rate
    /// <paramref name="falsePositiveRate"/>. A zero token count returns the minimal shape
    /// (<c>m = 1</c>, <c>k = 1</c>) so an empty folder still yields a usable all-zero filter.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distinctTokenCount"/> is negative, or the rate is not in (0,1).</exception>
    public static (int BitCount, int HashCount) Optimal(long distinctTokenCount, double falsePositiveRate = DefaultFalsePositiveRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distinctTokenCount);
        if (!(falsePositiveRate > 0.0 && falsePositiveRate < 1.0))
            throw new ArgumentOutOfRangeException(
                nameof(falsePositiveRate), falsePositiveRate, "The target false-positive rate must be in the open interval (0, 1).");

        if (distinctTokenCount == 0)
            return (1, 1);

        double m = Math.Ceiling(-distinctTokenCount * Math.Log(falsePositiveRate) / Ln2Squared);
        // Clamp to a sane, non-overflowing bit budget; k from the realized m/n.
        long bitCount = (long)Math.Min(m, int.MaxValue - 64);
        if (bitCount < 1) bitCount = 1;
        int hashCount = (int)Math.Round((double)bitCount / distinctTokenCount * Ln2);
        if (hashCount < 1) hashCount = 1;
        if (hashCount > 64) hashCount = 64; // beyond this the marginal FP gain is nil; caps probe cost
        return ((int)bitCount, hashCount);
    }

    /// <summary>
    /// Builds a filter over the DISTINCT token hashes in <paramref name="tokenHashes"/>, sized for
    /// <paramref name="falsePositiveRate"/> (default ~1%). Callers hash each Tier 1 token to a 64-bit
    /// value (<see cref="ListingBloomTokens"/>); duplicates are folded by the set, so the realized
    /// false-positive rate matches the sizing math. An empty input builds a minimal all-zero filter.
    /// </summary>
    public static BloomFilter Build(IEnumerable<ulong> tokenHashes, double falsePositiveRate = DefaultFalsePositiveRate)
    {
        ArgumentNullException.ThrowIfNull(tokenHashes);
        var distinct = tokenHashes as HashSet<ulong> ?? new HashSet<ulong>(tokenHashes);

        var (bitCount, hashCount) = Optimal(distinct.Count, falsePositiveRate);
        var bits = new byte[(bitCount + 7) / 8];
        var filter = new BloomFilter(bitCount, hashCount, distinct.Count, bits);
        foreach (var hash in distinct)
            filter.SetHash(hash);
        return filter;
    }

    /// <summary>
    /// Whether <paramref name="tokenHash"/> <b>might</b> be present: true when every one of the token's
    /// <c>k</c> derived bits is set (a real member, or a false positive), false when any is clear (a
    /// certain non-member). A false result is authoritative — the token was never added.
    /// </summary>
    public bool MightContainHash(ulong tokenHash)
    {
        Split(tokenHash, out uint h1, out uint h2);
        ulong pos = h1;
        for (int i = 0; i < HashCount; i++)
        {
            if (!GetBit((int)(pos % (uint)BitCount)))
                return false;
            pos += h2;
        }
        return true;
    }

    private void SetHash(ulong tokenHash)
    {
        Split(tokenHash, out uint h1, out uint h2);
        ulong pos = h1;
        for (int i = 0; i < HashCount; i++)
        {
            SetBit((int)(pos % (uint)BitCount));
            pos += h2;
        }
    }

    private static void Split(ulong tokenHash, out uint h1, out uint h2)
    {
        h1 = (uint)(tokenHash & 0xFFFFFFFF);
        h2 = (uint)(tokenHash >> 32);
        h2 |= 1; // force odd so the arithmetic progression can reach every residue class mod m
    }

    private bool GetBit(int index) => (_bits[index >> 3] & (1 << (index & 7))) != 0;
    private void SetBit(int index) => _bits[index >> 3] |= (byte)(1 << (index & 7));

    // ------------------------------------------------------------- Serialization

    /// <summary>Bytes of the fixed header: BitCount(4) + HashCount(4) + TokenCount(8) + ByteLength(4).</summary>
    public const int HeaderSize = 4 + 4 + 8 + 4;

    /// <summary>Serialized byte length of this filter (header + bit array).</summary>
    public int SerializedLength => HeaderSize + _bits.Length;

    /// <summary>Serializes the filter (header + bit array) into a right-sized array (little-endian, spec Section 4).</summary>
    public byte[] Serialize()
    {
        var buffer = new byte[SerializedLength];
        WriteTo(buffer);
        return buffer;
    }

    /// <summary>Serializes into <paramref name="destination"/>, returning the bytes written.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="SerializedLength"/>.</exception>
    public int WriteTo(Span<byte> destination)
    {
        if (destination.Length < SerializedLength)
            throw new ArgumentException(
                $"Destination must be at least {SerializedLength} bytes, got {destination.Length}.", nameof(destination));
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(0, 4), BitCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), HashCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8, 8), TokenCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(16, 4), _bits.Length);
        _bits.CopyTo(destination.Slice(HeaderSize, _bits.Length));
        return SerializedLength;
    }

    /// <summary>
    /// Deserializes a filter from the front of <paramref name="source"/>, reporting how many bytes it
    /// consumed via <paramref name="bytesConsumed"/> (so a catalog can read the next entry).
    /// </summary>
    public static Result<BloomFilter> Deserialize(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        bytesConsumed = 0;
        if (source.Length < HeaderSize)
            return Result<BloomFilter>.Failure(
                $"Bloom filter payload must be at least {HeaderSize} bytes, got {source.Length}.");

        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(0, 4));
        int hashCount = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
        long tokenCount = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8, 8));
        int byteLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(16, 4));

        if (bitCount < 1 || hashCount < 1 || tokenCount < 0 || byteLength < 0)
            return Result<BloomFilter>.Failure(
                $"Bloom filter header is malformed (bits={bitCount}, hashes={hashCount}, tokens={tokenCount}, bytes={byteLength}).");
        if (byteLength != (bitCount + 7) / 8)
            return Result<BloomFilter>.Failure(
                $"Bloom filter byte length {byteLength} does not match its bit count {bitCount} (corrupt).");
        if (source.Length < HeaderSize + byteLength)
            return Result<BloomFilter>.Failure(
                $"Bloom filter truncated: need {HeaderSize + byteLength} bytes, got {source.Length}.");

        var bits = source.Slice(HeaderSize, byteLength).ToArray();
        bytesConsumed = HeaderSize + byteLength;
        return Result<BloomFilter>.Success(new BloomFilter(bitCount, hashCount, tokenCount, bits));
    }
}
