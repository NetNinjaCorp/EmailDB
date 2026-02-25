using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that the write path writes 16-byte BLAKE3-128 checksums.
/// Validates acceptance criterion: "Write path writes 16-byte checksums"
/// </summary>
public class WritePathChecksumTests : IDisposable
{
    private readonly string testFilePath;

    public WritePathChecksumTests()
    {
        testFilePath = Path.Combine(Path.GetTempPath(), $"test_write_checksum_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(testFilePath))
            File.Delete(testFilePath);
    }

    [Fact]
    public async Task WriteBlockAsync_HeaderChecksum_Is16Bytes()
    {
        // Arrange
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 1,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new byte[] { 10, 20, 30, 40 }
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        // Assert: read raw file bytes and extract header checksum at offset 36
        var fileBytes = File.ReadAllBytes(testFilePath);
        var headerChecksum = new byte[16];
        Array.Copy(fileBytes, RawBlockManager.HeaderSize, headerChecksum, 0, 16);

        Assert.Equal(16, headerChecksum.Length);
        // Verify it's a valid BLAKE3 checksum of the header
        var headerBytes = new byte[RawBlockManager.HeaderSize];
        Array.Copy(fileBytes, 0, headerBytes, 0, RawBlockManager.HeaderSize);
        var expected = RawBlockManager.ComputeChecksum(headerBytes);
        Assert.Equal(expected, headerChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_PayloadChecksum_Is16Bytes()
    {
        // Arrange
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 2,
            Type = BlockType.Segment,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        // Assert: payload checksum starts after header + header checksum + payload
        var fileBytes = File.ReadAllBytes(testFilePath);
        int payloadChecksumOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize + payload.Length;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, payloadChecksumOffset, payloadChecksum, 0, 16);

        Assert.Equal(16, payloadChecksum.Length);
        var expected = RawBlockManager.ComputeChecksum(payload);
        Assert.Equal(expected, payloadChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_BothChecksums_AreExactly16Bytes_InFileLayout()
    {
        // Arrange
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 3,
            Type = BlockType.BTreeLeaf,
            Version = 1,
            Timestamp = 1234567890L,
            Payload = payload
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        // Assert: total file size matches expected layout
        var fileBytes = File.ReadAllBytes(testFilePath);
        int expectedSize = RawBlockManager.TotalFixedOverhead + payload.Length;
        Assert.Equal(expectedSize, fileBytes.Length);

        // Header checksum region: bytes [36..52)
        int hcStart = RawBlockManager.HeaderSize;
        int hcEnd = hcStart + RawBlockManager.HeaderChecksumSize;
        Assert.Equal(16, hcEnd - hcStart);

        // Payload checksum region: bytes [52 + payloadLen .. 52 + payloadLen + 16)
        int pcStart = hcEnd + payload.Length;
        int pcEnd = pcStart + RawBlockManager.PayloadChecksumSize;
        Assert.Equal(16, pcEnd - pcStart);

        // Footer starts right after payload checksum
        int footerStart = pcEnd;
        ulong footerMagic = BitConverter.ToUInt64(fileBytes, footerStart);
        Assert.Equal(RawBlockManager.FOOTER_MAGIC, footerMagic);
    }

    [Fact]
    public async Task WriteBlockAsync_LargePayload_ChecksumsAre16Bytes()
    {
        // Arrange
        var payload = new byte[8192];
        new Random(42).NextBytes(payload);
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 4,
            Type = BlockType.EmailContent,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        // Assert
        var fileBytes = File.ReadAllBytes(testFilePath);

        // Header checksum
        var headerChecksum = new byte[16];
        Array.Copy(fileBytes, RawBlockManager.HeaderSize, headerChecksum, 0, 16);
        var headerBytes = new byte[RawBlockManager.HeaderSize];
        Array.Copy(fileBytes, 0, headerBytes, 0, RawBlockManager.HeaderSize);
        Assert.Equal(RawBlockManager.ComputeChecksum(headerBytes), headerChecksum);

        // Payload checksum
        int pcOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize + payload.Length;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, pcOffset, payloadChecksum, 0, 16);
        Assert.Equal(RawBlockManager.ComputeChecksum(payload), payloadChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_ChecksumsAreNotAllZeros()
    {
        // Arrange
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 5,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new byte[] { 1, 2, 3 }
        };

        // Act
        var result = await manager.WriteBlockAsync(block);
        Assert.True(result.IsSuccess);
        manager.Dispose();

        var fileBytes = File.ReadAllBytes(testFilePath);

        // Header checksum should not be all zeros
        var headerChecksum = new byte[16];
        Array.Copy(fileBytes, RawBlockManager.HeaderSize, headerChecksum, 0, 16);
        Assert.NotEqual(new byte[16], headerChecksum);

        // Payload checksum should not be all zeros for non-empty payload
        int pcOffset = RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize + block.Payload.Length;
        var payloadChecksum = new byte[16];
        Array.Copy(fileBytes, pcOffset, payloadChecksum, 0, 16);
        Assert.NotEqual(new byte[16], payloadChecksum);
    }

    [Fact]
    public async Task WriteBlockAsync_RoundTrip_ChecksumsVerify()
    {
        // Arrange: write a block, then read it back — read path should verify checksums
        var payload = new byte[] { 100, 200, 50, 75 };
        using var manager = new RawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 6,
            Type = BlockType.Segment,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload
        };

        // Act
        var writeResult = await manager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await manager.ReadBlockAsync(6);

        // Assert: read succeeds (implying checksums are valid)
        Assert.True(readResult.IsSuccess);
        Assert.Equal(payload, readResult.Value.Payload);
    }
}
