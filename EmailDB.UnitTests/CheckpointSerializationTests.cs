using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the variable-length Checkpoint block payload (BlockType 9,
/// EmailDB_FileFormat_Spec.md Section 10.1): the single commit point carrying the
/// root table (ULID + offset hint pairs), a generic secondary-index table, the
/// FileId cross-check, CheckpointSequence, the previous-checkpoint chain link, and
/// live/dead byte accounting.
///
/// Covers the exact little-endian layout at spec offsets, round-trip of every
/// field including empty and maximal secondary-index tables and boundary values,
/// arbitrary IndexKind entries, the walkable previous-checkpoint chain, and
/// bounds-checked rejection of malformed payloads (short, length/count mismatch,
/// negative counters) plus rejection of inconsistent inputs on serialize.
/// </summary>
public class CheckpointSerializationTests
{
    // Field offsets under test (spec Section 10.1 Checkpoint layout).
    private const int FormatVersionOffset = 0;
    private const int CheckpointSequenceOffset = 2;
    private const int FileIdOffset = 10;
    private const int FolderTreeRootOffset = 26;
    private const int PrimaryIndexRootOffset = 50;
    private const int LocationIndexRootOffset = 74;
    private const int MetadataRootOffset = 98;
    private const int KeyStoreRootOffset = 122;
    private const int PreviousCheckpointOffset = 146;
    private const int SecondaryIndexCountOffset = 170;
    private const int SecondaryIndexesOffset = 172;

    private static byte[] Ulid(byte seed) =>
        Enumerable.Range(0, Checkpoint.FileIdSize).Select(i => (byte)(seed + i)).ToArray();

    private static CheckpointRootPointer Pointer(byte seed, long offset) =>
        CheckpointRootPointer.Create(Ulid(seed), offset);

    private static Checkpoint Sample(
        IReadOnlyList<CheckpointSecondaryIndex>? secondaries = null,
        ulong sequence = 42,
        long liveBlockCount = 1000,
        long liveByteCount = 5_000_000,
        long deadByteCount = 250_000) => new()
        {
            FormatVersion = 3,
            CheckpointSequence = sequence,
            FileId = Ulid(0x01),
            FolderTreeRoot = Pointer(0x10, 8192),
            PrimaryIndexRoot = Pointer(0x20, 16384),
            LocationIndexRoot = Pointer(0x30, 24576),
            MetadataRoot = Pointer(0x40, 32768),
            KeyStoreRoot = Pointer(0x50, 40960),
            PreviousCheckpoint = Pointer(0x60, 49152),
            SecondaryIndexes = secondaries ?? Array.Empty<CheckpointSecondaryIndex>(),
            LiveBlockCount = liveBlockCount,
            LiveByteCount = liveByteCount,
            DeadByteCount = deadByteCount,
        };

    private static void AssertPointersEqual(CheckpointRootPointer expected, CheckpointRootPointer actual)
    {
        Assert.Equal(expected.BlockId, actual.BlockId);
        Assert.Equal(expected.Offset, actual.Offset);
    }

    private static void AssertCheckpointsEqual(Checkpoint expected, Checkpoint actual)
    {
        Assert.Equal(expected.FormatVersion, actual.FormatVersion);
        Assert.Equal(expected.CheckpointSequence, actual.CheckpointSequence);
        Assert.Equal(expected.FileId, actual.FileId);
        AssertPointersEqual(expected.FolderTreeRoot, actual.FolderTreeRoot);
        AssertPointersEqual(expected.PrimaryIndexRoot, actual.PrimaryIndexRoot);
        AssertPointersEqual(expected.LocationIndexRoot, actual.LocationIndexRoot);
        AssertPointersEqual(expected.MetadataRoot, actual.MetadataRoot);
        AssertPointersEqual(expected.KeyStoreRoot, actual.KeyStoreRoot);
        AssertPointersEqual(expected.PreviousCheckpoint, actual.PreviousCheckpoint);
        Assert.Equal(expected.SecondaryIndexes.Count, actual.SecondaryIndexes.Count);
        for (int i = 0; i < expected.SecondaryIndexes.Count; i++)
        {
            Assert.Equal(expected.SecondaryIndexes[i].IndexKind, actual.SecondaryIndexes[i].IndexKind);
            AssertPointersEqual(expected.SecondaryIndexes[i].Pointer, actual.SecondaryIndexes[i].Pointer);
        }
        Assert.Equal(expected.LiveBlockCount, actual.LiveBlockCount);
        Assert.Equal(expected.LiveByteCount, actual.LiveByteCount);
        Assert.Equal(expected.DeadByteCount, actual.DeadByteCount);
    }

    // ---- Size constants and exact little-endian layout ----

    [Fact]
    public void SizeConstants_MatchSpecLayout()
    {
        Assert.Equal(172, CheckpointSerializer.FixedPrefixSize);
        Assert.Equal(24, CheckpointSerializer.RootPointerSize);
        Assert.Equal(26, CheckpointSerializer.SecondaryIndexEntrySize);
        Assert.Equal(24, CheckpointSerializer.TrailerSize);
        Assert.Equal(196, CheckpointSerializer.MinPayloadSize);
        Assert.Equal(196, CheckpointSerializer.PayloadSize(0));
        Assert.Equal(196 + 3 * 26, CheckpointSerializer.PayloadSize(3));
    }

    [Fact]
    public void Serialize_EmptyTable_ProducesMinPayloadSize()
    {
        Assert.Equal(CheckpointSerializer.MinPayloadSize, CheckpointSerializer.Serialize(Sample()).Length);
    }

    [Fact]
    public void Serialize_WritesEachFieldLittleEndianAtSpecOffset()
    {
        var secondaries = new[]
        {
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, Ulid(0x70), 0x1122334455667788),
        };
        var checkpoint = new Checkpoint
        {
            FormatVersion = 0x0304,
            CheckpointSequence = 0x0102030405060708,
            FileId = Ulid(0x01),
            FolderTreeRoot = Pointer(0x10, 0x0A0B0C0D),
            PrimaryIndexRoot = Pointer(0x20, 0x1000),
            LocationIndexRoot = Pointer(0x30, 0x2000),
            MetadataRoot = Pointer(0x40, 0x3000),
            KeyStoreRoot = Pointer(0x50, 0x4000),
            PreviousCheckpoint = Pointer(0x60, 0x5000),
            SecondaryIndexes = secondaries,
            LiveBlockCount = 0x0011223344556677,
            LiveByteCount = 0x1000000000000000,
            DeadByteCount = 0x0FEDCBA987654321,
        };

        var span = CheckpointSerializer.Serialize(checkpoint).AsSpan();

        Assert.Equal((ushort)0x0304, BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(FormatVersionOffset, 2)));
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(CheckpointSequenceOffset, 8)));
        Assert.True(span.Slice(FileIdOffset, 16).SequenceEqual(Ulid(0x01)));

        AssertPointerAt(span, FolderTreeRootOffset, Ulid(0x10), 0x0A0B0C0D);
        AssertPointerAt(span, PrimaryIndexRootOffset, Ulid(0x20), 0x1000);
        AssertPointerAt(span, LocationIndexRootOffset, Ulid(0x30), 0x2000);
        AssertPointerAt(span, MetadataRootOffset, Ulid(0x40), 0x3000);
        AssertPointerAt(span, KeyStoreRootOffset, Ulid(0x50), 0x4000);
        AssertPointerAt(span, PreviousCheckpointOffset, Ulid(0x60), 0x5000);

        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(SecondaryIndexCountOffset, 2)));
        var entry = span.Slice(SecondaryIndexesOffset, CheckpointSerializer.SecondaryIndexEntrySize);
        Assert.Equal((ushort)BTreeIndexKind.Date, BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(0, 2)));
        Assert.True(entry.Slice(2, 16).SequenceEqual(Ulid(0x70)));
        Assert.Equal(0x1122334455667788L, BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(18, 8)));

        int trailer = SecondaryIndexesOffset + CheckpointSerializer.SecondaryIndexEntrySize;
        Assert.Equal(0x0011223344556677L, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(trailer, 8)));
        Assert.Equal(0x1000000000000000L, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(trailer + 8, 8)));
        Assert.Equal(0x0FEDCBA987654321L, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(trailer + 16, 8)));
    }

    private static void AssertPointerAt(ReadOnlySpan<byte> span, int offset, byte[] expectedId, long expectedOffset)
    {
        Assert.True(span.Slice(offset, 16).SequenceEqual(expectedId));
        Assert.Equal(expectedOffset, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 16, 8)));
    }

    // ---- Round trip ----

    [Fact]
    public void RoundTrip_EmptySecondaryTable_PreservesEveryField()
    {
        var original = Sample();
        var result = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(original));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value.SecondaryIndexes);
        AssertCheckpointsEqual(original, result.Value);
    }

    [Fact]
    public void RoundTrip_WithSecondaryTable_PreservesEveryField()
    {
        var secondaries = new[]
        {
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, Ulid(0x11), 100),
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Fts, Ulid(0x22), 200),
            CheckpointSecondaryIndex.Create((BTreeIndexKind)100, Ulid(0x33), 300), // experimental kind
        };
        var original = Sample(secondaries);

        var result = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(original));

        Assert.True(result.IsSuccess, result.Error);
        AssertCheckpointsEqual(original, result.Value);
    }

    // ---- Acceptance: secondary index table round-trips arbitrary IndexKind entries ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(100)]     // experimental
    [InlineData(50_000)]  // reserved/unregistered but valid ushort
    [InlineData(65_535)]  // max ushort
    public void SecondaryTable_RoundTripsArbitraryIndexKind(int kindValue)
    {
        var kind = (BTreeIndexKind)kindValue;
        var secondaries = new[] { CheckpointSecondaryIndex.Create(kind, Ulid(0x44), 4096) };

        var round = CheckpointSerializer.Deserialize(
            CheckpointSerializer.Serialize(Sample(secondaries))).Value;

        Assert.Single(round.SecondaryIndexes);
        Assert.Equal(kind, round.SecondaryIndexes[0].IndexKind);
        Assert.Equal(Ulid(0x44), round.SecondaryIndexes[0].Pointer.BlockId);
        Assert.Equal(4096, round.SecondaryIndexes[0].Pointer.Offset);
    }

    [Fact]
    public void Deserialize_UnknownIndexKinds_NotRejected_ForwardCompatible()
    {
        // Forward compatibility (spec Sections 6.1, 10.1): a reader on an older build
        // encountering only future/experimental IndexKind values it does not recognize
        // must accept the checkpoint, never reject it, and surface the raw kinds intact.
        var unknownOnly = new[]
        {
            CheckpointSecondaryIndex.Create((BTreeIndexKind)50, Ulid(0x11), 1),     // reserved 4-99
            CheckpointSecondaryIndex.Create((BTreeIndexKind)100, Ulid(0x22), 2),    // experimental 100+
            CheckpointSecondaryIndex.Create((BTreeIndexKind)40_000, Ulid(0x33), 3), // far-future
            CheckpointSecondaryIndex.Create((BTreeIndexKind)ushort.MaxValue, Ulid(0x44), 4),
        };
        var original = Sample(unknownOnly);

        var result = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(original));

        Assert.True(result.IsSuccess, result.Error);
        AssertCheckpointsEqual(original, result.Value);
        Assert.Equal(
            new[] { 50, 100, 40_000, ushort.MaxValue },
            result.Value.SecondaryIndexes.Select(e => (int)e.IndexKind));
    }

    [Fact]
    public void SecondaryTable_PreservesDuplicateKindEntries_InOrder()
    {
        // The spec's secondary-index table (Section 10.1) is a generic list with no
        // uniqueness constraint: repeated IndexKind values with distinct pointers must
        // survive round-trip with both their multiplicity and their order preserved.
        var duplicates = new[]
        {
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, Ulid(0x11), 100),
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, Ulid(0x22), 200),
            CheckpointSecondaryIndex.Create((BTreeIndexKind)500, Ulid(0x33), 300),
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, Ulid(0x44), 400),
            CheckpointSecondaryIndex.Create((BTreeIndexKind)500, Ulid(0x55), 500),
        };
        var original = Sample(duplicates);

        var result = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(original));

        Assert.True(result.IsSuccess, result.Error);
        // Multiplicity preserved: three Date entries and two kind-500 entries.
        Assert.Equal(3, result.Value.SecondaryIndexes.Count(e => e.IndexKind == BTreeIndexKind.Date));
        Assert.Equal(2, result.Value.SecondaryIndexes.Count(e => (int)e.IndexKind == 500));
        // Order and per-entry pointers preserved exactly (would catch dedup/reordering).
        AssertCheckpointsEqual(original, result.Value);
    }

    [Fact]
    public void RoundTrip_MaxSecondaryEntries_Succeeds()
    {
        var secondaries = Enumerable.Range(0, Checkpoint.MaxSecondaryIndexCount)
            .Select(i => CheckpointSecondaryIndex.Create((BTreeIndexKind)(i % 200), Ulid((byte)i), i))
            .ToArray();
        var original = Sample(secondaries);

        var bytes = CheckpointSerializer.Serialize(original);
        Assert.Equal(CheckpointSerializer.PayloadSize(Checkpoint.MaxSecondaryIndexCount), bytes.Length);

        var result = CheckpointSerializer.Deserialize(bytes);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(Checkpoint.MaxSecondaryIndexCount, result.Value.SecondaryIndexes.Count);
        AssertCheckpointsEqual(original, result.Value);
    }

    [Fact]
    public void RoundTrip_BoundaryValues_Preserved()
    {
        var original = new Checkpoint
        {
            FormatVersion = ushort.MaxValue,
            CheckpointSequence = ulong.MaxValue,
            FileId = Ulid(0xFF),
            FolderTreeRoot = Pointer(0x01, long.MaxValue),
            PrimaryIndexRoot = Pointer(0x02, 0),
            LocationIndexRoot = Pointer(0x03, long.MaxValue),
            MetadataRoot = CheckpointRootPointer.None,
            KeyStoreRoot = CheckpointRootPointer.None,
            PreviousCheckpoint = CheckpointRootPointer.None,
            SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
            LiveBlockCount = long.MaxValue,
            LiveByteCount = long.MaxValue,
            DeadByteCount = long.MaxValue,
        };

        var round = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(original)).Value;

        AssertCheckpointsEqual(original, round);
        Assert.True(round.KeyStoreRoot.IsAbsent);
        Assert.True(round.PreviousCheckpoint.IsAbsent);
    }

    // ---- FileId cross-check field round-trips exactly ----

    [Fact]
    public void FileId_RoundTripsExactly()
    {
        var fileId = Ulid(0xAB);
        var checkpoint = Sample();
        var withId = new Checkpoint
        {
            FormatVersion = checkpoint.FormatVersion,
            CheckpointSequence = checkpoint.CheckpointSequence,
            FileId = fileId,
            FolderTreeRoot = checkpoint.FolderTreeRoot,
            PrimaryIndexRoot = checkpoint.PrimaryIndexRoot,
            LocationIndexRoot = checkpoint.LocationIndexRoot,
            MetadataRoot = checkpoint.MetadataRoot,
            KeyStoreRoot = checkpoint.KeyStoreRoot,
            PreviousCheckpoint = checkpoint.PreviousCheckpoint,
            SecondaryIndexes = checkpoint.SecondaryIndexes,
            LiveBlockCount = checkpoint.LiveBlockCount,
            LiveByteCount = checkpoint.LiveByteCount,
            DeadByteCount = checkpoint.DeadByteCount,
        };

        var round = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(withId)).Value;
        Assert.Equal(fileId, round.FileId);
    }

    // ---- Acceptance: CheckpointSequence monotonic and previous-checkpoint chain walkable ----

    [Fact]
    public void PreviousCheckpointChain_IsWalkableAcrossRoundTrips()
    {
        // Build a chain of three serialized checkpoints, each pointing at the prior
        // one's block id, with a strictly increasing CheckpointSequence.
        var idByBlock = new Dictionary<string, byte[]>();

        static string Key(byte[] id) => Convert.ToHexString(id);

        var checkpoints = new List<Checkpoint>();
        CheckpointRootPointer prev = CheckpointRootPointer.None;
        ulong seq = 0;
        for (int i = 0; i < 3; i++)
        {
            var blockId = Ulid((byte)(0x80 + i));
            var cp = new Checkpoint
            {
                FormatVersion = 3,
                CheckpointSequence = seq,
                FileId = Ulid(0x01),
                FolderTreeRoot = Pointer(0x10, 8192),
                PrimaryIndexRoot = Pointer(0x20, 16384),
                LocationIndexRoot = Pointer(0x30, 24576),
                MetadataRoot = Pointer(0x40, 32768),
                KeyStoreRoot = CheckpointRootPointer.None,
                PreviousCheckpoint = prev,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = i,
                LiveByteCount = i * 10,
                DeadByteCount = i,
            };
            checkpoints.Add(cp);
            idByBlock[Key(blockId)] = CheckpointSerializer.Serialize(cp);
            prev = CheckpointRootPointer.Create(blockId, 1000 + i);
            seq++;
        }

        // Walk from the newest (block 0x82) back through the chain, deserializing each.
        var newestId = Ulid(0x82);
        var walkedSequences = new List<ulong>();
        byte[]? currentId = newestId;
        while (currentId is not null)
        {
            var cp = CheckpointSerializer.Deserialize(idByBlock[Key(currentId)]).Value;
            walkedSequences.Add(cp.CheckpointSequence);
            currentId = cp.PreviousCheckpoint.IsAbsent ? null : cp.PreviousCheckpoint.BlockId;
        }

        // Chain walked newest -> oldest, sequences strictly decreasing.
        Assert.Equal(new ulong[] { 2, 1, 0 }, walkedSequences);
        for (int i = 1; i < walkedSequences.Count; i++)
            Assert.True(walkedSequences[i] < walkedSequences[i - 1]);
    }

    // ---- Deserialize: bounds-checked rejection of malformed payloads ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(195)]
    public void Deserialize_Rejects_TooShort(int length)
    {
        var result = CheckpointSerializer.Deserialize(new byte[length]);
        Assert.True(result.IsFailure);
        Assert.Contains("at least", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_LengthCountMismatch_TooLong()
    {
        // Declares 0 secondary entries but the buffer is longer than 196.
        var payload = CheckpointSerializer.Serialize(Sample());
        var tooLong = new byte[payload.Length + CheckpointSerializer.SecondaryIndexEntrySize];
        payload.CopyTo(tooLong.AsSpan());

        var result = CheckpointSerializer.Deserialize(tooLong);
        Assert.True(result.IsFailure);
        Assert.Contains("does not match", result.Error);
    }

    [Fact]
    public void Deserialize_Rejects_LengthCountMismatch_TruncatedTable()
    {
        var secondaries = new[]
        {
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Date, Ulid(0x11), 100),
            CheckpointSecondaryIndex.Create(BTreeIndexKind.Fts, Ulid(0x22), 200),
        };
        var payload = CheckpointSerializer.Serialize(Sample(secondaries));
        // Drop the last entry's bytes but leave SecondaryIndexCount = 2.
        var truncated = payload.AsSpan(0, payload.Length - CheckpointSerializer.SecondaryIndexEntrySize).ToArray();

        var result = CheckpointSerializer.Deserialize(truncated);
        Assert.True(result.IsFailure);
        Assert.Contains("does not match", result.Error);
    }

    [Theory]
    [InlineData("LiveBlockCount")]
    [InlineData("LiveByteCount")]
    [InlineData("DeadByteCount")]
    public void Deserialize_Rejects_NegativeCounter(string counter)
    {
        var payload = CheckpointSerializer.Serialize(Sample());
        int trailer = SecondaryIndexesOffset; // no secondary entries
        int fieldOffset = counter switch
        {
            "LiveBlockCount" => trailer,
            "LiveByteCount" => trailer + 8,
            "DeadByteCount" => trailer + 16,
            _ => throw new ArgumentOutOfRangeException(nameof(counter)),
        };
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(fieldOffset, 8), -1);

        var result = CheckpointSerializer.Deserialize(payload);
        Assert.True(result.IsFailure);
        Assert.Contains(counter, result.Error);
    }

    // ---- Serialize: rejects inconsistent inputs (programming errors) ----

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(0)]
    public void Serialize_Rejects_WrongFileIdWidth(int width)
    {
        var checkpoint = Sample();
        var bad = new Checkpoint
        {
            FormatVersion = checkpoint.FormatVersion,
            CheckpointSequence = checkpoint.CheckpointSequence,
            FileId = new byte[width],
            FolderTreeRoot = checkpoint.FolderTreeRoot,
            PrimaryIndexRoot = checkpoint.PrimaryIndexRoot,
            LocationIndexRoot = checkpoint.LocationIndexRoot,
            MetadataRoot = checkpoint.MetadataRoot,
            KeyStoreRoot = checkpoint.KeyStoreRoot,
            PreviousCheckpoint = checkpoint.PreviousCheckpoint,
            SecondaryIndexes = checkpoint.SecondaryIndexes,
            LiveBlockCount = checkpoint.LiveBlockCount,
            LiveByteCount = checkpoint.LiveByteCount,
            DeadByteCount = checkpoint.DeadByteCount,
        };

        Assert.Throws<ArgumentException>(() => CheckpointSerializer.Serialize(bad));
    }

    [Fact]
    public void Serialize_Rejects_MalformedRootPointer()
    {
        var checkpoint = Sample();
        var bad = new Checkpoint
        {
            FormatVersion = checkpoint.FormatVersion,
            CheckpointSequence = checkpoint.CheckpointSequence,
            FileId = checkpoint.FileId,
            FolderTreeRoot = new CheckpointRootPointer { BlockId = new byte[15], Offset = 0 },
            PrimaryIndexRoot = checkpoint.PrimaryIndexRoot,
            LocationIndexRoot = checkpoint.LocationIndexRoot,
            MetadataRoot = checkpoint.MetadataRoot,
            KeyStoreRoot = checkpoint.KeyStoreRoot,
            PreviousCheckpoint = checkpoint.PreviousCheckpoint,
            SecondaryIndexes = checkpoint.SecondaryIndexes,
            LiveBlockCount = checkpoint.LiveBlockCount,
            LiveByteCount = checkpoint.LiveByteCount,
            DeadByteCount = checkpoint.DeadByteCount,
        };

        Assert.Throws<ArgumentException>(() => CheckpointSerializer.Serialize(bad));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Serialize_Rejects_NegativeCounters(long value)
    {
        Assert.Throws<ArgumentException>(() => CheckpointSerializer.Serialize(Sample(liveBlockCount: value)));
        Assert.Throws<ArgumentException>(() => CheckpointSerializer.Serialize(Sample(liveByteCount: value)));
        Assert.Throws<ArgumentException>(() => CheckpointSerializer.Serialize(Sample(deadByteCount: value)));
    }

    // ---- CheckpointRootPointer helpers ----

    [Fact]
    public void RootPointer_None_IsAbsent_AllZeroUlid()
    {
        var none = CheckpointRootPointer.None;
        Assert.True(none.IsAbsent);
        Assert.Equal(0, none.Offset);
        Assert.Equal(new byte[16], none.BlockId);
    }

    [Fact]
    public void RootPointer_Create_Rejects_NegativeOffset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CheckpointRootPointer.Create(Ulid(1), -1));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void RootPointer_Create_Rejects_WrongUlidWidth(int width)
    {
        Assert.Throws<ArgumentException>(() => CheckpointRootPointer.Create(new byte[width], 0));
    }
}
