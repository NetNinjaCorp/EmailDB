using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.ZoneTree;
using Tenray.ZoneTree;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Focused test to debug ZoneTree persistence issues
/// </summary>
public class ZoneTreePersistenceDebugTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;

    public ZoneTreePersistenceDebugTest(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"zonetree_debug_{Guid.NewGuid()}");
    }

    [Fact]
    public async Task TestBasicZoneTreePersistence()
    {
        _output.WriteLine("=== Basic ZoneTree Persistence Test ===\n");
        
        // Step 1: Create and populate ZoneTree
        _output.WriteLine("Step 1: Creating ZoneTree and adding data...");
        
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
            factory.CreateZoneTree("test");
            
            using (var tree = factory.OpenOrCreate())
            {
                // Add test data
                tree.Upsert("key1", "value1");
                tree.Upsert("key2", "value2");
                tree.Upsert("key3", "value3");
                
                // Verify data is accessible
                Assert.True(tree.TryGet("key1", out var val1));
                Assert.Equal("value1", val1);
                _output.WriteLine("✓ Added 3 key-value pairs");
                
                // Force maintenance to ensure data is persisted
                tree.Maintenance.MoveMutableSegmentForward();
                var mergeThread = tree.Maintenance.StartMergeOperation();
                mergeThread?.Join();
                tree.Maintenance.SaveMetaData();
                _output.WriteLine("✓ Forced maintenance operations");
            }
            
            _output.WriteLine("✓ Disposed ZoneTree");
        }
        
        _output.WriteLine($"\nDatabase file size: {new FileInfo(_testDbPath).Length} bytes");
        
        // Step 2: Reopen and verify
        _output.WriteLine("\nStep 2: Reopening ZoneTree to verify persistence...");
        
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            // List all blocks
            var blocks = blockManager.GetBlockLocations();
            _output.WriteLine($"Found {blocks.Count} blocks in storage:");
            foreach (var (blockId, location) in blocks)
            {
                _output.WriteLine($"  Block {blockId}: Position={location.Position}, Length={location.Length}");
            }
            
            var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
            factory.CreateZoneTree("test");
            
            using (var tree = factory.OpenOrCreate())
            {
                // Try to retrieve data
                var found1 = tree.TryGet("key1", out var val1);
                var found2 = tree.TryGet("key2", out var val2);
                var found3 = tree.TryGet("key3", out var val3);
                
                _output.WriteLine($"\nData retrieval results:");
                _output.WriteLine($"  key1: {(found1 ? $"Found = '{val1}'" : "NOT FOUND")}");
                _output.WriteLine($"  key2: {(found2 ? $"Found = '{val2}'" : "NOT FOUND")}");
                _output.WriteLine($"  key3: {(found3 ? $"Found = '{val3}'" : "NOT FOUND")}");
                
                if (!found1 || !found2 || !found3)
                {
                    _output.WriteLine("\n❌ Data was not persisted correctly!");
                    
                    // Try to understand what's in the tree
                    var iterator = tree.CreateIterator();
                    var count = 0;
                    while (iterator.Next())
                    {
                        count++;
                        _output.WriteLine($"  Found in tree: {iterator.CurrentKey} = {iterator.CurrentValue}");
                    }
                    _output.WriteLine($"  Total items in tree: {count}");
                }
                else
                {
                    _output.WriteLine("\n✅ All data persisted correctly!");
                }
            }
        }
    }

    [Fact]
    public async Task TestMetadataStorePersistence()
    {
        _output.WriteLine("=== Metadata Store Persistence Test ===\n");
        
        // Step 1: Create EmailDatabase and add data to metadata store
        _output.WriteLine("Step 1: Creating EmailDatabase and updating metadata...");
        
        using (var db = new EmailDatabase(_testDbPath))
        {
            // Access the internal metadata through reflection or debug method
            var debugValue = db.GetEmailIdsIndexDebug();
            _output.WriteLine($"Initial email_ids_index: {debugValue}");
            
            // Import an email to trigger metadata update
            var testEmail = @"From: test@example.com
To: recipient@example.com
Subject: Test Email
Date: Mon, 1 Jan 2024 12:00:00 +0000
Message-ID: <test123@example.com>

This is a test email.";
            
            var emailId = await db.ImportEMLAsync(testEmail);
            _output.WriteLine($"✓ Imported email with ID: {emailId}");
            
            // Check if metadata was updated
            debugValue = db.GetEmailIdsIndexDebug();
            _output.WriteLine($"After import email_ids_index: {debugValue}");
        }
        
        // Step 2: Reopen and check metadata
        _output.WriteLine("\nStep 2: Reopening database to check metadata persistence...");
        
        using (var db = new EmailDatabase(_testDbPath))
        {
            var debugValue = db.GetEmailIdsIndexDebug();
            _output.WriteLine($"After reopen email_ids_index: {debugValue}");
            
            var emailIds = await db.GetAllEmailIDsAsync();
            _output.WriteLine($"Found {emailIds.Count} email IDs");
            
            if (debugValue == "NOT_FOUND" || emailIds.Count == 0)
            {
                _output.WriteLine("\n❌ Metadata was not persisted!");
            }
            else
            {
                _output.WriteLine("\n✅ Metadata persisted correctly!");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch { }
    }
}