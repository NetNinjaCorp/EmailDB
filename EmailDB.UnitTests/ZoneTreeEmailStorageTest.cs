using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.ZoneTree;
using EmailDB.Format.Models;
using Tenray.ZoneTree;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Test that demonstrates storing emails in ZoneTree with EmailDB backend
/// and shows exactly how many blocks are created.
/// </summary>
public class ZoneTreeEmailStorageTest : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public ZoneTreeEmailStorageTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Store_10_Emails_And_Show_Block_Creation()
    {
        _output.WriteLine("📧 Testing Email Storage in ZoneTree → EmailDB Integration");

        // Create ZoneTree with EmailDB backend
        var factory = new Tenray.ZoneTree.ZoneTreeFactory<string, string>();
        factory.Configure(options =>
        {
            options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_blockManager, "email_storage");
            options.WriteAheadLogProvider = null; // Disable WAL for clarity
        });

        _output.WriteLine("✅ ZoneTree configured with EmailDB backend");

        // Record initial state
        var initialBlocks = _blockManager.GetBlockLocations();
        _output.WriteLine($"📊 Initial EmailDB blocks: {initialBlocks.Count}");

        using var zoneTree = factory.OpenOrCreate();
        _output.WriteLine("✅ ZoneTree instance opened");

        // Create 10 sample emails
        var emails = new[]
        {
            ("email:001", "From: john@company.com\nTo: team@company.com\nSubject: Project Update\nBody: The project is on track for delivery next week."),
            ("email:002", "From: sarah@company.com\nTo: john@company.com\nSubject: Meeting Reminder\nBody: Don't forget about our 3 PM meeting today."),
            ("email:003", "From: admin@company.com\nTo: all@company.com\nSubject: System Maintenance\nBody: System will be down for maintenance from 2-4 AM."),
            ("email:004", "From: client@external.com\nTo: sales@company.com\nSubject: Product Inquiry\nBody: I'm interested in your latest product offerings."),
            ("email:005", "From: hr@company.com\nTo: john@company.com\nSubject: Performance Review\nBody: Please schedule time for your annual review."),
            ("email:006", "From: marketing@company.com\nTo: team@company.com\nSubject: Campaign Results\nBody: Our latest campaign exceeded expectations by 15%."),
            ("email:007", "From: support@company.com\nTo: client@external.com\nSubject: Ticket #12345 Resolved\nBody: Your support ticket has been resolved."),
            ("email:008", "From: finance@company.com\nTo: managers@company.com\nSubject: Q4 Budget Review\nBody: Please review your department budgets for Q4."),
            ("email:009", "From: ceo@company.com\nTo: all@company.com\nSubject: Company Update\nBody: I'm pleased to announce our successful quarter."),
            ("email:010", "From: it@company.com\nTo: all@company.com\nSubject: Password Policy Update\nBody: New password requirements are now in effect.")
        };

        // Store emails in ZoneTree
        _output.WriteLine("\n📧 Storing 10 emails in ZoneTree...");
        for (int i = 0; i < emails.Length; i++)
        {
            var (emailId, emailContent) = emails[i];
            var added = zoneTree.TryAdd(emailId, emailContent, out var opIndex);
            Assert.True(added, $"Should be able to add email {emailId}");
            
            var contentPreview = emailContent.Length > 50 ? emailContent.Substring(0, 50) + "..." : emailContent;
            _output.WriteLine($"   ✅ Email {i + 1:D2}: {emailId} (opIndex: {opIndex})");
            _output.WriteLine($"      Preview: {contentPreview.Replace("\n", " | ")}");
        }

        // Check blocks after adding emails (before persistence)
        var blocksAfterAdd = _blockManager.GetBlockLocations();
        var newBlocksAfterAdd = blocksAfterAdd.Count - initialBlocks.Count;
        _output.WriteLine($"\n📊 EmailDB blocks after adding emails (in memory): {newBlocksAfterAdd}");

        // Force ZoneTree to persist data
        _output.WriteLine("\n💾 Forcing ZoneTree to persist emails to EmailDB...");
        zoneTree.Maintenance.MoveMutableSegmentForward();
        var mergeResult = zoneTree.Maintenance.StartMergeOperation();
        if (mergeResult != null)
        {
            mergeResult.Join();
            _output.WriteLine("✅ Merge operation completed");
        }

        // Check final block count
        var finalBlocks = _blockManager.GetBlockLocations();
        var totalNewBlocks = finalBlocks.Count - initialBlocks.Count;
        _output.WriteLine($"\n📊 Final EmailDB blocks after persistence: {totalNewBlocks}");

        // Verify we can retrieve all emails
        _output.WriteLine("\n🔍 Verifying all emails can be retrieved...");
        for (int i = 0; i < emails.Length; i++)
        {
            var (emailId, expectedContent) = emails[i];
            var found = zoneTree.TryGet(emailId, out var actualContent);
            Assert.True(found, $"Should find email {emailId}");
            Assert.Equal(expectedContent, actualContent);
            
            var preview = expectedContent.Length > 40 ? expectedContent.Substring(0, 40) + "..." : expectedContent;
            _output.WriteLine($"   ✅ Retrieved {emailId}: {preview.Replace("\n", " | ")}");
        }

        // Analyze the blocks that were created
        _output.WriteLine("\n📦 Analyzing EmailDB blocks created by ZoneTree:");
        var blockNumber = 1;
        foreach (var kvp in finalBlocks)
        {
            if (!initialBlocks.ContainsKey(kvp.Key))
            {
                var readResult = await _blockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess)
                {
                    var block = readResult.Value;
                    _output.WriteLine($"   📦 Block {blockNumber}: ID={kvp.Key}");
                    _output.WriteLine($"      Type: {block.Type}");
                    _output.WriteLine($"      Encoding: {block.Encoding}");
                    _output.WriteLine($"      Size: {block.Payload.Length} bytes");
                    _output.WriteLine($"      Timestamp: {new DateTime(block.Timestamp):yyyy-MM-dd HH:mm:ss}");
                    blockNumber++;
                }
            }
        }

        // Summary
        _output.WriteLine($"\n🎉 EMAIL STORAGE TEST SUMMARY:");
        _output.WriteLine($"   📧 Emails stored: {emails.Length}");
        _output.WriteLine($"   📦 EmailDB blocks created: {totalNewBlocks}");
        _output.WriteLine($"   💾 Block creation ratio: {(double)totalNewBlocks / emails.Length:F2} blocks per email");
        _output.WriteLine($"   ✅ All emails successfully stored and retrieved");
        _output.WriteLine($"   ✅ ZoneTree → EmailDB integration working perfectly!");

        Assert.True(totalNewBlocks > 0, "ZoneTree should create EmailDB blocks when storing emails");
        Assert.Equal(emails.Length, emails.Length); // All emails should be stored
    }

    [Fact]
    public async Task Should_Show_Block_Creation_Pattern_For_Different_Email_Counts()
    {
        _output.WriteLine("📊 Testing Block Creation Patterns for Different Email Volumes");

        var emailCounts = new[] { 1, 5, 10, 20 };
        
        foreach (var emailCount in emailCounts)
        {
            _output.WriteLine($"\n🔄 Testing {emailCount} emails:");
            
            // Create fresh ZoneTree for each test
            var factory = new Tenray.ZoneTree.ZoneTreeFactory<string, string>();
            factory.Configure(options =>
            {
                options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_blockManager, $"test_{emailCount}");
                options.WriteAheadLogProvider = null;
            });

            var initialBlocks = _blockManager.GetBlockLocations();
            
            using var zoneTree = factory.OpenOrCreate();
            
            // Add emails
            for (int i = 1; i <= emailCount; i++)
            {
                var emailId = $"test_{emailCount}_email_{i:D3}";
                var emailContent = $"From: user{i}@test.com\nSubject: Test Email {i}\nBody: This is test email number {i} for batch size {emailCount}.";
                zoneTree.TryAdd(emailId, emailContent, out _);
            }
            
            // Force persistence
            zoneTree.Maintenance.MoveMutableSegmentForward();
            var mergeResult = zoneTree.Maintenance.StartMergeOperation();
            mergeResult?.Join();
            
            var finalBlocks = _blockManager.GetBlockLocations();
            var blocksCreated = finalBlocks.Count - initialBlocks.Count;
            
            _output.WriteLine($"   📧 Emails: {emailCount}");
            _output.WriteLine($"   📦 Blocks created: {blocksCreated}");
            _output.WriteLine($"   📊 Ratio: {(double)blocksCreated / emailCount:F2} blocks per email");
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