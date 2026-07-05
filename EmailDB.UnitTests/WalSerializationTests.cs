using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the variable-length WAL block payload (BlockType 1,
/// EmailDB_FileFormat_Spec.md Section 10.4):
///   WalSequence (8) + CheckpointBlockId (16) + EntryCount (4) +
///   EntryCount × { Op (1) + Key (32) + BlockId (16) + AuxLength (4) + Aux }.
/// Covers exact little-endian layout, round-trip for every op kind with empty and
/// max/large entry sets, monotonic-preserving round-trips, and bounds-checked
/// rejection of malformed payloads (truncated, oversized, undefined op, all-zero
/// checkpoint fence, negative counts).
/// </summary>
public class WalSerializationTests
{
    // Fixed-prefix field offsets under test (spec Section 10.4 layout).
    private const int WalSequenceOffset = 0;
    private const int CheckpointBlockIdOffset = 8;
    private const int EntryCountOffset = 24;
    private const int EntriesOffset = 28;

    private static byte[] Ulid(byte seed) =>
        Enumerable.Range(0, WalBlock.CheckpointBlockIdSize).Select(i => (byte)(seed + i)).ToArray();

    private static byte[] Key(byte seed) =>
        Enumerable.Range(0, WalEntry.KeySize).Select(i => (byte)(seed * 3 + i)).ToArray();

    private static WalBlock Sample(ulong sequence = 7, params WalEntry[] entries) => new()
    {
        WalSequence = sequence,
        CheckpointBlockId = Ulid(0x40),
        Entries = entries,
    };

    private static void Ok<T>(Result<T> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    // ---- Fixed-prefix layout ----

    [Fact]
    public void Serialize_EmptyBlock_IsExactlyFixedPrefix()
    {
        Assert.Equal(28, WalSerializer.FixedPrefixSize);
        Assert.Equal(28, WalSerializer.Serialize(Sample()).Length);
    }

    [Fact]
    public void Serialize_WritesPrefixFieldsLittleEndianAtSpecOffset()
    {
        var wal = new WalBlock
        {
            WalSequence = 0x1122334455667788,
            CheckpointBlockId = Ulid(0xA0),
            Entries = Array.Empty<WalEntry>(),
        };

        var span = WalSerializer.Serialize(wal).AsSpan();

        Assert.Equal(0x1122334455667788UL,
            BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(WalSequenceOffset, 8)));
        Assert.True(span.Slice(CheckpointBlockIdOffset, 16).SequenceEqual(Ulid(0xA0)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(span.Slice(EntryCountOffset, 4)));
    }

    [Fact]
    public void Serialize_WritesEntryFieldsAtExpectedOffsets()
    {
        var entry = WalEntry.FolderOp(Key(0x05), aux: new byte[] { 1, 2, 3 }, blockId: Ulid(0x30));
        var span = WalSerializer.Serialize(Sample(1, entry)).AsSpan();

        int c = EntriesOffset;
        Assert.Equal((byte)WalOpKind.FolderOp, span[c]);
        Assert.True(span.Slice(c + 1, WalEntry.KeySize).SequenceEqual(Key(0x05)));
        Assert.True(span.Slice(c + 1 + WalEntry.KeySize, WalEntry.BlockIdSize).SequenceEqual(Ulid(0x30)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(
            span.Slice(c + 1 + WalEntry.KeySize + WalEntry.BlockIdSize, 4)));
        Assert.True(span.Slice(c + WalSerializer.EntryFixedSize, 3).SequenceEqual(new byte[] { 1, 2, 3 }));
    }

    // ---- Round trip: every op kind ----

    [Fact]
    public void RoundTrip_InsertDeleteFolderOps_PreserveEveryField()
    {
        var insert = WalEntry.Insert(Key(0x01), Ulid(0x11));
        var delete = WalEntry.Delete(Key(0x02));
        var folder = WalEntry.FolderOp(Key(0x03), aux: new byte[] { 9, 8, 7, 6, 5 }, blockId: Ulid(0x22));

        var original = Sample(42, insert, delete, folder);
        var result = WalSerializer.Deserialize(WalSerializer.Serialize(original));
        Ok(result);
        var round = result.Value;

        Assert.Equal(42UL, round.WalSequence);
        Assert.Equal(Ulid(0x40), round.CheckpointBlockId);
        Assert.Equal(3, round.Entries.Count);

        Assert.Equal(WalOpKind.Insert, round.Entries[0].Op);
        Assert.Equal(Key(0x01), round.Entries[0].Key);
        Assert.Equal(Ulid(0x11), round.Entries[0].BlockId);
        Assert.Empty(round.Entries[0].Aux);

        Assert.Equal(WalOpKind.Delete, round.Entries[1].Op);
        Assert.Equal(Key(0x02), round.Entries[1].Key);
        Assert.Equal(WalEntry.NoBlockId, round.Entries[1].BlockId);
        Assert.Empty(round.Entries[1].Aux);

        Assert.Equal(WalOpKind.FolderOp, round.Entries[2].Op);
        Assert.Equal(Key(0x03), round.Entries[2].Key);
        Assert.Equal(Ulid(0x22), round.Entries[2].BlockId);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, round.Entries[2].Aux);
    }

    [Fact]
    public void RoundTrip_FolderOp_LargeNonTrivialAux_InMixedBlock_IsByteIdentical()
    {
        // The 5000-entry test varies aux LENGTH but zero-fills its bytes; the
        // richest aux CONTENT verified elsewhere is 5 bytes. A FolderOp is the
        // spec's variable-length carrier (Section 10.4), so prove a large aux of
        // non-trivial (non-zero, varied) bytes survives byte-identically, and that
        // the entries on either side of it keep their order and fields across the
        // big cursor advance.
        var bigAux = new byte[8192];
        for (int i = 0; i < bigAux.Length; i++)
            bigAux[i] = (byte)((i * 31 + 7) ^ (i >> 3));
        Assert.Contains(bigAux, b => b != 0); // genuinely non-trivial content

        var insert = WalEntry.Insert(Key(0x0A), Ulid(0x1A));
        var folder = WalEntry.FolderOp(Key(0x0B), aux: bigAux, blockId: Ulid(0x2B));
        var delete = WalEntry.Delete(Key(0x0C));

        var result = WalSerializer.Deserialize(
            WalSerializer.Serialize(Sample(99, insert, folder, delete)));
        Ok(result);
        var round = result.Value;

        Assert.Equal(3, round.Entries.Count);

        Assert.Equal(WalOpKind.Insert, round.Entries[0].Op);
        Assert.Equal(Key(0x0A), round.Entries[0].Key);
        Assert.Equal(Ulid(0x1A), round.Entries[0].BlockId);
        Assert.Empty(round.Entries[0].Aux);

        Assert.Equal(WalOpKind.FolderOp, round.Entries[1].Op);
        Assert.Equal(Key(0x0B), round.Entries[1].Key);
        Assert.Equal(Ulid(0x2B), round.Entries[1].BlockId);
        Assert.Equal(bigAux, round.Entries[1].Aux); // full byte-for-byte aux fidelity

        Assert.Equal(WalOpKind.Delete, round.Entries[2].Op);
        Assert.Equal(Key(0x0C), round.Entries[2].Key);
        Assert.Equal(WalEntry.NoBlockId, round.Entries[2].BlockId);
        Assert.Empty(round.Entries[2].Aux);
    }

    [Fact]
    public void RoundTrip_EmptyEntries()
    {
        var result = WalSerializer.Deserialize(WalSerializer.Serialize(Sample(0)));
        Ok(result);
        Assert.Empty(result.Value.Entries);
        Assert.Equal(0UL, result.Value.WalSequence);
    }

    [Fact]
    public void RoundTrip_ManyEntries_WithVaryingAuxSizes()
    {
        var entries = new List<WalEntry>();
        for (int i = 0; i < 5000; i++)
        {
            WalOpKind kind = (WalOpKind)(i % 3 + 1);
            entries.Add(new WalEntry
            {
                Op = kind,
                Key = Key((byte)i),
                BlockId = kind == WalOpKind.Delete ? WalEntry.NoBlockId : Ulid((byte)i),
                Aux = kind == WalOpKind.FolderOp ? new byte[i % 17] : Array.Empty<byte>(),
            });
        }

        var original = new WalBlock
        {
            WalSequence = ulong.MaxValue,
            CheckpointBlockId = Ulid(0x40),
            Entries = entries,
        };

        var result = WalSerializer.Deserialize(WalSerializer.Serialize(original));
        Ok(result);
        Assert.Equal(ulong.MaxValue, result.Value.WalSequence);
        Assert.Equal(5000, result.Value.Entries.Count);
        for (int i = 0; i < 5000; i++)
        {
            Assert.Equal(entries[i].Op, result.Value.Entries[i].Op);
            Assert.Equal(entries[i].Key, result.Value.Entries[i].Key);
            Assert.Equal(entries[i].BlockId, result.Value.Entries[i].BlockId);
            Assert.Equal(entries[i].Aux, result.Value.Entries[i].Aux);
        }
    }

    // ---- Deserialize: bounds-checked rejection ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(27)]
    public void Deserialize_Rejects_TooShortForPrefix(int length)
    {
        var result = WalSerializer.Deserialize(new byte[length]);
        Assert.True(result.IsFailure);
        Assert.Contains("at least", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_AllZeroCheckpointFence()
    {
        var payload = new byte[WalSerializer.FixedPrefixSize]; // all-zero checkpoint id, zero entries
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("CheckpointBlockId", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_NegativeEntryCount()
    {
        var payload = WalSerializer.Serialize(Sample(1));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(EntryCountOffset, 4), -1);
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("EntryCount", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_EntryCountThatOverrunsPayload()
    {
        var payload = WalSerializer.Serialize(Sample(1, WalEntry.Insert(Key(1), Ulid(1))));
        // Claim two entries when only one is present.
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(EntryCountOffset, 4), 2);
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("truncated", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_UndefinedOpByte()
    {
        var payload = WalSerializer.Serialize(Sample(1, WalEntry.Insert(Key(1), Ulid(1))));
        payload[EntriesOffset] = 0xFF; // undefined op
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("undefined op", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_NegativeAuxLength()
    {
        var payload = WalSerializer.Serialize(Sample(1, WalEntry.Insert(Key(1), Ulid(1))));
        int auxLenOffset = EntriesOffset + 1 + WalEntry.KeySize + WalEntry.BlockIdSize;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(auxLenOffset, 4), -5);
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("AuxLength", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_AuxLengthOverrunningPayload()
    {
        var payload = WalSerializer.Serialize(Sample(1, WalEntry.Insert(Key(1), Ulid(1))));
        int auxLenOffset = EntriesOffset + 1 + WalEntry.KeySize + WalEntry.BlockIdSize;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(auxLenOffset, 4), 100);
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("truncated", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_TrailingBytes()
    {
        var payload = WalSerializer.Serialize(Sample(1, WalEntry.Insert(Key(1), Ulid(1))));
        Array.Resize(ref payload, payload.Length + 4); // extra trailing bytes
        var result = WalSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains("does not match", result.Error);
    }

    // ---- Serialize: rejects inconsistent payloads (programming errors) ----

    [Fact]
    public void Serialize_Rejects_AllZeroCheckpointFence()
    {
        var wal = new WalBlock
        {
            WalSequence = 1,
            CheckpointBlockId = new byte[WalBlock.CheckpointBlockIdSize],
            Entries = Array.Empty<WalEntry>(),
        };
        Assert.Throws<ArgumentException>(() => WalSerializer.Serialize(wal));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void Serialize_Rejects_WrongCheckpointBlockIdWidth(int width)
    {
        var wal = new WalBlock
        {
            WalSequence = 1,
            CheckpointBlockId = Enumerable.Repeat((byte)1, width).ToArray(),
            Entries = Array.Empty<WalEntry>(),
        };
        Assert.Throws<ArgumentException>(() => WalSerializer.Serialize(wal));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Serialize_Rejects_WrongKeyWidth(int width)
    {
        var entry = new WalEntry
        {
            Op = WalOpKind.Insert,
            Key = new byte[width],
            BlockId = Ulid(1),
            Aux = Array.Empty<byte>(),
        };
        Assert.Throws<ArgumentException>(() => WalSerializer.Serialize(Sample(1, entry)));
    }

    [Fact]
    public void Serialize_Rejects_UndefinedOp()
    {
        var entry = new WalEntry
        {
            Op = (WalOpKind)200,
            Key = Key(1),
            BlockId = Ulid(1),
            Aux = Array.Empty<byte>(),
        };
        Assert.Throws<ArgumentException>(() => WalSerializer.Serialize(Sample(1, entry)));
    }
}
