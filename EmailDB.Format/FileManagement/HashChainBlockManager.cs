using System;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Wraps RawBlockManager to automatically maintain hash chain integrity
/// for all block writes in an append-only fashion.
/// </summary>
public class HashChainBlockManager : IDisposable
{
    private readonly RawBlockManager _blockManager;
    private readonly HashChainManager _hashChainManager;
    private readonly bool _enforceChaining;
    
    /// <summary>
    /// Creates a new HashChainBlockManager that automatically chains all writes.
    /// </summary>
    /// <param name="filePath">Path to the EmailDB file</param>
    /// <param name="enforceChaining">If true, write fails if hash chain fails</param>
    public HashChainBlockManager(string filePath, bool enforceChaining = true)
    {
        _blockManager = new RawBlockManager(filePath);
        _hashChainManager = new HashChainManager(_blockManager);
        _enforceChaining = enforceChaining;
    }

    /// <summary>
    /// Writes a block and automatically adds it to the hash chain.
    /// In append-only system, updates create new blocks linked in the chain.
    /// </summary>
    public async Task<Result<HashChainWriteResult>> WriteBlockAsync(Block block)
    {
        // Step 1: Write the block to storage
        var writeResult = await _blockManager.WriteBlockAsync(block);
        if (!writeResult.IsSuccess)
        {
            return Result<HashChainWriteResult>.Failure($"Failed to write block: {writeResult.Error}");
        }
        
        // Step 2: Add to hash chain
        var chainResult = await _hashChainManager.AddToChainAsync(block);
        if (!chainResult.IsSuccess)
        {
            if (_enforceChaining)
            {
                // In strict mode, we should mark this block as invalid
                // Since we can't delete in append-only, we write a tombstone
                await WriteTombstoneAsync(block.BlockId, "Hash chain failed");
                return Result<HashChainWriteResult>.Failure($"Hash chain failed: {chainResult.Error}");
            }
        }
        
        var result = new HashChainWriteResult
        {
            BlockId = block.BlockId,
            WriteSuccess = true,
            HashChainSuccess = chainResult.IsSuccess,
            HashChainEntry = chainResult.IsSuccess ? chainResult.Value : null
        };
        
        return Result<HashChainWriteResult>.Success(result);
    }

    /// <summary>
    /// Updates a logical record by writing a new version block.
    /// The old block remains in the chain, new block references it.
    /// </summary>
    public async Task<Result<UpdateResult>> UpdateRecordAsync(
        long originalBlockId, 
        Block newBlock, 
        UpdateReason reason)
    {
        // Read original block to verify it exists
        var originalResult = await _blockManager.ReadBlockAsync(originalBlockId);
        if (!originalResult.IsSuccess)
        {
            return Result<UpdateResult>.Failure($"Original block {originalBlockId} not found");
        }
        
        // Create update metadata
        var updateMetadata = new UpdateMetadata
        {
            OriginalBlockId = originalBlockId,
            UpdatedBlockId = newBlock.BlockId,
            UpdateReason = reason,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = Environment.UserName
        };
        
        // Add update reference to the new block's metadata
        newBlock.Flags |= 0x10; // Set "update" flag
        
        // Write the new block
        var writeResult = await WriteBlockAsync(newBlock);
        if (!writeResult.IsSuccess)
        {
            return Result<UpdateResult>.Failure($"Failed to write update block: {writeResult.Error}");
        }
        
        // Write update metadata block
        var metadataBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0x20, // Update metadata flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = GenerateBlockId(),
            Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(updateMetadata)
        };
        
        var metadataResult = await WriteBlockAsync(metadataBlock);
        
