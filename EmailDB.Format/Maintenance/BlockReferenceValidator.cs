using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Indexing;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.Helpers;

namespace EmailDB.Format.Maintenance;

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
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    public async Task<Result<bool>> CheckIndexReferencesAsync(long blockId)
    {
        try
        {
            var isReferenced = await _indexManager.IsBlockReferencedAsync(blockId);
            return Result<bool>.Success(isReferenced);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to check index references: {ex.Message}");
        }
    }
    
    public async Task<Result<bool>> CheckFolderReferencesAsync(long blockId)
    {
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            
            foreach (var (id, location) in blockLocations)
            {
                var blockResult = await _blockManager.ReadBlockAsync(id);
                if (!blockResult.IsSuccess || blockResult.Value.Type != BlockType.Folder)
                    continue;
                    
                try
                {
                    var serializer = new DefaultBlockContentSerializer();
                    var deserializeResult = serializer.Deserialize<FolderContent>(blockResult.Value.Payload, blockResult.Value.Encoding);
                    
                    if (deserializeResult.IsSuccess)
                    {
                        var folder = deserializeResult.Value;
                        if (folder.EnvelopeBlockId == blockId)
                        {
                            _logger.LogDebug($"Block {blockId} referenced by folder {folder.Name}");
                            return Result<bool>.Success(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to deserialize folder block {id}: {ex.Message}");
                }
            }
            
            return Result<bool>.Success(false);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to check folder references: {ex.Message}");
        }
    }
    
    public async Task<Result<bool>> CheckCrossBlockReferencesAsync(long blockId)
    {
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            
            foreach (var (id, location) in blockLocations)
            {
                var blockResult = await _blockManager.ReadBlockAsync(id);
                if (!blockResult.IsSuccess || blockResult.Value.Type != BlockType.FolderEnvelope)
                    continue;
                        
                try
                {
                    var serializer = new DefaultBlockContentSerializer();
                    var deserializeResult = serializer.Deserialize<FolderEnvelopeBlock>(blockResult.Value.Payload, blockResult.Value.Encoding);
                        
                    if (deserializeResult.IsSuccess)
                    {
                        var envelope = deserializeResult.Value;
                        if (envelope.PreviousBlockId == blockId)
                        {
                            _logger.LogDebug($"Block {blockId} referenced by envelope block {id} as previous version");
                            return Result<bool>.Success(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to deserialize envelope block {id}: {ex.Message}");
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