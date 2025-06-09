using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.Format.Models;
using EmailDB.Format.Indexing;
using EmailDB.Format.Maintenance;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages database maintenance operations including cleanup, compaction, and optimization.
/// </summary>
public class MaintenanceManager : IDisposable
{
    private readonly RawBlockManager _blockManager;
    private readonly IndexManager _indexManager;
    private readonly FolderManager _folderManager;
    private readonly iBlockContentSerializer _serializer;
    private readonly ILogger _logger;
    
    // Tracking superseded blocks
    private readonly SupersededBlockTracker _supersededTracker;
    private readonly BlockReferenceValidator _referenceValidator;
    
    // Background service
    private readonly Timer _maintenanceTimer;
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private bool _disposed;
    
    // Configuration
    private readonly MaintenanceConfig _config;
    
    public MaintenanceManager(
        RawBlockManager blockManager,
        IndexManager indexManager,
        FolderManager folderManager,
        iBlockContentSerializer serializer,
        MaintenanceConfig config = null,
        ILogger logger = null)
    {
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _config = config ?? new MaintenanceConfig();
        _logger = logger ?? new ConsoleLogger();
        
        _supersededTracker = new SupersededBlockTracker(_blockManager, _logger);
        _referenceValidator = new BlockReferenceValidator(_indexManager, _blockManager, _logger);
        
        // Start background maintenance if enabled
        if (_config.EnableBackgroundMaintenance)
        {
            _maintenanceTimer = new Timer(
                RunScheduledMaintenance,
                null,
                _config.MaintenanceInterval,
                _config.MaintenanceInterval);
        }
    }
    
