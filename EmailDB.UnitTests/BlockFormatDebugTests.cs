using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

public class BlockFormatDebugTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public BlockFormatDebugTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Debug_Block_Write_And_Read()
    {
        // Arrange
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 12345,
            Payload = new byte[] { 1, 2, 3, 4, 5 }
        };

        _output.WriteLine($"Writing block: Version={block.Version}, Type={block.Type}, Encoding={block.Encoding}, BlockId={block.BlockId}");

        // Act - Write
        var writeResult = await _blockManager.WriteBlockAsync(block);
        if (!writeResult.IsSuccess)
        {
            _output.WriteLine($"Write failed: {writeResult.Error}");
            Assert.True(false, $"Write failed: {writeResult.Error}");
        }

        _output.WriteLine($"Write successful at position {writeResult.Value.Position}, length {writeResult.Value.Length}");

        // Act - Read
        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        if (!readResult.IsSuccess)
        {
            _output.WriteLine($"Read failed: {readResult.Error}");
            
            // Let's dump the raw file content
            _output.WriteLine("\nRaw file content (first 100 bytes):");
            using (var fs = new FileStream(_testFile, FileMode.Open, FileAccess.Read))
            {
                var buffer = new byte[Math.Min(100, fs.Length)];
                fs.Read(buffer, 0, buffer.Length);
                _output.WriteLine(BitConverter.ToString(buffer).Replace("-", " "));
            }
            
            Assert.True(false, $"Read failed: {readResult.Error}");
        }

        // Assert
        var readBlock = readResult.Value;
        _output.WriteLine($"Read block: Version={readBlock.Version}, Type={readBlock.Type}, Encoding={readBlock.Encoding}, BlockId={readBlock.BlockId}");
        
        Assert.Equal(block.Encoding, readBlock.Encoding);
        Assert.Equal(block.Version, readBlock.Version);
        Assert.Equal(block.Type, readBlock.Type);
        Assert.Equal(block.Flags, readBlock.Flags);
        Assert.Equal(block.BlockId, readBlock.BlockId);
    }

    [Fact]
    public async Task Debug_Header_Size_And_Offsets()
    {
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.Protobuf,
            Timestamp = 0x0123456789ABCDEF,
            BlockId = 0x7EDCBA9876543210,  // Keep positive for long
            Payload = Array.Empty<byte>()
        };

        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Read raw bytes to inspect header
        using (var fs = new FileStream(_testFile, FileMode.Open, FileAccess.Read))
        {
            var headerBytes = new byte[41]; // 37 header + 4 checksum
            fs.Read(headerBytes, 0, headerBytes.Length);
            
            _output.WriteLine("Header bytes (37 bytes + 4 checksum):");
            for (int i = 0; i < headerBytes.Length; i++)
            {
                if (i == 37) _output.WriteLine("\nHeader Checksum:");
                _output.WriteLine($"Offset {i:D2}: 0x{headerBytes[i]:X2}");
            }
        }
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
        if (File.Exists(_testFile))
        {
            File.Delete(_testFile);
        }
    }
}