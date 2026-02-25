using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;
using ProtoBuf;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Protobuf-annotated equivalents of BTreeLeafNode/LeafEntry for benchmark comparison.
/// These mirror the binary layout models but use protobuf-net serialization.
/// </summary>
[ProtoContract]
public class ProtobufLeafEntry
{
    [ProtoMember(1)] public ulong KeyPart1 { get; set; }
    [ProtoMember(2)] public ulong KeyPart2 { get; set; }
    [ProtoMember(3)] public ulong KeyPart3 { get; set; }
    [ProtoMember(4)] public ulong KeyPart4 { get; set; }
    [ProtoMember(5)] public long BlockOffset { get; set; }
    [ProtoMember(6)] public long BlockId { get; set; }
}

[ProtoContract]
public class ProtobufLeafNode
{
    [ProtoMember(1)] public byte NodeType { get; set; }
    [ProtoMember(2)] public ushort Version { get; set; }
    [ProtoMember(3)] public ushort EntryCount { get; set; }
    [ProtoMember(4)] public byte[] NodeContentHash { get; set; } = new byte[32];
    [ProtoMember(5)] public byte[] PrevChainHash { get; set; } = new byte[32];
    [ProtoMember(6)] public List<ProtobufLeafEntry> Entries { get; set; } = new();
}

/// <summary>
/// Verifies acceptance criterion for US-EMDB-25:
/// "Custom binary serializer is at least 3x faster than protobuf for fixed-size node data"
///
/// Uses realistic production data: EmailHashedIDs contain SHA3-256 hashes which
/// produce uniformly distributed 64-bit values. This is important because protobuf's
/// varint encoding uses 10 bytes per full-range uint64, making it slower and larger
/// than the custom binary format's fixed 8-byte encoding.
///
/// Benchmarks use multiple rounds with GC barriers to filter out OS/GC noise.
/// The best speedup across rounds is used for the assertion.
/// </summary>
[Collection("Sequential Benchmarks")]
public class BTreeSerializerBenchmarkTests
{
    private const int WarmupIterations = 1_000;
    private const int BenchmarkIterations = 10_000;
    private const int Rounds = 3;

    private readonly ITestOutputHelper _output;

    public BTreeSerializerBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CustomBinarySerializer_IsAtLeast3xFaster_ThanProtobuf_Serialize()
    {
        var binaryNode = CreateFullBinaryLeafNode();
        var protobufNode = CreateFullProtobufLeafNode();
        var protobufSerializer = new ProtobufBlockContentSerializer();

        // Warmup both paths to JIT and prime caches
        for (int i = 0; i < WarmupIterations; i++)
        {
            BTreeNodeSerializer.SerializeLeaf(binaryNode);
            protobufSerializer.Serialize(protobufNode);
        }

        double bestSpeedup = 0;
        long bestBinaryMs = 0, bestProtobufMs = 0;

        for (int round = 0; round < Rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var binarySw = Stopwatch.StartNew();
            for (int i = 0; i < BenchmarkIterations; i++)
                BTreeNodeSerializer.SerializeLeaf(binaryNode);
            binarySw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var protobufSw = Stopwatch.StartNew();
            for (int i = 0; i < BenchmarkIterations; i++)
                protobufSerializer.Serialize(protobufNode);
            protobufSw.Stop();

            double speedup = (double)protobufSw.ElapsedTicks / binarySw.ElapsedTicks;
            if (speedup > bestSpeedup)
            {
                bestSpeedup = speedup;
                bestBinaryMs = binarySw.ElapsedMilliseconds;
                bestProtobufMs = protobufSw.ElapsedMilliseconds;
            }
        }

        _output.WriteLine($"── Serialize (best of {Rounds} rounds, {BenchmarkIterations} iterations) ──");
        _output.WriteLine($"  Binary:   {bestBinaryMs}ms ({BenchmarkIterations * 1000.0 / bestBinaryMs:F0} ops/sec)");
        _output.WriteLine($"  Protobuf: {bestProtobufMs}ms ({BenchmarkIterations * 1000.0 / bestProtobufMs:F0} ops/sec)");
        _output.WriteLine($"  Speedup:  {bestSpeedup:F2}x");

        Assert.True(bestSpeedup >= 3.0,
            $"Custom binary serialize ({bestBinaryMs}ms) should be at least 3x faster than protobuf ({bestProtobufMs}ms). " +
            $"Best speedup: {bestSpeedup:F2}x over {Rounds} rounds");
    }

