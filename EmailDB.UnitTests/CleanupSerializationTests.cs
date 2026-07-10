using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the variable-length Cleanup block payload (BlockType 3,
/// EmailDB_FileFormat_Spec.md Section 5; docs/Compaction.md Section 3): the durable
/// audit record of which BlockIds a supersession batch retired, and by how many bytes.
/// Covers the exact little-endian layout, round-trip of every field including the empty
/// and multi-entry cases, and bounds-checked rejection of malformed payloads.
/// </summary>
public class CleanupSerializationTests
{
    private static byte[] Ulid(byte seed) =>
        Enumerable.Range(0, SupersededBlockRecord.BlockIdSize).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void Round_trips_a_multi_entry_block()
    {
        var block = new CleanupBlock
        {
            CheckpointSequence = 0xDEAD_BEEFUL,
            SupersededBlocks = new[]
            {
                SupersededBlockRecord.Create(Ulid(1), 4096),
                SupersededBlockRecord.Create(Ulid(9), 128),
                SupersededBlockRecord.Create(Ulid(200), 1_000_000_000),
            },
        };

        var bytes = CleanupSerializer.Serialize(block);
        Assert.Equal(CleanupSerializer.PayloadSize(3), bytes.Length);

        var round = CleanupSerializer.Deserialize(bytes);
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(block.CheckpointSequence, round.Value.CheckpointSequence);
        Assert.Equal(3, round.Value.SupersededBlocks.Count);
        Assert.Equal(Ulid(1), round.Value.SupersededBlocks[0].BlockId);
        Assert.Equal(4096, round.Value.SupersededBlocks[0].TotalBlockLength);
        Assert.Equal(Ulid(200), round.Value.SupersededBlocks[2].BlockId);
        Assert.Equal(1_000_000_000, round.Value.SupersededBlocks[2].TotalBlockLength);
        Assert.Equal(4096L + 128 + 1_000_000_000, round.Value.TotalDeadBytes);
    }

    [Fact]
    public void Round_trips_a_max_length_entry()
    {
        var block = new CleanupBlock
        {
            CheckpointSequence = ulong.MaxValue,
            SupersededBlocks = new[] { SupersededBlockRecord.Create(Ulid(1), long.MaxValue) },
        };
        var round = CleanupSerializer.Deserialize(CleanupSerializer.Serialize(block));
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(ulong.MaxValue, round.Value.CheckpointSequence);
        Assert.Equal(long.MaxValue, round.Value.SupersededBlocks[0].TotalBlockLength);
    }

    [Fact]
    public void Round_trips_an_empty_block()
    {
        var block = new CleanupBlock
        {
            CheckpointSequence = 42,
            SupersededBlocks = Array.Empty<SupersededBlockRecord>(),
        };

        var bytes = CleanupSerializer.Serialize(block);
        Assert.Equal(CleanupSerializer.FixedPrefixSize, bytes.Length);

        var round = CleanupSerializer.Deserialize(bytes);
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(42UL, round.Value.CheckpointSequence);
        Assert.Empty(round.Value.SupersededBlocks);
        Assert.Equal(0, round.Value.TotalDeadBytes);
    }

    [Fact]
    public void Layout_is_little_endian_at_the_spec_offsets()
    {
        var block = new CleanupBlock
        {
            CheckpointSequence = 0x0102_0304_0506_0708UL,
            SupersededBlocks = new[] { SupersededBlockRecord.Create(Ulid(7), 0x1122_3344) },
        };
        var bytes = CleanupSerializer.Serialize(block);

        Assert.Equal(0x0102_0304_0506_0708UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(Ulid(7), bytes.AsSpan(12, 16).ToArray());
        Assert.Equal(0x1122_3344L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28, 8)));
    }

    [Fact]
    public void FromLocations_captures_block_ids_and_sizes()
    {
        var locations = new[]
        {
            new BlockLocation { BlockId = Ulid(3), Offset = 8192, TotalBlockLength = 300 },
            new BlockLocation { BlockId = Ulid(50), Offset = 8492, TotalBlockLength = 700 },
        };
        var block = CleanupBlock.FromLocations(checkpointSequence: 5, locations);

        Assert.Equal(5UL, block.CheckpointSequence);
        Assert.Equal(2, block.SupersededBlocks.Count);
        Assert.Equal(Ulid(3), block.SupersededBlocks[0].BlockId);
        Assert.Equal(300, block.SupersededBlocks[0].TotalBlockLength);
        Assert.Equal(1000, block.TotalDeadBytes);
    }

    [Fact]
    public void Rejects_a_truncated_payload()
    {
        var bytes = new byte[CleanupSerializer.FixedPrefixSize - 1];
        Assert.True(CleanupSerializer.Deserialize(bytes).IsFailure);
    }

    [Fact]
    public void Rejects_a_length_that_disagrees_with_the_entry_count()
    {
        var block = new CleanupBlock
        {
            CheckpointSequence = 1,
            SupersededBlocks = new[] { SupersededBlockRecord.Create(Ulid(1), 64) },
        };
        var bytes = CleanupSerializer.Serialize(block);
        // Claim two entries while only one entry's bytes follow.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 2);
        Assert.True(CleanupSerializer.Deserialize(bytes).IsFailure);
    }

    [Fact]
    public void Rejects_a_non_positive_entry_length()
    {
        var block = new CleanupBlock
        {
            CheckpointSequence = 1,
            SupersededBlocks = new[] { SupersededBlockRecord.Create(Ulid(1), 64) },
        };
        var bytes = CleanupSerializer.Serialize(block);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28, 8), 0); // corrupt the length to 0
        Assert.True(CleanupSerializer.Deserialize(bytes).IsFailure);
    }

    [Fact]
    public void Record_rejects_bad_widths_and_lengths()
    {
        Assert.Throws<ArgumentException>(() => SupersededBlockRecord.Create(new byte[15], 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupersededBlockRecord.Create(Ulid(1), 0));
    }
}
