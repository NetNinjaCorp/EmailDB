using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 dual-slot superblock write protocol (EmailDB_FileFormat_Spec.md
/// Section 3): alternating slot writes at offsets 0/4096 with monotonic sequence and
/// fsync per write, pick-higher-valid-sequence on read, and rewrite (repair) of an
/// invalid torn slot on the next update.
/// </summary>
public class SuperblockManagerTests : IDisposable
{
    private const int SlotSize = SuperblockSerializer.SlotSize;
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-superblock-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static Superblock CreateSuperblock(uint shardIndex = 0) => new()
    {
        FileId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        ShardIndex = shardIndex,
        CreatedTimestamp = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc).Ticks,
        CleanShutdown = 1,
    };

    /// <summary>Reads the raw 4096-byte slot at the given offset directly from the file.</summary>
    private byte[] ReadRawSlot(long offset)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[SlotSize];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Corrupts one byte of the slot at the given offset, simulating a torn write.</summary>
    private void CorruptSlot(long offset, int byteWithinSlot = 100)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(offset + byteWithinSlot, SeekOrigin.Begin);
        var b = (byte)fs.ReadByte();
        fs.Seek(offset + byteWithinSlot, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    private static ulong SlotSequence(byte[] slot) =>
        BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(10, 8));

    /// <summary>
    /// Serializes a superblock (with the exact sequence set on it) directly into the
    /// slot at the given offset, bypassing the manager's alternation/sequence logic.
    /// Lets tests craft arbitrary slot arrangements (ties, non-contiguous sequences).
    /// </summary>
    private void WriteRawSlot(long offset, Superblock superblock)
    {
        var bytes = SuperblockSerializer.Serialize(superblock);
        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Write_AlternatesSlots_AB_A_WithIncrementedSequence()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);

        Assert.True(manager.Write(CreateSuperblock(shardIndex: 1)).IsSuccess);
        Assert.Equal(0, manager.CurrentSlot); // first write lands in slot A
        Assert.True(manager.Write(CreateSuperblock(shardIndex: 2)).IsSuccess);
        Assert.Equal(1, manager.CurrentSlot); // second write lands in slot B
        Assert.True(manager.Write(CreateSuperblock(shardIndex: 3)).IsSuccess);
        Assert.Equal(0, manager.CurrentSlot); // third write alternates back to slot A

        // Verify on disk: slot A holds the 3rd write (seq 3), slot B the 2nd (seq 2).
        var slotA = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotAOffset));
        var slotB = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotBOffset));
        Assert.True(slotA.IsSuccess, slotA.Error);
        Assert.True(slotB.IsSuccess, slotB.Error);
        Assert.Equal(3UL, slotA.Value.SuperblockSequence);
        Assert.Equal(3U, slotA.Value.ShardIndex);
        Assert.Equal(2UL, slotB.Value.SuperblockSequence);
        Assert.Equal(2U, slotB.Value.ShardIndex);
    }

    [Fact]
    public void Write_AssignsMonotonicSequence_AndReturnsIt()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);

        for (ulong expected = 1; expected <= 5; expected++)
        {
            var result = manager.Write(CreateSuperblock());
            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(expected, result.Value.SuperblockSequence);
            Assert.Equal(expected, manager.Current!.SuperblockSequence);
        }
    }

    [Fact]
    public void Write_FlushesToDiskOncePerWrite()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        Assert.Equal(0, manager.FlushToDiskCount);

        manager.Write(CreateSuperblock());
        Assert.Equal(1, manager.FlushToDiskCount);
        manager.Write(CreateSuperblock());
        Assert.Equal(2, manager.FlushToDiskCount);

        // Data must be visible through an independent handle immediately after Write returns.
        Assert.Equal(2UL, SlotSequence(ReadRawSlot(SuperblockManager.SlotBOffset)));
    }

    [Fact]
    public void Load_PicksHigherValidSequence_WhenBothSlotsValid()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2UL, result.Value.SuperblockSequence);
        Assert.Equal(2U, result.Value.ShardIndex);
        Assert.Equal(1, reader.CurrentSlot); // slot B holds the winner
    }

    [Fact]
    public void Load_PicksHigherValidSequence_WhenSlotAIsNewer()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
            writer.Write(CreateSuperblock(shardIndex: 3)); // slot A, seq 3
        }

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3UL, result.Value.SuperblockSequence);
        Assert.Equal(3U, result.Value.ShardIndex);
        Assert.Equal(0, reader.CurrentSlot); // slot A holds the winner this time
    }

    [Fact]
    public void Load_ComparesSequences_NotSlotOrder_WithNonContiguousCraftedSequences()
    {
        // Craft slots directly: slot A carries a much higher sequence than slot B.
        var newer = CreateSuperblock(shardIndex: 100);
        newer.SuperblockSequence = 100;
        var older = CreateSuperblock(shardIndex: 7);
        older.SuperblockSequence = 7;
        WriteRawSlot(SuperblockManager.SlotAOffset, newer);
        WriteRawSlot(SuperblockManager.SlotBOffset, older);

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(100UL, result.Value.SuperblockSequence);
        Assert.Equal(100U, result.Value.ShardIndex);
        Assert.Equal(0, reader.CurrentSlot);
    }

    [Fact]
    public void Load_EqualValidSequences_IsDeterministic_PicksSlotA()
    {
        // Cannot arise under the alternating write protocol (every write increments the
        // sequence), but Load must still resolve a crafted tie deterministically.
        var inA = CreateSuperblock(shardIndex: 1);
        inA.SuperblockSequence = 5;
        var inB = CreateSuperblock(shardIndex: 2);
        inB.SuperblockSequence = 5;
        WriteRawSlot(SuperblockManager.SlotAOffset, inA);
        WriteRawSlot(SuperblockManager.SlotBOffset, inB);

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(5UL, result.Value.SuperblockSequence);
        Assert.Equal(1U, result.Value.ShardIndex); // slot A wins the tie
        Assert.Equal(0, reader.CurrentSlot);
    }

    [Fact]
    public void Load_FallsBackToLowerSequence_WhenHigherSlotIsTorn()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }
        CorruptSlot(SuperblockManager.SlotBOffset); // tear the newer slot

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1UL, result.Value.SuperblockSequence);
        Assert.Equal(1U, result.Value.ShardIndex);
        Assert.Equal(0, reader.CurrentSlot); // survives on the previous good superblock
    }

    [Fact]
    public void Load_FallsBackToSlotB_WhenNewerSlotAIsTorn()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
            writer.Write(CreateSuperblock(shardIndex: 3)); // slot A, seq 3
        }
        CorruptSlot(SuperblockManager.SlotAOffset); // tear the newer slot (A this time)

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2UL, result.Value.SuperblockSequence);
        Assert.Equal(2U, result.Value.ShardIndex);
        Assert.Equal(1, reader.CurrentSlot); // survives on slot B
    }

    [Fact]
    public void Load_PicksNewerSlotB_WhenOlderSlotAIsTorn()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }
        CorruptSlot(SuperblockManager.SlotAOffset); // corrupt the OLDER slot

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        // Slot A is validated (and rejected) without aborting the load; B wins.
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2UL, result.Value.SuperblockSequence);
        Assert.Equal(2U, result.Value.ShardIndex);
        Assert.Equal(1, reader.CurrentSlot);
    }

    [Fact]
    public void Load_FallsBackToOlderSlot_WhenNewerSlotHasBadMagic()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }
        CorruptSlot(SuperblockManager.SlotBOffset, byteWithinSlot: 0); // flip a magic byte of B

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        // Magic validation (not just checksum) applies to slot B; A wins.
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1UL, result.Value.SuperblockSequence);
        Assert.Equal(1U, result.Value.ShardIndex);
        Assert.Equal(0, reader.CurrentSlot);
    }

    [Fact]
    public void Load_SucceedsOnSlotA_WhenSlotBRegionIsZeroFilled()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1

        // Preallocate the slot B region as zeros (long enough, but no magic).
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.SetLength(SuperblockManager.SuperblockRegionSize);

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1UL, result.Value.SuperblockSequence);
        Assert.Equal(0, reader.CurrentSlot);
    }

    [Fact]
    public void Load_SucceedsWhenOnlySlotAExists_FileTooShortForSlotB()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
            writer.Write(CreateSuperblock()); // only slot A written; file is 4096 bytes

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1UL, result.Value.SuperblockSequence);
        Assert.Equal(0, reader.CurrentSlot);
    }

    [Fact]
    public void Load_Fails_WhenFileIsEmpty()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = manager.Load();
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Load_Fails_WhenBothSlotsAreCorrupt()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock());
            writer.Write(CreateSuperblock());
        }
        CorruptSlot(SuperblockManager.SlotAOffset);
        CorruptSlot(SuperblockManager.SlotBOffset);

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var result = reader.Load();

        Assert.True(result.IsFailure);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_AfterLoadWithTornSlot_RewritesAndRepairsTheTornSlot()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }
        CorruptSlot(SuperblockManager.SlotBOffset); // simulate torn write of slot B

        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        var loaded = manager.Load();
        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Equal(1UL, loaded.Value.SuperblockSequence); // running on slot A

        // Next update must target the torn slot B, repairing it.
        var written = manager.Write(CreateSuperblock(shardIndex: 9));
        Assert.True(written.IsSuccess, written.Error);
        Assert.Equal(1, manager.CurrentSlot);
        Assert.Equal(2UL, written.Value.SuperblockSequence);

        var slotB = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotBOffset));
        Assert.True(slotB.IsSuccess, slotB.Error); // torn slot is valid again
        Assert.Equal(2UL, slotB.Value.SuperblockSequence);
        Assert.Equal(9U, slotB.Value.ShardIndex);

        // The previous good superblock in slot A was never touched.
        var slotA = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotAOffset));
        Assert.True(slotA.IsSuccess, slotA.Error);
        Assert.Equal(1UL, slotA.Value.SuperblockSequence);
        Assert.Equal(1U, slotA.Value.ShardIndex);
    }

    [Theory]
    [InlineData(8)]     // start of the checksummed range past the magic
    [InlineData(2040)]  // middle of the checksummed range (reserved region)
    [InlineData(4079)]  // last byte covered by the checksum
    [InlineData(4088)]  // within the stored checksum itself
    public void Write_AfterLoadWithTornSlot_RepairsTearAtAnyPosition_FileFullyHealthyAfter(
        int tornByteWithinSlot)
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }
        // Tear slot B at the given position. The magic stays intact, so the slot is
        // rejected specifically by the BLAKE3-128 checksum, not the magic check.
        CorruptSlot(SuperblockManager.SlotBOffset, byteWithinSlot: tornByteWithinSlot);
        var rawTorn = ReadRawSlot(SuperblockManager.SlotBOffset);
        Assert.Equal(0x53E3A11DBB00DBEEUL, BinaryPrimitives.ReadUInt64LittleEndian(rawTorn.AsSpan(0, 8)));
        var tornResult = SuperblockSerializer.Deserialize(rawTorn);
        Assert.True(tornResult.IsFailure);
        Assert.Contains("checksum", tornResult.Error, StringComparison.OrdinalIgnoreCase);

        using (var manager = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            var loaded = manager.Load();
            Assert.True(loaded.IsSuccess, loaded.Error);
            Assert.Equal(1UL, loaded.Value.SuperblockSequence); // fell back to slot A

            // The next update targets the torn slot and repairs it.
            var written = manager.Write(CreateSuperblock(shardIndex: 9));
            Assert.True(written.IsSuccess, written.Error);
            Assert.Equal(1, manager.CurrentSlot);
        }

        // The file is fully healthy afterwards: BOTH slots deserialize as valid.
        var slotA = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotAOffset));
        var slotB = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotBOffset));
        Assert.True(slotA.IsSuccess, slotA.Error);
        Assert.True(slotB.IsSuccess, slotB.Error);
        Assert.Equal(1UL, slotA.Value.SuperblockSequence); // previous good slot untouched
        Assert.Equal(2UL, slotB.Value.SuperblockSequence); // repaired with the next sequence
        Assert.Equal(9U, slotB.Value.ShardIndex);

        // A fresh reader also opens cleanly and adopts the repaired slot.
        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var reloaded = reader.Load();
        Assert.True(reloaded.IsSuccess, reloaded.Error);
        Assert.Equal(2UL, reloaded.Value.SuperblockSequence);
        Assert.Equal(1, reader.CurrentSlot);
    }

    [Fact]
    public void Write_AfterLoadWithTornOlderSlot_RepairsItOnNextUpdate()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock(shardIndex: 1)); // slot A, seq 1
            writer.Write(CreateSuperblock(shardIndex: 2)); // slot B, seq 2
        }
        CorruptSlot(SuperblockManager.SlotAOffset); // tear the OLDER slot (A)

        using (var manager = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            var loaded = manager.Load();
            Assert.True(loaded.IsSuccess, loaded.Error);
            Assert.Equal(2UL, loaded.Value.SuperblockSequence); // newer slot B wins
            Assert.Equal(1, manager.CurrentSlot);

            // Next update alternates to slot A — exactly the torn slot — repairing it.
            var written = manager.Write(CreateSuperblock(shardIndex: 9));
            Assert.True(written.IsSuccess, written.Error);
            Assert.Equal(0, manager.CurrentSlot);
            Assert.Equal(3UL, written.Value.SuperblockSequence);
        }

        // Fully healthy: both slots valid, torn slot A now carries the newest superblock.
        var slotA = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotAOffset));
        var slotB = SuperblockSerializer.Deserialize(ReadRawSlot(SuperblockManager.SlotBOffset));
        Assert.True(slotA.IsSuccess, slotA.Error);
        Assert.True(slotB.IsSuccess, slotB.Error);
        Assert.Equal(3UL, slotA.Value.SuperblockSequence);
        Assert.Equal(9U, slotA.Value.ShardIndex);
        Assert.Equal(2UL, slotB.Value.SuperblockSequence); // good slot B never touched
        Assert.Equal(2U, slotB.Value.ShardIndex);

        using var reader = new SuperblockManager(OpenStream(), ownsStream: true);
        var reloaded = reader.Load();
        Assert.True(reloaded.IsSuccess, reloaded.Error);
        Assert.Equal(3UL, reloaded.Value.SuperblockSequence);
        Assert.Equal(0, reader.CurrentSlot);
    }

    [Fact]
    public void Write_AfterLoad_ContinuesSequenceAndAlternation()
    {
        using (var writer = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            writer.Write(CreateSuperblock()); // slot A, seq 1
            writer.Write(CreateSuperblock()); // slot B, seq 2
        }

        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        Assert.True(manager.Load().IsSuccess);

        var result = manager.Write(CreateSuperblock());
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3UL, result.Value.SuperblockSequence);
        Assert.Equal(0, manager.CurrentSlot); // current was slot B, so write went to slot A
        Assert.Equal(3UL, SlotSequence(ReadRawSlot(SuperblockManager.SlotAOffset)));
        Assert.Equal(2UL, SlotSequence(ReadRawSlot(SuperblockManager.SlotBOffset)));
    }

    [Fact]
    public void Write_WithInvalidFixedField_FailsWithoutTouchingDisk()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        manager.Write(CreateSuperblock());

        var bad = CreateSuperblock();
        bad.Salt = new byte[3];
        var result = manager.Write(bad);

        Assert.True(result.IsFailure);
        Assert.Equal(0, manager.CurrentSlot); // state unchanged
        Assert.Equal(1UL, manager.Current!.SuperblockSequence);
        Assert.Equal(SlotSize, new FileInfo(_path).Length); // slot B never written
    }

    [Fact]
    public void Write_ABAB_EachWriteTouchesOnlyTheTargetSlot_ByteForByte()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);

        // Write 1 -> slot A (seq 1).
        Assert.True(manager.Write(CreateSuperblock(shardIndex: 1)).IsSuccess);
        var slotAAfter1 = ReadRawSlot(SuperblockManager.SlotAOffset);
        Assert.Equal(1UL, SlotSequence(slotAAfter1));

        // Write 2 -> slot B (seq 2); slot A bytes must be untouched.
        Assert.True(manager.Write(CreateSuperblock(shardIndex: 2)).IsSuccess);
        var slotAAfter2 = ReadRawSlot(SuperblockManager.SlotAOffset);
        var slotBAfter2 = ReadRawSlot(SuperblockManager.SlotBOffset);
        Assert.Equal(slotAAfter1, slotAAfter2);
        Assert.Equal(2UL, SlotSequence(slotBAfter2));

        // Write 3 -> slot A (seq 3); slot B bytes must be untouched.
        Assert.True(manager.Write(CreateSuperblock(shardIndex: 3)).IsSuccess);
        var slotAAfter3 = ReadRawSlot(SuperblockManager.SlotAOffset);
        var slotBAfter3 = ReadRawSlot(SuperblockManager.SlotBOffset);
        Assert.Equal(slotBAfter2, slotBAfter3);
        Assert.Equal(3UL, SlotSequence(slotAAfter3));
        Assert.NotEqual(slotAAfter2, slotAAfter3); // slot A actually rewritten

        // Write 4 -> slot B (seq 4); slot A bytes must be untouched.
        Assert.True(manager.Write(CreateSuperblock(shardIndex: 4)).IsSuccess);
        var slotAAfter4 = ReadRawSlot(SuperblockManager.SlotAOffset);
        var slotBAfter4 = ReadRawSlot(SuperblockManager.SlotBOffset);
        Assert.Equal(slotAAfter3, slotAAfter4);
        Assert.Equal(4UL, SlotSequence(slotBAfter4));
        Assert.NotEqual(slotBAfter3, slotBAfter4); // slot B actually rewritten

        // File is exactly the dual-slot region: no writes strayed past offset 8192.
        Assert.Equal(SuperblockManager.SuperblockRegionSize, new FileInfo(_path).Length);
    }

    [Fact]
    public void Write_SequenceStrictlyMonotonic_AcrossMultipleReopenSessions()
    {
        ulong lastSequence = 0;
        for (var session = 0; session < 3; session++)
        {
            using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
            if (session > 0)
            {
                var loaded = manager.Load();
                Assert.True(loaded.IsSuccess, loaded.Error);
                Assert.Equal(lastSequence, loaded.Value.SuperblockSequence);
            }

            for (var i = 0; i < 2; i++)
            {
                var result = manager.Write(CreateSuperblock());
                Assert.True(result.IsSuccess, result.Error);
                Assert.True(result.Value.SuperblockSequence > lastSequence,
                    $"sequence {result.Value.SuperblockSequence} not > {lastSequence}");
                Assert.Equal(lastSequence + 1, result.Value.SuperblockSequence); // strictly +1
                lastSequence = result.Value.SuperblockSequence;
            }
        }

        // 6 writes total; both on-disk slots reflect the final alternating pair.
        Assert.Equal(6UL, lastSequence);
        Assert.Equal(5UL, SlotSequence(ReadRawSlot(SuperblockManager.SlotAOffset)));
        Assert.Equal(6UL, SlotSequence(ReadRawSlot(SuperblockManager.SlotBOffset)));
    }

    [Fact]
    public void Write_FailedSerialization_PerformsNoFlushToDisk()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        manager.Write(CreateSuperblock());
        Assert.Equal(1, manager.FlushToDiskCount);

        var bad = CreateSuperblock();
        bad.Salt = new byte[3]; // invalid fixed-size field -> serialization failure
        Assert.True(manager.Write(bad).IsFailure);

        // fsync happens exactly once per successful write; a failed write adds none.
        Assert.Equal(1, manager.FlushToDiskCount);

        // A subsequent good write resumes: one more flush, next sequence, next slot.
        var recovered = manager.Write(CreateSuperblock());
        Assert.True(recovered.IsSuccess, recovered.Error);
        Assert.Equal(2, manager.FlushToDiskCount);
        Assert.Equal(2UL, recovered.Value.SuperblockSequence);
        Assert.Equal(1, manager.CurrentSlot);
    }

    [Fact]
    public void Constructor_RejectsNonWritableStream()
    {
        File.WriteAllBytes(_path, new byte[SlotSize]);
        using var readOnly = new FileStream(_path, FileMode.Open, FileAccess.Read);
        Assert.Throws<ArgumentException>(() => new SuperblockManager(readOnly));
    }
}
