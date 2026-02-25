using System.IO.MemoryMappedFiles;
using System.Reflection;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that ScanExistingBlocksAsync and TryReadBlockLocation use 16-byte BLAKE3-128 checksums.
/// Validates acceptance criterion: "Scan and TryReadBlockLocation use 16-byte checksums"
/// </summary>
public class ScanAndTryReadBlockLocationChecksumTests : IDisposable
{
    private readonly string testFilePath;

    public ScanAndTryReadBlockLocationChecksumTests()
    {
        testFilePath = Path.Combine(Path.GetTempPath(), $"test_scan_tryread_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(testFilePath))
            File.Delete(testFilePath);
    }

    #region ScanExistingBlocksAsync Tests

    [Fact]
    public void Scan_FindsBlock_WithValid16ByteHeaderChecksum()
    {
        // Arrange: write a block, dispose, reopen — constructor calls ScanExistingBlocksAsync
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 1,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 10, 20, 30 }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Act: reopen — scan runs in constructor
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: scan found the block (proving it read the 16-byte checksum correctly)
        Assert.Single(locations);
        Assert.True(locations.ContainsKey(1));
    }

    [Fact]
    public void Scan_FindsMultipleBlocks_WithValid16ByteChecksums()
    {
        // Arrange: write several blocks, reopen to trigger scan
        using (var manager = new RawBlockManager(testFilePath))
        {
            for (long i = 1; i <= 5; i++)
            {
                var block = new Block
                {
                    BlockId = i,
                    Type = BlockType.Segment,
                    Version = 1,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = new byte[] { (byte)i, (byte)(i * 2) }
                };
                manager.WriteBlockAsync(block).GetAwaiter().GetResult();
            }
        }

        // Act
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: all 5 blocks found by scan
        Assert.Equal(5, locations.Count);
        for (long i = 1; i <= 5; i++)
            Assert.True(locations.ContainsKey(i), $"Block {i} not found by scan");
    }

    [Fact]
    public void Scan_RejectsBlock_WhenHeaderChecksumCorrupted()
    {
        // Arrange: write a block, corrupt the 16-byte header checksum, reopen
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 10,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 1, 2, 3 }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Corrupt the first byte of the 16-byte header checksum
        var fileBytes = File.ReadAllBytes(testFilePath);
        fileBytes[RawBlockManager.HeaderSize] ^= 0xFF;
        File.WriteAllBytes(testFilePath, fileBytes);

