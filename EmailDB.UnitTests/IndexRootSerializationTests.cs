using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the 68-byte IndexRoot descriptor (EmailDB_FileFormat_Spec.md
/// Section 6, docs/BTree_Index.md Sections 4, 6):
///   IndexKind (2) + RootBlockId (16) + EntryCount (8) + TreeHeight (2) +
///   RootHash (32) + Sequence (8).
/// Covers exact little-endian layout, round-trip for every IndexKind,
/// bounds-checked rejection of malformed payloads (wrong length, bad values),
/// and the monotonic-per-index Sequence semantics.
/// </summary>
public class IndexRootSerializationTests
{
    // Field offsets under test (spec Section 6 IndexRoot layout).
    private const int IndexKindOffset = 0;
    private const int RootBlockIdOffset = 2;
    private const int EntryCountOffset = 18;
    private const int TreeHeightOffset = 26;
    private const int RootHashOffset = 28;
    private const int SequenceOffset = 60;

    private static byte[] RootBlockId(byte seed) =>
        Enumerable.Range(0, IndexRootSerializer.RootBlockIdSize).Select(i => (byte)(seed + i)).ToArray();

    private static byte[] RootHash(byte seed) =>
        Enumerable.Range(0, IndexRootSerializer.RootHashSize).Select(i => (byte)(seed * 7 + i)).ToArray();

    private static IndexRoot Sample(
        BTreeIndexKind kind = BTreeIndexKind.PrimaryEmail,
        long entryCount = 4150,
        int treeHeight = 2,
        ulong sequence = 5) => new()
        {
            IndexKind = kind,
            RootBlockId = RootBlockId(0x10),
            EntryCount = entryCount,
            TreeHeight = treeHeight,
            RootHash = RootHash(0x20),
            Sequence = sequence,
        };

    // ---- Fixed size and exact little-endian layout ----

    [Fact]
    public void Serialize_ProducesExactly68Bytes()
    {
        Assert.Equal(68, IndexRootSerializer.IndexRootPayloadSize);
        Assert.Equal(68, IndexRootSerializer.Serialize(Sample()).Length);
    }

    [Fact]
    public void Serialize_WritesEachFieldLittleEndianAtSpecOffset()
    {
        var root = new IndexRoot
        {
            IndexKind = BTreeIndexKind.Date,          // 2
            RootBlockId = RootBlockId(0xA0),
            EntryCount = 0x0102030405060708,
            TreeHeight = 0x0304,
            RootHash = RootHash(0x33),
            Sequence = 0x1122334455667788,
        };

        var payload = IndexRootSerializer.Serialize(root);
        var span = payload.AsSpan();

        Assert.Equal((ushort)BTreeIndexKind.Date,
            BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(IndexKindOffset, 2)));
        Assert.True(span.Slice(RootBlockIdOffset, 16).SequenceEqual(RootBlockId(0xA0)));
        Assert.Equal(0x0102030405060708L,
            BinaryPrimitives.ReadInt64LittleEndian(span.Slice(EntryCountOffset, 8)));
        Assert.Equal((ushort)0x0304,
            BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(TreeHeightOffset, 2)));
        Assert.True(span.Slice(RootHashOffset, 32).SequenceEqual(RootHash(0x33)));
        Assert.Equal(0x1122334455667788UL,
            BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(SequenceOffset, 8)));
    }

    // ---- Round trip for every IndexKind (and an experimental one) ----

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail)]
    [InlineData(BTreeIndexKind.BlockLocation)]
    [InlineData(BTreeIndexKind.Date)]
    [InlineData(BTreeIndexKind.Fts)]
    [InlineData((BTreeIndexKind)100)] // experimental kind: not restricted, per spec 6.1
    public void RoundTrip_PreservesEveryField(BTreeIndexKind kind)
    {
        var original = new IndexRoot
        {
            IndexKind = kind,
            RootBlockId = RootBlockId(0x40),
            EntryCount = 519_000_000,
            TreeHeight = 5,
            RootHash = RootHash(0x50),
            Sequence = 987_654_321,
        };

        var result = IndexRootSerializer.Deserialize(IndexRootSerializer.Serialize(original));

        Assert.True(result.IsSuccess, result.Error);
        var round = result.Value;
        Assert.Equal(original.IndexKind, round.IndexKind);
        Assert.Equal(original.RootBlockId, round.RootBlockId);
        Assert.Equal(original.EntryCount, round.EntryCount);
        Assert.Equal(original.TreeHeight, round.TreeHeight);
        Assert.Equal(original.RootHash, round.RootHash);
        Assert.Equal(original.Sequence, round.Sequence);
    }

    [Fact]
    public void RoundTrip_HandlesBoundaryValues()
    {
        var original = new IndexRoot
        {
            IndexKind = BTreeIndexKind.BlockLocation,
            RootBlockId = RootBlockId(0x00),
            EntryCount = long.MaxValue,
            TreeHeight = ushort.MaxValue,
            RootHash = RootHash(0xFF),
            Sequence = ulong.MaxValue,
        };

        var round = IndexRootSerializer.Deserialize(IndexRootSerializer.Serialize(original)).Value;

        Assert.Equal(long.MaxValue, round.EntryCount);
        Assert.Equal(ushort.MaxValue, round.TreeHeight);
        Assert.Equal(ulong.MaxValue, round.Sequence);
    }

    // ---- Deserialize: bounds-checked rejection of malformed payloads ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(67)]
    [InlineData(69)]
    [InlineData(136)]
    public void Deserialize_Rejects_WrongLength(int length)
    {
        var result = IndexRootSerializer.Deserialize(new byte[length]);

        Assert.True(result.IsFailure);
        Assert.Contains("68 bytes", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_NegativeEntryCount()
    {
        var payload = IndexRootSerializer.Serialize(Sample());
        // Force EntryCount to -1 (all-ones) — a corrupt, impossible count.
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(EntryCountOffset, 8), -1);

        var result = IndexRootSerializer.Deserialize(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("EntryCount", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_ZeroTreeHeight()
    {
        var payload = IndexRootSerializer.Serialize(Sample());
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(TreeHeightOffset, 2), 0);

        var result = IndexRootSerializer.Deserialize(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("TreeHeight", result.Error);
    }

    // ---- Serialize: rejects inconsistent descriptors (programming errors) ----

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(0)]
    public void Serialize_Rejects_WrongRootBlockIdWidth(int width)
    {
        var root = new IndexRoot
        {
            IndexKind = BTreeIndexKind.PrimaryEmail,
            RootBlockId = new byte[width],
            EntryCount = 1,
            TreeHeight = 1,
            RootHash = RootHash(1),
            Sequence = 0,
        };

        Assert.Throws<ArgumentException>(() => IndexRootSerializer.Serialize(root));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Serialize_Rejects_WrongRootHashWidth(int width)
    {
        var root = new IndexRoot
        {
            IndexKind = BTreeIndexKind.PrimaryEmail,
            RootBlockId = RootBlockId(1),
            EntryCount = 1,
            TreeHeight = 1,
            RootHash = new byte[width],
            Sequence = 0,
        };

        Assert.Throws<ArgumentException>(() => IndexRootSerializer.Serialize(root));
    }

    [Fact]
    public void Serialize_Rejects_NegativeEntryCount()
    {
        Assert.Throws<ArgumentException>(() =>
            IndexRootSerializer.Serialize(Sample(entryCount: -1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Serialize_Rejects_OutOfRangeTreeHeight(int treeHeight)
    {
        Assert.Throws<ArgumentException>(() =>
            IndexRootSerializer.Serialize(Sample(treeHeight: treeHeight)));
    }

    // ---- Monotonic Sequence per index ----

    [Fact]
    public void CreateInitial_StartsAtInitialSequence()
    {
        var root = IndexRoot.CreateInitial(
            BTreeIndexKind.PrimaryEmail, RootBlockId(1), entryCount: 83, treeHeight: 1, RootHash(1));

        Assert.Equal(IndexRoot.InitialSequence, root.Sequence);
    }

    [Fact]
    public void NextVersion_IncrementsSequenceByOne_AndPreservesIndexKind()
    {
        var v0 = IndexRoot.CreateInitial(
            BTreeIndexKind.Date, RootBlockId(1), entryCount: 10, treeHeight: 1, RootHash(1));

        var v1 = v0.NextVersion(RootBlockId(2), entryCount: 20, treeHeight: 2, RootHash(2));

        Assert.Equal(v0.Sequence + 1, v1.Sequence);
        Assert.Equal(v0.IndexKind, v1.IndexKind);
        // The new descriptor carries the updated shape/root, not the old one.
        Assert.Equal(20, v1.EntryCount);
        Assert.Equal(2, v1.TreeHeight);
        Assert.Equal(RootBlockId(2), v1.RootBlockId);
    }

    [Fact]
    public void NextVersion_ProducesStrictlyMonotonicChain()
    {
        var root = IndexRoot.CreateInitial(
            BTreeIndexKind.BlockLocation, RootBlockId(1), entryCount: 1, treeHeight: 1, RootHash(1));

        ulong previous = root.Sequence;
        for (int i = 0; i < 100; i++)
        {
            root = root.NextVersion(RootBlockId((byte)i), entryCount: i, treeHeight: 1, RootHash((byte)i));
            Assert.Equal(previous + 1, root.Sequence);
            previous = root.Sequence;
        }

        Assert.Equal(100UL, root.Sequence);
    }

    [Fact]
    public void Sequences_AreIndependentPerIndex()
    {
        // Two indexes advance their own Sequence counters independently.
        var primary = IndexRoot.CreateInitial(
            BTreeIndexKind.PrimaryEmail, RootBlockId(1), 1, 1, RootHash(1));
        var location = IndexRoot.CreateInitial(
            BTreeIndexKind.BlockLocation, RootBlockId(1), 1, 1, RootHash(1));

        primary = primary.NextVersion(RootBlockId(2), 2, 1, RootHash(2));
        primary = primary.NextVersion(RootBlockId(3), 3, 1, RootHash(3));
        location = location.NextVersion(RootBlockId(2), 2, 1, RootHash(2));

        Assert.Equal(2UL, primary.Sequence);
        Assert.Equal(1UL, location.Sequence);
    }

    [Fact]
    public void NextVersion_Throws_OnSequenceOverflow()
    {
        var root = new IndexRoot
        {
            IndexKind = BTreeIndexKind.PrimaryEmail,
            RootBlockId = RootBlockId(1),
            EntryCount = 1,
            TreeHeight = 1,
            RootHash = RootHash(1),
            Sequence = ulong.MaxValue,
        };

        Assert.Throws<InvalidOperationException>(() =>
            root.NextVersion(RootBlockId(2), 2, 1, RootHash(2)));
    }

    [Fact]
    public void MonotonicSequence_SurvivesRoundTrip()
    {
        var v0 = IndexRoot.CreateInitial(
            BTreeIndexKind.PrimaryEmail, RootBlockId(1), 83, 1, RootHash(1));
        var v1 = v0.NextVersion(RootBlockId(2), 166, 2, RootHash(2));

        var restored = IndexRootSerializer.Deserialize(IndexRootSerializer.Serialize(v1)).Value;

        Assert.Equal(v1.Sequence, restored.Sequence);
        Assert.Equal(v0.Sequence + 1, restored.Sequence);
    }
}