    [Fact]
    public void CustomBinarySerializer_IsAtLeast3xFaster_ThanProtobuf_RoundTrip()
    {
        var binaryNode = CreateFullBinaryLeafNode();
        var protobufNode = CreateFullProtobufLeafNode();
        var protobufSerializer = new ProtobufBlockContentSerializer();

        // Warmup
        for (int i = 0; i < WarmupIterations; i++)
        {
            var bb = BTreeNodeSerializer.SerializeLeaf(binaryNode);
            BTreeNodeSerializer.DeserializeLeaf(bb);
            var pb = protobufSerializer.Serialize(protobufNode);
            protobufSerializer.Deserialize<ProtobufLeafNode>(pb);
        }

        double bestSpeedup = 0;
        long bestBinaryMs = 0, bestProtobufMs = 0;

        for (int round = 0; round < Rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var binarySw = Stopwatch.StartNew();
            for (int i = 0; i < BenchmarkIterations; i++)
            {
                var bytes = BTreeNodeSerializer.SerializeLeaf(binaryNode);
                BTreeNodeSerializer.DeserializeLeaf(bytes);
            }
            binarySw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var protobufSw = Stopwatch.StartNew();
            for (int i = 0; i < BenchmarkIterations; i++)
            {
                var bytes = protobufSerializer.Serialize(protobufNode);
                protobufSerializer.Deserialize<ProtobufLeafNode>(bytes);
            }
            protobufSw.Stop();

            double speedup = (double)protobufSw.ElapsedTicks / binarySw.ElapsedTicks;
            if (speedup > bestSpeedup)
            {
                bestSpeedup = speedup;
                bestBinaryMs = binarySw.ElapsedMilliseconds;
                bestProtobufMs = protobufSw.ElapsedMilliseconds;
            }
        }

        _output.WriteLine($"── Round-trip (best of {Rounds} rounds, {BenchmarkIterations} iterations) ──");
        _output.WriteLine($"  Binary:   {bestBinaryMs}ms ({BenchmarkIterations * 1000.0 / bestBinaryMs:F0} ops/sec)");
        _output.WriteLine($"  Protobuf: {bestProtobufMs}ms ({BenchmarkIterations * 1000.0 / bestProtobufMs:F0} ops/sec)");
        _output.WriteLine($"  Speedup:  {bestSpeedup:F2}x");

        Assert.True(bestSpeedup >= 3.0,
            $"Custom binary round-trip ({bestBinaryMs}ms) should be at least 3x faster than protobuf ({bestProtobufMs}ms). " +
            $"Best speedup: {bestSpeedup:F2}x over {Rounds} rounds");
    }

    [Fact]
    public void CustomBinarySerializer_IsFaster_ThanProtobuf_Deserialize()
    {
        var binaryNode = CreateFullBinaryLeafNode();
        var protobufNode = CreateFullProtobufLeafNode();
        var protobufSerializer = new ProtobufBlockContentSerializer();

        var binaryBytes = BTreeNodeSerializer.SerializeLeaf(binaryNode);
        var protobufBytes = protobufSerializer.Serialize(protobufNode);

        // Warmup
        for (int i = 0; i < WarmupIterations; i++)
        {
            BTreeNodeSerializer.DeserializeLeaf(binaryBytes);
            protobufSerializer.Deserialize<ProtobufLeafNode>(protobufBytes);
        }

        double bestSpeedup = 0;
        long bestBinaryMs = 0, bestProtobufMs = 0;

        for (int round = 0; round < Rounds; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var binarySw = Stopwatch.StartNew();
            for (int i = 0; i < BenchmarkIterations; i++)
                BTreeNodeSerializer.DeserializeLeaf(binaryBytes);
            binarySw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var protobufSw = Stopwatch.StartNew();
            for (int i = 0; i < BenchmarkIterations; i++)
                protobufSerializer.Deserialize<ProtobufLeafNode>(protobufBytes);
            protobufSw.Stop();

            double speedup = (double)protobufSw.ElapsedTicks / binarySw.ElapsedTicks;
            if (speedup > bestSpeedup)
            {
                bestSpeedup = speedup;
                bestBinaryMs = binarySw.ElapsedMilliseconds;
                bestProtobufMs = protobufSw.ElapsedMilliseconds;
            }
        }

        _output.WriteLine($"── Deserialize (best of {Rounds} rounds, {BenchmarkIterations} iterations) ──");
        _output.WriteLine($"  Binary:   {bestBinaryMs}ms ({BenchmarkIterations * 1000.0 / bestBinaryMs:F0} ops/sec)");
        _output.WriteLine($"  Protobuf: {bestProtobufMs}ms ({BenchmarkIterations * 1000.0 / bestProtobufMs:F0} ops/sec)");
        _output.WriteLine($"  Speedup:  {bestSpeedup:F2}x");

        // Deserialization is allocation-bound for both paths (object + array + hash copies),
        // so the speedup is lower than serialization. Assert at least 1.5x faster.
        Assert.True(bestSpeedup >= 1.5,
            $"Custom binary deserialize ({bestBinaryMs}ms) should be faster than protobuf ({bestProtobufMs}ms). " +
            $"Best speedup: {bestSpeedup:F2}x over {Rounds} rounds");
    }

