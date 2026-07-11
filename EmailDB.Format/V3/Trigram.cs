using System.Buffers.Binary;
using System.Text;

namespace EmailDB.Format.V3;

/// <summary>
/// A single trigram — three consecutive Unicode scalar values (runes) drawn from a
/// normalized address string (docs/Search.md Phase 1). Substring search over
/// addresses works by extracting the sliding-window trigrams of the indexed text
/// (<see cref="TrigramExtractor"/>) and of the query, then intersecting the trigrams'
/// posting lists; a trigram match is necessary but not sufficient, so candidates are
/// verified against Tier 1 records.
///
/// <para><b>Why runes, not bytes or chars.</b> Holding three <i>runes</i> (each a full
/// Unicode scalar, so an astral character such as an emoji is one element, not a
/// surrogate pair) makes the trigram alphabet the same as the user-visible characters:
/// "rya" over an ASCII address and "café" over an accented one both trigram cleanly,
/// and a query trigram compares equal to an indexed trigram exactly when the three
/// characters match. Normalization (NFC + case folding) is the caller's job
/// (<see cref="TrigramExtractor.Normalize"/>) and is applied before runes are read, so
/// two spellings of the same address produce identical trigrams.</para>
///
/// <para><b>On-disk form (<see cref="Size"/> = 12 bytes).</b> Three little-endian
/// <c>uint32</c> rune values (file-wide little-endian convention, spec Section 4). The
/// fixed width lets a trigram be a B+-tree key or a fixed dictionary-entry field, and
/// makes byte-wise ascending order equal <see cref="CompareTo(Trigram)"/> order for
/// the ASCII/BMP range so a sorted-by-trigram dictionary is also sorted by its raw
/// key bytes.</para>
/// </summary>
public readonly struct Trigram : IEquatable<Trigram>, IComparable<Trigram>
{
    /// <summary>Number of runes in a trigram.</summary>
    public const int RuneCount = 3;

    /// <summary>Serialized width in bytes: three little-endian uint32 rune values.</summary>
    public const int Size = RuneCount * sizeof(uint);

    /// <summary>First rune's scalar value.</summary>
    public int Rune0 { get; }

    /// <summary>Second rune's scalar value.</summary>
    public int Rune1 { get; }

    /// <summary>Third rune's scalar value.</summary>
    public int Rune2 { get; }

    /// <summary>Constructs a trigram from three Unicode scalar values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Any value is not a valid Unicode scalar (see <see cref="Rune"/>).</exception>
    public Trigram(int rune0, int rune1, int rune2)
    {
        // Validate each is a real Unicode scalar; the Rune ctor throws otherwise.
        _ = new Rune(rune0);
        _ = new Rune(rune1);
        _ = new Rune(rune2);
        Rune0 = rune0;
        Rune1 = rune1;
        Rune2 = rune2;
    }

    /// <summary>Constructs a trigram from three runes.</summary>
    public Trigram(Rune rune0, Rune rune1, Rune rune2)
    {
        Rune0 = rune0.Value;
        Rune1 = rune1.Value;
        Rune2 = rune2.Value;
    }

    /// <summary>
    /// Writes the trigram as three little-endian uint32 rune values into
    /// <paramref name="destination"/> (exactly <see cref="Size"/> bytes).
    /// </summary>
    /// <exception cref="ArgumentException">Destination is shorter than <see cref="Size"/> bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException(
                $"Trigram destination must be at least {Size} bytes, got {destination.Length}.",
                nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), (uint)Rune0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), (uint)Rune1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), (uint)Rune2);
    }

    /// <summary>Returns the 12-byte on-disk encoding as a new array.</summary>
    public byte[] GetBytes()
    {
        var bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Reads a trigram from its <see cref="Size"/>-byte on-disk encoding.</summary>
    /// <exception cref="ArgumentException">The span is not exactly <see cref="Size"/> bytes.</exception>
    public static Trigram ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length != Size)
            throw new ArgumentException(
                $"Trigram encoding must be exactly {Size} bytes, got {source.Length}.", nameof(source));
        return new Trigram(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(0, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4)));
    }

    public bool Equals(Trigram other) =>
        Rune0 == other.Rune0 && Rune1 == other.Rune1 && Rune2 == other.Rune2;

    public override bool Equals(object? obj) => obj is Trigram other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Rune0, Rune1, Rune2);

    /// <summary>Orders trigrams by rune value, matching the ascending order the dictionary is stored in.</summary>
    public int CompareTo(Trigram other)
    {
        int c = Rune0.CompareTo(other.Rune0);
        if (c != 0) return c;
        c = Rune1.CompareTo(other.Rune1);
        if (c != 0) return c;
        return Rune2.CompareTo(other.Rune2);
    }

    public static bool operator ==(Trigram left, Trigram right) => left.Equals(right);
    public static bool operator !=(Trigram left, Trigram right) => !left.Equals(right);

    /// <summary>The three characters as a string, for logging/debugging.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(RuneCount);
        sb.Append(new Rune(Rune0).ToString());
        sb.Append(new Rune(Rune1).ToString());
        sb.Append(new Rune(Rune2).ToString());
        return sb.ToString();
    }
}
