# Phase 4 Implementation Plan: Maintenance and Cleanup

## Overview
Phase 4 implements the maintenance and cleanup infrastructure needed to keep the EmailDB efficient over time. This includes removing superseded blocks, compacting the database, managing versions, and ensuring data integrity through safe cleanup operations.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                   Maintenance Architecture                       │
├─────────────────────────────────────────────────────────────────┤
│ MaintenanceManager                                               │
│ ├── SupersededBlockTracker (tracks obsolete blocks)             │
│ ├── BlockReferenceValidator (ensures safe deletion)             │
│ ├── DatabaseCompactor (removes deleted blocks)                  │
│ └── MaintenanceScheduler (background operations)                │
│                                                                  │
│ VersionManager                                                   │
│ ├── BlockVersionTracker (tracks all block versions)             │
│ ├── VersionConflictResolver (handles conflicts)                 │
│ └── RetentionPolicyManager (manages old versions)               │
│                                                                  │
│ Safety Infrastructure                                            │
│ ├── TransactionLog (audit trail of changes)                     │
│ ├── BackupManager (pre-compaction backups)                      │
│ └── RecoveryManager (rollback capabilities)                     │
└─────────────────────────────────────────────────────────────────┘
```

## Section 4.1: MaintenanceManager Implementation

### Task 4.1.1: Create Core MaintenanceManager
**File**: `EmailDB.Format/FileManagement/MaintenanceManager.cs`
**Dependencies**: RawBlockManager, IndexManager, FolderManager
**Description**: Central coordinator for all maintenance operations

```csharp
namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages database maintenance operations including cleanup, compaction, and optimization.
/// </summary>
public class MaintenanceManager : IDisposable
{
    private readonly RawBlockManager _blockManager;
    private readonly IndexManager _indexManager;
    private readonly FolderManager _folderManager;
    private readonly IBlockContentSerializer _serializer;
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
        IBlockContentSerializer serializer,
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
    
    /// <summary>
    /// Rebuilds all indexes from blocks.
    /// </summary>
    public async Task<Result> RebuildIndexesAsync(IProgress<RebuildProgress> progress = null)
    {
        try
        {
            _logger.LogInfo("Starting index rebuild...");
            
            var rebuilder = new IndexRebuilder(
                _blockManager,
                _indexManager,
                _serializer,
                _logger);
                
            return await rebuilder.RebuildAllIndexesAsync(progress);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Index rebuild failed: {ex.Message}");
            return Result.Failure($"Rebuild failed: {ex.Message}");
        }
    }
    
    private async Task<List<SupersededBlock>> FindOldEnvelopeVersionsAsync()
    {
        var oldEnvelopes = new List<SupersededBlock>();
        var blockLocations = _blockManager.GetBlockLocations();
        
        // Group envelope blocks by folder
        var envelopesByFolder = new Dictionary<string, List<(long blockId, FolderEnvelopeBlock block)>>();
        
        foreach (var (offset, location) in blockLocations)
        {
            if (location.Type != BlockType.FolderEnvelope)
                continue;
                
            var blockResult = await _blockManager.ReadBlockAsync(offset);
            if (!blockResult.IsSuccess)
                continue;
                
            var deserializeResult = _serializer.Deserialize<FolderEnvelopeBlock>(
                blockResult.Value.Payload,
                blockResult.Value.PayloadEncoding);
                
            if (deserializeResult.IsSuccess)
            {
                var envelope = deserializeResult.Value;
                if (!envelopesByFolder.ContainsKey(envelope.FolderPath))
                    envelopesByFolder[envelope.FolderPath] = new();
                    
                envelopesByFolder[envelope.FolderPath].Add((location.Id, envelope));
            }
        }
        
        // Find old versions
        foreach (var (folder, envelopes) in envelopesByFolder)
        {
            if (envelopes.Count <= 1)
                continue;
                
            // Sort by version descending
            var sorted = envelopes.OrderByDescending(e => e.block.Version).ToList();
            
            // Keep the latest, mark others as superseded
            for (int i = 1; i < sorted.Count; i++)
            {
                oldEnvelopes.Add(new SupersededBlock
                {
                    BlockId = sorted[i].blockId,
                    BlockType = BlockType.FolderEnvelope,
                    SupersededAt = sorted[i].block.LastModified,
                    Reason = "Newer envelope version exists"
                });
            }
        }
        
        return oldEnvelopes;
    }
    