        // Act
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: scan rejected the block due to checksum mismatch
        Assert.Empty(locations);
    }

    [Fact]
    public void Scan_RejectsBlock_WhenLastByteOfHeaderChecksumCorrupted()
    {
        // Arrange: write a block, corrupt the last (16th) byte of header checksum
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 11,
                Type = BlockType.EmailContent,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 0xAA, 0xBB }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Corrupt byte 15 (the last byte) of the 16-byte header checksum
        var fileBytes = File.ReadAllBytes(testFilePath);
        int lastChecksumByte = RawBlockManager.HeaderSize + 15;
        fileBytes[lastChecksumByte] ^= 0x01;
        File.WriteAllBytes(testFilePath, fileBytes);

        // Act
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: scan rejects — proves it reads all 16 bytes, not just 4
        Assert.Empty(locations);
    }

    [Fact]
    public void Scan_ReadsChecksumFromCorrectOffset()
    {
        // Verify that scan reads the checksum starting at offset HeaderSize (36)
        // and spanning 16 bytes — not 4 bytes as CRC32 would
        var payload = new byte[] { 42, 43, 44 };
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 12,
                Type = BlockType.BTreeLeaf,
                Version = 1,
                Timestamp = 1234567890L,
                Payload = payload
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Read raw file and verify the BLAKE3 checksum at offset 36 spans 16 bytes
        var fileBytes = File.ReadAllBytes(testFilePath);
        var headerBytes = new byte[RawBlockManager.HeaderSize];
        Array.Copy(fileBytes, 0, headerBytes, 0, RawBlockManager.HeaderSize);

        var expectedChecksum = RawBlockManager.ComputeChecksum(headerBytes);
        Assert.Equal(16, expectedChecksum.Length);

        var storedChecksum = new byte[16];
        Array.Copy(fileBytes, RawBlockManager.HeaderSize, storedChecksum, 0, 16);
        Assert.Equal(expectedChecksum, storedChecksum);

        // Reopen — scan reads these 16 bytes and finds the block
        using var manager2 = new RawBlockManager(testFilePath);
        Assert.Single(manager2.GetBlockLocations());
    }

    [Fact]
    public void Scan_TracksLatestMetadataBlock_WithValid16ByteChecksums()
    {
        // Arrange: write non-metadata then metadata blocks
        using (var manager = new RawBlockManager(testFilePath))
        {
            var folderBlock = new Block
            {
                BlockId = 100,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 1 }
            };
            manager.WriteBlockAsync(folderBlock).GetAwaiter().GetResult();

            var metadataBlock = new Block
            {
                BlockId = 200,
                Type = BlockType.Metadata,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 2 }
            };
            manager.WriteBlockAsync(metadataBlock).GetAwaiter().GetResult();
        }

        // Act: reopen triggers scan
        using var manager2 = new RawBlockManager(testFilePath);

        // Assert: scan found both blocks and identified the metadata block
        Assert.Equal(2, manager2.GetBlockLocations().Count);
        Assert.Equal(200, manager2.GetLatestMetadataBlockId());
    }

    [Fact]
    public void Scan_HandlesEmptyPayloadBlock_With16ByteZeroChecksum()
    {
        // Arrange: write block with null payload (uses 16 zero bytes as checksum)
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 13,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = null
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Act: reopen triggers scan
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: scan found the block (header checksum is valid 16-byte BLAKE3)
        Assert.Single(locations);
        Assert.True(locations.ContainsKey(13));
    }

    [Fact]
    public void Scan_CalculatesCorrectBlockLength_With16ByteOverhead()
    {
        // Verify that scan uses TotalFixedOverhead (84 = 36 + 16 + 16 + 16) for block length
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 14,
                Type = BlockType.Segment,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = payload
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Act
        using var manager2 = new RawBlockManager(testFilePath);
        var locations = manager2.GetBlockLocations();

        // Assert: block length = TotalFixedOverhead + payload length
        Assert.Single(locations);
        var location = locations[14];
        Assert.Equal(RawBlockManager.TotalFixedOverhead + payload.Length, location.Length);
    }

    #endregion

    #region TryReadBlockLocation Tests

    [Fact]
    public void TryReadBlockLocation_ReturnsTrue_ForValidBlockWith16ByteChecksum()
    {
        // Arrange: write a valid block
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 20,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 10, 20, 30 }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Act: invoke TryReadBlockLocation via reflection on a fresh manager instance
        using var manager2 = new RawBlockManager(testFilePath);
        long fileLength = new FileInfo(testFilePath).Length;

        using var mmf = MemoryMappedFile.CreateFromFile(testFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var method = typeof(RawBlockManager).GetMethod("TryReadBlockLocation", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var parameters = new object[] { accessor, 0L, fileLength, default(BlockLocation), 0L };
        bool result = (bool)method.Invoke(manager2, parameters);

        // Assert
        Assert.True(result);
        var location = (BlockLocation)parameters[3];
        var blockId = (long)parameters[4];
        Assert.Equal(20, blockId);
        Assert.Equal(0, location.Position);
    }

    [Fact]
    public void TryReadBlockLocation_ReturnsFalse_WhenHeaderChecksumCorrupted()
    {
        // Arrange: write a valid block, then corrupt the header checksum
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 21,
                Type = BlockType.Segment,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 5, 6, 7 }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Corrupt byte 8 of the 16-byte header checksum (mid-range)
        var fileBytes = File.ReadAllBytes(testFilePath);
        fileBytes[RawBlockManager.HeaderSize + 8] ^= 0xFF;
        File.WriteAllBytes(testFilePath, fileBytes);

        long fileLength = new FileInfo(testFilePath).Length;

        // Act: call TryReadBlockLocation via reflection on a new manager (opened on the corrupted file)
        // Note: the constructor's scan will also reject this block, so we create with a separate temp file
        // and use reflection directly
        using var cleanFile = new RawBlockManager(Path.Combine(Path.GetTempPath(), $"tryread_clean_{Guid.NewGuid()}.dat"));
        using var mmf = MemoryMappedFile.CreateFromFile(testFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var method = typeof(RawBlockManager).GetMethod("TryReadBlockLocation", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var parameters = new object[] { accessor, 0L, fileLength, default(BlockLocation), 0L };
        bool result = (bool)method.Invoke(cleanFile, parameters);

        // Assert: returns false due to corrupted 16-byte checksum
        Assert.False(result);

        // Cleanup
        var cleanPath = Path.Combine(Path.GetTempPath(), $"tryread_clean_{Guid.NewGuid()}.dat");
        // cleanFile's path is already handled by its own disposal
    }

    [Fact]
    public void TryReadBlockLocation_ReturnsFalse_WhenLastByteOfChecksumCorrupted()
    {
        // Proves TryReadBlockLocation reads all 16 bytes, not just the first 4
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 22,
                Type = BlockType.BTreeInternal,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 0xDE, 0xAD }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Corrupt the 16th byte (index 15) of the header checksum
        var fileBytes = File.ReadAllBytes(testFilePath);
        fileBytes[RawBlockManager.HeaderSize + 15] ^= 0x01;
        File.WriteAllBytes(testFilePath, fileBytes);

        long fileLength = new FileInfo(testFilePath).Length;

        var cleanFilePath = Path.Combine(Path.GetTempPath(), $"tryread_clean2_{Guid.NewGuid()}.dat");
        using var cleanManager = new RawBlockManager(cleanFilePath);
        using var mmf = MemoryMappedFile.CreateFromFile(testFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var method = typeof(RawBlockManager).GetMethod("TryReadBlockLocation", BindingFlags.NonPublic | BindingFlags.Instance);
        var parameters = new object[] { accessor, 0L, fileLength, default(BlockLocation), 0L };
        bool result = (bool)method.Invoke(cleanManager, parameters);

        // Assert: returns false — proves all 16 bytes are read and compared
        Assert.False(result);

        // Cleanup
        cleanManager.Dispose();
        if (File.Exists(cleanFilePath))
            File.Delete(cleanFilePath);
    }

    [Fact]
    public void TryReadBlockLocation_ReadsChecksumViaReadArray_16Bytes()
    {
        // Verify the checksum is read as 16 bytes (ReadArray with count 16)
        // by checking that a valid block at a non-zero position is found
        var payload1 = new byte[] { 1, 2, 3 };
        var payload2 = new byte[] { 4, 5, 6, 7, 8 };

        using (var manager = new RawBlockManager(testFilePath))
        {
            var block1 = new Block
            {
                BlockId = 30,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = payload1
            };
            manager.WriteBlockAsync(block1).GetAwaiter().GetResult();

            var block2 = new Block
            {
                BlockId = 31,
                Type = BlockType.Segment,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = payload2
            };
            manager.WriteBlockAsync(block2).GetAwaiter().GetResult();
        }

        long fileLength = new FileInfo(testFilePath).Length;
        long block2Position = RawBlockManager.TotalFixedOverhead + payload1.Length;

        using var manager2 = new RawBlockManager(testFilePath);
        using var mmf = MemoryMappedFile.CreateFromFile(testFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var method = typeof(RawBlockManager).GetMethod("TryReadBlockLocation", BindingFlags.NonPublic | BindingFlags.Instance);
        var parameters = new object[] { accessor, block2Position, fileLength, default(BlockLocation), 0L };
        bool result = (bool)method.Invoke(manager2, parameters);

        // Assert: found the second block at its correct offset
        Assert.True(result);
        var location = (BlockLocation)parameters[3];
        var blockId = (long)parameters[4];
        Assert.Equal(31, blockId);
        Assert.Equal(block2Position, location.Position);
        Assert.Equal(RawBlockManager.TotalFixedOverhead + payload2.Length, location.Length);
    }

    [Fact]
    public void TryReadBlockLocation_ReturnsFalse_WhenNotEnoughSpaceForHeaderAndChecksum()
    {
        // Verify TryReadBlockLocation checks for HeaderSize + HeaderChecksumSize (36 + 16 = 52)
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 40,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = new byte[] { 1 }
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        long fileLength = new FileInfo(testFilePath).Length;

        using var manager2 = new RawBlockManager(testFilePath);
        using var mmf = MemoryMappedFile.CreateFromFile(testFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var method = typeof(RawBlockManager).GetMethod("TryReadBlockLocation", BindingFlags.NonPublic | BindingFlags.Instance);

        // Position near end of file where HeaderSize + HeaderChecksumSize won't fit
        long nearEnd = fileLength - (RawBlockManager.HeaderSize + RawBlockManager.HeaderChecksumSize) + 1;
        var parameters = new object[] { accessor, nearEnd, fileLength, default(BlockLocation), 0L };
        bool result = (bool)method.Invoke(manager2, parameters);

        // Assert: returns false — not enough space for header + 16-byte checksum
        Assert.False(result);
    }

    [Fact]
    public void TryReadBlockLocation_VerifiesFooterMagic_AfterChecksumValidation()
    {
        // Arrange: write a valid block, corrupt the footer magic
        var payload = new byte[] { 1, 2, 3 };
        using (var manager = new RawBlockManager(testFilePath))
        {
            var block = new Block
            {
                BlockId = 50,
                Type = BlockType.Folder,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = payload
            };
            manager.WriteBlockAsync(block).GetAwaiter().GetResult();
        }

        // Corrupt footer magic (last 16 bytes: 8 for footer magic + 8 for length)
        var fileBytes = File.ReadAllBytes(testFilePath);
        int footerMagicOffset = fileBytes.Length - RawBlockManager.FooterSize;
        fileBytes[footerMagicOffset] ^= 0xFF;
        File.WriteAllBytes(testFilePath, fileBytes);

        long fileLength = new FileInfo(testFilePath).Length;

        var cleanFilePath = Path.Combine(Path.GetTempPath(), $"tryread_footer_{Guid.NewGuid()}.dat");
        using var cleanManager = new RawBlockManager(cleanFilePath);
        using var mmf = MemoryMappedFile.CreateFromFile(testFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var method = typeof(RawBlockManager).GetMethod("TryReadBlockLocation", BindingFlags.NonPublic | BindingFlags.Instance);
        var parameters = new object[] { accessor, 0L, fileLength, default(BlockLocation), 0L };
        bool result = (bool)method.Invoke(cleanManager, parameters);

        // Assert: header checksum passes (16 bytes valid) but footer is corrupted
        Assert.False(result);

        cleanManager.Dispose();
        if (File.Exists(cleanFilePath))
            File.Delete(cleanFilePath);
    }

    #endregion
}
