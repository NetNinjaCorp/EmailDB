using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 generic B+-tree node deserializers and capacity helpers
/// (EmailDB_FileFormat_Spec.md Section 6 / 6.1): the declared
/// EntryCount/KeySize/ValueSize must be validated against the actual payload
/// length before any body byte is read, and capacities at the 4096-byte target
/// node must match the spec (83/50 primary, 124/71 location, 166/55 date).
/// </summary>
public class BTreeNodeDeserializationTests
{
    // Node header field offsets (spec Section 6 table).
    private const int KeySizeOffset = 4;
    private const int ValueSizeOffset = 5;
    private const int EntryCountOffset = 7;
    private const int ReservedOffset = 9;

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

    // ---- Round-trip through the deserializers (IndexKind 0/1/2 widths) ----

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail, 32, 16)]
    [InlineData(BTreeIndexKind.BlockLocation, 16, 16)]
    [InlineData(BTreeIndexKind.Date, 24, 0)]
    public void DeserializeLeaf_RoundTrips_RegistryLayouts(BTreeIndexKind kind, byte keySize, ushort valueSize)
    {
        var node = SampleLeaf(kind, keySize, valueSize, entries: 5);
        var payload = BTreeNodeSerializer.SerializeLeaf(node);

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.Equal(kind, result.Value.IndexKind);
        Assert.Equal(keySize, result.Value.KeySize);
        Assert.Equal(valueSize, result.Value.ValueSize);
        Assert.Equal(node.Entries.Count, result.Value.Entries.Count);
        for (int i = 0; i < node.Entries.Count; i++)
        {
            Assert.Equal(node.Entries[i].Key, result.Value.Entries[i].Key);
            Assert.Equal(node.Entries[i].Value, result.Value.Entries[i].Value);
        }
    }

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail, 32, 48)]
    [InlineData(BTreeIndexKind.BlockLocation, 16, 40)]
    [InlineData(BTreeIndexKind.Date, 24, 48)]
    public void DeserializeInternal_RoundTrips_RegistryLayouts(BTreeIndexKind kind, byte keySize, ushort valueSize)
    {
        var node = SampleInternal(kind, keySize, valueSize, keys: 4);
        var payload = BTreeNodeSerializer.SerializeInternal(node);

        var result = BTreeNodeSerializer.DeserializeInternal(payload);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.Equal(kind, result.Value.IndexKind);
        Assert.Equal(node.Keys.Count, result.Value.Keys.Count);
        Assert.Equal(node.Keys.Count + 1, result.Value.Children.Count);
        for (int i = 0; i < node.Keys.Count; i++)
            Assert.Equal(node.Keys[i], result.Value.Keys[i]);
        for (int i = 0; i < node.Children.Count; i++)
            Assert.Equal(node.Children[i], result.Value.Children[i]);
    }

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail)]
    [InlineData(BTreeIndexKind.BlockLocation)]
    [InlineData(BTreeIndexKind.Date)]
    public void SerializeLeaf_AfterRoundTrip_IsByteExact_AtMaxCapacity(BTreeIndexKind kind)
    {
        var (keySize, leafValueSize, _) = BTreeNodeCapacity.GetLayout(kind);
        var node = SampleLeaf(kind, keySize, leafValueSize, BTreeNodeCapacity.MaxLeafEntries(kind));
        var payload = BTreeNodeSerializer.SerializeLeaf(node);

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.Equal(node.Entries.Count, result.Value.Entries.Count);
        for (int i = 0; i < node.Entries.Count; i++)
        {
            Assert.Equal(node.Entries[i].Key, result.Value.Entries[i].Key);
            Assert.Equal(node.Entries[i].Value, result.Value.Entries[i].Value);
        }
        // Re-serializing the deserialized node must reproduce the exact bytes.
        Assert.Equal(payload, BTreeNodeSerializer.SerializeLeaf(result.Value));
    }

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail)]
    [InlineData(BTreeIndexKind.BlockLocation)]
    [InlineData(BTreeIndexKind.Date)]
    public void SerializeInternal_AfterRoundTrip_IsByteExact_AtMaxCapacity(BTreeIndexKind kind)
    {
        var (keySize, _, childRecordSize) = BTreeNodeCapacity.GetLayout(kind);
        var node = SampleInternal(kind, keySize, childRecordSize, BTreeNodeCapacity.MaxInternalKeys(kind));
        var payload = BTreeNodeSerializer.SerializeInternal(node);

        var result = BTreeNodeSerializer.DeserializeInternal(payload);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.Equal(kind, result.Value.IndexKind);
        Assert.Equal(keySize, result.Value.KeySize);
        Assert.Equal(childRecordSize, result.Value.ValueSize);
        Assert.Equal(node.Keys.Count, result.Value.Keys.Count);
        Assert.Equal(node.Children.Count, result.Value.Children.Count);
        for (int i = 0; i < node.Keys.Count; i++)
            Assert.Equal(node.Keys[i], result.Value.Keys[i]);
        for (int i = 0; i < node.Children.Count; i++)
            Assert.Equal(node.Children[i], result.Value.Children[i]);
        // Re-serializing the deserialized node must reproduce the exact bytes.
        Assert.Equal(payload, BTreeNodeSerializer.SerializeInternal(result.Value));
    }

    [Fact]
    public void DeserializeLeaf_RoundTrips_EmptyLeaf()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 0));

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Entries);
    }

    // ---- Bounds checks: declared shape vs actual payload length ----

    [Fact]
    public void DeserializeHeader_Rejects_PayloadShorterThanHeader()
    {
        var result = BTreeNodeSerializer.DeserializeHeader(new byte[BTreeNodeSerializer.NodeHeaderSize - 1]);

        Assert.True(result.IsFailure);
        Assert.Contains("at least", result.Error);
    }

    [Fact]
    public void DeserializeLeaf_Rejects_EntryCountOverflowingPayload()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 3));
        // Claim more entries than the payload holds.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(EntryCountOffset, 2), 4);

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeLeaf_Rejects_MaxEntryCountOverflowAttempt()
    {
        // Hostile header: EntryCount 65535 with wide values — the implied size
        // must be computed without 32-bit overflow and rejected, never read.
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, ushort.MaxValue, entries: 1));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(EntryCountOffset, 2), ushort.MaxValue);

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    /// <summary>
    /// Crafts a raw node payload with a hostile hand-written header (bypassing
    /// the serializers, which refuse to build oversized nodes) and a body of
    /// exactly <paramref name="bodyLength"/> zero bytes.
    /// </summary>
    private static byte[] HostilePayload(
        BTreeNodeKind kind, byte keySize, ushort valueSize, ushort entryCount, int bodyLength)
    {
        var payload = new byte[BTreeNodeSerializer.NodeHeaderSize + bodyLength];
        payload[0] = (byte)kind;
        payload[1] = BTreeNodeSerializer.CurrentNodeVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), (ushort)BTreeIndexKind.PrimaryEmail);
        payload[KeySizeOffset] = keySize;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ValueSizeOffset, 2), valueSize);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(EntryCountOffset, 2), entryCount);
        return payload;
    }

    [Fact]
    public void DeserializeLeaf_Rejects_EntryCountWhoseImpliedSizeWraps32BitArithmetic()
    {
        // EntryCount 65535 × entry stride (32 + 65535) = 4,296,933,345 bytes —
        // past int.MaxValue. In unchecked 32-bit arithmetic it wraps to
        // 1,966,049, so we hand the deserializer a payload of exactly that
        // wrapped size: only 64-bit size math rejects it. Accepting would mean
        // reading entries far beyond the buffer the header actually paid for.
        const int wrappedBodySize = 1_966_049;
        var payload = HostilePayload(
            BTreeNodeKind.Leaf, keySize: 32, valueSize: ushort.MaxValue,
            entryCount: ushort.MaxValue, bodyLength: wrappedBodySize);

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeInternal_Rejects_EntryCountWhoseImpliedSizeWraps32BitArithmetic()
    {
        // Internal body: 65535 keys × 2 + 65536 children × 65535 =
        // 4,295,032,830 bytes, which wraps to 65,534 in unchecked 32-bit
        // arithmetic. Feed exactly the wrapped size — must still be rejected.
        const int wrappedBodySize = 65_534;
        var payload = HostilePayload(
            BTreeNodeKind.Internal, keySize: 2, valueSize: ushort.MaxValue,
            entryCount: ushort.MaxValue, bodyLength: wrappedBodySize);

        var result = BTreeNodeSerializer.DeserializeInternal(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeHeader_Rejects_HeaderOnlyPayloadWithNonZeroEntryCount()
    {
        // A bare 12-byte header claiming 1 entry: the bounds check must fire
        // even though not a single body byte exists to read.
        var payload = HostilePayload(
            BTreeNodeKind.Leaf, keySize: 32, valueSize: 16, entryCount: 1, bodyLength: 0);

        var result = BTreeNodeSerializer.DeserializeHeader(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeLeaf_Rejects_TrailingBytes()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 3));
        var oversized = payload.Concat(new byte[] { 0xAA }).ToArray();

        var result = BTreeNodeSerializer.DeserializeLeaf(oversized);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void DeserializeLeaf_Rejects_TruncatedPayload()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 3));

        var result = BTreeNodeSerializer.DeserializeLeaf(payload.AsSpan(0, payload.Length - 1));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void DeserializeInternal_Rejects_EntryCountOverflowingPayload()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.PrimaryEmail, 32, 48, keys: 3));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(EntryCountOffset, 2), 5);

        var result = BTreeNodeSerializer.DeserializeInternal(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeInternal_Rejects_TruncatedPayload()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.PrimaryEmail, 32, 48, keys: 3));

        var result = BTreeNodeSerializer.DeserializeInternal(payload.AsSpan(0, payload.Length - 1));

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeInternal_Rejects_TrailingBytes()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.PrimaryEmail, 32, 48, keys: 3));
        var oversized = payload.Concat(new byte[] { 0xAA }).ToArray();

        var result = BTreeNodeSerializer.DeserializeInternal(oversized);

        Assert.True(result.IsFailure);
        Assert.Contains("truncated or corrupt", result.Error);
    }

    [Fact]
    public void DeserializeHeader_Rejects_KeySizeZero()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 1, 16, entries: 0));
        payload[KeySizeOffset] = 0;

        var result = BTreeNodeSerializer.DeserializeHeader(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("KeySize", result.Error);
    }

    [Fact]
    public void DeserializeInternal_Rejects_ValueSizeZero()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.PrimaryEmail, 32, 48, keys: 0));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(ValueSizeOffset, 2), 0);

        var result = BTreeNodeSerializer.DeserializeInternal(payload.AsSpan(0, BTreeNodeSerializer.NodeHeaderSize));

        Assert.True(result.IsFailure);
        Assert.Contains("ValueSize", result.Error);
    }

    [Fact]
    public void DeserializeHeader_Rejects_UnknownNodeKind()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 1));
        payload[0] = 2;

        var result = BTreeNodeSerializer.DeserializeHeader(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("NodeKind", result.Error);
    }

    [Fact]
    public void DeserializeHeader_Rejects_UnsupportedNodeVersion()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 1));
        payload[1] = 2;

        var result = BTreeNodeSerializer.DeserializeHeader(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("version", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DeserializeHeader_Rejects_NonZeroReservedBytes(int reservedByte)
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 1));
        payload[ReservedOffset + reservedByte] = 1;

        var result = BTreeNodeSerializer.DeserializeHeader(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("reserved", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeLeaf_Rejects_InternalNodePayload()
    {
        var payload = BTreeNodeSerializer.SerializeInternal(
            SampleInternal(BTreeIndexKind.PrimaryEmail, 32, 48, keys: 2));

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("leaf", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeInternal_Rejects_LeafNodePayload()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.PrimaryEmail, 32, 16, entries: 2));

        var result = BTreeNodeSerializer.DeserializeInternal(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("internal", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeLeaf_Rejects_UnsortedKeys()
    {
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf(BTreeIndexKind.BlockLocation, 16, 16, entries: 2));
        // Swap the two keys so ordering is violated (entry stride 32, key width 16).
        var span = payload.AsSpan(BTreeNodeSerializer.NodeHeaderSize);
        var key0 = span.Slice(0, 16).ToArray();
        span.Slice(32, 16).CopyTo(span.Slice(0, 16));
        key0.CopyTo(span.Slice(32, 16));

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsFailure);
        Assert.Contains("ascending", result.Error);
    }

    [Fact]
    public void DeserializeHeader_Accepts_UnregisteredIndexKind()
    {
        // Widths are declared per node, so unknown kinds (reserved/experimental)
        // still parse — future indexes need no format change.
        var payload = BTreeNodeSerializer.SerializeLeaf(
            SampleLeaf((BTreeIndexKind)100, 8, 4, entries: 2));

        var result = BTreeNodeSerializer.DeserializeLeaf(payload);

        Assert.True(result.IsSuccess);
        Assert.Equal((BTreeIndexKind)100, result.Value.IndexKind);
    }

    // ---- Capacities at the 4096-byte target node (spec Section 6.1) ----

    [Fact]
    public void UsableBodySize_Is3988()
    {
        // Spec Section 6.1: "3988 usable after 96 B block overhead + 12 B node
        // header" at the 4096-byte target node. Pin every term of the formula.
        Assert.Equal(4096, BTreeNodeCapacity.TargetNodeSize);
        Assert.Equal(12, BTreeNodeSerializer.NodeHeaderSize);
        Assert.Equal(
            BTreeNodeCapacity.TargetNodeSize - BlockSerializer.FixedOverhead - BTreeNodeSerializer.NodeHeaderSize,
            BTreeNodeCapacity.UsableBodySize);
        Assert.Equal(3988, BTreeNodeCapacity.UsableBodySize);
    }

    [Theory]
    // Spec Section 6.1 registry: Kind 0 PrimaryEmail — EmailHashedID (32) key,
    // BlockId (16) leaf value, ChildBlockId (16) + ChildHash (32) = 48 child record.
    [InlineData(BTreeIndexKind.PrimaryEmail, 32, 16, 48)]
    // Kind 1 BlockLocation — BlockId (16) key, Offset (8) + Length (8) = 16 leaf
    // value, ChildOffset (8) + ChildHash (32) = 40 child record.
    [InlineData(BTreeIndexKind.BlockLocation, 16, 16, 40)]
    // Kind 2 Date — DateTicks (8) ‖ BlockId (16) = 24 composite key, empty (0)
    // leaf value, ChildBlockId (16) + ChildHash (32) = 48 child record.
    [InlineData(BTreeIndexKind.Date, 24, 0, 48)]
    public void RegistryWidths_MatchSpec(BTreeIndexKind kind, int keySize, int leafValueSize, int childRecordSize)
    {
        var layout = BTreeNodeCapacity.GetLayout(kind);
        Assert.Equal(keySize, layout.KeySize);
        Assert.Equal(leafValueSize, layout.LeafValueSize);
        Assert.Equal(childRecordSize, layout.ChildRecordSize);
    }

    [Theory]
    [InlineData(BTreeIndexKind.PrimaryEmail, 83, 49, 50)]
    [InlineData(BTreeIndexKind.BlockLocation, 124, 70, 71)]
    [InlineData(BTreeIndexKind.Date, 166, 54, 55)]
    public void Capacities_MatchSpec(BTreeIndexKind kind, int leafEntries, int internalKeys, int internalChildren)
    {
        Assert.Equal(leafEntries, BTreeNodeCapacity.MaxLeafEntries(kind));
        Assert.Equal(internalKeys, BTreeNodeCapacity.MaxInternalKeys(kind));
        Assert.Equal(internalChildren, BTreeNodeCapacity.MaxInternalChildren(kind));
    }

    [Fact]
    public void CapacityHelpers_UseDeclaredWidths()
    {
        Assert.Equal(83, BTreeNodeCapacity.MaxLeafEntries(32, 16));
        Assert.Equal(124, BTreeNodeCapacity.MaxLeafEntries(16, 16));
        Assert.Equal(166, BTreeNodeCapacity.MaxLeafEntries(24, 0));
        Assert.Equal(50, BTreeNodeCapacity.MaxInternalChildren(32, 48));
        Assert.Equal(71, BTreeNodeCapacity.MaxInternalChildren(16, 40));
        Assert.Equal(55, BTreeNodeCapacity.MaxInternalChildren(24, 48));
    }

    [Fact]
    public void MaxCapacityNodes_FitTargetNodeSize_AndRoundTrip()
    {
        foreach (var kind in new[] { BTreeIndexKind.PrimaryEmail, BTreeIndexKind.BlockLocation, BTreeIndexKind.Date })
        {
            var (keySize, leafValueSize, childRecordSize) = BTreeNodeCapacity.GetLayout(kind);

            var leafPayload = BTreeNodeSerializer.SerializeLeaf(
                SampleLeaf(kind, keySize, leafValueSize, BTreeNodeCapacity.MaxLeafEntries(kind)));
            Assert.True(leafPayload.Length + BlockSerializer.FixedOverhead <= BTreeNodeCapacity.TargetNodeSize);
            Assert.True(BTreeNodeSerializer.DeserializeLeaf(leafPayload).IsSuccess);

            var internalPayload = BTreeNodeSerializer.SerializeInternal(
                SampleInternal(kind, keySize, childRecordSize, BTreeNodeCapacity.MaxInternalKeys(kind)));
            Assert.True(internalPayload.Length + BlockSerializer.FixedOverhead <= BTreeNodeCapacity.TargetNodeSize);
            Assert.True(BTreeNodeSerializer.DeserializeInternal(internalPayload).IsSuccess);

            // One more entry/key must not fit the target size.
            long overLeaf = BTreeNodeSerializer.GetLeafNodeSize(
                BTreeNodeCapacity.MaxLeafEntries(kind) + 1, keySize, leafValueSize);
            Assert.True(overLeaf + BlockSerializer.FixedOverhead > BTreeNodeCapacity.TargetNodeSize);
            long overInternal = BTreeNodeSerializer.GetInternalNodeSize(
                BTreeNodeCapacity.MaxInternalKeys(kind) + 1, keySize, childRecordSize);
            Assert.True(overInternal + BlockSerializer.FixedOverhead > BTreeNodeCapacity.TargetNodeSize);
        }
    }

    [Fact]
    public void GetLayout_Rejects_KindsWithoutRegistryLayout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BTreeNodeCapacity.GetLayout(BTreeIndexKind.Fts));
        Assert.Throws<ArgumentOutOfRangeException>(() => BTreeNodeCapacity.GetLayout((BTreeIndexKind)7));
    }
}