    private async Task<List<SupersededBlock>> FindOldKeyManagerVersionsAsync()
    {
        var oldKeyManagers = new List<SupersededBlock>();
        var keyManagerBlocks = new List<(long blockId, KeyManagerContent block, DateTime created)>();
        
        var blockLocations = _blockManager.GetBlockLocations();
        
        foreach (var (offset, location) in blockLocations)
        {
            if (location.Type != BlockType.KeyManager)
                continue;
                
            var blockResult = await _blockManager.ReadBlockAsync(offset);
            if (!blockResult.IsSuccess)
                continue;
                
            // Key manager blocks are encrypted, so we track by metadata
            keyManagerBlocks.Add((location.Id, null, location.Timestamp));
        }
        
        // Keep only the last N versions
        var sorted = keyManagerBlocks.OrderByDescending(k => k.created).ToList();
        var keepCount = _config.KeyManagerVersionsToKeep;
        
        for (int i = keepCount; i < sorted.Count; i++)
        {
            oldKeyManagers.Add(new SupersededBlock
            {
                BlockId = sorted[i].blockId,
                BlockType = BlockType.KeyManager,
                SupersededAt = sorted[i].created,
                Reason = $"Exceeded retention limit of {keepCount} versions"
            });
        }
        
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
            var tempPath = $"{_blockManager.FilePath}.compact";
            var blockLocations = _blockManager.GetBlockLocations();
            
            using (var sourceStream = new FileStream(_blockManager.FilePath, FileMode.Open, FileAccess.Read))
            using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                var newOffset = 0L;
                var processedBlocks = 0;
                var totalBlocks = blockLocations.Count;
                
                foreach (var (offset, location) in blockLocations.OrderBy(b => b.Key))
                {
                    // Skip deleted blocks
                    if (blocksToDelete.Contains(location.Id))
                    {
                        _logger.LogDebug($"Skipping deleted block {location.Id}");
                        processedBlocks++;
                        continue;
                    }
                    
                    // Read block
                    var blockResult = await _blockManager.ReadBlockAsync(offset);
                    if (!blockResult.IsSuccess)
                    {
                        _logger.LogWarning($"Failed to read block at offset {offset}");
                        processedBlocks++;
                        continue;
                    }
                    
                    // Write to new file
                    var blockData = SerializeBlock(blockResult.Value);
                    await destStream.WriteAsync(blockData, 0, blockData.Length);
                    
                    newOffset += blockData.Length;
                    processedBlocks++;
                    
                    // Report progress
                    var percentComplete = 50 + (processedBlocks * 30 / totalBlocks);
                    progress?.Report(new CompactionProgress
                    {
                        Phase = "Compacting file",
                        PercentComplete = percentComplete,
                        BlocksProcessed = processedBlocks,
                        TotalBlocks = totalBlocks
                    });
                }
            }
            
            // Replace original file
            _blockManager.Dispose();
            File.Delete(_blockManager.FilePath);
            File.Move(tempPath, _blockManager.FilePath);
            
