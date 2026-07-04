using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 monotonic ULID generator (EmailDB_FileFormat_Spec.md Section 4.2):
/// 48-bit big-endian millisecond timestamp + 80-bit CSPRNG randomness, byte-comparable
/// ordering, monotonic-increment mode under same-millisecond and clock-regression
/// conditions (including random-part overflow), and thread safety.
/// </summary>
public class UlidGeneratorTests
{
    /// <summary>Compares two ULIDs as raw big-endian bytes (lexicographic).</summary>
    private static int CompareBytes(byte[] a, byte[] b) =>
        a.AsSpan().SequenceCompareTo(b);

    /// <summary>Reads the 48-bit big-endian timestamp prefix of a ULID.</summary>
    private static long ReadTimestamp(byte[] ulid) =>
        ((long)ulid[0] << 40) | ((long)ulid[1] << 32) | ((long)ulid[2] << 24) |
        ((long)ulid[3] << 16) | ((long)ulid[4] << 8) | ulid[5];

    // ---- Layout / endianness -------------------------------------------------

    [Fact]
    public void Next_Produces16ByteUlid()
    {
        var generator = new UlidGenerator();
        Assert.Equal(16, generator.Next().Length);
    }

    [Fact]
    public void TimestampIsStoredBigEndianInFirst6Bytes()
    {
        // 0x0123456789AB ms — every timestamp byte distinct so endianness mistakes show.
        const long timestampMs = 0x0123456789AB;
        var generator = new UlidGenerator(() => timestampMs);

        var ulid = generator.Next();

        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB }, ulid[..6]);
        Assert.Equal(timestampMs, ReadTimestamp(ulid));
    }

    [Fact]
    public void RandomPartOccupiesLast10Bytes()
    {
        var filled = new byte[]
        {
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9,
        };
        var generator = new UlidGenerator(() => 5, buffer => filled.CopyTo(buffer, 0));

        var ulid = generator.Next();

        Assert.Equal(filled, ulid[6..]);
    }

    [Fact]
    public void Next_IntoSpan_RejectsWrongLength()
    {
        var generator = new UlidGenerator();
        Assert.Throws<ArgumentException>(() => generator.Next(new byte[15]));
        Assert.Throws<ArgumentException>(() => generator.Next(new byte[17]));
    }

    [Fact]
    public void ClockOutside48BitRangeIsRejected()
    {
        var negative = new UlidGenerator(() => -1);
        Assert.Throws<InvalidOperationException>(() => negative.Next());

        var tooLarge = new UlidGenerator(() => 1L << 48);
        Assert.Throws<InvalidOperationException>(() => tooLarge.Next());
    }

    // ---- Ordering == byte ordering -------------------------------------------

    [Fact]
    public void ChronologicalOrderEqualsByteOrder_AcrossAdvancingClock()
    {
        long clock = 1_000;
        var generator = new UlidGenerator(() => clock);

        var previous = generator.Next();
        for (int i = 0; i < 1_000; i++)
        {
            if (i % 3 == 0)
                clock++; // mix same-millisecond and advancing-clock transitions
            var current = generator.Next();
            Assert.True(CompareBytes(current, previous) > 0,
                $"ULID {i} did not compare greater than its predecessor as raw bytes.");
            previous = current;
        }
    }

    // ---- Same-millisecond monotonicity ----------------------------------------

    [Fact]
    public void SameMillisecond_IncrementsRandomPartByOne()
    {
        var generator = new UlidGenerator(
            () => 42,
            buffer => Array.Clear(buffer)); // random part starts at zero

        var first = generator.Next();
        var second = generator.Next();
        var third = generator.Next();

        Assert.Equal(42, ReadTimestamp(second));
        Assert.Equal(0, first[15]);
        Assert.Equal(1, second[15]);
        Assert.Equal(2, third[15]);
        Assert.Equal(first[..15], second[..15]); // only the low byte changed
    }

    [Fact]
    public void SameMillisecond_StaysStrictlyMonotonic()
    {
        var generator = new UlidGenerator(() => 999);

        var previous = generator.Next();
        for (int i = 0; i < 10_000; i++)
        {
            var current = generator.Next();
            Assert.True(CompareBytes(current, previous) > 0,
                $"ULID {i} was not strictly greater within the same millisecond.");
            Assert.Equal(999, ReadTimestamp(current));
            previous = current;
        }
    }

    [Fact]
    public void SameMillisecond_RandomOverflowAdvancesTimestampField()
    {
        var allOnes = Enumerable.Repeat((byte)0xFF, 10).ToArray();
        var generator = new UlidGenerator(() => 100, buffer => allOnes.CopyTo(buffer, 0));

        var first = generator.Next();   // ts=100, random = FF..FF
        var second = generator.Next();  // increment overflows -> ts=101, re-seeded

        Assert.Equal(100, ReadTimestamp(first));
        Assert.Equal(101, ReadTimestamp(second));
        Assert.True(CompareBytes(second, first) > 0);
    }

    // ---- Clock regression ------------------------------------------------------

    [Fact]
    public void ClockRegression_NeverEmitsOutOfOrderUlid()
    {
        long clock = 50_000;
        var generator = new UlidGenerator(() => clock);
        var previous = generator.Next();

        clock = 10_000; // wall clock jumps backward by 40 seconds

        for (int i = 0; i < 1_000; i++)
        {
            var current = generator.Next();
            Assert.True(CompareBytes(current, previous) > 0,
                $"ULID {i} regressed after the clock moved backward.");
            // Timestamp field must hold at the last issued value, not follow the clock.
            Assert.True(ReadTimestamp(current) >= 50_000);
            previous = current;
        }
    }

    [Fact]
    public void ClockRegression_ResumesFreshTimestampsOnceClockCatchesUp()
    {
        long clock = 5_000;
        var generator = new UlidGenerator(() => clock);
        var beforeRegression = generator.Next();

        clock = 1_000; // regression
        var duringRegression = generator.Next();

        clock = 6_000; // clock recovers past the high-water mark
        var afterRecovery = generator.Next();

        Assert.True(CompareBytes(duringRegression, beforeRegression) > 0);
        Assert.True(CompareBytes(afterRecovery, duringRegression) > 0);
        Assert.Equal(5_000, ReadTimestamp(duringRegression));
        Assert.Equal(6_000, ReadTimestamp(afterRecovery));
    }

    [Fact]
    public void ClockRegression_WithRandomOverflow_StaysMonotonic()
    {
        var allOnes = Enumerable.Repeat((byte)0xFF, 10).ToArray();
        long clock = 200;
        var generator = new UlidGenerator(() => clock, buffer => allOnes.CopyTo(buffer, 0));

        var first = generator.Next(); // ts=200, random = FF..FF
        clock = 100;                  // regression

        var previous = first;
        for (int i = 0; i < 100; i++)
        {
            // Every call overflows the all-ones random part and bumps the timestamp.
            var current = generator.Next();
            Assert.True(CompareBytes(current, previous) > 0,
                $"ULID {i} regressed under combined clock regression and overflow.");
            Assert.Equal(200 + i + 1, ReadTimestamp(current));
            previous = current;
        }
    }

    [Fact]
    public void ClockRegression_MultiStepJitteryClock_StaysStrictlyMonotonic()
    {
        // Deterministic random walk: the clock repeatedly jumps backward and forward
        // by varying amounts (multi-step regression), never settling.
        var rng = new Random(12345);
        long clock = 1_000_000;
        var generator = new UlidGenerator(() => clock);

        var previous = generator.Next();
        long highWater = ReadTimestamp(previous);

        for (int i = 0; i < 5_000; i++)
        {
            // ~half the steps regress the clock, by anything from 1 ms to 10 s.
            clock += rng.Next(0, 2) == 0
                ? -rng.Next(1, 10_000)
                : rng.Next(0, 100);
            clock = Math.Max(clock, 0); // keep the simulated clock in the valid range

            var current = generator.Next();
            Assert.True(CompareBytes(current, previous) > 0,
                $"ULID {i} regressed under a jittery clock (clock={clock}).");

            // Timestamp field must never move backward, regardless of the clock.
            long ts = ReadTimestamp(current);
            Assert.True(ts >= highWater,
                $"Timestamp field regressed at step {i}: {ts} < {highWater}.");
            highWater = ts;
            previous = current;
        }
    }

    // ---- Randomness sanity -------------------------------------------------------

    [Fact]
    public void RandomPart_HasReasonableDistribution()
    {
        // Fresh CSPRNG draw each call: advance the clock every time.
        long clock = 1;
        var generator = new UlidGenerator(() => clock++);

        const int count = 2_000;
        var randomParts = new HashSet<string>();
        var byteValuesSeen = new bool[256];

        for (int i = 0; i < count; i++)
        {
            var ulid = generator.Next();
            var random = ulid[6..];
            randomParts.Add(Convert.ToHexString(random));
            foreach (var b in random)
                byteValuesSeen[b] = true;
        }

        // 2000 draws of 80 CSPRNG bits: collisions are astronomically unlikely.
        Assert.Equal(count, randomParts.Count);

        // 20,000 random bytes should cover essentially all 256 values; require most.
        int distinctByteValues = byteValuesSeen.Count(seen => seen);
        Assert.True(distinctByteValues > 240,
            $"Only {distinctByteValues} distinct byte values in {count * 10} CSPRNG bytes.");
    }

    // ---- Thread safety -------------------------------------------------------------

    [Fact]
    public void ConcurrentGeneration_ProducesUniqueMonotonicUlids()
    {
        var generator = new UlidGenerator();
        const int threads = 8;
        const int perThread = 5_000;
        var results = new byte[threads][][];

        Parallel.For(0, threads, t =>
        {
            var local = new byte[perThread][];
            for (int i = 0; i < perThread; i++)
                local[i] = generator.Next();
            results[t] = local;
        });

        // Per-thread sequences must be strictly increasing (the generator is globally
        // monotonic, so each thread's own emission order is too).
        foreach (var local in results)
            for (int i = 1; i < perThread; i++)
                Assert.True(CompareBytes(local[i], local[i - 1]) > 0,
                    "A thread observed a non-increasing ULID sequence.");

        // All ULIDs across all threads must be unique.
        var all = new HashSet<string>();
        foreach (var local in results)
            foreach (var ulid in local)
                Assert.True(all.Add(Convert.ToHexString(ulid)), "Duplicate ULID generated.");
        Assert.Equal(threads * perThread, all.Count);
    }

    [Fact]
    public void ConcurrentGeneration_UnderFixedClock_StaysUniqueAndOrdered()
    {
        // Fixed clock forces every call through the same-millisecond increment path.
        var generator = new UlidGenerator(() => 12_345);
        const int threads = 8;
        const int perThread = 2_000;
        var all = new System.Collections.Concurrent.ConcurrentBag<byte[]>();

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
                all.Add(generator.Next());
        });

        var unique = new HashSet<string>(all.Select(Convert.ToHexString));
        Assert.Equal(threads * perThread, unique.Count);
        foreach (var ulid in all)
            Assert.Equal(12_345, ReadTimestamp(ulid));
    }
}
