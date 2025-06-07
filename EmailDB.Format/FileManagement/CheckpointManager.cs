using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages checkpoints and recovery for EmailDB blocks.
/// Provides automatic backup and recovery of critical blocks.
/// </summary>
public class CheckpointManager
{
    private readonly RawBlockManager _blockManager;
    private readonly Dictionary<ulong, List<ulong>> _checkpointIndex;
    private readonly object _lock = new object();
    
    // Checkpoint block IDs start at a high range to avoid conflicts
    private const ulong CHECKPOINT_ID_BASE = 1_000_000_000_000;
    private ulong _nextCheckpointId = CHECKPOINT_ID_BASE;
    
    // Flag to mark checkpoint blocks
    private const byte CHECKPOINT_FLAG = 0x80;

    public CheckpointManager(RawBlockManager blockManager)
    {
        _blockManager = blockManager;
        _checkpointIndex = new Dictionary<ulong, List<ulong>>();
        
        // Load existing checkpoint index on initialization
        Task.Run(async () => await LoadCheckpointIndexAsync());
    }

    /// <summary>
    /// Creates a checkpoint (backup) of a specific block.
    /// </summary>
    public async Task<Result<ulong>> CreateCheckpointAsync(ulong originalBlockId)
    {
        // Read the original block
        var readResult = await _blockManager.ReadBlockAsync((long)originalBlockId);
        if (!readResult.IsSuccess)
        {
            return Result<ulong>.Failure($"Failed to read original block {originalBlockId}: {readResult.Error}");
        }

        var originalBlock = readResult.Value;
        if (originalBlock == null)
        {
            return Result<ulong>.Failure($"Original block {originalBlockId} not found");
        }

        // Create checkpoint block
        var checkpointBlock = new Block
        {
            Version = originalBlock.Version,
            Type = originalBlock.Type,
            Flags = (byte)(originalBlock.Flags | CHECKPOINT_FLAG), // Add checkpoint flag
            Encoding = originalBlock.Encoding,
            Timestamp = DateTime.UtcNow.Ticks, // New timestamp for checkpoint
            BlockId = (long)GenerateCheckpointId(),
            Payload = originalBlock.Payload // Copy payload
        };

        // Write checkpoint block
        var writeResult = await _blockManager.WriteBlockAsync(checkpointBlock);
        if (!writeResult.IsSuccess)
        {
            return Result<ulong>.Failure($"Failed to write checkpoint: {writeResult.Error}");
        }

        // Update index
        lock (_lock)
        {
            if (!_checkpointIndex.ContainsKey(originalBlockId))
            {
                _checkpointIndex[originalBlockId] = new List<ulong>();
            }
            _checkpointIndex[originalBlockId].Add((ulong)checkpointBlock.BlockId);
        }

        // Persist checkpoint metadata
        await PersistCheckpointMetadataAsync(originalBlockId, (ulong)checkpointBlock.BlockId);

        return Result<ulong>.Success((ulong)checkpointBlock.BlockId);
    }

