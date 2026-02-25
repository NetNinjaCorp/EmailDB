using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that the read path reads and verifies 16-byte BLAKE3-128 checksums.
/// Validates acceptance criterion: "Read path reads and verifies 16-byte checksums"
/// </summary>
public class ReadPathChecksumTests : IDisposable
{
    private readonly string testFilePath;

    public ReadPathChecksumTests()
    {
        testFilePath = Path.Combine(Path.GetTempPath(), $"test_read_checksum_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(testFilePath))
            File.Delete(testFilePath);
    }

    [Fact]
    public async Task ReadBlockAsync_ValidBlock_SucceedsWithCorrect16ByteChecksums()
    {
        // Arrange: write a block, then read it back — read path must verify both checksums
        var payload = new byte[] { 10, 20, 30, 40, 50 };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 1,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Act
        var readResult = await manager.ReadBlockAsync(1);

        // Assert: read succeeds, proving 16-byte checksums were verified
        Assert.True(readResult.IsSuccess);
        Assert.Equal(payload, readResult.Value.Payload);
        Assert.Equal(block.BlockId, readResult.Value.BlockId);
        Assert.Equal(block.Type, readResult.Value.Type);
    }

    [Fact]
    public async Task ReadBlockAsync_CorruptedHeaderChecksum_Fails()
    {
        // Arrange: write a valid block, then corrupt the header checksum bytes
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 2,
            Type = BlockType.Segment,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Corrupt the header checksum (bytes at offset HeaderSize, 16 bytes long)
        var fileBytes = File.ReadAllBytes(testFilePath);
        int headerChecksumOffset = RawBlockManager.HeaderSize;
        for (int i = 0; i < 16; i++)
            fileBytes[headerChecksumOffset + i] ^= 0xFF; // Flip all bits
        File.WriteAllBytes(testFilePath, fileBytes);

        // Act: open a new manager (which will try to scan) and read the block
        using var manager2 = new RawBlockManager(testFilePath);
        var readResult = await manager2.ReadBlockAsync(2);

        // Assert: block should not be found because scan rejects corrupted header checksum
        Assert.True(readResult.IsFailure);
    }

    [Fact]
    public async Task ReadBlockAsync_CorruptedPayloadChecksum_Fails()
    {
        // Arrange: write a valid block, then corrupt the payload checksum bytes
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 3,
            Type = BlockType.EmailContent,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Corrupt the payload checksum (located after header + header checksum + payload)
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize + payload.Length;
        for (int i = 0; i < 16; i++)
            fileBytes[payloadChecksumOffset + i] ^= 0xFF;
        File.WriteAllBytes(testFilePath, fileBytes);

        // Act: re-open — scan will succeed (header checksum is fine) but read will fail on payload checksum
        using var manager2 = new RawBlockManager(testFilePath);
        var readResult = await manager2.ReadBlockAsync(3);

        // Assert: read fails due to payload checksum mismatch
        Assert.True(readResult.IsFailure);
        Assert.Contains("Payload checksum mismatch", readResult.Error);
    }

    [Fact]
    public async Task ReadBlockAsync_CorruptedPayload_FailsChecksumVerification()
    {
        // Arrange: write a valid block, then corrupt the payload data itself
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 4,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Corrupt one byte of payload data (after header + header checksum)
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize;
        fileBytes[payloadOffset] ^= 0x01;
        File.WriteAllBytes(testFilePath, fileBytes);

        // Act
        using var manager2 = new RawBlockManager(testFilePath);
        var readResult = await manager2.ReadBlockAsync(4);

        // Assert: read fails because payload no longer matches its checksum
        Assert.True(readResult.IsFailure);
        Assert.Contains("Payload checksum mismatch", readResult.Error);
    }

    [Fact]
    public async Task ReadBlockAsync_LargePayload_RoundTrip_VerifiesChecksums()
    {
        // Arrange: large payload to ensure 16-byte checksums work at scale
        var payload = new byte[16384];
        new Random(42).NextBytes(payload);
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 5,
            Type = BlockType.BTreeLeaf,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Act
        var readResult = await manager.ReadBlockAsync(5);

        // Assert
        Assert.True(readResult.IsSuccess);
        Assert.Equal(payload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ReadBlockAsync_MultipleBlocks_AllVerifyChecksums()
    {
        // Arrange: write several blocks, read them all back
        using var manager = new RawBlockManager(testFilePath);
        var payloads = new Dictionary<long, byte[]>
        {
            { 10, new byte[] { 1, 2, 3 } },
            { 20, new byte[] { 4, 5, 6, 7, 8, 9 } },
            { 30, new byte[1024] }
        };
        new Random(99).NextBytes(payloads[30]);

        foreach (var kvp in payloads)
        {
            var block = new Block
            {
                BlockId = kvp.Key,
                Type = BlockType.Segment,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = kvp.Value
            };
            var writeResult = await manager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
        }

        // Act & Assert: each block round-trips successfully
        foreach (var kvp in payloads)
        {
            var readResult = await manager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read block {kvp.Key}");
            Assert.Equal(kvp.Value, readResult.Value.Payload);
        }
    }

    [Fact]
    public async Task ReadBlockAsync_EmptyPayload_Reads16ByteZeroChecksum()
    {
        // Arrange: empty payload should have 16 zero bytes as checksum
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

        // Act
        var readResult = await manager.ReadBlockAsync(6);

        // Assert: read path accepts 16 zero bytes for empty payload
        Assert.True(readResult.IsSuccess);
    }

    [Fact]
    public async Task ReadBlockAsync_FileLayout_HeaderChecksumIs16BytesAtCorrectOffset()
    {
        // Verify that the read path is reading from the right offset (16 bytes, not 4)
        var payload = new byte[] { 42 };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 7,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = 1234567890L,
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Read raw file and verify the header checksum at offset 36 is exactly 16 bytes of BLAKE3
        var fileBytes = File.ReadAllBytes(testFilePath);
        var headerBytes = new byte[RawBlockManager.HeaderSize];
        Array.Copy(fileBytes, 0, headerBytes, 0, RawBlockManager.HeaderSize);

        var expectedChecksum = RawBlockManager.ComputeChecksum(headerBytes);
        var storedChecksum = new byte[16];
        Array.Copy(fileBytes, RawBlockManager.HeaderSize, storedChecksum, 0, 16);

        Assert.Equal(expectedChecksum, storedChecksum);

        // Re-open and read — this proves the read path reads 16 bytes at offset 36
        using var manager2 = new RawBlockManager(testFilePath);
        var readResult = await manager2.ReadBlockAsync(7);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(payload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ReadBlockAsync_FileLayout_PayloadChecksumIs16BytesAtCorrectOffset()
    {
        // Verify the payload checksum is 16 bytes in the file and the read path validates it
        var payload = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 8,
            Type = BlockType.Segment,
            Version = 1,
            Timestamp = 9876543210L,
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Read raw file and verify payload checksum at correct offset
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize + payload.Length;
        var expectedChecksum = RawBlockManager.ComputeChecksum(payload);
        var storedChecksum = new byte[16];
        Array.Copy(fileBytes, payloadChecksumOffset, storedChecksum, 0, 16);

        Assert.Equal(expectedChecksum, storedChecksum);

        // Re-open and read — proves the read path reads 16 bytes for payload checksum
        using var manager2 = new RawBlockManager(testFilePath);
        var readResult = await manager2.ReadBlockAsync(8);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(payload, readResult.Value.Payload);
    }

    [Fact]
    public async Task ScanExistingBlocks_RejectsBlock_WithCorruptedHeaderChecksum()
    {
        // Arrange: write a valid block, corrupt header checksum, re-open file
        var payload = new byte[] { 1, 2, 3 };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 9,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        manager.Dispose();

        // Corrupt header checksum
        var fileBytes = File.ReadAllBytes(testFilePath);
        fileBytes[RawBlockManager.HeaderSize] ^= 0xFF;
        File.WriteAllBytes(testFilePath, fileBytes);

        // Act: re-open — ScanExistingBlocksAsync should reject the block
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: block was not indexed because its header checksum failed verification
        Assert.Empty(locations);
    }

    [Fact]
    public async Task ReadBlockAsync_PreservesAllBlockFields_AfterChecksumVerification()
    {
        // Ensure the read path returns complete block data after checksum verification passes
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 11,
            Type = BlockType.EmailContent,
            Version = 3,
            Flags = 0x01,
            Timestamp = 999888777L,
            Payload = new byte[] { 100, 200, 50, 75, 25 }
        };

        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Act
        var readResult = await manager.ReadBlockAsync(11);

        // Assert: all fields preserved after checksum-verified read
        Assert.True(readResult.IsSuccess);
        var readBlock = readResult.Value;
        Assert.Equal(block.BlockId, readBlock.BlockId);
        Assert.Equal(block.Type, readBlock.Type);
        Assert.Equal(block.Version, readBlock.Version);
        Assert.Equal(block.Flags, readBlock.Flags);
        Assert.Equal(block.Timestamp, readBlock.Timestamp);
        Assert.Equal(block.Payload, readBlock.Payload);
    }
}
