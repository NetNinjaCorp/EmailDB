using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Tenray.ZoneTree;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests demonstrating how ZoneTree and EmailDB can work together.
/// Shows manual integration patterns since direct FileStreamProvider integration
/// requires more complex ZoneTree setup.
/// </summary>
public class ZoneTreeEmailDBIntegrationTest : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public ZoneTreeEmailDBIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Store_ZoneTree_Data_In_EmailDB_Blocks()
    {
        // Arrange - Create a temporary directory for ZoneTree
        var tempDir = Path.Combine(Path.GetTempPath(), $"zt_emaildb_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Act - Create ZoneTree and store email data
            var emails = new[]
            {
                ("email001", "Subject: Welcome Message\nFrom: admin@company.com\nTo: user@company.com\n\nWelcome to our service!"),
                ("email002", "Subject: Meeting Reminder\nFrom: boss@company.com\nTo: team@company.com\n\nDon't forget the meeting tomorrow."),
                ("email003", "Subject: Project Status\nFrom: dev@company.com\nTo: manager@company.com\n\nProject is on track for delivery.")
            };

            // Use ZoneTree in a scope to ensure it's disposed before reading files
            using (var zoneTree = new ZoneTreeFactory<string, string>()
                       .SetDataDirectory(tempDir)
                       .OpenOrCreate())
            {
                _output.WriteLine("Adding emails to ZoneTree...");
                foreach (var (id, content) in emails)
                {
                    var success = zoneTree.TryAdd(id, content, out _);
                    Assert.True(success, $"Failed to add email {id}");
                    _output.WriteLine($"✅ Added email: {id}");
                }

                // Force ZoneTree to flush data to disk
                zoneTree.Maintenance.SaveMetaData();

                // Verify emails are in ZoneTree
                foreach (var (id, expectedContent) in emails)
                {
                    var found = zoneTree.TryGet(id, out var retrievedContent);
                    Assert.True(found, $"Email {id} not found in ZoneTree");
                    Assert.Equal(expectedContent, retrievedContent);
                }
            } // ZoneTree is disposed here, releasing file handles

            // Now manually copy ZoneTree data files to EmailDB blocks
            _output.WriteLine("\nCopying ZoneTree files to EmailDB blocks...");
            var zoneTreeFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            
            foreach (var filePath in zoneTreeFiles)
            {
                var fileName = Path.GetFileName(filePath);
                var fileData = await File.ReadAllBytesAsync(filePath);
                var fileInfo = new FileInfo(filePath);

                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.ZoneTreeSegment_KV,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = fileName.GetHashCode(), // Use filename hash as block ID
                    Payload = fileData
                };

                var result = await _blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess, $"Failed to store ZoneTree file {fileName} in EmailDB");
                
                _output.WriteLine($"✅ Stored ZoneTree file '{fileName}' ({fileData.Length} bytes) as EmailDB block {block.BlockId}");
            }

            // Verify EmailDB contains the ZoneTree data
            var blockLocations = _blockManager.GetBlockLocations();
            _output.WriteLine($"\nEmailDB now contains {blockLocations.Count} blocks:");
            
            foreach (var kvp in blockLocations)
            {
                var readResult = await _blockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess)
                {
                    var block = readResult.Value;
                    _output.WriteLine($"  Block {kvp.Key}: Type={block.Type}, Size={block.Payload.Length} bytes");
                }
            }

            Assert.True(blockLocations.Count >= zoneTreeFiles.Length, 
                "EmailDB should contain at least as many blocks as ZoneTree files");

            var fileSize = new FileInfo(_testFile).Length;
            _output.WriteLine($"\nEmailDB file size: {fileSize} bytes");
            Assert.True(fileSize > 0, "EmailDB file should contain data");

            _output.WriteLine("\n🎉 SUCCESS: ZoneTree data successfully stored in EmailDB blocks!");
            _output.WriteLine("✅ ZoneTree handles email indexing");
            _output.WriteLine("✅ EmailDB provides durable block storage");
            _output.WriteLine("✅ Manual integration pattern demonstrated");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    [Fact]
    public async Task Should_Restore_ZoneTree_From_EmailDB_Blocks()
    {
        // Arrange - First create and store ZoneTree data in EmailDB
        var tempDir1 = Path.Combine(Path.GetTempPath(), $"zt_store_{Guid.NewGuid()}");
        var tempDir2 = Path.Combine(Path.GetTempPath(), $"zt_restore_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir1);
        Directory.CreateDirectory(tempDir2);

        try
        {
            // Phase 1: Create ZoneTree with data and store in EmailDB
            var originalEmails = new[]
            {
                ("msg001", "Original email 1 content"),
                ("msg002", "Original email 2 content"),
                ("msg003", "Original email 3 content")
            };

            using (var zoneTree = new ZoneTreeFactory<string, string>()
                       .SetDataDirectory(tempDir1)
                       .OpenOrCreate())
            {
                foreach (var (id, content) in originalEmails)
                {
                    zoneTree.TryAdd(id, content, out _);
                }
                zoneTree.Maintenance.SaveMetaData();
            }

            // Store ZoneTree files in EmailDB
            var zoneTreeFiles = Directory.GetFiles(tempDir1, "*", SearchOption.AllDirectories);
            var fileBlockMapping = new Dictionary<string, int>();

            foreach (var filePath in zoneTreeFiles)
            {
                var fileName = Path.GetFileName(filePath);
                var fileData = await File.ReadAllBytesAsync(filePath);
                var blockId = fileName.GetHashCode();

                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.ZoneTreeSegment_KV,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = blockId,
                    Payload = fileData
                };

                await _blockManager.WriteBlockAsync(block);
                fileBlockMapping[fileName] = blockId;
                
                _output.WriteLine($"Stored {fileName} as block {blockId}");
            }

            // Phase 2: Restore ZoneTree files from EmailDB blocks
            _output.WriteLine("\nRestoring ZoneTree files from EmailDB blocks...");
            
            foreach (var kvp in fileBlockMapping)
            {
                var fileName = kvp.Key;
                var blockId = kvp.Value;
                
                var readResult = await _blockManager.ReadBlockAsync(blockId);
                Assert.True(readResult.IsSuccess, $"Failed to read block {blockId}");
                
                var restoredFilePath = Path.Combine(tempDir2, fileName);
                await File.WriteAllBytesAsync(restoredFilePath, readResult.Value.Payload);
                
                _output.WriteLine($"Restored block {blockId} to {fileName}");
            }

            // Phase 3: Verify restored ZoneTree works
            using var restoredZoneTree = new ZoneTreeFactory<string, string>()
                .SetDataDirectory(tempDir2)
                .OpenOrCreate();

            foreach (var (id, expectedContent) in originalEmails)
            {
                var found = restoredZoneTree.TryGet(id, out var retrievedContent);
                Assert.True(found, $"Email {id} not found in restored ZoneTree");
                Assert.Equal(expectedContent, retrievedContent);
                
                _output.WriteLine($"✅ Verified restored email: {id}");
            }

            _output.WriteLine("\n🎉 SUCCESS: ZoneTree data restored from EmailDB blocks!");
            _output.WriteLine("✅ Data persistence through EmailDB works");
            _output.WriteLine("✅ ZoneTree state fully recoverable");
        }
        finally
        {
            // Cleanup
            foreach (var dir in new[] { tempDir1, tempDir2 })
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }
            }
        }
    }

    [Fact]
    public async Task Should_Demonstrate_Hybrid_Email_Storage_Pattern()
    {
        // Arrange - Set up hybrid storage: ZoneTree for indexing, EmailDB for persistence
        var tempDir = Path.Combine(Path.GetTempPath(), $"hybrid_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var zoneTree = new ZoneTreeFactory<string, int>() // string email ID -> int block ID
                .SetDataDirectory(tempDir)
                .OpenOrCreate();

            // Simulate storing emails: ZoneTree holds index, EmailDB holds content
            var emails = new[]
            {
                ("user1@company.com", "Subject: Welcome\nBody: Welcome to the company!"),
                ("user2@company.com", "Subject: Training\nBody: Please attend training session."),
                ("user3@company.com", "Subject: Benefits\nBody: Here's information about your benefits.")
            };

            _output.WriteLine("Hybrid storage: ZoneTree for indexing, EmailDB for email content...");

            foreach (var (emailId, content) in emails)
            {
                // Step 1: Store email content in EmailDB block
                var emailBytes = System.Text.Encoding.UTF8.GetBytes(content);
                var blockId = emailId.GetHashCode(); // Use email ID hash as block ID

                var emailBlock = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment, // Email content block
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = blockId,
                    Payload = emailBytes
                };

                var storeResult = await _blockManager.WriteBlockAsync(emailBlock);
                Assert.True(storeResult.IsSuccess);

                // Step 2: Store email ID -> block ID mapping in ZoneTree
                var indexSuccess = zoneTree.TryAdd(emailId, blockId, out _);
                Assert.True(indexSuccess);

                _output.WriteLine($"✅ Stored email {emailId} -> block {blockId}");
            }

            zoneTree.Maintenance.SaveMetaData();

            // Test retrieval: Use ZoneTree index to find EmailDB blocks
            _output.WriteLine("\nRetrieving emails using ZoneTree index...");

            foreach (var (originalEmailId, originalContent) in emails)
            {
                // Step 1: Look up block ID in ZoneTree
                var found = zoneTree.TryGet(originalEmailId, out var blockId);
                Assert.True(found, $"Email ID {originalEmailId} not found in ZoneTree index");

                // Step 2: Retrieve email content from EmailDB block
                var readResult = await _blockManager.ReadBlockAsync(blockId);
                Assert.True(readResult.IsSuccess, $"Block {blockId} not found in EmailDB");

                var retrievedContent = System.Text.Encoding.UTF8.GetString(readResult.Value.Payload);
                Assert.Equal(originalContent, retrievedContent);

                _output.WriteLine($"✅ Retrieved {originalEmailId}: {retrievedContent.Substring(0, 30)}...");
            }

            // Show storage statistics
            var blockCount = _blockManager.GetBlockLocations().Count;
            var fileSize = new FileInfo(_testFile).Length;
            var zoneTreeFiles = Directory.GetFiles(tempDir).Length;

            _output.WriteLine($"\n📊 Hybrid Storage Statistics:");
            _output.WriteLine($"  ZoneTree index files: {zoneTreeFiles}");
            _output.WriteLine($"  EmailDB blocks: {blockCount}");
            _output.WriteLine($"  EmailDB file size: {fileSize} bytes");

            _output.WriteLine("\n🎉 SUCCESS: Hybrid ZoneTree + EmailDB storage pattern works!");
            _output.WriteLine("✅ ZoneTree provides fast email indexing");
            _output.WriteLine("✅ EmailDB provides durable email storage");
            _output.WriteLine("✅ Efficient lookup: Index -> Block ID -> Email Content");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
        
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}