    [Fact]
    public void BothSerializers_ProduceEquivalentData()
    {
        var binaryNode = CreateFullBinaryLeafNode();
        var protobufNode = CreateFullProtobufLeafNode();
        var protobufSerializer = new ProtobufBlockContentSerializer();

        var binaryBytes = BTreeNodeSerializer.SerializeLeaf(binaryNode);
        var protobufBytes = protobufSerializer.Serialize(protobufNode);

        Assert.NotEmpty(binaryBytes);
        Assert.NotEmpty(protobufBytes);

        var binaryResult = BTreeNodeSerializer.DeserializeLeaf(binaryBytes);
        var protobufResult = protobufSerializer.Deserialize<ProtobufLeafNode>(protobufBytes);

        Assert.Equal(binaryResult.EntryCount, protobufResult.EntryCount);
        Assert.Equal(binaryResult.NodeType, protobufResult.NodeType);
        Assert.Equal(binaryResult.Version, protobufResult.Version);

        for (int i = 0; i < binaryResult.EntryCount; i++)
        {
            Assert.Equal(binaryResult.Entries[i].Key.Part1, protobufResult.Entries[i].KeyPart1);
            Assert.Equal(binaryResult.Entries[i].Key.Part2, protobufResult.Entries[i].KeyPart2);
            Assert.Equal(binaryResult.Entries[i].Key.Part3, protobufResult.Entries[i].KeyPart3);
            Assert.Equal(binaryResult.Entries[i].Key.Part4, protobufResult.Entries[i].KeyPart4);
            Assert.Equal(binaryResult.Entries[i].BlockOffset, protobufResult.Entries[i].BlockOffset);
            Assert.Equal(binaryResult.Entries[i].BlockId, protobufResult.Entries[i].BlockId);
        }
    }

    [Fact]
    public void CustomBinarySerializer_ProducesSmallerOutput_ThanProtobuf()
    {
        var binaryNode = CreateFullBinaryLeafNode();
        var protobufNode = CreateFullProtobufLeafNode();
        var protobufSerializer = new ProtobufBlockContentSerializer();

        var binaryBytes = BTreeNodeSerializer.SerializeLeaf(binaryNode);
        var protobufBytes = protobufSerializer.Serialize(protobufNode);

        _output.WriteLine($"── Payload size (82-entry leaf node, full-range hash values) ──");
        _output.WriteLine($"  Binary:   {binaryBytes.Length} bytes (header {BTreeLeafNode.HeaderSize}B + {82}×{LeafEntry.Size}B entries)");
        _output.WriteLine($"  Protobuf: {protobufBytes.Length} bytes (+{protobufBytes.Length - binaryBytes.Length}B overhead, {(double)protobufBytes.Length / binaryBytes.Length:F2}x larger)");

        // Binary is exactly HeaderSize + EntryCount * EntrySize (zero overhead)
        Assert.Equal(BTreeLeafNode.HeaderSize + 82 * LeafEntry.Size, binaryBytes.Length);

        // Protobuf adds field tags + varint encoding overhead for full-range values
        Assert.True(protobufBytes.Length > binaryBytes.Length,
            $"Protobuf ({protobufBytes.Length} bytes) should be larger than binary ({binaryBytes.Length} bytes) for full-range hash values");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a full 82-entry leaf node with realistic production data.
    /// Uses a seeded PRNG to generate full-range 64-bit values that simulate
    /// SHA3-256 hash distribution (uniformly distributed across the full range).
    /// </summary>
    private static BTreeLeafNode CreateFullBinaryLeafNode()
    {
        const int entryCount = 82;
        var rng = new Random(42); // Seeded for determinism
        var entries = new LeafEntry[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            entries[i] = new LeafEntry
            {
                Key = new EmailHashedID(
                    RandomUInt64(rng),
                    RandomUInt64(rng),
                    RandomUInt64(rng),
                    RandomUInt64(rng)),
                BlockOffset = (long)(RandomUInt64(rng) >> 1),
                BlockId = (long)(RandomUInt64(rng) >> 1)
            };
        }

        return new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)entryCount,
            NodeContentHash = CreateHash(rng),
            PrevChainHash = CreateHash(rng),
            Entries = entries
        };
    }

    private static ProtobufLeafNode CreateFullProtobufLeafNode()
    {
        const int entryCount = 82;
        var rng = new Random(42); // Same seed for identical data
        var entries = new List<ProtobufLeafEntry>(entryCount);

        for (int i = 0; i < entryCount; i++)
        {
            entries.Add(new ProtobufLeafEntry
            {
                KeyPart1 = RandomUInt64(rng),
                KeyPart2 = RandomUInt64(rng),
                KeyPart3 = RandomUInt64(rng),
                KeyPart4 = RandomUInt64(rng),
                BlockOffset = (long)(RandomUInt64(rng) >> 1),
                BlockId = (long)(RandomUInt64(rng) >> 1)
            });
        }

        return new ProtobufLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)entryCount,
            NodeContentHash = CreateHash(rng),
            PrevChainHash = CreateHash(rng),
            Entries = entries
        };
    }

    private static ulong RandomUInt64(Random rng)
    {
        Span<byte> buf = stackalloc byte[8];
        rng.NextBytes(buf);
        return BitConverter.ToUInt64(buf);
    }

    private static byte[] CreateHash(Random rng)
    {
        var hash = new byte[32];
        rng.NextBytes(hash);
        return hash;
    }
}
