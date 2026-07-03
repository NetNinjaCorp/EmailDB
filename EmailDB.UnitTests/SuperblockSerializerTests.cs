using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 superblock slot serialization (EmailDB_FileFormat_Spec.md Section 3.1):
/// 4096-byte slot layout, little-endian field encoding at spec offsets, reserved zero-fill,
/// and the BLAKE3-128 slot checksum over bytes 0..4079.
/// </summary>
public class SuperblockSerializerTests
{
    private static Superblock CreateSampleSuperblock() => new()
    {
        SuperblockSequence = 0x0102030405060708UL,
        FileId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        ShardIndex = 7,
        CreatedTimestamp = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc).Ticks,
        CompatFlags = 0x00000001,
        ReadOnlyCompatFlags = 0x00000002,
        IncompatFlags = 0x00000004,
        CleanShutdown = 1,
        MaxPayloadLength = 268_435_456,
        LastCheckpointBlockId = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray(),
        LastCheckpointOffset = 123_456_789,
        EncryptionEnabled = 1,
        AlgorithmId = 1,
        KdfType = 1,
        KdfParams = Enumerable.Range(50, 16).Select(i => (byte)i).ToArray(),
        Salt = Enumerable.Range(200, 16).Select(i => (byte)i).ToArray(),
        KeyVerificationToken = Enumerable.Range(1, 32).Select(i => (byte)(i * 3)).ToArray(),
        ActiveKeyStoreBlockId = Enumerable.Range(30, 16).Select(i => (byte)i).ToArray(),
        ActiveKeyStoreOffset = 987_654_321,
    };

    [Fact]
    public void Serialize_ProducesExactly4096Bytes()
    {
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());
        Assert.Equal(4096, slot.Length);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = CreateSampleSuperblock();
        var slot = SuperblockSerializer.Serialize(original);

        var result = SuperblockSerializer.Deserialize(slot);

        Assert.True(result.IsSuccess, result.Error);
        var restored = result.Value;
        Assert.Equal(original.FormatVersion, restored.FormatVersion);
        Assert.Equal(original.SuperblockSequence, restored.SuperblockSequence);
        Assert.Equal(original.FileId, restored.FileId);
        Assert.Equal(original.ShardIndex, restored.ShardIndex);
        Assert.Equal(original.CreatedTimestamp, restored.CreatedTimestamp);
        Assert.Equal(original.CompatFlags, restored.CompatFlags);
        Assert.Equal(original.ReadOnlyCompatFlags, restored.ReadOnlyCompatFlags);
        Assert.Equal(original.IncompatFlags, restored.IncompatFlags);
        Assert.Equal(original.CleanShutdown, restored.CleanShutdown);
        Assert.Equal(original.MaxPayloadLength, restored.MaxPayloadLength);
        Assert.Equal(original.LastCheckpointBlockId, restored.LastCheckpointBlockId);
        Assert.Equal(original.LastCheckpointOffset, restored.LastCheckpointOffset);
        Assert.Equal(original.EncryptionEnabled, restored.EncryptionEnabled);
        Assert.Equal(original.AlgorithmId, restored.AlgorithmId);
        Assert.Equal(original.KdfType, restored.KdfType);
        Assert.Equal(original.KdfParams, restored.KdfParams);
        Assert.Equal(original.Salt, restored.Salt);
        Assert.Equal(original.KeyVerificationToken, restored.KeyVerificationToken);
        Assert.Equal(original.ActiveKeyStoreBlockId, restored.ActiveKeyStoreBlockId);
        Assert.Equal(original.ActiveKeyStoreOffset, restored.ActiveKeyStoreOffset);
    }

    [Fact]
    public void RoundTrip_DefaultSuperblock_Succeeds()
    {
        var result = SuperblockSerializer.Deserialize(SuperblockSerializer.Serialize(new Superblock()));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3, result.Value.FormatVersion);
        Assert.Equal(268_435_456, result.Value.MaxPayloadLength);
        Assert.Equal(new byte[16], result.Value.FileId);
    }

    [Fact]
    public void Serialize_WritesFieldsAtSpecOffsetsLittleEndian()
    {
        var sb = CreateSampleSuperblock();
        var slot = SuperblockSerializer.Serialize(sb);
        var span = slot.AsSpan();

        Assert.Equal(0x53E3A11DBB00DBEEUL, BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(0, 8)));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2)));
        Assert.Equal(sb.SuperblockSequence, BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(10, 8)));
        Assert.Equal(sb.FileId, span.Slice(18, 16).ToArray());
        Assert.Equal(sb.ShardIndex, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(34, 4)));
        Assert.Equal(sb.CreatedTimestamp, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(38, 8)));
        Assert.Equal(sb.CompatFlags, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(46, 4)));
        Assert.Equal(sb.ReadOnlyCompatFlags, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(50, 4)));
        Assert.Equal(sb.IncompatFlags, BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(54, 4)));
        Assert.Equal(sb.CleanShutdown, span[58]);
        Assert.Equal(sb.MaxPayloadLength, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(59, 8)));
        Assert.Equal(sb.LastCheckpointBlockId, span.Slice(67, 16).ToArray());
        Assert.Equal(sb.LastCheckpointOffset, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(83, 8)));
        Assert.Equal(sb.EncryptionEnabled, span[91]);
        Assert.Equal(sb.AlgorithmId, span[92]);
        Assert.Equal(sb.KdfType, span[93]);
        Assert.Equal(sb.KdfParams, span.Slice(94, 16).ToArray());
        Assert.Equal(sb.Salt, span.Slice(110, 16).ToArray());
        Assert.Equal(sb.KeyVerificationToken, span.Slice(126, 32).ToArray());
        Assert.Equal(sb.ActiveKeyStoreBlockId, span.Slice(158, 16).ToArray());
        Assert.Equal(sb.ActiveKeyStoreOffset, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(174, 8)));
    }

    [Fact]
    public void Serialize_ZeroFillsReservedRegion()
    {
        // Serialize into a dirty buffer to prove the reserved region (bytes 182..4079) is actively cleared.
        var slot = new byte[4096];
        Array.Fill(slot, (byte)0xFF);
        SuperblockSerializer.Serialize(CreateSampleSuperblock(), slot);

        Assert.All(slot.AsSpan(182, 3898).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Checksum_IsBlake3_128_OverFirst4080Bytes()
    {
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());

        var expected = Blake3.Hasher.Hash(slot.AsSpan(0, 4080)).AsSpan().Slice(0, 16).ToArray();

        Assert.Equal(expected, slot.AsSpan(4080, 16).ToArray());
    }

    [Theory]
    [InlineData(0)]     // magic
    [InlineData(10)]    // sequence
    [InlineData(58)]    // CleanShutdown
    [InlineData(2000)]  // reserved region (still checksummed)
    [InlineData(4080)]  // checksum itself
    [InlineData(4095)]  // last checksum byte
    public void Deserialize_CorruptedByte_FailsChecksumOrMagic(int corruptOffset)
    {
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());
        slot[corruptOffset] ^= 0xFF;

        var result = SuperblockSerializer.Deserialize(slot);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData(8)]     // start of the checksummed range past the magic (FormatVersion)
    [InlineData(18)]    // FileId
    [InlineData(182)]   // first reserved byte
    [InlineData(2040)]  // middle of the checksummed range (reserved region)
    [InlineData(4079)]  // last byte covered by the checksum
    [InlineData(4080)]  // first byte of the stored checksum itself
    [InlineData(4087)]  // middle of the stored checksum
    [InlineData(4095)]  // last byte of the stored checksum
    public void Deserialize_TearWithValidMagic_IsDetectedViaChecksum(int corruptOffset)
    {
        // The magic is left intact, so only the BLAKE3-128 checksum comparison can
        // reject the slot — proving detection is via checksum, not magic. Covers
        // tears at the start/middle/end of the 4080-byte checksummed range and
        // within the 16-byte stored checksum itself.
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());
        slot[corruptOffset] ^= 0x01; // minimal single-bit tear

        var result = SuperblockSerializer.Deserialize(slot);

        Assert.True(result.IsFailure);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("magic", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_WrongMagic_ReportsMagicError()
    {
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());
        BinaryPrimitives.WriteUInt64LittleEndian(slot, 0xDEADBEEFDEADBEEF);

        var result = SuperblockSerializer.Deserialize(slot);

        Assert.True(result.IsFailure);
        Assert.Contains("magic", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_TornSlot_ReportsChecksumError()
    {
        // Simulate a torn write: valid magic/header, but the tail was never written.
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());
        slot.AsSpan(2048).Clear();

        var result = SuperblockSerializer.Deserialize(slot);

        Assert.True(result.IsFailure);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_UnsupportedFormatVersion_Fails()
    {
        var slot = SuperblockSerializer.Serialize(CreateSampleSuperblock());
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(8, 2), 4);
        // Recompute a valid checksum so only the version check can fail.
        Blake3.Hasher.Hash(slot.AsSpan(0, 4080)).AsSpan().Slice(0, 16).CopyTo(slot.AsSpan(4080, 16));

        var result = SuperblockSerializer.Deserialize(slot);

        Assert.True(result.IsFailure);
        Assert.Contains("version", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4095)]
    [InlineData(8192)]
    public void Deserialize_WrongLength_Fails(int length)
    {
        var result = SuperblockSerializer.Deserialize(new byte[length]);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Serialize_WrongLengthFixedField_Throws()
    {
        var sb = CreateSampleSuperblock();
        sb.Salt = new byte[15];

        Assert.Throws<ArgumentException>(() => SuperblockSerializer.Serialize(sb));
    }

    [Fact]
    public void Serialize_WrongBufferSize_Throws()
    {
        var sb = CreateSampleSuperblock();
        Assert.Throws<ArgumentException>(() => SuperblockSerializer.Serialize(sb, new byte[4095]));
    }
}
