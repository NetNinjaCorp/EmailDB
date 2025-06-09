using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Basic tests for RawBlockManager functionality.
/// Consolidated from EmailDB.Testing.RawBlocks project.
/// </summary>
public class RawBlockManagerBasicTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testFile;

    public RawBlockManagerBasicTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.blk");
    }

    [Fact]
    public async Task Test_Basic_Block_Operations()
    {
        _output.WriteLine("Testing basic RawBlockManager operations...");
        
        // Create and write blocks
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: true))
        {
            // Check if there are any blocks initially
            var initialScan = await blockManager.ScanFile();
            _output.WriteLine($"Initial blocks in new file: {initialScan.Count}");
            
            // Write just 3 blocks to simplify debugging
            var block1 = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                BlockId = 100,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = new byte[512],
                Encoding = PayloadEncoding.RawBytes,
                Flags = 0
            };

            var result1 = await blockManager.WriteBlockAsync(block1);
            Assert.True(result1.IsSuccess);
            _output.WriteLine($"✓ Block 100 written at position {result1.Value.Position}");

            var block2 = new Block
            {
                Version = 1,
                Type = BlockType.WAL,
                BlockId = 200,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = new byte[512],
                Encoding = PayloadEncoding.RawBytes,
                Flags = 0
            };

            var result2 = await blockManager.WriteBlockAsync(block2);
            Assert.True(result2.IsSuccess);
            _output.WriteLine($"✓ Block 200 written at position {result2.Value.Position}");

            var block3 = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                BlockId = 300,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = new byte[1024],
                Encoding = PayloadEncoding.RawBytes,
                Flags = 0
            };

            var result3 = await blockManager.WriteBlockAsync(block3);
            Assert.True(result3.IsSuccess);
            _output.WriteLine($"✓ Block 300 written at position {result3.Value.Position}");
        }

        // Ensure file is not locked
        await Task.Delay(100);
        
        // Read blocks back
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            _output.WriteLine("Created second RawBlockManager instance");
            
            var scanResult = await blockManager.ScanFile();
            _output.WriteLine($"✓ Found {scanResult.Count} magic positions in file");
            
            // Print all block locations for debugging
            var locations = blockManager.GetBlockLocations();
            _output.WriteLine($"Block locations count: {locations.Count}");
            
            foreach (var loc in locations.OrderBy(x => x.Key))
            {
                _output.WriteLine($"  Block ID {loc.Key}: Position={loc.Value.Position}, Length={loc.Value.Length}");
            }
            
            Assert.Equal(3, scanResult.Count);
            Assert.Equal(3, locations.Count);

            // Verify we can read specific blocks
            var readResult = await blockManager.ReadBlockAsync(100);
            Assert.True(readResult.IsSuccess, $"Failed to read block 100: {readResult.Error}");
            Assert.Equal(BlockType.Metadata, readResult.Value.Type);
            _output.WriteLine("✓ Successfully read block 100");

            readResult = await blockManager.ReadBlockAsync(200);
            Assert.True(readResult.IsSuccess, $"Failed to read block 200: {readResult.Error}");
            Assert.Equal(BlockType.WAL, readResult.Value.Type);
            _output.WriteLine("✓ Successfully read block 200");
            
            readResult = await blockManager.ReadBlockAsync(300);
            Assert.True(readResult.IsSuccess, $"Failed to read block 300: {readResult.Error}");
            Assert.Equal(BlockType.Segment, readResult.Value.Type);
            _output.WriteLine("✓ Successfully read block 300");
        }
    }

    [Fact]
    public async Task Test_Large_Block_Handling()
    {
        _output.WriteLine("Testing large block handling...");
        
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: true))
        {
            // Test various block sizes
            var sizes = new[] { 1024, 64 * 1024, 256 * 1024, 1024 * 1024 };
            
            foreach (var size in sizes)
            {
                var payload = new byte[size];
                new Random(42).NextBytes(payload);
                
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    BlockId = size,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Payload = payload,
                    Encoding = PayloadEncoding.RawBytes,
                    Flags = 0
                };

                var result = await blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess, $"Failed to write {size} byte block");
                _output.WriteLine($"✓ Successfully wrote {size / 1024}KB block");
            }
        }

        // Verify all blocks
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var scanResult = await blockManager.ScanFile();
            Assert.Equal(4, scanResult.Count);
            
            var sizes2 = new[] { 1024, 64 * 1024, 256 * 1024, 1024 * 1024 };
            foreach (var size in sizes2)
            {
                var readResult = await blockManager.ReadBlockAsync(size);
                Assert.True(readResult.IsSuccess);
                Assert.Equal(size, readResult.Value.Payload.Length);
            }
            _output.WriteLine("✓ All large blocks verified successfully");
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testFile))
                File.Delete(_testFile);
        }
        catch { }
    }
}