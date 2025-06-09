using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Block storage extension for FolderManager
/// </summary>
public partial class FolderManager
{
    private readonly Dictionary<string, long> _folderBlockCache = new();
    private readonly Dictionary<string, long> _envelopeBlockCache = new();
    
    // Track superseded blocks
    private readonly List<SupersededBlock> _supersededBlocks = new();
    
    public class SupersededBlock
    {
        public long BlockId { get; set; }
        public BlockType Type { get; set; }
        public DateTime SupersededAt { get; set; }
        public string Reason { get; set; }
    }
    
    /// <summary>
    /// Stores a folder content block and returns the block ID.
    /// </summary>
    public async Task<Result<long>> StoreFolderBlockAsync(FolderContent folder)
    {
        try
        {
            // Increment version
            folder.Version++;
            folder.LastModified = DateTime.UtcNow;
            
            // Serialize folder
            var serialized = _serializer.Serialize(folder);
            
            // Write as new block
            var block = new Block
            {
                Type = BlockType.Folder,
                Payload = serialized,
                PayloadLength = serialized.Length,
                Encoding = PayloadEncoding.Protobuf,
                Flags = (byte)BlockFlags.None.SetCompressionAlgorithm(CompressionAlgorithm.LZ4)
            };
            
            var blockResult = await _blockManager.WriteBlockAsync(block);
            if (!blockResult.IsSuccess)
                return Result<long>.Failure(blockResult.Error);
            
            var blockId = block.BlockId;
            
            // Track old block as superseded if exists
            if (_folderBlockCache.TryGetValue(folder.Name, out var oldBlockId))
            {
                _supersededBlocks.Add(new SupersededBlock
                {
                    BlockId = oldBlockId,
                    Type = BlockType.Folder,
                    SupersededAt = DateTime.UtcNow,
                    Reason = "Folder update"
                });
            }
            
            // Update cache
            _folderBlockCache[folder.Name] = blockId;
            
            return Result<long>.Success(blockId);
        }
        catch (Exception ex)
        {
            return Result<long>.Failure($"Failed to store folder block: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Stores an envelope block for a folder.
    /// </summary>
    public async Task<Result<long>> StoreEnvelopeBlockAsync(FolderEnvelopeBlock envelopeBlock)
    {
        try
        {
            // Increment version
            envelopeBlock.Version++;
            envelopeBlock.LastModified = DateTime.UtcNow;
            
            // Link to previous version if exists
            if (_envelopeBlockCache.TryGetValue(envelopeBlock.FolderPath, out var previousBlockId))
            {
                envelopeBlock.PreviousBlockId = previousBlockId;
            }
            
            // Serialize
            var serialized = _serializer.Serialize(envelopeBlock);
            
            // Write block (no compression for fast access)
            var block = new Block
            {
                Type = BlockType.FolderEnvelope,
                Payload = serialized,
                PayloadLength = serialized.Length,
                Encoding = PayloadEncoding.Protobuf,
                Flags = (byte)BlockFlags.None
            };
            
            var blockResult = await _blockManager.WriteBlockAsync(block);
            if (!blockResult.IsSuccess)
                return Result<long>.Failure(blockResult.Error);
            
            var blockId = block.BlockId;
            
            // Track old block as superseded
            if (envelopeBlock.PreviousBlockId.HasValue)
            {
                _supersededBlocks.Add(new SupersededBlock
                {
                    BlockId = envelopeBlock.PreviousBlockId.Value,
                    Type = BlockType.FolderEnvelope,
                    SupersededAt = DateTime.UtcNow,
                    Reason = "Envelope update"
                });
            }
            
            // Update cache
            _envelopeBlockCache[envelopeBlock.FolderPath] = blockId;
            
            return Result<long>.Success(blockId);
        }
        catch (Exception ex)
        {
            return Result<long>.Failure($"Failed to store envelope block: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads a folder from its block.
    /// </summary>
    private async Task<Result<FolderContent>> LoadFolderAsync(string folderPath)
    {
        try
        {
            // Try cache first
            if (_folderBlockCache.TryGetValue(folderPath, out var blockId))
            {
                var blockResult = await _blockManager.ReadBlockAsync(blockId);
                if (!blockResult.IsSuccess)
                    return Result<FolderContent>.Failure($"Failed to read folder block: {blockResult.Error}");
                
                var folder = _serializer.Deserialize<FolderContent>(blockResult.Value.Payload);
                return Result<FolderContent>.Success(folder);
            }
            
            // Load from folder tree
            var folderTreeResult = await LoadFolderTreeFromBlocksAsync();
            if (!folderTreeResult.IsSuccess)
                return Result<FolderContent>.Failure($"Failed to load folder tree: {folderTreeResult.Error}");
            
            var folderTree = folderTreeResult.Value;
            if (!folderTree.FolderIDs.TryGetValue(folderPath, out var folderId))
                return Result<FolderContent>.Failure($"Folder '{folderPath}' not found");
            
            if (!folderTree.FolderOffsets.TryGetValue(folderId, out var folderBlockId))
                return Result<FolderContent>.Failure($"Folder block location not found for folder ID {folderId}");
            
            var folderBlockResult = await _blockManager.ReadBlockAsync(folderBlockId);
            if (!folderBlockResult.IsSuccess)
                return Result<FolderContent>.Failure($"Failed to read folder block: {folderBlockResult.Error}");
            
            var folderContent = _serializer.Deserialize<FolderContent>(folderBlockResult.Value.Payload);
            
            // Update cache
            _folderBlockCache[folderPath] = folderBlockId;
            
            return Result<FolderContent>.Success(folderContent);
        }
        catch (Exception ex)
        {
            return Result<FolderContent>.Failure($"Failed to load folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads an envelope block.
    /// </summary>
    private async Task<Result<FolderEnvelopeBlock>> LoadEnvelopeBlockAsync(long blockId)
    {
        try
        {
            var blockResult = await _blockManager.ReadBlockAsync(blockId);
            if (!blockResult.IsSuccess)
                return Result<FolderEnvelopeBlock>.Failure($"Failed to read envelope block: {blockResult.Error}");
            
            var envelopeBlock = _serializer.Deserialize<FolderEnvelopeBlock>(blockResult.Value.Payload);
            return Result<FolderEnvelopeBlock>.Success(envelopeBlock);
        }
        catch (Exception ex)
        {
            return Result<FolderEnvelopeBlock>.Failure($"Failed to load envelope block: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads the folder tree from blocks.
    /// </summary>
    private async Task<Result<FolderTreeContent>> LoadFolderTreeFromBlocksAsync()
    {
        try
        {
            // Get metadata to find folder tree
            var metadata = await metadataManager.GetMetadataAsync();
            if (metadata == null || metadata.FolderTreeOffset < 0)
                return Result<FolderTreeContent>.Failure("Folder tree not found in metadata");
            
            var blockResult = await _blockManager.ReadBlockAsync(metadata.FolderTreeOffset);
            if (!blockResult.IsSuccess)
                return Result<FolderTreeContent>.Failure($"Failed to read folder tree block: {blockResult.Error}");
            
            var folderTree = _serializer.Deserialize<FolderTreeContent>(blockResult.Value.Payload);
            return Result<FolderTreeContent>.Success(folderTree);
        }
        catch (Exception ex)
        {
            return Result<FolderTreeContent>.Failure($"Failed to load folder tree: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Stores the folder tree in a block.
    /// </summary>
    private async Task<Result<long>> StoreFolderTreeBlockAsync(FolderTreeContent folderTree)
    {
        try
        {
            var serialized = _serializer.Serialize(folderTree);
            
            var block = new Block
            {
                Type = BlockType.FolderTree,
                Payload = serialized,
                PayloadLength = serialized.Length,
                Encoding = PayloadEncoding.Protobuf,
                Flags = (byte)BlockFlags.None.SetCompressionAlgorithm(CompressionAlgorithm.LZ4)
            };
            
            var blockResult = await _blockManager.WriteBlockAsync(block);
            if (!blockResult.IsSuccess)
                return Result<long>.Failure(blockResult.Error);
            
            var blockId = block.BlockId;
            
            // Update metadata with new folder tree location
            var metadata = await metadataManager.GetMetadataAsync();
            if (metadata != null)
            {
                metadata.FolderTreeOffset = blockResult.Value.Position;
                // Note: In the current implementation, metadata is updated via block writes
                // We would need to implement a proper metadata update mechanism
            }
            
            return Result<long>.Success(blockId);
        }
        catch (Exception ex)
        {
            return Result<long>.Failure($"Failed to store folder tree: {ex.Message}");
        }
    }
    
    
    /// <summary>
    /// Creates a new folder at the specified path using block storage.
    /// </summary>
    public async Task<Result> CreateFolderWithBlockStorageAsync(string path)
    {
        try
        {
            ValidatePath(path);
            
            // Get folder tree from blocks (not cache)
            var folderTreeResult = await LoadFolderTreeFromBlocksAsync();
            if (!folderTreeResult.IsSuccess)
                return Result.Failure($"Failed to load folder tree: {folderTreeResult.Error}");
                
            var folderTree = folderTreeResult.Value;
            
            // Check if path already exists
            if (folderTree.FolderIDs.ContainsKey(path))
                return Result.Failure($"Folder '{path}' already exists");
            
            var (parentPath, folderName) = SplitPath(path);
            
            // Get parent folder ID
            long parentFolderId = 0;
            if (!string.IsNullOrEmpty(parentPath))
            {
                if (!folderTree.FolderIDs.TryGetValue(parentPath, out parentFolderId))
                    return Result.Failure($"Parent folder '{parentPath}' not found");
            }
            
            // Create new folder
            var folderId = GetNextFolderId(folderTree);
            var folder = new FolderContent
            {
                FolderId = folderId,
                ParentFolderId = parentFolderId,
                Name = path,
                EmailIds = new List<EmailHashedID>(),
                Version = 0
            };
            
            // Store folder in block
            var folderBlockResult = await StoreFolderBlockAsync(folder);
            if (!folderBlockResult.IsSuccess)
                return Result.Failure($"Failed to store folder: {folderBlockResult.Error}");
            
            // Create empty envelope block
            var envelopeBlock = new FolderEnvelopeBlock
            {
                FolderPath = path,
                Version = 0,
                Envelopes = new List<EmailEnvelope>()
            };
            
            var envelopeBlockResult = await StoreEnvelopeBlockAsync(envelopeBlock);
            if (!envelopeBlockResult.IsSuccess)
                return Result.Failure($"Failed to store envelope block: {envelopeBlockResult.Error}");
            
            // Update folder to reference envelope block
            folder.EnvelopeBlockId = envelopeBlockResult.Value;
            
            // Store updated folder
            var updateResult = await StoreFolderBlockAsync(folder);
            if (!updateResult.IsSuccess)
                return Result.Failure($"Failed to update folder with envelope reference: {updateResult.Error}");
            
            // Update folder tree
            folderTree.FolderIDs[path] = folderId;
            folderTree.FolderOffsets[folderId] = folderBlockResult.Value;
            
            // Store updated folder tree
            await StoreFolderTreeBlockAsync(folderTree);
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to create folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Adds an email to a folder and updates the envelope block.
    /// </summary>
    public async Task<Result> AddEmailToFolderAsync(
        string folderPath, 
        EmailHashedID emailId,
        EmailEnvelope envelope)
    {
        try
        {
            // Load folder
            var folderResult = await LoadFolderAsync(folderPath);
            if (!folderResult.IsSuccess)
                return Result.Failure($"Failed to load folder: {folderResult.Error}");
                
            var folder = folderResult.Value;
            
            // Add email ID to folder
            folder.EmailIds.Add(emailId);
            
            // Load envelope block
            var envelopeResult = await LoadEnvelopeBlockAsync(folder.EnvelopeBlockId);
            if (!envelopeResult.IsSuccess)
                return Result.Failure($"Failed to load envelope block: {envelopeResult.Error}");
                
            var envelopeBlock = envelopeResult.Value;
            
            // Add envelope
            envelope.CompoundId = $"{emailId.BlockId}:{emailId.LocalId}";
            envelopeBlock.Envelopes.Add(envelope);
            
            // Store updated envelope block
            var newEnvelopeBlockResult = await StoreEnvelopeBlockAsync(envelopeBlock);
            if (!newEnvelopeBlockResult.IsSuccess)
                return Result.Failure($"Failed to store envelope block: {newEnvelopeBlockResult.Error}");
            
            // Update folder with new envelope block reference
            folder.EnvelopeBlockId = newEnvelopeBlockResult.Value;
            
            // Store updated folder
            var folderBlockResult = await StoreFolderBlockAsync(folder);
            if (!folderBlockResult.IsSuccess)
                return Result.Failure($"Failed to store folder: {folderBlockResult.Error}");
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to add email to folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Removes an email from a folder.
    /// </summary>
    public async Task<Result> RemoveEmailFromFolderAsync(string folderPath, EmailHashedID emailId)
    {
        return await RemoveEmailFromFolderWithBlockStorageAsync(folderPath, emailId);
    }
    
    /// <summary>
    /// Removes an email from a folder using block storage.
    /// </summary>
    public async Task<Result> RemoveEmailFromFolderWithBlockStorageAsync(string folderPath, EmailHashedID emailId)
    {
        try
        {
            // Load folder
            var folderResult = await LoadFolderAsync(folderPath);
            if (!folderResult.IsSuccess)
                return Result.Failure($"Failed to load folder: {folderResult.Error}");
                
            var folder = folderResult.Value;
            
            // Remove email ID from folder
            if (!folder.EmailIds.Remove(emailId))
                return Result.Success(); // Email wasn't in folder
            
            // Load envelope block
            var envelopeResult = await LoadEnvelopeBlockAsync(folder.EnvelopeBlockId);
            if (!envelopeResult.IsSuccess)
                return Result.Failure($"Failed to load envelope block: {envelopeResult.Error}");
                
            var envelopeBlock = envelopeResult.Value;
            
            // Remove envelope
            var compoundId = $"{emailId.BlockId}:{emailId.LocalId}";
            envelopeBlock.Envelopes.RemoveAll(e => e.CompoundId == compoundId);
            
            // Store updated envelope block
            var newEnvelopeBlockResult = await StoreEnvelopeBlockAsync(envelopeBlock);
            if (!newEnvelopeBlockResult.IsSuccess)
                return Result.Failure($"Failed to store envelope block: {newEnvelopeBlockResult.Error}");
            
            // Update folder with new envelope block reference
            folder.EnvelopeBlockId = newEnvelopeBlockResult.Value;
            
            // Store updated folder
            var folderBlockResult = await StoreFolderBlockAsync(folder);
            if (!folderBlockResult.IsSuccess)
                return Result.Failure($"Failed to store folder: {folderBlockResult.Error}");
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to remove email from folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets folder listing by loading the envelope block.
    /// </summary>
    public async Task<Result<List<EmailEnvelope>>> GetFolderListingAsync(string folderPath)
    {
        try
        {
            // Check cache first
            if (_envelopeBlockCache.TryGetValue(folderPath, out var cachedBlockId))
            {
                var cachedResult = await LoadEnvelopeBlockAsync(cachedBlockId);
                if (cachedResult.IsSuccess)
                    return Result<List<EmailEnvelope>>.Success(cachedResult.Value.Envelopes);
            }
            
            // Load folder
            var folderResult = await LoadFolderAsync(folderPath);
            if (!folderResult.IsSuccess)
                return Result<List<EmailEnvelope>>.Failure($"Failed to load folder: {folderResult.Error}");
                
            var folder = folderResult.Value;
            
            // Load envelope block
            var envelopeResult = await LoadEnvelopeBlockAsync(folder.EnvelopeBlockId);
            if (!envelopeResult.IsSuccess)
                return Result<List<EmailEnvelope>>.Failure($"Failed to load envelope block: {envelopeResult.Error}");
            
            return Result<List<EmailEnvelope>>.Success(envelopeResult.Value.Envelopes);
        }
        catch (Exception ex)
        {
            return Result<List<EmailEnvelope>>.Failure($"Failed to get folder listing: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets list of superseded blocks for cleanup.
    /// </summary>
    public async Task<Result<List<SupersededBlock>>> GetSupersededBlocksAsync()
    {
        // In production, this would query from a persistent store
        // For now, return from in-memory list
        var oldBlocks = _supersededBlocks
            .Where(b => (DateTime.UtcNow - b.SupersededAt).TotalHours > 24)
            .ToList();
            
        return Result<List<SupersededBlock>>.Success(oldBlocks);
    }
}