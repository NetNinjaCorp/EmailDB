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
        using (var blockManager = new RawBlockManager(_testFile))
        {
            // Write metadata block
            var blockMetadata = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                BlockId = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = new byte[512]
            };

            var metaResult = await blockManager.WriteBlockAsync(blockMetadata);
            Assert.True(metaResult.IsSuccess, $"Failed to write metadata block: {metaResult.Error}");
            _output.WriteLine("✓ Metadata block written successfully");

            // Write WAL block
            var blockWal = new Block
            {
                Version = 1,
                Type = BlockType.WAL,
                BlockId = 2,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = new byte[512]
            };

            var walResult = await blockManager.WriteBlockAsync(blockWal);
            Assert.True(walResult.IsSuccess, $"Failed to write WAL block: {walResult.Error}");
            _output.WriteLine("✓ WAL block written successfully");

            // Write multiple segment blocks
            var random = new Random(42);
            for (int i = 0; i < 10; i++)
            {
                var payloadSize = random.Next(4096, 16384);
                var payload = new byte[payloadSize];
                random.NextBytes(payload);
                
                var blockSegment = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    BlockId = 3 + i,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Payload = payload
                };

                var segResult = await blockManager.WriteBlockAsync(blockSegment);
                Assert.True(segResult.IsSuccess, $"Failed to write segment block {i}: {segResult.Error}");
            }
            _output.WriteLine("✓ 10 segment blocks written successfully");
        }

        // Read blocks back
        using (var blockManager = new RawBlockManager(_testFile, false))
        {
            var scanResult = await blockManager.ScanFile();
            _output.WriteLine($"✓ Found {scanResult.Count} blocks in file");
            
            Assert.Equal(12, scanResult.Count); // 1 metadata + 1 WAL + 10 segments

            // Verify we can read specific blocks
            var readResult = await blockManager.ReadBlockAsync(1);
            Assert.True(readResult.IsSuccess);
            Assert.Equal(BlockType.Metadata, readResult.Value.Type);
            _output.WriteLine("✓ Successfully read metadata block");

            readResult = await blockManager.ReadBlockAsync(2);
            Assert.True(readResult.IsSuccess);
            Assert.Equal(BlockType.WAL, readResult.Value.Type);
            _output.WriteLine("✓ Successfully read WAL block");
        }
    }

    [Fact]
    public async Task Test_Large_Block_Handling()
    {
        _output.WriteLine("Testing large block handling...");
        
        using (var blockManager = new RawBlockManager(_testFile))
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
                    Payload = payload
                };

                var result = await blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess, $"Failed to write {size} byte block");
                _output.WriteLine($"✓ Successfully wrote {size / 1024}KB block");
            }
        }

        // Verify all blocks
        using (var blockManager = new RawBlockManager(_testFile, false))
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