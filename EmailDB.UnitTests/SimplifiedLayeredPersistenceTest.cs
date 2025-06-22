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
using EmailDB.Format.ZoneTree;
using Tenray.ZoneTree;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Simplified layered persistence tests to verify each component independently
/// </summary>
public class SimplifiedLayeredPersistenceTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;

    public SimplifiedLayeredPersistenceTest(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"simple_layered_test_{Guid.NewGuid()}");
    }

    [Fact]
    public async Task Test1_RawBlockManager_Persistence()
    {
        _output.WriteLine("=== TEST 1: RawBlockManager Persistence ===\n");
        
        var testData = new Dictionary<int, string>
        {
            { 1001, "Test block 1 content" },
            { 1002, "Test block 2 content" },
            { 1003, "Test block 3 content" }
        };
        
        // WRITE PHASE
        _output.WriteLine("WRITE PHASE: Writing blocks...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            foreach (var (blockId, content) in testData)
            {
                var block = new Block
                {
                    BlockId = blockId,
                    Type = BlockType.EmailBatch,
                    Payload = Encoding.UTF8.GetBytes(content),
                    Version = 1,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks
                };
                
                var result = await blockManager.WriteBlockAsync(block);
                _output.WriteLine($"  Block {blockId}: Write {(result.IsSuccess ? "SUCCESS" : "FAILED")}");
                Assert.True(result.IsSuccess);
            }
        }
        
        // READ PHASE
        _output.WriteLine("\nREAD PHASE: Reopening and reading blocks...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var successCount = 0;
            foreach (var (blockId, expectedContent) in testData)
            {
                var result = await blockManager.ReadBlockAsync(blockId);
                if (result.IsSuccess)
                {
                    var actualContent = Encoding.UTF8.GetString(result.Value.Payload);
                    Assert.Equal(expectedContent, actualContent);
                    _output.WriteLine($"  Block {blockId}: READ SUCCESS - Content matches");
                    successCount++;
                }
                else
                {
                    _output.WriteLine($"  Block {blockId}: READ FAILED - {result.Error}");
                }
            }
            
            _output.WriteLine($"\nRESULT: {successCount}/{testData.Count} blocks persisted successfully");
            Assert.Equal(testData.Count, successCount);
        }
    }

    [Fact]
    public async Task Test2_ZoneTreeMetadata_Persistence()
    {
        _output.WriteLine("=== TEST 2: ZoneTree Metadata File Persistence ===\n");
        
        // WRITE PHASE
        _output.WriteLine("WRITE PHASE: Creating ZoneTree metadata files...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
            factory.CreateZoneTree("test");
            
            using (var tree = factory.OpenOrCreate())
            {
                // Just create the tree, which should create metadata files
                _output.WriteLine("  ZoneTree created");
                
                // Force metadata save
                tree.Maintenance.SaveMetaData();
                _output.WriteLine("  Metadata saved");
            }
        }
        
        // CHECK PHASE
        _output.WriteLine("\nCHECK PHASE: Checking what blocks were created...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var locations = blockManager.GetBlockLocations();
            _output.WriteLine($"  Total blocks created: {locations.Count}");
            
            foreach (var (blockId, location) in locations)
            {
                _output.WriteLine($"    Block {blockId}: Position={location.Position}, Length={location.Length}");
            }
            
            Assert.True(locations.Count > 0, "No blocks were created by ZoneTree");
        }
        
        // REOPEN PHASE
        _output.WriteLine("\nREOPEN PHASE: Reopening ZoneTree...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
            factory.CreateZoneTree("test");
            
            using (var tree = factory.OpenOrCreate())
            {
                _output.WriteLine("  ZoneTree reopened successfully");
            }
        }
    }

    [Fact]
    public async Task Test3_ZoneTreeData_Persistence()
    {
        _output.WriteLine("=== TEST 3: ZoneTree Data Persistence ===\n");
        
        var testData = new Dictionary<string, string>
        {
            { "key1", "value1" },
            { "key2", "value2" },
            { "key3", "value3" }
        };
        
        // WRITE PHASE
        _output.WriteLine("WRITE PHASE: Storing data in ZoneTree...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
            factory.CreateZoneTree("datatest");
            
            using (var tree = factory.OpenOrCreate())
            {
                foreach (var (key, value) in testData)
                {
                    tree.Upsert(key, value);
                    _output.WriteLine($"  Stored: {key} = {value}");
                }
                
                // Force data to disk
                _output.WriteLine("\n  Forcing data to disk:");
                tree.Maintenance.MoveMutableSegmentForward();
                _output.WriteLine("    - Moved mutable segment");
                
                var mergeThread = tree.Maintenance.StartMergeOperation();
                mergeThread?.Join();
                _output.WriteLine("    - Completed merge");
                
                tree.Maintenance.SaveMetaData();
                _output.WriteLine("    - Saved metadata");
            }
        }
        
        // CHECK BLOCKS
        _output.WriteLine("\nBLOCK CHECK: Examining created blocks...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var locations = blockManager.GetBlockLocations();
            _output.WriteLine($"  Total blocks: {locations.Count}");
            
            var blockTypes = new Dictionary<BlockType, int>();
            foreach (var (blockId, _) in locations)
            {
                var result = await blockManager.ReadBlockAsync(blockId);
                if (result.IsSuccess)
                {
                    var type = result.Value.Type;
                    blockTypes[type] = blockTypes.ContainsKey(type) ? blockTypes[type] + 1 : 1;
                }
            }
            
            foreach (var (type, count) in blockTypes)
            {
                _output.WriteLine($"    {type}: {count} blocks");
            }
        }
        
        // READ PHASE
        _output.WriteLine("\nREAD PHASE: Reopening and reading data...");
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
            factory.CreateZoneTree("datatest");
            
            using (var tree = factory.OpenOrCreate())
            {
                var successCount = 0;
                foreach (var (key, expectedValue) in testData)
                {
                    if (tree.TryGet(key, out var actualValue))
                    {
                        Assert.Equal(expectedValue, actualValue);
                        _output.WriteLine($"  {key}: READ SUCCESS - Value matches");
                        successCount++;
                    }
                    else
                    {
                        _output.WriteLine($"  {key}: READ FAILED - Not found");
                    }
                }
                
                _output.WriteLine($"\nRESULT: {successCount}/{testData.Count} entries persisted successfully");
                
                if (successCount == 0)
                {
                    _output.WriteLine("\n⚠️ DEBUGGING: Checking what's in the tree...");
                    var iterator = tree.CreateIterator();
                    var count = 0;
                    while (iterator.Next())
                    {
                        _output.WriteLine($"    Found: {iterator.CurrentKey} = {iterator.CurrentValue}");
                        count++;
                    }
                    _output.WriteLine($"    Total entries in tree: {count}");
                }
            }
        }
    }

    [Fact]
    public async Task Test4_EmailDatabase_Metadata_Persistence()
    {
        _output.WriteLine("=== TEST 4: EmailDatabase Metadata Store Persistence ===\n");
        
        // WRITE PHASE
        _output.WriteLine("WRITE PHASE: Using EmailDatabase metadata store directly...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            // Access the internal metadata store through importing an email
            var testEmail = @"From: test@example.com
To: recipient@example.com
Subject: Metadata Test Email
Date: Mon, 1 Jan 2024 12:00:00 +0000
Message-ID: <metadata-test@example.com>

This email tests metadata persistence.";
            
            var emailId = await db.ImportEMLAsync(testEmail);
            _output.WriteLine($"  Imported email: {emailId}");
            
            // Check if metadata was updated
            var debugValue = db.GetEmailIdsIndexDebug();
            _output.WriteLine($"  email_ids_index: {debugValue}");
            Assert.NotEqual("NOT_FOUND", debugValue);
        }
        
        // READ PHASE
        _output.WriteLine("\nREAD PHASE: Reopening to check metadata persistence...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            var debugValue = db.GetEmailIdsIndexDebug();
            _output.WriteLine($"  email_ids_index after reopen: {debugValue}");
            
            var emailIds = await db.GetAllEmailIDsAsync();
            _output.WriteLine($"  Email count: {emailIds.Count}");
            
            if (debugValue == "NOT_FOUND")
            {
                _output.WriteLine("\n❌ METADATA NOT PERSISTED");
            }
            else
            {
                _output.WriteLine("\n✅ METADATA PERSISTED SUCCESSFULLY");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDbPath))
            {
                Directory.Delete(_testDbPath, true);
            }
        }
        catch { }
    }
}