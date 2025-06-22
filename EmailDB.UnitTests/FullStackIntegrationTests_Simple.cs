using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class FullStackIntegrationTests_Simple : IDisposable
{
    private readonly string _testDirectory;
    
    public FullStackIntegrationTests_Simple()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"EmailDB_Simple_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task SimpleBlockWriteReadTest()
    {
        var dbPath = Path.Combine(_testDirectory, "simple.edb");
        using var rawManager = new RawBlockManager(dbPath);
        
        // Create a simple block
        var block = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 123,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = new byte[] { 1, 2, 3, 4, 5 },
            Encoding = PayloadEncoding.RawBytes
        };
        
        // Write the block
        var writeResult = await rawManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");
        
        // Read it back using the block ID
        var readResult = await rawManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");
        
        // Verify the data
        Assert.Equal(block.Type, readResult.Value.Type);
        Assert.Equal(block.BlockId, readResult.Value.BlockId);
        Assert.Equal(block.Payload, readResult.Value.Payload);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}