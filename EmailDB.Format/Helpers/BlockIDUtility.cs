using EmailDB.Format.Models;
using System;

namespace EmailDB.Format.Helpers;

/// <summary>
/// Utility class for managing and ensuring proper block IDs across the application.
/// This class is designed to be used by CacheManager to ensure proper ID assignment.
/// </summary>
public static class BlockIdUtility
{
    /// <summary>
    /// Ensures a block has a valid ID based on its type. If the block already has an
    /// appropriate non-zero ID, it will be preserved. Otherwise, a new ID will be generated.
    /// </summary>
    /// <param name="block">The block to ensure has a valid ID.</param>
    /// <returns>The block with a valid ID assigned.</returns>
    public static Block EnsureBlockId(this Block block)
    {
        if(block == null)
        {
            throw new ArgumentNullException(nameof(block), "Block cannot be null");
        }
        // Special case: Header block should always have ID 0
        if (block.Type == BlockType.Metadata && block.BlockId == BlockIdGenerator.MetadataBlockId)
        {
            return block; // Header block ID is already correct
        }

        // If block ID is not set or not valid for its type, generate a new one
        if (block.BlockId == 0 || !BlockIdGenerator.Instance.IsValidBlockIdForType(block.BlockId, block.Type))
        {
            // Get or generate a proper ID for this block type
            block.BlockId = GetAppropriateBlockId(block.Type);
        }

        return block;
    }

    /// <summary>
    /// Gets the appropriate block ID for a given block type, either a fixed system ID
    /// or a newly generated one for dynamic types.
    /// </summary>
    /// <param name="blockType">The type of block.</param>
    /// <returns>An appropriate block ID for the given type.</returns>
    public static long GetAppropriateBlockId(BlockType blockType)
    {
        var idGenerator = BlockIdGenerator.Instance;

        // For system blocks, return fixed IDs
        switch (blockType)
        {
            case BlockType.Metadata:
                return idGenerator.GetSystemBlockId(BlockType.Metadata);

            case BlockType.WAL:
                return idGenerator.GetSystemBlockId(BlockType.WAL);

            case BlockType.FolderTree:
                return idGenerator.GetSystemBlockId(BlockType.FolderTree);
        }

        // For other block types, generate dynamic IDs
        return idGenerator.GetNextBlockId(blockType);
    }

    /// <summary>
    /// Gets a block ID for a specific folder by natural ID.
    /// </summary>
    /// <param name="folderId">The natural ID of the folder.</param>
    /// <returns>A block ID that incorporates the folder's natural ID.</returns>
    public static long GetFolderBlockId(long folderId)
    {
        // If this is a special system folder, preserve its ID
        if (folderId <= BlockIdGenerator.WalBlockId)
        {
            return folderId;
        }

        // Otherwise, return a proper folder block ID
        return BlockIdGenerator.Instance.GetNextFolderId();
    }

    /// <summary>
    /// Gets a block ID for a specific segment by natural ID.
    /// </summary>
    /// <param name="segmentId">The natural ID of the segment.</param>
    /// <returns>A block ID that incorporates the segment's natural ID.</returns>
    public static long GetSegmentBlockId(long segmentId)
    {
        return BlockIdGenerator.Instance.GetNextSegmentId();
    }

    /// <summary>
    /// Gets the header block ID.
    /// </summary>
    /// <returns>The header block ID.</returns>
    public static long GetHeaderBlockId()
    {
        return BlockIdGenerator.Instance.GetHeaderBlockId();
    }

    /// <summary>
    /// Gets the metadata block ID.
    /// </summary>
    /// <returns>The metadata block ID.</returns>
    public static long GetMetadataBlockId()
    {
        return BlockIdGenerator.Instance.GetSystemBlockId(BlockType.Metadata);
    }

    /// <summary>
    /// Gets the folder tree block ID.
    /// </summary>
    /// <returns>The folder tree block ID.</returns>
    public static long GetFolderTreeBlockId()
    {
        return BlockIdGenerator.Instance.GetSystemBlockId(BlockType.FolderTree);
    }

    /// <summary>
    /// Gets the WAL block ID.
    /// </summary>
    /// <returns>The WAL block ID.</returns>
    public static long GetWalBlockId()
    {
        return BlockIdGenerator.Instance.GetSystemBlockId(BlockType.WAL);
    }

    /// <summary>
    /// Registers an existing block ID to prevent future collisions.
    /// </summary>
    /// <param name="block">The block containing the ID to register.</param>
    public static void RegisterExistingBlockId(Block block)
    {
        BlockIdGenerator.Instance.RegisterExistingId(block.Type, block.BlockId);
    }
}