        return Result<UpdateResult>.Success(new UpdateResult
        {
            OriginalBlockId = originalBlockId,
            NewBlockId = newBlock.BlockId,
            UpdateMetadataBlockId = metadataBlock.BlockId,
            HashChainEntry = writeResult.Value.HashChainEntry
        });
    }

    /// <summary>
    /// Marks a block as deleted by writing a deletion marker block.
    /// Original block remains but is marked as logically deleted.
    /// </summary>
    public async Task<Result> DeleteRecordAsync(long blockId, string reason)
    {
        var deletionBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0x80, // Deletion flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = GenerateBlockId(),
            Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                DeletedBlockId = blockId,
                DeletionReason = reason,
                DeletedAt = DateTime.UtcNow,
                DeletedBy = Environment.UserName
            })
        };
        
        var result = await WriteBlockAsync(deletionBlock);
        return result.IsSuccess 
            ? Result.Success() 
            : Result.Failure($"Failed to write deletion marker: {result.Error}");
    }

    /// <summary>
    /// Reads a block and verifies its hash chain integrity.
    /// </summary>
    public async Task<Result<VerifiedBlock>> ReadVerifiedBlockAsync(long blockId)
    {
        var blockResult = await _blockManager.ReadBlockAsync(blockId);
        if (!blockResult.IsSuccess)
        {
            return Result<VerifiedBlock>.Failure(blockResult.Error);
        }
        
        var block = blockResult.Value;
        
        // Check if block has been updated or deleted
        var status = await GetBlockStatusAsync(blockId);
        
        // Verify hash chain
        var chainVerification = await _hashChainManager.VerifyBlockAsync(blockId);
        
        var verifiedBlock = new VerifiedBlock
        {
            Block = block,
            Status = status,
            HashChainValid = chainVerification.IsSuccess && chainVerification.Value.IsValid,
            HashChainEntry = _hashChainManager.GetChainEntry(blockId)
        };
        
        return Result<VerifiedBlock>.Success(verifiedBlock);
    }

    /// <summary>
    /// Gets the current version of a logical record by following update chain.
    /// </summary>
    public async Task<Result<Block>> GetCurrentVersionAsync(long originalBlockId)
    {
        var currentId = originalBlockId;
        var maxDepth = 100; // Prevent infinite loops
        
        while (maxDepth-- > 0)
        {
            var status = await GetBlockStatusAsync(currentId);
            
            if (status.IsDeleted)
            {
                return Result<Block>.Failure($"Block {originalBlockId} has been deleted");
            }
            
            if (status.UpdatedToBlockId.HasValue)
            {
                currentId = status.UpdatedToBlockId.Value;
            }
            else
            {
                // This is the current version
                var result = await _blockManager.ReadBlockAsync(currentId);
                return result;
            }
        }
        
        return Result<Block>.Failure("Update chain too deep or circular");
    }

    /// <summary>
    /// Gets the complete history of a record including all updates.
    /// </summary>
    public async Task<RecordHistory> GetRecordHistoryAsync(long originalBlockId)
    {
        var history = new RecordHistory
        {
            OriginalBlockId = originalBlockId,
            Versions = new List<RecordVersion>()
        };
        
        var currentId = originalBlockId;
        var visited = new HashSet<long>();
        
        while (currentId != 0 && visited.Add(currentId))
        {
            var blockResult = await ReadVerifiedBlockAsync(currentId);
            if (!blockResult.IsSuccess)
                break;
                
            var version = new RecordVersion
            {
                BlockId = currentId,
                Timestamp = new DateTime(blockResult.Value.Block.Timestamp),
                HashChainEntry = blockResult.Value.HashChainEntry,
                IsDeleted = blockResult.Value.Status.IsDeleted,
                UpdateReason = blockResult.Value.Status.UpdateReason
            };
            
            history.Versions.Add(version);
            
            if (blockResult.Value.Status.UpdatedToBlockId.HasValue)
            {
                currentId = blockResult.Value.Status.UpdatedToBlockId.Value;
            }
            else
            {
                break;
            }
        }
        
        return history;
    }

    private async Task<BlockStatus> GetBlockStatusAsync(long blockId)
    {
        var status = new BlockStatus { BlockId = blockId };
        
        // Search for update or deletion markers
        // In production, this would use an index for efficiency
        var locations = _blockManager.GetBlockLocations();
        
        foreach (var (id, _) in locations)
        {
            var result = await _blockManager.ReadBlockAsync(id);
            if (!result.IsSuccess || result.Value == null)
                continue;
                
            var block = result.Value;
            
            // Check for update metadata
            if ((block.Flags & 0x20) == 0x20) // Update metadata flag
            {
                try
                {
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<UpdateMetadata>(block.Payload);
                    if (metadata.OriginalBlockId == blockId)
                    {
                        status.UpdatedToBlockId = metadata.UpdatedBlockId;
                        status.UpdateReason = metadata.UpdateReason;
                    }
                }
                catch { }
            }
            
            // Check for deletion marker
            if ((block.Flags & 0x80) == 0x80) // Deletion flag
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(block.Payload);
                    if (json.TryGetValue("DeletedBlockId", out var deletedId) && 
                        Convert.ToInt64(deletedId) == blockId)
                    {
                        status.IsDeleted = true;
                        status.DeletionReason = json.GetValueOrDefault("DeletionReason")?.ToString();
                    }
                }
                catch { }
            }
        }
        
        return status;
    }

    private async Task WriteTombstoneAsync(long blockId, string reason)
    {
        var tombstone = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0xFF, // Tombstone flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = GenerateBlockId(),
            Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                InvalidBlockId = blockId,
                Reason = reason,
                Timestamp = DateTime.UtcNow
            })
        };
        
        await _blockManager.WriteBlockAsync(tombstone);
    }

    private long GenerateBlockId()
    {
        // Simple ID generation - in production use proper ID generator
        return DateTime.UtcNow.Ticks;
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
    }
}

