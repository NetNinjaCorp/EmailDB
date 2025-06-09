using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.Helpers;

namespace EmailDB.Format.Maintenance;

public class SupersededBlockTracker
{
    private readonly RawBlockManager _blockManager;
    private readonly ILogger _logger;
    
    public SupersededBlockTracker(RawBlockManager blockManager, ILogger logger)
    {
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    public async Task<List<SupersededBlock>> FindOrphanedBlocksAsync()
    {
        var orphanedBlocks = new List<SupersededBlock>();
        var blockLocations = _blockManager.GetBlockLocations();
        var referencedBlocks = new HashSet<long>();
        
        _logger.LogInfo($"Scanning {blockLocations.Count} blocks for orphaned references...");
        
        foreach (var (blockId, location) in blockLocations)
        {
            var blockResult = await _blockManager.ReadBlockAsync(blockId);
            if (!blockResult.IsSuccess)
            {
                _logger.LogWarning($"Failed to read block {blockId} during orphan scan: {blockResult.Error}");
                continue;
            }
                
            var block = blockResult.Value;
            
            switch (block.Type)
            {
                case BlockType.Folder:
                    var folderRefs = await ExtractFolderReferencesAsync(block);
                    referencedBlocks.UnionWith(folderRefs);
                    break;
                    
                case BlockType.FolderEnvelope:
                    var envelopeRefs = await ExtractEnvelopeReferencesAsync(block);
                    referencedBlocks.UnionWith(envelopeRefs);
                    break;
                    
                case BlockType.KeyManager:
                    var keyRefs = await ExtractKeyManagerReferencesAsync(block);
                    referencedBlocks.UnionWith(keyRefs);
                    break;
                    
                case BlockType.EmailBatch:
                    referencedBlocks.Add(blockId);
                    break;
            }
        }
        
        foreach (var (blockId, location) in blockLocations)
        {
            var blockResult = await _blockManager.ReadBlockAsync(blockId);
            if (!blockResult.IsSuccess) continue;
            
            var blockType = blockResult.Value.Type;
            if (!referencedBlocks.Contains(blockId) && IsOrphanableType(blockType))
            {
                orphanedBlocks.Add(new SupersededBlock
                {
                    BlockId = blockId,
                    BlockType = blockType,
                    SupersededAt = DateTimeOffset.FromUnixTimeSeconds(blockResult.Value.Timestamp).UtcDateTime,
                    Reason = "Orphaned block - no references found"
                });
            }
        }
        
        _logger.LogInfo($"Found {orphanedBlocks.Count} orphaned blocks");
        return orphanedBlocks;
    }
    
    private async Task<HashSet<long>> ExtractFolderReferencesAsync(Block block)
    {
        var references = new HashSet<long>();
        
        try
        {
            var serializer = new DefaultBlockContentSerializer();
            var deserializeResult = serializer.Deserialize<FolderContent>(block.Payload, block.Encoding);
            
            if (deserializeResult.IsSuccess)
            {
                var folder = deserializeResult.Value;
                if (folder.EnvelopeBlockId > 0)
                {
                    references.Add(folder.EnvelopeBlockId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to extract folder references from block {block.BlockId}: {ex.Message}");
        }
        
        return references;
    }
    
    private async Task<HashSet<long>> ExtractEnvelopeReferencesAsync(Block block)
    {
        var references = new HashSet<long>();
        
        try
        {
            var serializer = new DefaultBlockContentSerializer();
            var deserializeResult = serializer.Deserialize<FolderEnvelopeBlock>(block.Payload, block.Encoding);
            
            if (deserializeResult.IsSuccess)
            {
                var envelope = deserializeResult.Value;
                if (envelope.PreviousBlockId.HasValue)
                {
                    references.Add(envelope.PreviousBlockId.Value);
                }
                
                foreach (var emailEnvelope in envelope.Envelopes)
                {
                    var emailId = EmailHashedID.FromCompoundKey(emailEnvelope.CompoundId);
                    references.Add(emailId.BlockId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to extract envelope references from block {block.BlockId}: {ex.Message}");
        }
        
        return references;
    }
    
    private async Task<HashSet<long>> ExtractKeyManagerReferencesAsync(Block block)
    {
        var references = new HashSet<long>();
        
        return references;
    }
    
    private bool IsOrphanableType(BlockType type)
    {
        return type != BlockType.Metadata &&
               type != BlockType.EmailBatch;
    }
}