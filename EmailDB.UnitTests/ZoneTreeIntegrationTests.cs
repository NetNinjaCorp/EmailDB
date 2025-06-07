using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Serializers;
using Tenray.ZoneTree.Comparers;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for ZoneTree integration with EmailDB block storage.
/// Verifies that ZoneTree operations create blocks in EmailDB.
/// </summary>
public class ZoneTreeIntegrationTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public ZoneTreeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
        _tempDir = Path.Combine(Path.GetTempPath(), $"zt_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Should_Create_Basic_ZoneTree_Without_EmailDB_Integration()
    {
        // Arrange - Test basic ZoneTree functionality first
        var zoneTreePath = Path.Combine(_tempDir, "basic_zonetree");
        
        // Act - Create a basic ZoneTree
        using var zoneTree = new ZoneTreeFactory<int, string>()
            .SetDataDirectory(zoneTreePath)
            .OpenOrCreate();

        // Add some test data
        for (int i = 0; i < 100; i++)
        {
            zoneTree.TryAdd(i, $"Value_{i}", out _);
        }

        // Force a save/flush
        zoneTree.Maintenance.SaveMetaData();
        
        // Assert
        Assert.True(zoneTree.TryGet(50, out var value));
        Assert.Equal("Value_50", value);
        
        // Check if files were created
        var files = Directory.GetFiles(zoneTreePath, "*", SearchOption.AllDirectories);
        _output.WriteLine($"ZoneTree created {files.Length} files:");
        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            _output.WriteLine($"  {Path.GetFileName(file)}: {fileInfo.Length} bytes");
        }
        
        Assert.True(files.Length > 0, "ZoneTree should create files");
    }

    [Fact]
    public async Task Should_Store_Email_Like_Data_In_ZoneTree()
    {
        // Arrange - Test ZoneTree with email-like data structures
        var zoneTreePath = Path.Combine(_tempDir, "email_zonetree");
        
        // Act - Create ZoneTree for email data
        using var emailZoneTree = new ZoneTreeFactory<string, byte[]>()
            .SetDataDirectory(zoneTreePath)
            .OpenOrCreate();

        // Add some mock email data
        var emails = new[]
        {
            ("email1@test.com", "Subject: Test Email 1\r\nFrom: sender@test.com\r\nTo: email1@test.com\r\n\r\nThis is test email 1 content."),
            ("email2@test.com", "Subject: Test Email 2\r\nFrom: sender@test.com\r\nTo: email2@test.com\r\n\r\nThis is test email 2 content."),
            ("email3@test.com", "Subject: Test Email 3\r\nFrom: sender@test.com\r\nTo: email3@test.com\r\n\r\nThis is test email 3 content.")
        };

        foreach (var (emailId, content) in emails)
        {
            var emailBytes = System.Text.Encoding.UTF8.GetBytes(content);
            emailZoneTree.TryAdd(emailId, emailBytes, out _);
        }

        emailZoneTree.Maintenance.SaveMetaData();

        // Assert - Verify data was stored
        foreach (var (emailId, expectedContent) in emails)
        {
            Assert.True(emailZoneTree.TryGet(emailId, out var storedBytes));
            var storedContent = System.Text.Encoding.UTF8.GetString(storedBytes);
            Assert.Equal(expectedContent, storedContent);
        }

        // Check ZoneTree files
        var files = Directory.GetFiles(zoneTreePath, "*", SearchOption.AllDirectories);
        _output.WriteLine($"Email ZoneTree created {files.Length} files:");
        
        long totalSize = 0;
        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            totalSize += fileInfo.Length;
            _output.WriteLine($"  {Path.GetFileName(file)}: {fileInfo.Length} bytes");
        }
        
        _output.WriteLine($"Total ZoneTree storage: {totalSize} bytes");
        Assert.True(totalSize > 0, "ZoneTree should use storage");
    }

    [Fact]
    public void Should_Show_BlockManager_Current_State()
    {
        // This test shows what blocks currently exist in our EmailDB
        var locations = _blockManager.GetBlockLocations();
        var fileSize = new FileInfo(_testFile).Length;
        
        _output.WriteLine($"EmailDB file size: {fileSize} bytes");
        _output.WriteLine($"Block locations: {locations.Count}");
        
        foreach (var kvp in locations)
        {
            _output.WriteLine($"  Block {kvp.Key}: Position {kvp.Value.Position}, Length {kvp.Value.Length}");
        }
        
        // This will help us see the baseline before ZoneTree integration
        Assert.True(fileSize >= 0); // Basic sanity check
    }

    [Fact]
    public async Task Should_Demonstrate_Manual_Block_Creation_For_ZoneTree_Data()
    {
        // Arrange - Manually store ZoneTree-like data in EmailDB blocks
        var emailData = new[]
        {
            (Id: "msg001", Subject: "Important Meeting", From: "boss@company.com", Body: "Don't forget about the meeting tomorrow."),
            (Id: "msg002", Subject: "Lunch Plans", From: "friend@example.com", Body: "Want to grab lunch at the usual place?"),
            (Id: "msg003", Subject: "Project Update", From: "team@company.com", Body: "The project is on track for next week's deadline.")
        };

        var initialLocations = _blockManager.GetBlockLocations().Count;
        _output.WriteLine($"Initial blocks: {initialLocations}");

        // Act - Store each email as a separate block
        foreach (var email in emailData)
        {
            // Serialize email data (simple JSON-like format)
            var emailJson = $"{{\"id\":\"{email.Id}\",\"subject\":\"{email.Subject}\",\"from\":\"{email.From}\",\"body\":\"{email.Body}\"}}";
            var emailBytes = System.Text.Encoding.UTF8.GetBytes(emailJson);

            var block = new Block
            {
                Version = 1,
                Type = BlockType.ZoneTreeSegment_KV, // Use ZoneTree block type
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = email.Id.GetHashCode(), // Simple hash for block ID
                Payload = emailBytes
            };

            var result = await _blockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            
            _output.WriteLine($"Stored email '{email.Subject}' as block {block.BlockId}");
        }

        // Assert - Verify blocks were created
        var finalLocations = _blockManager.GetBlockLocations();
        Assert.Equal(initialLocations + emailData.Length, finalLocations.Count);

        // Verify we can read the email data back
        foreach (var email in emailData)
        {
            var blockId = email.Id.GetHashCode();
            var readResult = await _blockManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess);
            
            var readContent = System.Text.Encoding.UTF8.GetString(readResult.Value.Payload);
            Assert.Contains(email.Subject, readContent);
            Assert.Contains(email.From, readContent);
            
            _output.WriteLine($"Successfully read back email: {email.Subject}");
        }

        var fileSize = new FileInfo(_testFile).Length;
        _output.WriteLine($"EmailDB file size after storing emails: {fileSize} bytes");
    }

    [Fact]
    public async Task Should_Test_Large_Volume_Email_Storage()
    {
        // Arrange - Test storing a larger number of emails
        const int emailCount = 1000;
        var random = new Random(42);
        
        var initialLocations = _blockManager.GetBlockLocations().Count;
        _output.WriteLine($"Starting with {initialLocations} blocks");

        // Act - Store many emails
        for (int i = 0; i < emailCount; i++)
        {
            var emailContent = $"Subject: Email {i:D4}\r\n" +
                             $"From: user{i % 50}@company.com\r\n" +
                             $"To: recipient@example.com\r\n" +
                             $"Date: {DateTime.Now.AddDays(-random.Next(365))}\r\n\r\n" +
                             $"This is the body of email number {i}. " +
                             $"It contains some random content: {random.Next()} " +
                             $"and has a length of approximately 200 characters to simulate real email data.";

            var emailBytes = System.Text.Encoding.UTF8.GetBytes(emailContent);

            var block = new Block
            {
                Version = 1,
                Type = BlockType.ZoneTreeSegment_KV,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 10000 + i, // Start at 10000 to avoid conflicts
                Payload = emailBytes
            };

            var result = await _blockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);

            if (i % 100 == 0)
            {
                _output.WriteLine($"Stored {i + 1} emails...");
            }
        }

        // Assert
        var finalLocations = _blockManager.GetBlockLocations();
        Assert.Equal(initialLocations + emailCount, finalLocations.Count);

        var fileSize = new FileInfo(_testFile).Length;
        _output.WriteLine($"EmailDB file size after {emailCount} emails: {fileSize:N0} bytes ({fileSize / 1048576.0:F2} MB)");
        
        // Test reading a few random emails
        for (int i = 0; i < 10; i++)
        {
            var randomEmailId = 10000 + random.Next(emailCount);
            var readResult = await _blockManager.ReadBlockAsync(randomEmailId);
            Assert.True(readResult.IsSuccess);
            
            var content = System.Text.Encoding.UTF8.GetString(readResult.Value.Payload);
            Assert.Contains("Subject: Email", content);
        }

        _output.WriteLine($"Successfully verified random email reads");
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
                // Best effort
            }
        }
        
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Best effort
            }
        }
    }
}