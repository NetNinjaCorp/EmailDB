using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Basic End-to-End test for EmailDatabase functionality with file size tracking.
/// Shows file size changes during email operations.
/// </summary>
public class EmailDatabaseBasicE2ETest : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public EmailDatabaseBasicE2ETest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"EmailDB_Basic_{Guid.NewGuid():N}.emdb");
    }

    [Fact]
    public async Task Should_Demonstrate_Email_Operations_With_File_Size_Tracking()
    {
        _output.WriteLine("🎯 EMAILDB BASIC E2E TEST");
        _output.WriteLine("========================");
        _output.WriteLine($"📁 Test file: {_testFile}");

        // Show initial file size
        await ShowFileSize("Initial state", 0);

        // Create EmailDB using RawBlockManager to demonstrate the underlying storage
        using var blockManager = new RawBlockManager(_testFile);
        
        // Create some test emails directly using RawBlockManager to show size impact
        _output.WriteLine("\n📧 STEP 1: Adding Emails to EmailDB Storage");
        _output.WriteLine("===========================================");

        var emailIds = new List<string>();
        for (int i = 1; i <= 5; i++)
        {
            // Create test email content
            var emailContent = CreateTestEmailContent(i);
            var emailJson = System.Text.Json.JsonSerializer.Serialize(emailContent);
            var emailBytes = System.Text.Encoding.UTF8.GetBytes(emailJson);

            // Store email as a block
            var emailBlock = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1000 + i,
                Payload = emailBytes
            };

            var result = await blockManager.WriteBlockAsync(emailBlock);
            if (result.IsSuccess)
            {
                emailIds.Add($"email_{i}");
                var email = CreateTestEmailContent(i);
                _output.WriteLine($"   ✅ Added email {i}: {email.GetType().GetProperty("Subject")?.GetValue(email)}");
            }
        }

        await ShowFileSize($"After adding {emailIds.Count} emails", 1);

        // Create search index entries
        _output.WriteLine("\n🔍 STEP 2: Creating Search Index Entries");
        _output.WriteLine("========================================");

        for (int i = 1; i <= emailIds.Count; i++)
        {
            var searchEntry = new { emailId = $"email_{i}", searchableText = $"subject content body email test data {i}" };
            var searchJson = System.Text.Json.JsonSerializer.Serialize(searchEntry);
            var searchBytes = System.Text.Encoding.UTF8.GetBytes(searchJson);

            var searchBlock = new Block
            {
                Version = 1,
                Type = BlockType.ZoneTreeSegment_Vector,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 2000 + i,
                Payload = searchBytes
            };

            var result = await blockManager.WriteBlockAsync(searchBlock);
            if (result.IsSuccess)
            {
                _output.WriteLine($"   🔍 Indexed email {i} for search");
            }
        }

        await ShowFileSize("After creating search indexes", 2);

        // Create folder/metadata entries
        _output.WriteLine("\n📁 STEP 3: Creating Folder Organization");
        _output.WriteLine("======================================");

        var folders = new[] { "Inbox", "Important", "Projects", "Archive" };
        for (int i = 0; i < emailIds.Count; i++)
        {
            var folderName = folders[i % folders.Length];
            var folderEntry = new { emailId = emailIds[i], folder = folderName };
            var folderJson = System.Text.Json.JsonSerializer.Serialize(folderEntry);
            var folderBytes = System.Text.Encoding.UTF8.GetBytes(folderJson);

            var folderBlock = new Block
            {
                Version = 1,
                Type = BlockType.Folder,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 3000 + i,
                Payload = folderBytes
            };

            var result = await blockManager.WriteBlockAsync(folderBlock);
            if (result.IsSuccess)
            {
                _output.WriteLine($"   📁 Organized email {i + 1} into '{folderName}' folder");
            }
        }

        await ShowFileSize("After folder organization", 3);

        // Add metadata blocks
        _output.WriteLine("\n📊 STEP 4: Adding Metadata");
        _output.WriteLine("==========================");

        var metadata = new { 
            version = "1.0", 
            emailCount = emailIds.Count, 
            created = DateTime.UtcNow,
            format = "EmailDB"
        };
        var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
        var metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadataJson);

        var metadataBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 4000,
            Payload = metadataBytes
        };

        var metadataResult = await blockManager.WriteBlockAsync(metadataBlock);
        if (metadataResult.IsSuccess)
        {
            _output.WriteLine("   📊 Added database metadata");
        }

        await ShowFileSize("After adding metadata", 4);

        // Simulate "deleting" emails by marking them as deleted
        _output.WriteLine("\n🗑️ STEP 5: Marking Emails as Deleted");
        _output.WriteLine("====================================");

        for (int i = 3; i <= 5; i++) // Delete last 3 emails
        {
            var deleteEntry = new { emailId = $"email_{i}", deleted = true, deletedAt = DateTime.UtcNow };
            var deleteJson = System.Text.Json.JsonSerializer.Serialize(deleteEntry);
            var deleteBytes = System.Text.Encoding.UTF8.GetBytes(deleteJson);

            var deleteBlock = new Block
            {
                Version = 1,
                Type = BlockType.Cleanup,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 5000 + i,
                Payload = deleteBytes
            };

            var result = await blockManager.WriteBlockAsync(deleteBlock);
            if (result.IsSuccess)
            {
                _output.WriteLine($"   🗑️ Marked email {i} as deleted");
            }
        }

        await ShowFileSize("After marking emails as deleted", 5);

        // Add more emails
        _output.WriteLine("\n📧 STEP 6: Adding More Emails");
        _output.WriteLine("=============================");

        for (int i = 6; i <= 10; i++)
        {
            var emailContent = CreateTestEmailContent(i);
            var emailJson = System.Text.Json.JsonSerializer.Serialize(emailContent);
            var emailBytes = System.Text.Encoding.UTF8.GetBytes(emailJson);

            var emailBlock = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 6000 + i,
                Payload = emailBytes
            };

            var result = await blockManager.WriteBlockAsync(emailBlock);
            if (result.IsSuccess)
            {
                var email = CreateTestEmailContent(i);
                _output.WriteLine($"   ✅ Added additional email {i}: {email.GetType().GetProperty("Subject")?.GetValue(email)}");
            }
        }

        await ShowFileSize("After adding more emails", 6);

        // Show final statistics
        _output.WriteLine("\n📊 FINAL STATISTICS");
        _output.WriteLine("===================");

        var locations = blockManager.GetBlockLocations();
        _output.WriteLine($"   📊 Total blocks: {locations.Count}");
        
        // Read some blocks to show their types (simplified approach for demo)
        var sampleBlockTypes = new List<BlockType>();
        foreach (var (blockId, location) in locations.Take(5))
        {
            var blockResult = await blockManager.ReadBlockAsync(blockId);
            if (blockResult.IsSuccess && blockResult.Value != null)
            {
                sampleBlockTypes.Add(blockResult.Value.Type);
            }
        }
        
        var typeGroups = sampleBlockTypes.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        foreach (var kvp in typeGroups)
        {
            _output.WriteLine($"   📦 {kvp.Key}: {kvp.Value} blocks (sample)");
        }

        await ShowFileSize("Final state", 7);

        // Summary
        _output.WriteLine("\n🎉 E2E TEST SUMMARY");
        _output.WriteLine("==================");
        _output.WriteLine("   ✅ Demonstrated EmailDB block storage");
        _output.WriteLine("   📧 Added multiple emails as blocks");
        _output.WriteLine("   🔍 Created search index blocks");
        _output.WriteLine("   📁 Organized emails into folders");
        _output.WriteLine("   📊 Added metadata blocks");
        _output.WriteLine("   🗑️ Marked emails as deleted");
        _output.WriteLine("   📈 Tracked file size at each step");
        _output.WriteLine("   💾 All data stored in custom EmailDB format");

        Assert.True(locations.Count > 10, "Should have created multiple blocks");
        Assert.True(sampleBlockTypes.Count > 0, "Should have block types");
    }

    private async Task ShowFileSize(string stepDescription, int stepNumber)
    {
        var fileInfo = new FileInfo(_testFile);
        var sizeBytes = fileInfo.Exists ? fileInfo.Length : 0;
        var sizeKB = (double)sizeBytes / 1024;
        var sizeMB = sizeKB / 1024;

        string sizeDisplay = sizeMB >= 1 
            ? $"{sizeMB:F2} MB" 
            : sizeKB >= 1 
                ? $"{sizeKB:F1} KB" 
                : $"{sizeBytes} bytes";

        _output.WriteLine($"📊 Step {stepNumber} - {stepDescription}:");
        _output.WriteLine($"   💾 EMDB file size: {sizeDisplay}");
    }

    private object CreateTestEmailContent(int emailNumber)
    {
        var subjects = new[]
        {
            "Project Status Update",
            "Weekly Team Meeting",
            "Budget Review Required",
            "Client Feedback Summary",
            "System Maintenance Window",
            "Performance Report Q4",
            "Training Session Reminder",
            "Policy Update Notification",
            "Emergency Contact Update",
            "Year-end Planning Session"
        };

        var senders = new[]
        {
            "john.smith@company.com",
            "sarah.wilson@company.com",
            "mike.johnson@company.com",
            "lisa.brown@company.com",
            "david.davis@company.com",
            "anna.taylor@company.com",
            "chris.martin@company.com",
            "emma.white@company.com",
            "james.lee@company.com",
            "sophia.clark@company.com"
        };

        return new
        {
            Subject = $"{subjects[(emailNumber - 1) % subjects.Length]} (Email {emailNumber})",
            From = senders[(emailNumber - 1) % senders.Length],
            To = "team@company.com",
            Date = DateTime.UtcNow.AddDays(-emailNumber),
            Body = $"This is the body content for email {emailNumber}. It contains important information about our business operations and various project updates. The email demonstrates how content is stored in the EmailDB format with proper indexing and organization capabilities.",
            Size = 1024 + (emailNumber * 100), // Simulate varying email sizes
            MessageId = $"<email{emailNumber}@company.com>"
        };
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
                // Best effort cleanup
            }
        }
    }
}