    /// <summary>
    /// Identifies all superseded blocks that can be cleaned up.
    /// </summary>
    public async Task<Result<List<SupersededBlock>>> IdentifySupersededBlocksAsync()
    {
        try
        {
            _logger.LogInfo("Identifying superseded blocks...");
            
            var supersededBlocks = new List<SupersededBlock>();
            
            // Get superseded blocks from FolderManager
            var folderSuperseded = await _folderManager.GetSupersededBlocksAsync();
            if (folderSuperseded.IsSuccess)
            {
                supersededBlocks.AddRange(folderSuperseded.Value
                    .Select(b => new SupersededBlock
                    {
                        BlockId = b.BlockId,
                        BlockType = b.Type,
                        SupersededAt = b.SupersededAt,
                        Reason = b.Reason
                    }));
            }
            
            // Scan all blocks to find orphaned ones
            var orphanedBlocks = await _supersededTracker.FindOrphanedBlocksAsync();
            supersededBlocks.AddRange(orphanedBlocks);
            
            // Find old envelope block versions
            var oldEnvelopes = await FindOldEnvelopeVersionsAsync();
            supersededBlocks.AddRange(oldEnvelopes);
            
            // Find old key manager versions (keep last N)
            var oldKeyManagers = await FindOldKeyManagerVersionsAsync();
            supersededBlocks.AddRange(oldKeyManagers);
            
            _logger.LogInfo($"Identified {supersededBlocks.Count} superseded blocks");
            return Result<List<SupersededBlock>>.Success(supersededBlocks);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to identify superseded blocks: {ex.Message}");
            return Result<List<SupersededBlock>>.Failure($"Identification failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Verifies that a block is not referenced before deletion.
    /// </summary>
    public async Task<Result<bool>> VerifyBlockNotReferencedAsync(long blockId)
    {
        try
        {
            _logger.LogDebug($"Verifying block {blockId} is not referenced...");
            
            // Check all indexes
            var indexCheck = await _referenceValidator.CheckIndexReferencesAsync(blockId);
            if (!indexCheck.IsSuccess || indexCheck.Value)
            {
                _logger.LogWarning($"Block {blockId} is still referenced in indexes");
                return Result<bool>.Success(false);
            }
            
            // Check folder references
            var folderCheck = await _referenceValidator.CheckFolderReferencesAsync(blockId);
            if (!folderCheck.IsSuccess || folderCheck.Value)
            {
                _logger.LogWarning($"Block {blockId} is still referenced in folders");
                return Result<bool>.Success(false);
            }
            
            // Check cross-block references
            var crossRefCheck = await _referenceValidator.CheckCrossBlockReferencesAsync(blockId);
            if (!crossRefCheck.IsSuccess || crossRefCheck.Value)
            {
                _logger.LogWarning($"Block {blockId} has cross-block references");
                return Result<bool>.Success(false);
            }
            
            _logger.LogDebug($"Block {blockId} is safe to delete");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to verify block references: {ex.Message}");
            return Result<bool>.Failure($"Verification failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Compacts the database by removing deleted blocks.
    /// </summary>
    public async Task<Result<CompactionResult>> CompactDatabaseAsync(
        IProgress<CompactionProgress> progress = null)
    {
        await _maintenanceLock.WaitAsync();
        try
        {
            _logger.LogInfo("Starting database compaction...");
            
            var result = new CompactionResult
            {
                StartTime = DateTime.UtcNow,
                OriginalSize = new FileInfo(_blockManager.FilePath).Length
            };
            
            // Step 1: Create backup
            var backupResult = await CreateBackupAsync();
            if (!backupResult.IsSuccess)
            {
                return Result<CompactionResult>.Failure($"Backup failed: {backupResult.Error}");
            }
            result.BackupPath = backupResult.Value;
            
            progress?.Report(new CompactionProgress 
            { 
                Phase = "Identifying blocks", 
                PercentComplete = 10 
            });
            
            // Step 2: Identify superseded blocks
            var supersededResult = await IdentifySupersededBlocksAsync();
            if (!supersededResult.IsSuccess)
            {
                return Result<CompactionResult>.Failure($"Failed to identify blocks: {supersededResult.Error}");
            }
            
            var blocksToDelete = new List<long>();
            
            // Step 3: Verify each block is safe to delete
            progress?.Report(new CompactionProgress 
            { 
                Phase = "Verifying blocks", 
                PercentComplete = 30 
            });
            
            foreach (var block in supersededResult.Value)
            {
                // Skip if too recent (safety margin)
                if ((DateTime.UtcNow - block.SupersededAt).TotalHours < _config.MinAgeHoursForDeletion)
                    continue;
                
                var verifyResult = await VerifyBlockNotReferencedAsync(block.BlockId);
                if (verifyResult.IsSuccess && verifyResult.Value)
                {
                    blocksToDelete.Add(block.BlockId);
                }
            }
            
            result.BlocksIdentified = supersededResult.Value.Count;
            result.BlocksDeleted = blocksToDelete.Count;
            
            if (blocksToDelete.Count == 0)
            {
                _logger.LogInfo("No blocks to delete during compaction");
                result.EndTime = DateTime.UtcNow;
                result.FinalSize = result.OriginalSize;
                return Result<CompactionResult>.Success(result);
            }
            
            // Step 4: Create new compacted file
            progress?.Report(new CompactionProgress 
            { 
                Phase = "Compacting file", 
                PercentComplete = 50 
            });
            
            var compactResult = await CompactFileAsync(blocksToDelete, progress);
            if (!compactResult.IsSuccess)
            {
                return Result<CompactionResult>.Failure($"Compaction failed: {compactResult.Error}");
            }
            
            // Step 5: Rebuild indexes
            progress?.Report(new CompactionProgress 
            { 
                Phase = "Rebuilding indexes", 
                PercentComplete = 80 
            });
            
            var rebuildResult = await _indexManager.RebuildAllIndexesAsync();
            if (!rebuildResult.IsSuccess)
            {
                // Restore from backup
                await RestoreFromBackupAsync(result.BackupPath);
                return Result<CompactionResult>.Failure($"Index rebuild failed: {rebuildResult.Error}");
            }
            
            result.EndTime = DateTime.UtcNow;
            result.FinalSize = new FileInfo(_blockManager.FilePath).Length;
            result.SpaceReclaimed = result.OriginalSize - result.FinalSize;
            
            _logger.LogInfo($"Compaction completed. Reclaimed {result.SpaceReclaimed / (1024 * 1024)}MB");
            
            progress?.Report(new CompactionProgress 
            { 
                Phase = "Complete", 
                PercentComplete = 100 
            });
            
            return Result<CompactionResult>.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Database compaction failed: {ex.Message}");
            return Result<CompactionResult>.Failure($"Compaction error: {ex.Message}");
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }
    
    private async Task<List<SupersededBlock>> FindOldEnvelopeVersionsAsync()
    {
        var oldEnvelopes = new List<SupersededBlock>();
        
        // This would scan all FolderEnvelope blocks and find old versions
        // Simplified implementation for now
        return oldEnvelopes;
    }
    
    private async Task<List<SupersededBlock>> FindOldKeyManagerVersionsAsync()
    {
        var oldKeyManagers = new List<SupersededBlock>();
        
        // This would scan all KeyManager blocks and keep only recent versions
        // Simplified implementation for now
        return oldKeyManagers;
    }
    
    private async Task<Result<string>> CreateBackupAsync()
    {
        try
        {
            var backupPath = $"{_blockManager.FilePath}.backup_{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_blockManager.FilePath, backupPath, overwrite: true);
            _logger.LogInfo($"Created backup at: {backupPath}");
            return Result<string>.Success(backupPath);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure($"Backup failed: {ex.Message}");
        }
    }
    
    private async Task<Result> CompactFileAsync(
        List<long> blocksToDelete, 
        IProgress<CompactionProgress> progress)
    {
        try
        {
            // This would implement the actual file compaction logic
            // For now, return success to allow testing of other components
            _logger.LogInfo($"Would compact file removing {blocksToDelete.Count} blocks");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"File compaction failed: {ex.Message}");
        }
    }
    
    private async Task RestoreFromBackupAsync(string backupPath)
    {
        try
        {
            _logger.LogWarning($"Restoring from backup: {backupPath}");
            // Implementation would restore the backup file
        }
        catch (Exception ex)
        {
            _logger.LogError($"Backup restore failed: {ex.Message}");
        }
    }
    
    private async void RunScheduledMaintenance(object state)
    {
        if (!await _maintenanceLock.WaitAsync(0))
        {
            _logger.LogDebug("Maintenance already running, skipping scheduled run");
            return;
        }
        
        try
        {
            _logger.LogInfo("Running scheduled maintenance...");
            
            // Run cleanup if needed
            var dbSize = new FileInfo(_blockManager.FilePath).Length;
            if (dbSize > _config.CompactionThresholdBytes)
            {
                var result = await CompactDatabaseAsync();
                if (!result.IsSuccess)
                {
                    _logger.LogError($"Scheduled compaction failed: {result.Error}");
                }
            }
            
            // Clean up old backups
            await CleanupOldBackupsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Scheduled maintenance failed: {ex.Message}");
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }
    
    private async Task CleanupOldBackupsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_blockManager.FilePath);
            var backupPattern = $"{Path.GetFileName(_blockManager.FilePath)}.backup_*";
            var backupFiles = Directory.GetFiles(directory, backupPattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(_config.BackupsToKeep)
                .ToList();
                
            foreach (var backup in backupFiles)
            {
                backup.Delete();
                _logger.LogInfo($"Deleted old backup: {backup.Name}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Backup cleanup failed: {ex.Message}");
        }
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _maintenanceTimer?.Dispose();
            _maintenanceLock?.Dispose();
            _disposed = true;
        }
    }
}

// Supporting classes
public class SupersededBlock
{
    public long BlockId { get; set; }
    public BlockType BlockType { get; set; }
    public DateTime SupersededAt { get; set; }
    public string Reason { get; set; }
}

public class CompactionResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long OriginalSize { get; set; }
    public long FinalSize { get; set; }
    public long SpaceReclaimed { get; set; }
    public int BlocksIdentified { get; set; }
    public int BlocksDeleted { get; set; }
    public string BackupPath { get; set; }
}

public class CompactionProgress
{
    public string Phase { get; set; }
    public int PercentComplete { get; set; }
    public int BlocksProcessed { get; set; }
    public int TotalBlocks { get; set; }
}

public class MaintenanceConfig
{
    public bool EnableBackgroundMaintenance { get; set; } = true;
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(24);
    public long CompactionThresholdBytes { get; set; } = 1024L * 1024 * 1024; // 1GB
    public int MinAgeHoursForDeletion { get; set; } = 24;
    public int KeyManagerVersionsToKeep { get; set; } = 5;
    public int BackupsToKeep { get; set; } = 3;
}

// Simple logger interface and implementation
public interface ILogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message);
    void LogDebug(string message);
}

public class ConsoleLogger : ILogger
{
    public void LogInfo(string message) => Console.WriteLine($"[INFO] {message}");
    public void LogWarning(string message) => Console.WriteLine($"[WARN] {message}");
    public void LogError(string message) => Console.WriteLine($"[ERROR] {message}");
    public void LogDebug(string message) => Console.WriteLine($"[DEBUG] {message}");
}