using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// Monotonic ULID generator for v3 block IDs (EmailDB_FileFormat_Spec.md Section 4.2).
///
/// Each ULID is 128 bits: a 48-bit millisecond UTC timestamp followed by 80 bits of
/// CSPRNG randomness, laid out as 16 big-endian bytes so that byte-wise comparison
/// equals chronological order.
///
/// The generator runs in monotonic-increment mode: within the same millisecond, or if
/// the wall clock moves backward (clock regression), it increments the previously
/// issued ULID's random part instead of emitting an out-of-order ID. If the 80-bit
/// random part overflows, the timestamp field is advanced by one millisecond and the
/// random part is re-seeded. A ULID returned by <see cref="Next()"/> therefore always
/// compares strictly greater (as raw bytes) than every ULID issued before it by the
/// same instance.
///
/// Thread-safe: all state is guarded by a single lock.
/// </summary>
public sealed class UlidGenerator
{
    /// <summary>Size of a ULID in bytes.</summary>
    public const int UlidSize = 16;

    /// <summary>Size of the big-endian millisecond timestamp prefix in bytes (48 bits).</summary>
    public const int TimestampSize = 6;

    /// <summary>Size of the random suffix in bytes (80 bits).</summary>
    public const int RandomSize = 10;

    /// <summary>Largest millisecond timestamp representable in 48 bits (year 10889).</summary>
    public const long MaxTimestampMs = (1L << 48) - 1;

    private readonly Func<long> _clock;
    private readonly Action<byte[]> _fillRandom;
    private readonly object _lock = new();

    /// <summary>Timestamp field of the last issued ULID; -1 before the first ULID.</summary>
    private long _lastTimestampMs = -1;

    /// <summary>Random field of the last issued ULID (big-endian significance).</summary>
    private readonly byte[] _lastRandom = new byte[RandomSize];

    /// <summary>Creates a generator using the system UTC clock and a CSPRNG.</summary>
    public UlidGenerator()
        : this(static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    {
    }

    /// <summary>
    /// Creates a generator with an injectable clock (and optionally an injectable
    /// randomness source, intended for tests only).
    /// </summary>
    /// <param name="clock">Returns the current time as Unix milliseconds (UTC).</param>
    /// <param name="fillRandom">
    /// Fills the given buffer with random bytes; defaults to
    /// <see cref="RandomNumberGenerator.Fill(Span{byte})"/>.
    /// </param>
    public UlidGenerator(Func<long> clock, Action<byte[]>? fillRandom = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fillRandom = fillRandom ?? (static buffer => RandomNumberGenerator.Fill(buffer));
    }

    /// <summary>Generates the next ULID as a new 16-byte array.</summary>
    public byte[] Next()
    {
        var ulid = new byte[UlidSize];
        Next(ulid);
        return ulid;
    }

    /// <summary>Generates the next ULID into <paramref name="destination"/> (16 bytes).</summary>
    /// <exception cref="ArgumentException">Destination is not exactly 16 bytes.</exception>
    /// <exception cref="InvalidOperationException">
    /// The clock reports a timestamp outside the 48-bit range, or the timestamp field
    /// can no longer be advanced without overflowing 48 bits.
    /// </exception>
    public void Next(Span<byte> destination)
    {
        if (destination.Length != UlidSize)
            throw new ArgumentException(
                $"ULID destination must be exactly {UlidSize} bytes, got {destination.Length}.",
                nameof(destination));

        lock (_lock)
        {
            long now = _clock();
            if (now < 0 || now > MaxTimestampMs)
                throw new InvalidOperationException(
                    $"Clock value {now} ms is outside the 48-bit ULID timestamp range.");

            if (now > _lastTimestampMs)
            {
                // Time moved forward: fresh timestamp, fresh randomness.
                _lastTimestampMs = now;
                _fillRandom(_lastRandom);
            }
            else
            {
                // Same millisecond, or the clock moved backward: keep issuing from the
                // last ULID's position so ordering never regresses.
                if (!TryIncrementRandom())
                {
                    // 80-bit random part overflowed: advance the timestamp field by one
                    // millisecond and re-seed. Still strictly greater than the last ULID.
                    if (_lastTimestampMs >= MaxTimestampMs)
                        throw new InvalidOperationException(
                            "ULID timestamp field overflowed 48 bits; cannot stay monotonic.");
                    _lastTimestampMs++;
                    _fillRandom(_lastRandom);
                }
            }

            WriteCurrent(destination);
        }
    }

    /// <summary>
    /// Increments the 80-bit random part as a big-endian unsigned integer.
    /// Returns false if it wrapped around to zero (overflow).
    /// </summary>
    private bool TryIncrementRandom()
    {
        for (int i = RandomSize - 1; i >= 0; i--)
        {
            if (++_lastRandom[i] != 0)
                return true;
        }
        return false;
    }

    /// <summary>Writes the current (timestamp, random) state as 16 big-endian bytes.</summary>
    private void WriteCurrent(Span<byte> destination)
    {
        long ts = _lastTimestampMs;
        destination[0] = (byte)(ts >> 40);
        destination[1] = (byte)(ts >> 32);
        destination[2] = (byte)(ts >> 24);
        destination[3] = (byte)(ts >> 16);
        destination[4] = (byte)(ts >> 8);
        destination[5] = (byte)ts;
        _lastRandom.CopyTo(destination[TimestampSize..]);
    }
}