    /// <summary>
    /// Creates checkpoints for multiple blocks in a batch.
    /// </summary>
    public async Task<CheckpointBatchResult> CreateCheckpointBatchAsync(IEnumerable<ulong> blockIds)
    {
        var result = new CheckpointBatchResult();
        var tasks = new List<Task>();

        foreach (var blockId in blockIds)
        {
            tasks.Add(Task.Run(async () =>
            {
                var checkpointResult = await CreateCheckpointAsync(blockId);
                if (checkpointResult.IsSuccess)
                {
                    lock (result)
                    {
                        result.SuccessfulCheckpoints[blockId] = checkpointResult.Value;
                    }
                }
                else
                {
                    lock (result)
                    {
                        result.FailedCheckpoints[blockId] = checkpointResult.Error;
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>
    /// Attempts to recover a corrupted block from its checkpoints.
    /// </summary>
    public async Task<Result<Block>> RecoverBlockAsync(ulong corruptedBlockId)
    {
        List<ulong> checkpointIds;
        lock (_lock)
        {
            if (!_checkpointIndex.TryGetValue(corruptedBlockId, out checkpointIds) || checkpointIds.Count == 0)
            {
                return Result<Block>.Failure($"No checkpoints found for block {corruptedBlockId}");
            }
        }

        // Try checkpoints from newest to oldest
        for (int i = checkpointIds.Count - 1; i >= 0; i--)
        {
            var checkpointId = checkpointIds[i];
            var readResult = await _blockManager.ReadBlockAsync((long)checkpointId);
            
            if (readResult.IsSuccess && readResult.Value != null)
            {
                // Create recovered block with original ID
                var recoveredBlock = new Block
                {
                    Version = readResult.Value.Version,
                    Type = readResult.Value.Type,
                    Flags = (byte)(readResult.Value.Flags & ~CHECKPOINT_FLAG), // Remove checkpoint flag
                    Encoding = readResult.Value.Encoding,
                    Timestamp = readResult.Value.Timestamp,
                    BlockId = (long)corruptedBlockId, // Restore original ID
                    Payload = readResult.Value.Payload
                };

                return Result<Block>.Success(recoveredBlock);
            }
        }

        return Result<Block>.Failure($"All checkpoints for block {corruptedBlockId} are corrupted or missing");
    }

    /// <summary>
    /// Automatically attempts to read a block with recovery fallback.
    /// </summary>
    public async Task<Result<Block>> ReadBlockWithRecoveryAsync(ulong blockId)
    {
        // First try to read the original block
        var readResult = await _blockManager.ReadBlockAsync((long)blockId);
        if (readResult.IsSuccess && readResult.Value != null)
        {
            return readResult;
        }

        // If failed, attempt recovery from checkpoint
        var recoveryResult = await RecoverBlockAsync(blockId);
        if (recoveryResult.IsSuccess)
        {
            return Result<Block>.Success(recoveryResult.Value);
        }

        // Return original error if recovery also failed
        return Result<Block>.Failure($"Block {blockId} corrupted and recovery failed: {readResult.Error}");
    }

    /// <summary>
    /// Creates a full system checkpoint of all critical blocks.
    /// </summary>
    public async Task<SystemCheckpointResult> CreateSystemCheckpointAsync(CheckpointCriteria criteria = null)
    {
        criteria ??= CheckpointCriteria.Default;
        var result = new SystemCheckpointResult
        {
            CheckpointTime = DateTime.UtcNow
        };

        // Get all blocks that meet criteria
        var blocksToCheckpoint = new List<ulong>();
        var allBlocks = _blockManager.GetBlockLocations();

        foreach (var (blockId, location) in allBlocks)
        {
            // Skip blocks that are already checkpoints
            if ((ulong)blockId >= CHECKPOINT_ID_BASE)
                continue;

            var readResult = await _blockManager.ReadBlockAsync(blockId);
            if (readResult.IsSuccess && readResult.Value != null)
            {
                var block = readResult.Value;
                
                // Check if block meets criteria
                if (criteria.ShouldCheckpoint(block))
                {
                    blocksToCheckpoint.Add((ulong)blockId);
                }
            }
        }

        // Create checkpoints in batches
        var batchResult = await CreateCheckpointBatchAsync(blocksToCheckpoint);
        
        result.TotalBlocks = blocksToCheckpoint.Count;
        result.SuccessfulCheckpoints = batchResult.SuccessfulCheckpoints.Count;
        result.FailedCheckpoints = batchResult.FailedCheckpoints.Count;
        result.CheckpointIds = batchResult.SuccessfulCheckpoints.Values.ToList();

        // Write system checkpoint metadata
        await WriteSystemCheckpointMetadataAsync(result);

        return result;
    }

    /// <summary>
    /// Gets checkpoint history for a specific block.
    /// </summary>
    public CheckpointHistory GetCheckpointHistory(ulong blockId)
    {
        lock (_lock)
        {
            if (!_checkpointIndex.TryGetValue(blockId, out var checkpointIds))
            {
                return new CheckpointHistory { OriginalBlockId = blockId };
            }

            return new CheckpointHistory
            {
                OriginalBlockId = blockId,
                CheckpointIds = checkpointIds.ToList(),
                CheckpointCount = checkpointIds.Count,
                LatestCheckpointId = checkpointIds.LastOrDefault()
            };
        }
    }

    /// <summary>
    /// Prunes old checkpoints to save space.
    /// </summary>
    public async Task<PruneResult> PruneOldCheckpointsAsync(int maxCheckpointsPerBlock = 3)
    {
        var result = new PruneResult();
        var blocksToPrune = new List<(ulong blockId, List<ulong> checkpointsToRemove)>();

        lock (_lock)
        {
            foreach (var kvp in _checkpointIndex)
            {
                if (kvp.Value.Count > maxCheckpointsPerBlock)
                {
                    // Keep only the most recent checkpoints
                    var toRemove = kvp.Value.Take(kvp.Value.Count - maxCheckpointsPerBlock).ToList();
                    blocksToPrune.Add((kvp.Key, toRemove));
                }
            }
        }

        // Remove old checkpoints
        foreach (var (blockId, checkpointsToRemove) in blocksToPrune)
        {
            foreach (var checkpointId in checkpointsToRemove)
            {
                // Note: RawBlockManager doesn't support deletion in append-only format
                // We just remove from index, blocks remain in file but become unreferenced
                lock (_lock)
                {
                    _checkpointIndex[blockId].Remove(checkpointId);
                }
                result.PrunedCheckpoints++;
            }
        }

        return result;
    }

    private ulong GenerateCheckpointId()
    {
        lock (_lock)
        {
            return _nextCheckpointId++;
        }
    }

    private async Task LoadCheckpointIndexAsync()
    {
        // Load checkpoint metadata block if it exists
        var metadataBlockId = CHECKPOINT_ID_BASE - 1; // Reserved ID for checkpoint metadata
        var result = await _blockManager.ReadBlockAsync((long)metadataBlockId);
        
        if (result.IsSuccess && result.Value != null)
        {
            // Deserialize checkpoint index from metadata block
            // Implementation depends on serialization format
        }
    }

    private async Task PersistCheckpointMetadataAsync(ulong originalBlockId, ulong checkpointBlockId)
    {
        // Persist checkpoint mapping as a metadata block
        var metadata = new CheckpointMetadata
        {
            OriginalBlockId = originalBlockId,
            CheckpointBlockId = checkpointBlockId,
            CreatedAt = DateTime.UtcNow
        };

        var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
        var metadataBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = CHECKPOINT_FLAG,
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = (long)GenerateCheckpointId(),
            Payload = System.Text.Encoding.UTF8.GetBytes(metadataJson)
        };

        await _blockManager.WriteBlockAsync(metadataBlock);
    }

    private async Task WriteSystemCheckpointMetadataAsync(SystemCheckpointResult result)
    {
        var metadataJson = System.Text.Json.JsonSerializer.Serialize(result);
        var metadataBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = (byte)(CHECKPOINT_FLAG | 0x40), // System checkpoint flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = (long)GenerateCheckpointId(),
            Payload = System.Text.Encoding.UTF8.GetBytes(metadataJson)
        };

        await _blockManager.WriteBlockAsync(metadataBlock);
    }
}

/// <summary>
/// Criteria for determining which blocks should be checkpointed.
/// </summary>
public class CheckpointCriteria
{
    public BlockType[] IncludedTypes { get; set; } = new[] { BlockType.Segment, BlockType.Metadata, BlockType.Folder };
    public TimeSpan? MinimumAge { get; set; }
    public int? MinimumSize { get; set; }

    public static CheckpointCriteria Default => new CheckpointCriteria
    {
        IncludedTypes = new[] { BlockType.Segment, BlockType.Metadata },
        MinimumSize = 1024 // Only checkpoint blocks larger than 1KB
    };

    public bool ShouldCheckpoint(Block block)
    {
        if (!IncludedTypes.Contains(block.Type))
            return false;

        if (MinimumSize.HasValue && block.Payload?.Length < MinimumSize.Value)
            return false;

        if (MinimumAge.HasValue)
        {
            var blockAge = DateTime.UtcNow - new DateTime(block.Timestamp);
            if (blockAge < MinimumAge.Value)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Result of a batch checkpoint operation.
/// </summary>
public class CheckpointBatchResult
{
    public Dictionary<ulong, ulong> SuccessfulCheckpoints { get; } = new();
    public Dictionary<ulong, string> FailedCheckpoints { get; } = new();
}

/// <summary>
/// Result of a system-wide checkpoint operation.
/// </summary>
public class SystemCheckpointResult
{
    public DateTime CheckpointTime { get; set; }
    public int TotalBlocks { get; set; }
    public int SuccessfulCheckpoints { get; set; }
    public int FailedCheckpoints { get; set; }
    public List<ulong> CheckpointIds { get; set; } = new();
}

/// <summary>
/// Checkpoint history for a specific block.
/// </summary>
public class CheckpointHistory
{
    public ulong OriginalBlockId { get; set; }
    public List<ulong> CheckpointIds { get; set; } = new();
    public int CheckpointCount { get; set; }
    public ulong LatestCheckpointId { get; set; }
}

/// <summary>
/// Metadata for a checkpoint mapping.
/// </summary>
public class CheckpointMetadata
{
    public ulong OriginalBlockId { get; set; }
    public ulong CheckpointBlockId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Result of pruning old checkpoints.
/// </summary>
public class PruneResult
{
    public int PrunedCheckpoints { get; set; }
}