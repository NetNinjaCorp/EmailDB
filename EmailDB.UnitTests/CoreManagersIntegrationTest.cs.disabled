using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Integration tests for core EmailDB managers working together.
/// Consolidated from NetNinja.Testing.BlockManager project.
/// </summary>
public class CoreManagersIntegrationTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testFile;

    public CoreManagersIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"integration_{Guid.NewGuid():N}.blk");
    }

    [Fact]
    public async Task Test_Core_Managers_Integration()
    {
        _output.WriteLine("Testing core managers integration...");
        
        // Initialize all managers
        var rawBlockManager = new RawBlockManager(_testFile);
        var cacheManager = new CacheManager(rawBlockManager, new DefaultBlockContentSerializer());
        var metadataManager = new MetadataManager(cacheManager);
        var folderManager = new FolderManager(cacheManager, metadataManager);
        var segmentManager = new SegmentManager(cacheManager, metadataManager);

        // Initialize new file
        await cacheManager.InitializeNewFile();
        _output.WriteLine("✓ File initialized successfully");

        // Create folders
        var inboxResult = await folderManager.CreateFolderAsync("Inbox");
        Assert.True(inboxResult.IsSuccess, $"Failed to create Inbox: {inboxResult.Error}");
        _output.WriteLine("✓ Created Inbox folder");

        var draftsResult = await folderManager.CreateFolderAsync("Drafts");
        Assert.True(draftsResult.IsSuccess, $"Failed to create Drafts: {draftsResult.Error}");
        _output.WriteLine("✓ Created Drafts folder");

        var sentResult = await folderManager.CreateFolderAsync("Sent");
        Assert.True(sentResult.IsSuccess, $"Failed to create Sent: {sentResult.Error}");
        _output.WriteLine("✓ Created Sent folder");

        // Write a segment
        var testContent = new byte[900];
        new Random(42).NextBytes(testContent);
        
        var segment = new SegmentContent()
        {
            FileName = "test_email.eml",
            SegmentData = testContent,
            IsDeleted = false,
            ContentLength = testContent.Length,
            SegmentId = 1,
            SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Version = 1
        };

        var segmentResult = await segmentManager.WriteSegmentAsync(segment);
        Assert.True(segmentResult.IsSuccess, $"Failed to write segment: {segmentResult.Error}");
        _output.WriteLine("✓ Written test segment");

        // Close and dispose
        rawBlockManager.Dispose();

        // Reopen and verify
        rawBlockManager = new RawBlockManager(_testFile, false);
        var scanResult = await rawBlockManager.ScanFile();
        _output.WriteLine($"✓ Scanned file, found {scanResult.Count} blocks");

        // Verify we have the expected blocks
        Assert.True(scanResult.Count >= 4); // At least: header, metadata, folder tree, segment

        // Reinitialize managers to verify data
        cacheManager = new CacheManager(rawBlockManager, new DefaultBlockContentSerializer());
        metadataManager = new MetadataManager(cacheManager);
        folderManager = new FolderManager(cacheManager, metadataManager);

        // Verify folders exist
        var folders = await folderManager.GetAllFoldersAsync();
        Assert.True(folders.IsSuccess);
        Assert.Contains("Inbox", folders.Value.Select(f => f.Name));
        Assert.Contains("Drafts", folders.Value.Select(f => f.Name));
        Assert.Contains("Sent", folders.Value.Select(f => f.Name));
        _output.WriteLine("✓ All folders verified");

        // Cleanup
        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task Test_Email_Storage_Workflow()
    {
        _output.WriteLine("Testing complete email storage workflow...");
        
        var rawBlockManager = new RawBlockManager(_testFile);
        var cacheManager = new CacheManager(rawBlockManager, new DefaultBlockContentSerializer());
        var metadataManager = new MetadataManager(cacheManager);
        var folderManager = new FolderManager(cacheManager, metadataManager);
        var segmentManager = new SegmentManager(cacheManager, metadataManager);

        // Initialize
        await cacheManager.InitializeNewFile();
        
        // Create folder structure
        await folderManager.CreateFolderAsync("Inbox");
        await folderManager.CreateFolderAsync("Important", "Inbox");
        _output.WriteLine("✓ Created folder hierarchy");

        // Store multiple emails
        for (int i = 0; i < 5; i++)
        {
            var emailContent = $"From: sender{i}@example.com\r\nTo: recipient@example.com\r\nSubject: Test Email {i}\r\n\r\nThis is test email number {i}.";
            var segment = new SegmentContent
            {
                FileName = $"email_{i}.eml",
                SegmentData = System.Text.Encoding.UTF8.GetBytes(emailContent),
                IsDeleted = false,
                ContentLength = emailContent.Length,
                SegmentId = i + 1,
                SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Version = 1
            };

            var result = await segmentManager.WriteSegmentAsync(segment);
            Assert.True(result.IsSuccess);
            
            // Add to inbox
            var addResult = await folderManager.AddEmailToFolderAsync("Inbox", new EmailHashedID { Value = (ulong)(i + 1) });
            Assert.True(addResult.IsSuccess);
        }
        _output.WriteLine("✓ Stored 5 emails in Inbox");

        // Move some emails to Important
        for (int i = 0; i < 2; i++)
        {
            var moveResult = await folderManager.MoveEmailAsync(
                new EmailHashedID { Value = (ulong)(i + 1) },
                "Inbox",
                "Important"
            );
            Assert.True(moveResult.IsSuccess);
        }
        _output.WriteLine("✓ Moved 2 emails to Important folder");

        // Verify folder contents
        var inboxFolder = await folderManager.GetFolderAsync("Inbox");
        Assert.True(inboxFolder.IsSuccess);
        Assert.Equal(3, inboxFolder.Value.EmailIds.Count);

        var importantFolder = await folderManager.GetFolderAsync("Important");
        Assert.True(importantFolder.IsSuccess);
        Assert.Equal(2, importantFolder.Value.EmailIds.Count);
        
        _output.WriteLine("✓ Verified folder contents");

        // Cleanup
        rawBlockManager.Dispose();
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