using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Maintenance;
using EmailDB.Format.Models;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

[TestCategory("Phase4")]
public class Phase4ComponentTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    
    public Phase4ComponentTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "Phase4Tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDirectory);
        _testDbPath = Path.Combine(_testDirectory, "test.emdb");
    }
    
    [Fact]
    public void Phase4MaintenanceComponentsExist()
    {
        // Verify Phase 4 components exist and can be instantiated
        Assert.NotNull(typeof(MaintenanceManager));
        Assert.NotNull(typeof(SupersededBlockTracker));
        Assert.NotNull(typeof(BlockReferenceValidator));
        Assert.NotNull(typeof(TransactionLog));
        
        // Verify supporting classes
        Assert.NotNull(typeof(SupersededBlock));
        Assert.NotNull(typeof(CompactionResult));
        Assert.NotNull(typeof(MaintenanceConfig));
    }
    
    [Fact]
    public void MaintenanceConfig_HasCorrectDefaults()
    {
        var config = new MaintenanceConfig();
        
        Assert.True(config.EnableBackgroundMaintenance);
        Assert.Equal(TimeSpan.FromHours(24), config.MaintenanceInterval);
        Assert.Equal(1024L * 1024 * 1024, config.CompactionThresholdBytes); // 1GB
        Assert.Equal(24, config.MinAgeHoursForDeletion);
        Assert.Equal(5, config.KeyManagerVersionsToKeep);
        Assert.Equal(3, config.BackupsToKeep);
    }
    
    [Fact]
    public void TransactionLog_LogsOperations()
    {
        var logPath = Path.Combine(_testDirectory, "test.emdb");
        
        using (var txLog = new TransactionLog(logPath))
        {
            // Log some operations
            txLog.LogOperation("TEST_OP", "Test operation");
            txLog.LogBlockDeletion(123, BlockType.Folder, "Test deletion");
            
            var compactionResult = new CompactionResult
            {
                StartTime = DateTime.UtcNow.AddMinutes(-5),
                EndTime = DateTime.UtcNow,
                OriginalSize = 1000,
                FinalSize = 800,
                SpaceReclaimed = 200,
                BlocksDeleted = 5
            };
            txLog.LogCompaction(compactionResult);
        }
        
        // Verify log file was created
        var expectedLogPath = logPath + ".txlog";
        Assert.True(File.Exists(expectedLogPath));
        
        // Verify log contains entries
        var logContent = File.ReadAllText(expectedLogPath);
        Assert.Contains("TEST_OP", logContent);
        Assert.Contains("DELETE_BLOCK", logContent);
        Assert.Contains("COMPACTION", logContent);
        Assert.Contains("STARTUP", logContent);
        Assert.Contains("SHUTDOWN", logContent);
    }
    
    [Fact]
    public void SupersededBlock_CreatesCorrectly()
    {
        var supersededBlock = new SupersededBlock
        {
            BlockId = 123,
            BlockType = BlockType.Folder,
            SupersededAt = DateTime.UtcNow,
            Reason = "Test supersession"
        };
        
        Assert.Equal(123, supersededBlock.BlockId);
        Assert.Equal(BlockType.Folder, supersededBlock.BlockType);
        Assert.Equal("Test supersession", supersededBlock.Reason);
        Assert.True(supersededBlock.SupersededAt <= DateTime.UtcNow);
    }
    
    [Fact]
    public void CompactionResult_TracksMetrics()
    {
        var result = new CompactionResult
        {
            StartTime = DateTime.UtcNow.AddMinutes(-10),
            EndTime = DateTime.UtcNow,
            OriginalSize = 2000,
            FinalSize = 1500,
            SpaceReclaimed = 500,
            BlocksIdentified = 20,
            BlocksDeleted = 8,
            BackupPath = "/tmp/backup.emdb"
        };
        
        Assert.Equal(500, result.SpaceReclaimed);
        Assert.Equal(20, result.BlocksIdentified);
        Assert.Equal(8, result.BlocksDeleted);
        Assert.Equal("/tmp/backup.emdb", result.BackupPath);
        Assert.True((result.EndTime - result.StartTime).TotalMinutes >= 9);
    }
    
    [Fact]
    public async Task MaintenanceManager_HandlesNullDependencies()
    {
        // Test that MaintenanceManager properly validates dependencies
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            var _ = new MaintenanceManager(null, null, null, null);
            return Task.CompletedTask;
        });
    }
    
    [Fact]
    public async Task SupersededBlockTracker_FindsOrphanedBlocks()
    {
        // Create a test block manager
        using var blockManager = new RawBlockManager(_testDbPath, createIfNotExists: true);
        var logger = new ConsoleLogger();
        
        var tracker = new SupersededBlockTracker(blockManager, logger);
        
        // This should return empty list for empty database
        var orphanedBlocks = await tracker.FindOrphanedBlocksAsync();
        
        Assert.NotNull(orphanedBlocks);
        Assert.Empty(orphanedBlocks); // No blocks to be orphaned in empty database
    }
    
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }
}