/// <summary>
/// Result of writing a block with hash chain.
/// </summary>
public class HashChainWriteResult
{
    public long BlockId { get; set; }
    public bool WriteSuccess { get; set; }
    public bool HashChainSuccess { get; set; }
    public HashChainEntry HashChainEntry { get; set; }
}

/// <summary>
/// Result of updating a record.
/// </summary>
public class UpdateResult
{
    public long OriginalBlockId { get; set; }
    public long NewBlockId { get; set; }
    public long UpdateMetadataBlockId { get; set; }
    public HashChainEntry HashChainEntry { get; set; }
}

/// <summary>
/// Metadata about an update operation.
/// </summary>
public class UpdateMetadata
{
    public long OriginalBlockId { get; set; }
    public long UpdatedBlockId { get; set; }
    public UpdateReason UpdateReason { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; }
}

/// <summary>
/// Reason for updating a record.
/// </summary>
public enum UpdateReason
{
    ContentCorrection,
    MetadataUpdate,
    Reclassification,
    LegalHold,
    ComplianceUpdate,
    UserEdit,
    SystemMigration
}

/// <summary>
/// Status of a block including update/deletion info.
/// </summary>
public class BlockStatus
{
    public long BlockId { get; set; }
    public bool IsDeleted { get; set; }
    public string DeletionReason { get; set; }
    public long? UpdatedToBlockId { get; set; }
    public UpdateReason? UpdateReason { get; set; }
}

/// <summary>
/// A block with verification information.
/// </summary>
public class VerifiedBlock
{
    public Block Block { get; set; }
    public BlockStatus Status { get; set; }
    public bool HashChainValid { get; set; }
    public HashChainEntry HashChainEntry { get; set; }
}

/// <summary>
/// Complete history of a record including all versions.
/// </summary>
public class RecordHistory
{
    public long OriginalBlockId { get; set; }
    public List<RecordVersion> Versions { get; set; }
}

/// <summary>
/// A version in a record's history.
/// </summary>
public class RecordVersion
{
    public long BlockId { get; set; }
    public DateTime Timestamp { get; set; }
    public HashChainEntry HashChainEntry { get; set; }
    public bool IsDeleted { get; set; }
    public UpdateReason? UpdateReason { get; set; }
}