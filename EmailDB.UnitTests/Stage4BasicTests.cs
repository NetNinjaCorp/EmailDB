using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Maintenance;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;
using EmailDB.Format.Caching;

namespace EmailDB.UnitTests;

/// <summary>
/// Basic tests for Stage 4 (Maintenance System) components
/// </summary>
[TestCategory("Stage4")]
public class Stage4BasicTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    
    public Stage4BasicTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"Stage4Basic_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _testDbPath = Path.Combine(_testDirectory, "test.emdb");
    }
    
    [Fact]
    public void TransactionLog_BasicOperations()
    {
        using var txLog = new TransactionLog(_testDbPath);
        
        // Log some basic operations
        txLog.LogOperation("TEST", "Test operation");
        txLog.LogBlockDeletion(123, BlockType.Folder, "Test reason");
        
        // Verify log file exists
        var logPath = _testDbPath + ".txlog";
        Assert.True(File.Exists(logPath));
        
        // Read and verify content
        var logContent = File.ReadAllText(logPath);
        Assert.Contains("STARTUP", logContent);
        Assert.Contains("TEST", logContent);
        Assert.Contains("DELETE_BLOCK", logContent);
    }
    
    [Fact]
    public void CompactionResult_Properties()
    {
        var result = new CompactionResult
        {
            StartTime = DateTime.UtcNow.AddMinutes(-5),
            EndTime = DateTime.UtcNow,
            OriginalSize = 1000,
            FinalSize = 800,
            SpaceReclaimed = 200,
            BlocksIdentified = 10,
            BlocksDeleted = 5
        };
        
        Assert.Equal(200, result.SpaceReclaimed);
        Assert.Equal(10, result.BlocksIdentified);
        Assert.Equal(5, result.BlocksDeleted);
        Assert.True(result.EndTime > result.StartTime);
    }
    
    [Fact]
    public void MaintenanceConfig_Defaults()
    {
        var config = new MaintenanceConfig();
        
        Assert.True(config.EnableBackgroundMaintenance);
        Assert.Equal(TimeSpan.FromHours(24), config.MaintenanceInterval);
        Assert.Equal(1024L * 1024 * 1024, config.CompactionThresholdBytes);
        Assert.Equal(24, config.MinAgeHoursForDeletion);
        Assert.Equal(5, config.KeyManagerVersionsToKeep);
        Assert.Equal(3, config.BackupsToKeep);
    }
    
    [Fact]
    public async Task SupersededBlockTracker_BasicOperation()
    {
        using var blockManager = new RawBlockManager(_testDbPath);
        var logger = new SimpleLogger();
        
        var tracker = new SupersededBlockTracker(blockManager, logger);
        
        // Empty database should return empty list
        var orphaned = await tracker.FindOrphanedBlocksAsync();
        Assert.NotNull(orphaned);
        Assert.Empty(orphaned);
    }
    
    [Fact]
    public async Task BlockReferenceValidator_Construction()
    {
        using var blockManager = new RawBlockManager(_testDbPath);
        var logger = new SimpleLogger();
        
        // Create minimal index manager
        var indexDir = Path.Combine(_testDirectory, "indexes");
        using var indexManager = new EmailDB.Format.Indexing.IndexManager(indexDir);
        
        var validator = new BlockReferenceValidator(indexManager, blockManager, logger);
        
        // Test basic operation - non-existent block should not be referenced
        var result = await validator.CheckFolderReferencesAsync(9999);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }
    
    [Fact]
    public void TransactionEntry_Structure()
    {
        var entry = new TransactionEntry
        {
            Timestamp = DateTime.UtcNow,
            Operation = "TEST_OP",
            Details = "Test details",
            Metadata = new Dictionary<string, object>
            {
                { "key1", "value1" },
                { "key2", 123 }
            }
        };
        
        Assert.Equal("TEST_OP", entry.Operation);
        Assert.Equal("Test details", entry.Details);
        Assert.Equal(2, entry.Metadata.Count);
    }
    
    [Fact]
    public void SupersededBlock_Structure()
    {
        var block = new SupersededBlock
        {
            BlockId = 123,
            BlockType = BlockType.Folder,
            SupersededAt = DateTime.UtcNow,
            Reason = "Test supersession"
        };
        
        Assert.Equal(123, block.BlockId);
        Assert.Equal(BlockType.Folder, block.BlockType);
        Assert.Equal("Test supersession", block.Reason);
    }
    
    [Fact]
    public async Task MaintenanceManager_ConstructionOnly()
    {
        // Test basic construction without background timer
        using var blockManager = new RawBlockManager(_testDbPath);
        var serializer = new DefaultBlockContentSerializer();
        var logger = new SimpleLogger();
        
        using var cacheManager = new CacheManager(blockManager, serializer);
        var metadataManager = new MetadataManager(cacheManager);
        var folderManager = new FolderManager(cacheManager, metadataManager, blockManager, serializer);
        
        var indexDir = Path.Combine(_testDirectory, "indexes");
        using var indexManager = new EmailDB.Format.Indexing.IndexManager(indexDir);
        
        var config = new MaintenanceConfig
        {
            EnableBackgroundMaintenance = false // Disable to avoid timer issues
        };
        
        using var maintenanceManager = new MaintenanceManager(
            blockManager, indexManager, folderManager, serializer, config, logger);
        
        // Just verify construction succeeded
        Assert.NotNull(maintenanceManager);
        
        // Test identify operation
        var identified = await maintenanceManager.IdentifySupersededBlocksAsync();
        Assert.NotNull(identified);
    }
    
    private class SimpleLogger : EmailDB.Format.FileManagement.ILogger
    {
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogDebug(string message) { }
    }
    
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}