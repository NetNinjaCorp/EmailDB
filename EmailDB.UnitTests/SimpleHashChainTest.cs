using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Simple test to demonstrate hash chain functionality.
/// </summary>
public class SimpleHashChainTest : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public SimpleHashChainTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"SimpleHashTest_{Guid.NewGuid():N}.emdb");
    }

    [Fact]
    public async Task Should_Create_Hash_Chain_For_Blocks()
    {
        _output.WriteLine("🔗 SIMPLE HASH CHAIN TEST");
        _output.WriteLine("========================");

        // Create blocks with regular manager
        using (var blockManager = new RawBlockManager(_testFile))
        {
            // Write some blocks
            for (int i = 0; i < 3; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = 100 + i,
                    Payload = Encoding.UTF8.GetBytes($"{{\"data\": \"Test block {i}\"}}")
                };

                var result = await blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess);
                _output.WriteLine($"✅ Wrote block {block.BlockId}");
            }
        }

        // Now create hash chain for existing blocks
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var hashChainManager = new HashChainManager(blockManager);

            // Add existing blocks to chain
            for (int i = 0; i < 3; i++)
            {
                var blockResult = await blockManager.ReadBlockAsync(100 + i);
                Assert.True(blockResult.IsSuccess);

                var chainResult = await hashChainManager.AddToChainAsync(blockResult.Value);
                Assert.True(chainResult.IsSuccess);
                
                _output.WriteLine($"✅ Added block {100 + i} to hash chain");
                _output.WriteLine($"   Sequence: {chainResult.Value.SequenceNumber}");
                _output.WriteLine($"   Block Hash: {chainResult.Value.BlockHash.Substring(0, 16)}...");
                _output.WriteLine($"   Chain Hash: {chainResult.Value.ChainHash.Substring(0, 16)}...");
            }

            // Verify the chain
            var verifyResult = await hashChainManager.VerifyEntireChainAsync();
            Assert.True(verifyResult.IsSuccess);
            
            _output.WriteLine($"\n📊 Chain Verification:");
            _output.WriteLine($"   Total blocks in chain: {verifyResult.Value.TotalBlocks}");
            _output.WriteLine($"   Valid blocks: {verifyResult.Value.ValidBlocks}");
            _output.WriteLine($"   Chain integrity: {verifyResult.Value.ChainIntegrity}");
            
            Assert.Equal(3, verifyResult.Value.TotalBlocks);
            Assert.Equal(3, verifyResult.Value.ValidBlocks);
            Assert.True(verifyResult.Value.ChainIntegrity ?? false);
        }

        _output.WriteLine("\n✅ SIMPLE HASH CHAIN TEST COMPLETED");
    }

    [Fact]  
    public async Task Should_Detect_Tampering_With_Hash_Chain()
    {
        _output.WriteLine("🚨 TAMPER DETECTION TEST");
        _output.WriteLine("======================");

        // Create blocks and hash chain
        using (var blockManager = new RawBlockManager(_testFile))
        {
            var hashChainManager = new HashChainManager(blockManager);

            // Write and chain a block
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 100,
                Payload = Encoding.UTF8.GetBytes("{\"data\": \"Important data\"}")
            };

            var writeResult = await blockManager.WriteBlockAsync(block);
            var chainResult = await hashChainManager.AddToChainAsync(block);
            
            Assert.True(writeResult.IsSuccess);
            Assert.True(chainResult.IsSuccess);
            
            _output.WriteLine($"✅ Created block 100 with hash chain");
            _output.WriteLine($"   Original hash: {chainResult.Value.BlockHash.Substring(0, 16)}...");
        }

        // Tamper with the file
        var fileBytes = await File.ReadAllBytesAsync(_testFile);
        
        // Find and corrupt some data (skip header area)
        var corruptionOffset = fileBytes.Length / 2;
        for (int i = 0; i < 10; i++)
        {
            if (corruptionOffset + i < fileBytes.Length)
            {
                fileBytes[corruptionOffset + i] ^= 0xFF; // Flip bits
            }
        }
        
        await File.WriteAllBytesAsync(_testFile, fileBytes);
        _output.WriteLine($"💥 Corrupted file at offset {corruptionOffset}");

        // Try to verify the chain
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var hashChainManager = new HashChainManager(blockManager);
            
            var verifyResult = await hashChainManager.VerifyBlockAsync(100);
            
            _output.WriteLine($"\n🔍 Verification Result:");
            if (verifyResult.IsSuccess)
            {
                _output.WriteLine($"   Block valid: {verifyResult.Value.IsValid}");
                _output.WriteLine($"   Chain valid: {verifyResult.Value.ChainValid}");
                
                if (!verifyResult.Value.IsValid)
                {
                    _output.WriteLine($"   Error: {verifyResult.Value.Error}");
                    _output.WriteLine($"   Expected hash: {verifyResult.Value.ExpectedBlockHash?.Substring(0, 16)}...");
                    _output.WriteLine($"   Actual hash: {verifyResult.Value.ActualBlockHash?.Substring(0, 16)}...");
                }
            }
            else
            {
                _output.WriteLine($"   Verification failed: {verifyResult.Error}");
            }
            
            // We expect either the block read to fail (checksum) or hash mismatch
            Assert.True(!verifyResult.IsSuccess || !verifyResult.Value.IsValid);
        }

        _output.WriteLine("\n✅ TAMPER DETECTION TEST COMPLETED");
    }

    public void Dispose()
    {
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort
            }
        }
    }
}