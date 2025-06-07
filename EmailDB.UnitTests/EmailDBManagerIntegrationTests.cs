using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

// Temporary EmailHashedID for testing
public struct EmailHashedID 
{
    public byte[] Hash { get; set; }
}

/// <summary>
/// Integration tests for EmailDB managers including:
/// - CacheManager
/// - MetadataManager
/// - FolderManager
/// - SegmentManager
/// - MaintenanceManager
/// </summary>
public class EmailDBManagerIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testFile;
    private readonly RawBlockManager _rawBlockManager;
    private readonly CacheManager _cacheManager;
    private readonly MetadataManager _metadataManager;
    private readonly FolderManager _folderManager;
    private readonly SegmentManager _segmentManager;
    // private readonly MaintenanceManager _maintenanceManager; // Commented out in the source
    private readonly ITestOutputHelper _output;

    public EmailDBManagerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testDirectory = Path.Combine(Path.GetTempPath(), $"EmailDBManagerTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _testFile = Path.Combine(_testDirectory, "test.emdb");
        
        _rawBlockManager = new RawBlockManager(_testFile);
        _cacheManager = new CacheManager(_rawBlockManager);
        _metadataManager = new MetadataManager(_cacheManager);
        _folderManager = new FolderManager(_cacheManager, _metadataManager);
        _segmentManager = new SegmentManager(_cacheManager, _metadataManager);
        // _maintenanceManager = new MaintenanceManager(_cacheManager, _metadataManager); // Not available
    }

    #region CacheManager Tests

    [Fact]
    public async Task CacheManager_Should_Cache_Frequently_Accessed_Blocks()
    {
        // Arrange
        var segment = new SegmentContent
        {
            SegmentId = 1001,
            SegmentData = Encoding.UTF8.GetBytes("Cached segment data"),
            ContentLength = 19,
            SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Version = 1
        };

        // Act - Write and read multiple times
        await _cacheManager.UpdateSegment("1001", segment);
        
        var read1 = await _cacheManager.GetSegmentAsync(1001);
        var read2 = await _cacheManager.GetSegmentAsync(1001);
        var read3 = await _cacheManager.GetSegmentAsync(1001);

        // Assert
        Assert.NotNull(read1);
        Assert.NotNull(read2);
        Assert.NotNull(read3);
        Assert.Equal("Cached segment data", Encoding.UTF8.GetString(read3.SegmentData));
        
        _output.WriteLine("CacheManager successfully cached frequently accessed segment");
    }

    [Fact]
    public async Task CacheManager_Should_Update_Metadata_Block()
    {
        // Arrange
        var metadata = new MetadataContent
        {
            Version = 1,
            CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LastModifiedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockCount = 10,
            FileSize = 1024,
            Properties = new Dictionary<string, string>
            {
                ["TestKey"] = "TestValue"
            }
        };

        // Act
        var result = await _cacheManager.UpdateMetadata(metadata);

        // Assert
        Assert.True(result > 0);
        
        var readMetadata = await _cacheManager.GetMetadataAsync();
        Assert.NotNull(readMetadata);
        Assert.Equal(10, readMetadata.BlockCount);
        Assert.Equal("TestValue", readMetadata.Properties["TestKey"]);
        
        _output.WriteLine($"Metadata block written at offset {result}");
    }

    [Fact]
    public async Task CacheManager_Should_Handle_Cache_Invalidation()
    {
        // Arrange
        var segment = new SegmentContent
        {
            SegmentId = 2001,
            SegmentData = Encoding.UTF8.GetBytes("Original data"),
            ContentLength = 13,
            SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Version = 1
        };

        await _cacheManager.UpdateSegment("2001", segment);
        
        // Act - Invalidate cache and update
        _cacheManager.InvalidateCache();
        
        segment.SegmentData = Encoding.UTF8.GetBytes("Updated data");
        segment.ContentLength = 12;
        segment.Version = 2;
        await _cacheManager.UpdateSegment("2001", segment);

        // Assert
        var updated = await _cacheManager.GetSegmentAsync(2001);
        Assert.NotNull(updated);
        Assert.Equal("Updated data", Encoding.UTF8.GetString(updated.SegmentData));
        Assert.Equal(2, updated.Version);
        
        _output.WriteLine("Cache invalidation and update successful");
    }

    #endregion

    #region MetadataManager Tests

    [Fact]
    public async Task MetadataManager_Should_Track_Segment_Offsets()
    {
        // Arrange & Act
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("seg1", 100);
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("seg2", 200);
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("seg3", 300);

        var allOffsets = await _metadataManager.GetAllSegmentOffsetsAsync();

        // Assert
        Assert.Equal(3, allOffsets.Count);
        Assert.Equal(100, allOffsets["seg1"]);
        Assert.Equal(200, allOffsets["seg2"]);
        Assert.Equal(300, allOffsets["seg3"]);
        
        _output.WriteLine($"MetadataManager tracking {allOffsets.Count} segment offsets");
    }

    [Fact]
    public async Task MetadataManager_Should_Track_Outdated_Segments()
    {
        // Arrange
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("old1", 100);
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("old2", 200);
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("current", 300);

        // Act
        await _metadataManager.MarkSegmentOutdatedAsync("old1");
        await _metadataManager.MarkSegmentOutdatedAsync("old2");

        var outdated = await _metadataManager.GetOutdatedSegmentOffsetsAsync();

        // Assert
        Assert.Contains(100L, outdated);
        Assert.Contains(200L, outdated);
        Assert.DoesNotContain(300L, outdated);
        
        _output.WriteLine($"Marked {outdated.Count} segments as outdated");
    }

    [Fact]
    public async Task MetadataManager_Should_Cleanup_Outdated_Segments()
    {
        // Arrange
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("cleanup1", 400);
        await _metadataManager.AddOrUpdateSegmentOffsetAsync("cleanup2", 500);
        await _metadataManager.MarkSegmentOutdatedAsync("cleanup1");
        await _metadataManager.MarkSegmentOutdatedAsync("cleanup2");

        // Act
        await _metadataManager.CleanupOutdatedSegmentsAsync();
        
        var outdated = await _metadataManager.GetOutdatedSegmentOffsetsAsync();
        var allOffsets = await _metadataManager.GetAllSegmentOffsetsAsync();

        // Assert
        Assert.Empty(outdated);
        Assert.DoesNotContain("cleanup1", allOffsets.Keys);
        Assert.DoesNotContain("cleanup2", allOffsets.Keys);
        
        _output.WriteLine("Cleanup of outdated segments completed");
    }

    #endregion

    #region FolderManager Tests

    [Fact]
    public async Task FolderManager_Should_Create_And_Manage_Folders()
    {
        // Arrange & Act
        var result1 = await _folderManager.CreateFolderAsync("Inbox");
        var result2 = await _folderManager.CreateFolderAsync("Sent");
        var result3 = await _folderManager.CreateFolderAsync("Drafts");

        var folders = await _folderManager.GetAllFoldersAsync();

        // Assert
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        Assert.True(result3.IsSuccess);
        Assert.Equal(3, folders.Count);
        Assert.Contains("Inbox", folders.Keys);
        Assert.Contains("Sent", folders.Keys);
        Assert.Contains("Drafts", folders.Keys);
        
        _output.WriteLine($"Created {folders.Count} folders successfully");
    }

    [Fact]
    public async Task FolderManager_Should_Add_Emails_To_Folders()
    {
        // Arrange
        await _folderManager.CreateFolderAsync("TestFolder");
        
        var emailIds = new List<EmailHashedID>();
        for (int i = 1; i <= 5; i++)
        {
            emailIds.Add(new EmailHashedID { Hash = BitConverter.GetBytes((long)(5000 + i)) });
        }

        // Act
        foreach (var emailId in emailIds)
        {
            var result = await _folderManager.AddEmailToFolderAsync("TestFolder", emailId);
            Assert.True(result.IsSuccess);
        }

        var folderEmails = await _folderManager.GetEmailsInFolderAsync("TestFolder");

        // Assert
        Assert.Equal(5, folderEmails.Count);
        
        _output.WriteLine($"Added {emailIds.Count} emails to folder");
    }

    [Fact]
    public async Task FolderManager_Should_Move_Emails_Between_Folders()
    {
        // Arrange
        await _folderManager.CreateFolderAsync("Source");
        await _folderManager.CreateFolderAsync("Destination");
        
        var emailId = new EmailHashedID { Hash = BitConverter.GetBytes(6001L) };
        await _folderManager.AddEmailToFolderAsync("Source", emailId);

        // Act
        var moveResult = await _folderManager.MoveEmailAsync(emailId, "Source", "Destination");

        // Assert
        Assert.True(moveResult.IsSuccess);
        
        var sourceEmails = await _folderManager.GetEmailsInFolderAsync("Source");
        var destEmails = await _folderManager.GetEmailsInFolderAsync("Destination");
        
        Assert.Empty(sourceEmails);
        Assert.Single(destEmails);
        Assert.Equal(emailId.Hash, destEmails[0].Hash);
        
        _output.WriteLine("Email moved between folders successfully");
    }

    [Fact]
    public async Task FolderManager_Should_Delete_Folder()
    {
        // Arrange
        await _folderManager.CreateFolderAsync("ToDelete");
        var emailId = new EmailHashedID { Hash = BitConverter.GetBytes(7001L) };
        await _folderManager.AddEmailToFolderAsync("ToDelete", emailId);

        // Act
        var deleteResult = await _folderManager.DeleteFolderAsync("ToDelete");

        // Assert
        Assert.True(deleteResult.IsSuccess);
        
        var folders = await _folderManager.GetAllFoldersAsync();
        Assert.DoesNotContain("ToDelete", folders.Keys);
        
        _output.WriteLine("Folder deleted successfully");
    }

    #endregion

    #region SegmentManager Tests

    [Fact]
    public async Task SegmentManager_Should_Create_And_Retrieve_Segments()
    {
        // Arrange
        var testData = Encoding.UTF8.GetBytes("This is segment test data");
        var metadata = new Dictionary<string, string>
        {
            ["ContentType"] = "text/plain",
            ["Encoding"] = "UTF-8"
        };

        // Act
        var segment = await _segmentManager.CreateSegmentAsync(testData, metadata);
        var retrieved = await _segmentManager.GetSegmentAsync(segment.SegmentId);

        // Assert
        Assert.NotNull(segment);
        Assert.NotNull(retrieved);
        Assert.Equal(testData.Length, retrieved.ContentLength);
        Assert.Equal("This is segment test data", Encoding.UTF8.GetString(retrieved.SegmentData));
        Assert.Equal("text/plain", retrieved.Metadata["ContentType"]);
        
        _output.WriteLine($"Created segment {segment.SegmentId} with {testData.Length} bytes");
    }

    [Fact]
    public async Task SegmentManager_Should_Update_Segment_Data()
    {
        // Arrange
        var originalData = Encoding.UTF8.GetBytes("Original segment data");
        var segment = await _segmentManager.CreateSegmentAsync(originalData);
        
        var updatedData = Encoding.UTF8.GetBytes("Updated segment data with more content");

        // Act
        var updated = await _segmentManager.UpdateSegmentAsync(
            segment.SegmentId, 
            updatedData,
            new Dictionary<string, string> { ["UpdatedBy"] = "Test" }
        );

        // Assert
        Assert.Equal(updatedData.Length, updated.ContentLength);
        Assert.Equal("Updated segment data with more content", Encoding.UTF8.GetString(updated.SegmentData));
        Assert.Equal(2, updated.Version); // Version should increment
        Assert.Equal("Test", updated.Metadata["UpdatedBy"]);
        
        _output.WriteLine($"Updated segment {segment.SegmentId} to version {updated.Version}");
    }

    [Fact]
    public async Task SegmentManager_Should_Delete_Segments()
    {
        // Arrange
        var segment = await _segmentManager.CreateSegmentAsync(
            Encoding.UTF8.GetBytes("To be deleted")
        );

        // Act
        var deleteResult = await _segmentManager.DeleteSegmentAsync(segment.SegmentId);
        var isOutdated = await _segmentManager.IsSegmentOutdatedAsync(segment.SegmentId);

        // Assert
        Assert.True(deleteResult.IsSuccess);
        Assert.True(isOutdated);
        
        _output.WriteLine($"Segment {segment.SegmentId} marked for deletion");
    }

    #endregion

    #region MaintenanceManager Tests - Commented out as MaintenanceManager is not implemented

    // MaintenanceManager tests would go here when the manager is implemented
    // Including:
    // - Cleanup operations
    // - Database integrity validation  
    // - Index rebuilding

    #endregion

    #region End-to-End Email Storage Scenario

    [Fact]
    public async Task Should_Handle_Complete_Email_Storage_Scenario()
    {
        _output.WriteLine("=== Starting Complete Email Storage Scenario ===");

        // Step 1: Create folder structure
        _output.WriteLine("\nStep 1: Creating folder structure...");
        await _folderManager.CreateFolderAsync("Inbox");
        await _folderManager.CreateFolderAsync("Sent");
        await _folderManager.CreateFolderAsync("Archive");

        // Step 2: Store emails
        _output.WriteLine("\nStep 2: Storing emails...");
        var emailIds = new List<EmailHashedID>();
        var segments = new List<SegmentContent>();

        for (int i = 1; i <= 15; i++)
        {
            // Create email content segment
            var emailContent = $@"From: sender{i}@example.com
To: recipient@example.com
Subject: Test Email {i}
Date: {DateTime.UtcNow:R}

This is the body of test email {i}.
It contains important information that needs to be stored securely.
";
            var segment = await _segmentManager.CreateSegmentAsync(
                Encoding.UTF8.GetBytes(emailContent),
                new Dictionary<string, string>
                {
                    ["Subject"] = $"Test Email {i}",
                    ["From"] = $"sender{i}@example.com",
                    ["Date"] = DateTime.UtcNow.ToString("O")
                }
            );
            segments.Add(segment);

            // Create email ID
            var emailId = new EmailHashedID { Hash = BitConverter.GetBytes(segment.SegmentId) };
            emailIds.Add(emailId);

            // Add to appropriate folder
            if (i <= 10)
            {
                await _folderManager.AddEmailToFolderAsync("Inbox", emailId);
            }
            else
            {
                await _folderManager.AddEmailToFolderAsync("Sent", emailId);
            }
        }

        _output.WriteLine($"Stored {emailIds.Count} emails");

        // Step 3: Move some emails to archive
        _output.WriteLine("\nStep 3: Archiving old emails...");
        var emailsToArchive = emailIds.Take(5).ToList();
        foreach (var emailId in emailsToArchive)
        {
            await _folderManager.MoveEmailAsync(emailId, "Inbox", "Archive");
        }

        // Step 4: Update metadata
        _output.WriteLine("\nStep 4: Updating database metadata...");
        await _metadataManager.UpdateMetadataAsync(new MetadataContent
        {
            Version = 1,
            CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LastModifiedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockCount = _rawBlockManager.GetAllBlockLocations().Count,
            FileSize = new FileInfo(_testFile).Length,
            Properties = new Dictionary<string, string>
            {
                ["TotalEmails"] = emailIds.Count.ToString(),
                ["InboxCount"] = "5",
                ["SentCount"] = "5",
                ["ArchiveCount"] = "5",
                ["LastUpdate"] = DateTime.UtcNow.ToString("O")
            }
        });

        // Step 5: Verify folder contents
        _output.WriteLine("\nStep 5: Verifying folder contents...");
        var inboxEmails = await _folderManager.GetEmailsInFolderAsync("Inbox");
        var sentEmails = await _folderManager.GetEmailsInFolderAsync("Sent");
        var archiveEmails = await _folderManager.GetEmailsInFolderAsync("Archive");

        Assert.Equal(5, inboxEmails.Count);
        Assert.Equal(5, sentEmails.Count);
        Assert.Equal(5, archiveEmails.Count);

        // Step 6: Delete some emails
        _output.WriteLine("\nStep 6: Deleting old archived emails...");
        var emailsToDelete = archiveEmails.Take(2).ToList();
        foreach (var emailId in emailsToDelete)
        {
            await _folderManager.RemoveEmailFromFolderAsync("Archive", emailId);
            
            // Mark corresponding segment as outdated
            var segmentId = BitConverter.ToInt64(emailId.Hash, 0);
            await _segmentManager.DeleteSegmentAsync(segmentId);
        }

        // Step 7: Perform maintenance
        _output.WriteLine("\nStep 7: Performing maintenance...");
        // await _maintenanceManager.PerformCleanupAsync();
        // await _maintenanceManager.RebuildIndexesAsync();
        _output.WriteLine("(MaintenanceManager operations skipped - not implemented)");

        // Step 8: Final verification
        _output.WriteLine("\nStep 8: Final verification...");
        var finalArchiveEmails = await _folderManager.GetEmailsInFolderAsync("Archive");
        Assert.Equal(3, finalArchiveEmails.Count);

        var finalMetadata = await _metadataManager.GetMetadataAsync();
        Assert.NotNull(finalMetadata);

        // var integrityCheck = await _maintenanceManager.ValidateDatabaseIntegrityAsync();
        // Assert.True(integrityCheck.IsSuccess);
        _output.WriteLine("(Integrity check skipped - MaintenanceManager not implemented)");

        _output.WriteLine($"\n=== Email Storage Scenario Complete ===");
        _output.WriteLine($"Final database size: {new FileInfo(_testFile).Length:N0} bytes");
        _output.WriteLine($"Total blocks: {_rawBlockManager.GetAllBlockLocations().Count}");
    }

    #endregion

    public void Dispose()
    {
        // _maintenanceManager?.Dispose();
        _segmentManager?.Dispose();
        _folderManager?.Dispose();
        _metadataManager?.Dispose();
        _cacheManager?.Dispose();
        _rawBlockManager?.Dispose();

        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}