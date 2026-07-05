using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for NodeContentHash (EmailDB_FileFormat_Spec.md Section 6):
/// BLAKE3-256 computed over the node's FULL serialized payload — the 12-byte
/// node header plus the body. The hash must be deterministic, must match an
/// independently computed BLAKE3 over the same bytes, and must change when
/// ANY byte of the serialized node changes (header bytes included, not just
/// entry data).
/// </summary>
public class BTreeNodeContentHashTests
{
    private static byte[] Key(int width, int seed) =>
        Enumerable.Range(0, width).Select(i => (byte)(i == width - 1 ? seed : 0)).ToArray();

    private static byte[] Bytes(int width, int seed) =>
        Enumerable.Range(0, width).Select(i => (byte)(seed * 31 + i)).ToArray();

    private static BTreeLeafNode SampleLeaf(BTreeIndexKind kind, byte keySize, ushort valueSize, int entries)
    {
        var node = new BTreeLeafNode { IndexKind = kind, KeySize = keySize, ValueSize = valueSize };
        for (int i = 0; i < entries; i++)
            node.Entries.Add(new BTreeLeafEntry(Key(keySize, i + 1), Bytes(valueSize, i)));
        return node;
    }

    private static BTreeInternalNode SampleInternal(BTreeIndexKind kind, byte keySize, ushort valueSize, int keys)
    {
        var node = new BTreeInternalNode { IndexKind = kind, KeySize = keySize, ValueSize = valueSize };
        for (int i = 0; i < keys; i++)
            node.Keys.Add(Key(keySize, i + 1));
        for (int i = 0; i < keys + 1; i++)
            node.Children.Add(Bytes(valueSize, i));
        return node;
    }

    // ---- Hash is BLAKE3-256 over exactly the full serialized payload ----

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail, 32, 16)]
    [InlineData(BTreeIndexKind.BlockLocation, 16, 16)]
    [InlineData(BTreeIndexKind.Date, 24, 0)]
    public void LeafHash_MatchesIndependentBlake3_OverHeaderPlusBody(
        BTreeIndexKind kind, byte keySize, ushort valueSize)
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(SampleLeaf(kind, keySize, valueSize, entries: 3));

        var hash = BTreeNodeSerializer.ComputeNodeContentHash(payload);

        // Independent incremental BLAKE3 fed the header and body as separate
        // updates: only a hash over the single contiguous full payload matches.
        using var hasher = Blake3.Hasher.New();
        hasher.Update(payload.AsSpan(0, BTreeNodeSerializer.NodeHeaderSize));
        hasher.Update(payload.AsSpan(BTreeNodeSerializer.NodeHeaderSize));
        Assert.Equal(hasher.Finalize().AsSpan().ToArray(), hash);
        Assert.Equal(BTreeNodeSerializer.NodeContentHashSize, hash.Length);
    }

    [Fact]
    public void InternalHash_MatchesIndependentBlake3_OverHeaderPlusBody()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.PrimaryEmail, 32, 48, keys: 3));

        var hash = BTreeNodeSerializer.ComputeNodeContentHash(payload);

        using var hasher = Blake3.Hasher.New();
        hasher.Update(payload.AsSpan(0, BTreeNodeSerializer.NodeHeaderSize));
        hasher.Update(payload.AsSpan(BTreeNodeSerializer.NodeHeaderSize));
        Assert.Equal(hasher.Finalize().AsSpan().ToArray(), hash);
    }

    [Fact]
    public void Hash_IsNotBodyOnly_HeaderBytesAreCovered()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 3));

        var full = BTreeNodeSerializer.ComputeNodeContentHash(payload);

        // Hash over only the body (header skipped) must NOT match: the header
        // is part of the hashed content.
        var bodyOnly = Blake3.Hasher.Hash(payload.AsSpan(BTreeNodeSerializer.NodeHeaderSize))
            .AsSpan().ToArray();
        Assert.NotEqual(bodyOnly, full);
    }

    // ---- Determinism ----

    [Fact]
    public void Hash_IsDeterministic_ForEquivalentNodes()
    {
        var a = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.BlockLocation, 16, 16, entries: 4));
        var b = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.BlockLocation, 16, 16, entries: 4));

        Assert.Equal(
            BTreeNodeSerializer.ComputeNodeContentHash(a),
            BTreeNodeSerializer.ComputeNodeContentHash(b));
    }

    // ---- Any single flipped byte changes the hash (header AND body) ----

    [Fact]
    public void LeafHash_Changes_WhenAnySingleByteFlips()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 3));
        var baseline = BTreeNodeSerializer.ComputeNodeContentHash(payload);

        // Every byte position: header (0..11), first entry byte, every entry
        // byte, and the final byte of the payload.
        for (int i = 0; i < payload.Length; i++)
        {
            var tampered = (byte[])payload.Clone();
            tampered[i] ^= 0xFF;

            var hash = BTreeNodeSerializer.ComputeNodeContentHash(tampered);

            Assert.False(baseline.SequenceEqual(hash),
                $"Flipping byte {i} of {payload.Length} did not change NodeContentHash.");
        }
    }

    [Fact]
    public void InternalHash_Changes_WhenAnySingleByteFlips()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.BlockLocation, 16, 40, keys: 3));
        var baseline = BTreeNodeSerializer.ComputeNodeContentHash(payload);

        for (int i = 0; i < payload.Length; i++)
        {
            var tampered = (byte[])payload.Clone();
            tampered[i] ^= 0xFF;

            var hash = BTreeNodeSerializer.ComputeNodeContentHash(tampered);

            Assert.False(baseline.SequenceEqual(hash),
                $"Flipping byte {i} of {payload.Length} did not change NodeContentHash.");
        }
    }

    // ---- Span overload and argument validation ----

    [Fact]
    public void SpanOverload_ProducesSameHash_AsAllocatingOverload()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.Date, 24, 0, entries: 2));

        var destination = new byte[BTreeNodeSerializer.NodeContentHashSize];
        BTreeNodeSerializer.ComputeNodeContentHash(payload, destination);

        Assert.Equal(BTreeNodeSerializer.ComputeNodeContentHash(payload), destination);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void SpanOverload_Rejects_WrongSizeDestination(int destinationSize)
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 1));

        Assert.Throws<ArgumentException>(() =>
            BTreeNodeSerializer.ComputeNodeContentHash(payload, new byte[destinationSize]));
    }

    [Fact]
    public void Hash_Rejects_PayloadShorterThanNodeHeader()
    {
        Assert.Throws<ArgumentException>(() =>
            BTreeNodeSerializer.ComputeNodeContentHash(
                new byte[BTreeNodeSerializer.NodeHeaderSize - 1]));
    }
}
