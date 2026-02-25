using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that empty payload uses 16 zero bytes as checksum.
/// Validates acceptance criterion: "Empty payload uses 16 zero bytes as checksum"
/// </summary>
public class EmptyPayloadZeroChecksumTests : IDisposable
{
    private readonly string testFilePath;

    public EmptyPayloadZeroChecksumTests()
    {
        testFilePath = Path.Combine(Path.GetTempPath(), $"test_empty_payload_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(testFilePath))
            File.Delete(testFilePath);
    }

    [Fact]
    public async Task WriteBlockAsync_NullPayload_Writes16ZeroBytesAsChecksum()
    {
        // Arrange
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 1,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = null
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        // Assert: payload checksum at offset HeaderSize + HeaderChecksumSize should be 16 zero bytes
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, payloadChecksumOffset, payloadChecksum, 0, 16);

        Assert.Equal(new byte[16], payloadChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_EmptyPayload_Writes16ZeroBytesAsChecksum()
    {
        // Arrange
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 2,
            Type = BlockType.Segment,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = Array.Empty<byte>()
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        // Assert: payload checksum should be 16 zero bytes
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, payloadChecksumOffset, payloadChecksum, 0, 16);

        Assert.Equal(new byte[16], payloadChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_NullPayload_RoundTrip_Succeeds()
    {
        // Arrange
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 3,
            Type = BlockType.Metadata,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = null
        };

        // Act
        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await manager.ReadBlockAsync(3);

        // Assert: round-trip succeeds, implying the 16-zero-byte checksum was accepted
        Assert.True(readResult.IsSuccess);
        Assert.Null(readResult.Value.Payload);
    }

    [Fact]
    public async Task WriteBlockAsync_EmptyPayload_RoundTrip_Succeeds()
    {
        // Arrange
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 4,
            Type = BlockType.BTreeLeaf,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = Array.Empty<byte>()
        };

        // Act
        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await manager.ReadBlockAsync(4);

        // Assert
        Assert.True(readResult.IsSuccess);
    }

    [Fact]
    public async Task WriteBlockAsync_NullPayload_ChecksumIsNotBlake3OfEmpty()
    {
        // The empty payload checksum should be 16 zero bytes, NOT the BLAKE3 hash of empty input.
        // This distinguishes the "no data" sentinel from an actual hash.
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 5,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = null
        };

        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, payloadChecksumOffset, payloadChecksum, 0, 16);

        // BLAKE3 of empty input is NOT all zeros
        var blake3OfEmpty = RawBlockManager.ComputeChecksum(Array.Empty<byte>());
        Assert.NotEqual(blake3OfEmpty, payloadChecksum);

        // It should be exactly 16 zero bytes
        Assert.Equal(new byte[16], payloadChecksum);
    }

    [Fact]
    public async Task ReadBlockAsync_TamperedEmptyPayloadChecksum_Fails()
    {
        // Write a block with null payload, then tamper with the zero-byte checksum
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 6,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = null
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Tamper: set the first byte of payload checksum to 0xFF
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize;
        fileBytes[payloadChecksumOffset] = 0xFF;
        File.WriteAllBytes(testFilePath, fileBytes);

        // Re-open and try to read — should fail checksum verification
        using var manager2 = new RawBlockManager(testFilePath, createIfNotExists: false);
        var readResult = await manager2.ReadBlockAsync(6);

        Assert.False(readResult.IsSuccess);
        Assert.Contains("checksum mismatch", readResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteBlockAsync_NonEmptyPayload_DoesNotUseZeroChecksum()
    {
        // Verify that a non-empty payload does NOT get the zero-byte checksum
        using var manager = new RawBlockManager(testFilePath);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var block = new Block
        {
            BlockId = 7,
            Type = BlockType.Segment,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize + payload.Length;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, payloadChecksumOffset, payloadChecksum, 0, 16);

        // Non-empty payload checksum must NOT be 16 zero bytes
        Assert.NotEqual(new byte[16], payloadChecksum);

        // It should be the actual BLAKE3 hash of the payload
        var expectedChecksum = RawBlockManager.ComputeChecksum(payload);
        Assert.Equal(expectedChecksum, payloadChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_EmptyPayload_TotalBlockSize_MatchesOverhead()
    {
        // With empty payload, the total block size should equal TotalFixedOverhead exactly
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 8,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = null
        };

        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        var fileBytes = File.ReadAllBytes(testFilePath);
        Assert.Equal(RawBlockManager.TotalFixedOverhead, fileBytes.Length);
    }
}