            // Reinitialize block manager
            await _blockManager.InitializeAsync();
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"File compaction failed: {ex.Message}");
        }
    }
    
    private byte[] SerializeBlock(Block block)
    {
        // This would use the existing block serialization logic
        // For now, simplified version
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Write header
        writer.Write(block.MagicNumber);
        writer.Write(block.Version);
        writer.Write((int)block.Type);
        writer.Write((byte)block.PayloadEncoding);
        writer.Write(block.Id);
        writer.Write(block.Timestamp);
        writer.Write(block.Flags);
        writer.Write(block.HeaderChecksum);
        
        // Write payload
        writer.Write(block.PayloadLength);
        writer.Write(block.Payload);
        writer.Write(block.PayloadChecksum);
        
        // Write footer
        writer.Write(block.FooterMagic);
        writer.Write(ms.Length);
        
        return ms.ToArray();
    }
    
    private async Task RestoreFromBackupAsync(string backupPath)
    {
        try
        {
            _logger.LogWarning($"Restoring from backup: {backupPath}");
            _blockManager.Dispose();
            File.Delete(_blockManager.FilePath);
            File.Move(backupPath, _blockManager.FilePath);
            await _blockManager.InitializeAsync();
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
```

### Task 4.1.2: Create Superseded Block Tracker
**File**: `EmailDB.Format/Maintenance/SupersededBlockTracker.cs`
**Dependencies**: RawBlockManager
**Description**: Tracks and identifies superseded blocks

```csharp
namespace EmailDB.Format.Maintenance;

/// <summary>
/// Tracks superseded blocks across the database.
/// </summary>
public class SupersededBlockTracker
{
    private readonly RawBlockManager _blockManager;
    private readonly ILogger _logger;
    
    public SupersededBlockTracker(RawBlockManager blockManager, ILogger logger)
    {
        _blockManager = blockManager;
        _logger = logger;
    }
    
    /// <summary>
    /// Finds orphaned blocks that are not referenced anywhere.
    /// </summary>
    public async Task<List<SupersededBlock>> FindOrphanedBlocksAsync()
    {
        var orphanedBlocks = new List<SupersededBlock>();
        var blockLocations = _blockManager.GetBlockLocations();
        var referencedBlocks = new HashSet<long>();
        
        // Build set of all referenced blocks
        foreach (var (offset, location) in blockLocations)
        {
            var blockResult = await _blockManager.ReadBlockAsync(offset);
            if (!blockResult.IsSuccess)
                continue;
                
            var block = blockResult.Value;
            
            // Extract references based on block type
            switch (block.Type)
            {
                case BlockType.Folder:
                    // Folder references envelope block
                    var folderRefs = ExtractFolderReferences(block);
                    referencedBlocks.UnionWith(folderRefs);
                    break;
                    
                case BlockType.FolderEnvelope:
                    // Envelope may reference previous version
                    var envelopeRefs = ExtractEnvelopeReferences(block);
                    referencedBlocks.UnionWith(envelopeRefs);
                    break;
                    
                case BlockType.KeyManager:
                    // Key manager references previous version
                    var keyRefs = ExtractKeyManagerReferences(block);
                    referencedBlocks.UnionWith(keyRefs);
                    break;
            }
        }
        
        // Find blocks that are not referenced
        foreach (var (offset, location) in blockLocations)
        {
            if (!referencedBlocks.Contains(location.Id) && 
                IsOrphanableType(location.Type))
            {
                orphanedBlocks.Add(new SupersededBlock
                {
                    BlockId = location.Id,
                    BlockType = location.Type,
                    SupersededAt = DateTimeOffset.FromUnixTimeSeconds(location.Timestamp).UtcDateTime,
                    Reason = "Orphaned block - no references found"
                });
            }
        }
        
        _logger.LogInfo($"Found {orphanedBlocks.Count} orphaned blocks");
        return orphanedBlocks;
    }
    
    private HashSet<long> ExtractFolderReferences(Block block)
    {
        // This would deserialize the folder and extract EnvelopeBlockId
        // Simplified for example
        return new HashSet<long>();
    }
    
    private HashSet<long> ExtractEnvelopeReferences(Block block)
    {
        // Extract PreviousBlockId from envelope
        return new HashSet<long>();
    }
    
    private HashSet<long> ExtractKeyManagerReferences(Block block)
    {
        // Extract PreviousKeyManagerBlockId
        return new HashSet<long>();
    }
    
    private bool IsOrphanableType(BlockType type)
    {
        // Some block types should never be considered orphaned
        return type != BlockType.Header && 
               type != BlockType.Metadata &&
               type != BlockType.EmailBatch; // Email batches are referenced by indexes
    }
}
```

### Task 4.1.3: Create Block Reference Validator
**File**: `EmailDB.Format/Maintenance/BlockReferenceValidator.cs`
**Dependencies**: IndexManager, RawBlockManager
**Description**: Validates that blocks are safe to delete

```csharp
namespace EmailDB.Format.Maintenance;

/// <summary>
/// Validates that blocks are not referenced before deletion.
/// </summary>
public class BlockReferenceValidator
{
    private readonly IndexManager _indexManager;
    private readonly RawBlockManager _blockManager;
    private readonly ILogger _logger;
    
    public BlockReferenceValidator(
        IndexManager indexManager,
        RawBlockManager blockManager,
        ILogger logger)
    {
        _indexManager = indexManager;
        _blockManager = blockManager;
        _logger = logger;
    }
    
    /// <summary>
    /// Checks if a block is referenced in any index.
    /// </summary>
    public async Task<Result<bool>> CheckIndexReferencesAsync(long blockId)
    {
        try
        {
            // Check if any email in this block is indexed
            var emailLocations = await _indexManager.GetAllEmailLocationsAsync();
            foreach (var location in emailLocations)
            {
                if (location.BlockId == blockId)
                {
                    _logger.LogDebug($"Block {blockId} referenced by email at local ID {location.LocalId}");
                    return Result<bool>.Success(true);
                }
            }
            
            // Check folder indexes
            var folderLocations = await _indexManager.GetAllFolderLocationsAsync();
            if (folderLocations.Contains(blockId))
            {
                _logger.LogDebug($"Block {blockId} referenced as folder block");
                return Result<bool>.Success(true);
            }
            
            // Check envelope locations
            var envelopeLocations = await _indexManager.GetAllEnvelopeLocationsAsync();
            if (envelopeLocations.Contains(blockId))
            {
                _logger.LogDebug($"Block {blockId} referenced as envelope block");
                return Result<bool>.Success(true);
            }
            
            return Result<bool>.Success(false);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to check index references: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Checks if a block is referenced by any folder.
    /// </summary>
    public async Task<Result<bool>> CheckFolderReferencesAsync(long blockId)
    {
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            
            foreach (var (offset, location) in blockLocations)
            {
                if (location.Type != BlockType.Folder)
                    continue;
                    
                var blockResult = await _blockManager.ReadBlockAsync(offset);
                if (!blockResult.IsSuccess)
                    continue;
                    
                // Check if this folder references the block
                // This would deserialize and check EnvelopeBlockId
                // Simplified for example
            }
            
            return Result<bool>.Success(false);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to check folder references: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Checks for cross-block references.
    /// </summary>
    public async Task<Result<bool>> CheckCrossBlockReferencesAsync(long blockId)
    {
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            
            // Check if any block has a previous block reference to this one
            foreach (var (offset, location) in blockLocations)
            {
                if (location.Type == BlockType.FolderEnvelope ||
                    location.Type == BlockType.KeyManager)
                {
                    var blockResult = await _blockManager.ReadBlockAsync(offset);
                    if (!blockResult.IsSuccess)
                        continue;
                        
                    // Check PreviousBlockId references
                    // Simplified for example
                }
            }
            
            return Result<bool>.Success(false);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to check cross-block references: {ex.Message}");
        }
    }
}
```

## Section 4.2: Version Management

### Task 4.2.1: Create Version Manager
**File**: `EmailDB.Format/Versioning/VersionManager.cs`
**Dependencies**: Block types
**Description**: Manages version tracking and conflict resolution

```csharp
namespace EmailDB.Format.Versioning;

/// <summary>
/// Manages version tracking for all mutable blocks.
/// </summary>
public class VersionManager
{
    private readonly Dictionary<string, BlockVersionInfo> _versionTracking = new();
    private readonly ILogger _logger;
    private readonly VersionConfig _config;
    
    public VersionManager(VersionConfig config = null, ILogger logger = null)
    {
        _config = config ?? new VersionConfig();
        _logger = logger ?? new ConsoleLogger();
    }
    
    /// <summary>
    /// Tracks a new version of a block.
    /// </summary>
    public Result TrackVersion(string resourceId, long blockId, int version, BlockType type)
    {
        try
        {
            if (!_versionTracking.TryGetValue(resourceId, out var versionInfo))
            {
                versionInfo = new BlockVersionInfo
                {
                    ResourceId = resourceId,
                    BlockType = type,
                    Versions = new List<VersionEntry>()
                };
                _versionTracking[resourceId] = versionInfo;
            }
            
            versionInfo.Versions.Add(new VersionEntry
            {
                BlockId = blockId,
                Version = version,
                Created = DateTime.UtcNow
            });
            
            // Trim old versions based on retention
            TrimOldVersions(versionInfo);
            
            _logger.LogDebug($"Tracked version {version} of {resourceId} at block {blockId}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to track version: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets the latest version of a resource.
    /// </summary>
    public Result<VersionEntry> GetLatestVersion(string resourceId)
    {
        if (!_versionTracking.TryGetValue(resourceId, out var versionInfo))
            return Result<VersionEntry>.Failure($"No versions found for {resourceId}");
            
        var latest = versionInfo.Versions
            .OrderByDescending(v => v.Version)
            .FirstOrDefault();
            
        if (latest == null)
            return Result<VersionEntry>.Failure($"No versions found for {resourceId}");
            
        return Result<VersionEntry>.Success(latest);
    }
    
    /// <summary>
    /// Gets all versions of a resource.
    /// </summary>
    public Result<List<VersionEntry>> GetVersionHistory(string resourceId)
    {
        if (!_versionTracking.TryGetValue(resourceId, out var versionInfo))
            return Result<List<VersionEntry>>.Success(new List<VersionEntry>());
            
        return Result<List<VersionEntry>>.Success(
            versionInfo.Versions.OrderByDescending(v => v.Version).ToList());
    }
    
    /// <summary>
    /// Resolves version conflicts using last-write-wins.
    /// </summary>
    public Result<VersionEntry> ResolveConflict(
        string resourceId, 
        List<VersionEntry> conflictingVersions)
    {
        if (conflictingVersions == null || conflictingVersions.Count == 0)
            return Result<VersionEntry>.Failure("No versions to resolve");
            
        // Last-write-wins strategy
        var winner = conflictingVersions
            .OrderByDescending(v => v.Created)
            .ThenByDescending(v => v.Version)
            .First();
            
        _logger.LogInfo($"Resolved version conflict for {resourceId}: " +
                       $"version {winner.Version} wins");
                       
        return Result<VersionEntry>.Success(winner);
    }
    
    /// <summary>
    /// Gets versions that can be cleaned up.
    /// </summary>
    public Result<List<VersionEntry>> GetVersionsForCleanup(string resourceId)
    {
        if (!_versionTracking.TryGetValue(resourceId, out var versionInfo))
            return Result<List<VersionEntry>>.Success(new List<VersionEntry>());
            
        var cutoffDate = DateTime.UtcNow - _config.RetentionPeriod;
        var sorted = versionInfo.Versions.OrderByDescending(v => v.Version).ToList();
        
        var toCleanup = new List<VersionEntry>();
        
        // Keep minimum number of versions
        for (int i = _config.MinVersionsToKeep; i < sorted.Count; i++)
        {
            if (sorted[i].Created < cutoffDate)
            {
                toCleanup.Add(sorted[i]);
            }
        }
        
        return Result<List<VersionEntry>>.Success(toCleanup);
    }
    
    private void TrimOldVersions(BlockVersionInfo versionInfo)
    {
        var sorted = versionInfo.Versions.OrderByDescending(v => v.Version).ToList();
        
        if (sorted.Count > _config.MaxVersionsToKeep)
        {
            versionInfo.Versions = sorted.Take(_config.MaxVersionsToKeep).ToList();
            _logger.LogDebug($"Trimmed versions for {versionInfo.ResourceId} to {_config.MaxVersionsToKeep}");
        }
    }
}

// Supporting classes
public class BlockVersionInfo
{
    public string ResourceId { get; set; }
    public BlockType BlockType { get; set; }
    public List<VersionEntry> Versions { get; set; }
}

public class VersionEntry
{
    public long BlockId { get; set; }
    public int Version { get; set; }
    public DateTime Created { get; set; }
}

public class VersionConfig
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(30);
    public int MinVersionsToKeep { get; set; } = 3;
    public int MaxVersionsToKeep { get; set; } = 10;
}
```

### Task 4.2.2: Create Transaction Log
**File**: `EmailDB.Format/Maintenance/TransactionLog.cs`
**Dependencies**: File I/O
**Description**: Maintains audit trail of maintenance operations

```csharp
namespace EmailDB.Format.Maintenance;

/// <summary>
/// Maintains a transaction log of all maintenance operations.
/// </summary>
public class TransactionLog : IDisposable
{
    private readonly string _logPath;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;
    
    public TransactionLog(string databasePath)
    {
        _logPath = $"{databasePath}.txlog";
        _writer = new StreamWriter(_logPath, append: true)
        {
            AutoFlush = true
        };
        
        WriteEntry("STARTUP", "Transaction log started");
    }
    
    /// <summary>
    /// Logs a maintenance operation.
    /// </summary>
    public void LogOperation(string operation, string details, Dictionary<string, object> metadata = null)
    {
        lock (_lock)
        {
            var entry = new TransactionEntry
            {
                Timestamp = DateTime.UtcNow,
                Operation = operation,
                Details = details,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
            
            var json = JsonSerializer.Serialize(entry);
            _writer.WriteLine(json);
        }
    }
    
    /// <summary>
    /// Logs block deletion.
    /// </summary>
    public void LogBlockDeletion(long blockId, BlockType type, string reason)
    {
        LogOperation("DELETE_BLOCK", $"Deleted block {blockId} of type {type}", 
            new Dictionary<string, object>
            {
                { "blockId", blockId },
                { "blockType", type.ToString() },
                { "reason", reason }
            });
    }
    
    /// <summary>
    /// Logs compaction operation.
    /// </summary>
    public void LogCompaction(CompactionResult result)
    {
        LogOperation("COMPACTION", "Database compacted",
            new Dictionary<string, object>
            {
                { "originalSize", result.OriginalSize },
                { "finalSize", result.FinalSize },
                { "spaceReclaimed", result.SpaceReclaimed },
                { "blocksDeleted", result.BlocksDeleted },
                { "duration", (result.EndTime - result.StartTime).TotalSeconds }
            });
    }
    
    /// <summary>
    /// Logs index rebuild.
    /// </summary>
    public void LogIndexRebuild(string reason, bool success, string error = null)
    {
        LogOperation("INDEX_REBUILD", reason,
            new Dictionary<string, object>
            {
                { "success", success },
                { "error", error }
            });
    }
    
    /// <summary>
    /// Gets recent log entries.
    /// </summary>
    public List<TransactionEntry> GetRecentEntries(int count = 100)
    {
        lock (_lock)
        {
            _writer.Flush();
            
            var entries = new List<TransactionEntry>();
            var lines = File.ReadAllLines(_logPath);
            
            for (int i = Math.Max(0, lines.Length - count); i < lines.Length; i++)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TransactionEntry>(lines[i]);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // Skip malformed entries
                }
            }
            
            return entries;
        }
    }
    
    private void WriteEntry(string operation, string details)
    {
        LogOperation(operation, details);
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                WriteEntry("SHUTDOWN", "Transaction log closed");
                _writer?.Dispose();
            }
            _disposed = true;
        }
    }
}

public class TransactionEntry
{
    public DateTime Timestamp { get; set; }
    public string Operation { get; set; }
    public string Details { get; set; }
    public Dictionary<string, object> Metadata { get; set; }
}
```

## Implementation Timeline

### Week 1: Core Maintenance Infrastructure (Days 1-5)
**Day 1-2: MaintenanceManager Core**
- [ ] Task 4.1.1: Create MaintenanceManager class
- [ ] Implement superseded block identification
- [ ] Create maintenance configuration

**Day 3-4: Block Tracking**
- [ ] Task 4.1.2: Create SupersededBlockTracker
- [ ] Task 4.1.3: Create BlockReferenceValidator
- [ ] Implement orphaned block detection

**Day 5: Safety Infrastructure**
- [ ] Task 4.2.2: Create TransactionLog
- [ ] Implement backup/restore functionality
- [ ] Add safety checks

### Week 2: Compaction and Cleanup (Days 6-10)
**Day 6-7: Compaction Implementation**
- [ ] Implement database compaction
- [ ] Create progress reporting
- [ ] Test with various scenarios

**Day 8-9: Version Management**
- [ ] Task 4.2.1: Create VersionManager
- [ ] Implement version tracking
- [ ] Add retention policies

**Day 10: Background Services**
- [ ] Implement scheduled maintenance
- [ ] Create background cleanup
- [ ] Add monitoring

### Week 3: Testing and Integration (Days 11-15)
**Day 11-12: Integration Testing**
- [ ] Test compaction with live data
- [ ] Verify reference validation
- [ ] Test recovery scenarios

**Day 13-14: Performance Testing**
- [ ] Benchmark compaction speed
- [ ] Measure maintenance overhead
- [ ] Optimize hot paths

**Day 15: Documentation**
- [ ] Maintenance best practices
- [ ] Configuration guide
- [ ] Troubleshooting guide

## Success Criteria

1. **Safe Cleanup**: No data loss during maintenance
2. **Efficient Compaction**: >50MB/s compaction speed
3. **Automatic Maintenance**: Background cleanup without manual intervention
4. **Complete Audit Trail**: All operations logged
5. **Version Management**: Proper tracking and retention
6. **Recovery Capability**: Can restore from backup if needed

## Risk Mitigation

1. **Data Loss**: Always create backup before compaction
2. **Corruption**: Validate all operations before committing
3. **Performance Impact**: Run maintenance during off-hours
4. **Space Issues**: Monitor available disk space
5. **Concurrent Access**: Proper locking mechanisms