using EmailDB.Format.Models;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace EmailDB.Format.Helpers;

/// <summary>
/// Provides unique BlockIDs for the EmailDB system, ensuring no overlaps between different types of blocks.
/// This class follows the singleton pattern to ensure system-wide uniqueness.
/// </summary>
public class BlockIdGenerator
{
    // System block IDs - these are fixed and reserved
    public const long HeaderBlockId = 0;
    public const long MetadataBlockId = 1;
    public const long FolderTreeBlockId = 2;
    public const long WalBlockId = 3;

    // ID ranges for different block types - allows 10 trillion IDs per type
    // These ranges are large enough to handle practical usage while keeping block types separate
    private const long BlockTypeRange = 10_000_000_000_000L; // 10 trillion

    // Base values for each block type (aligned to ranges)
    private const long FolderBaseId = 1 * BlockTypeRange;
    private const long SegmentBaseId = 2 * BlockTypeRange;
    private const long CleanupBaseId = 3 * BlockTypeRange;
    private const long CustomBlockBaseId = 4 * BlockTypeRange;

    // Current ID trackers for each block type
    private long folderIdCounter = 0;
    private long segmentIdCounter = 0;
    private long cleanupIdCounter = 0;
    private long customBlockIdCounter = 0;

    // Thread-safe dictionary to track last assigned IDs by category
    private readonly ConcurrentDictionary<string, long> customCategoryCounters = new();

    // Singleton instance
    private static BlockIdGenerator _instance;
    private static readonly object _lock = new object();

    /// <summary>
    /// Gets the singleton instance of the BlockIdGenerator.
    /// </summary>
    public static BlockIdGenerator Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new BlockIdGenerator();
                }
            }
            return _instance;
        }
    }

    // Private constructor to enforce singleton pattern
    private BlockIdGenerator() { }

    /// <summary>
    /// Returns a system block ID based on the block type.
    /// </summary>
    /// <param name="type">The type of system block.</param>
    /// <returns>A fixed system block ID.</returns>
    public long GetSystemBlockId(BlockType type)
    {
        return type switch
        {
            BlockType.Metadata => MetadataBlockId,
            BlockType.WAL => WalBlockId,
            BlockType.FolderTree => FolderTreeBlockId,
            _ => throw new ArgumentException($"Block type {type} is not a system block type")
        };
    }

    /// <summary>
    /// Gets the header block ID, which is a special case.
    /// </summary>
    public long GetHeaderBlockId() => HeaderBlockId;

    /// <summary>
    /// Generates a new unique folder block ID.
    /// </summary>
    /// <returns>A unique ID for a folder block.</returns>
    public long GetNextFolderId()
    {
        return FolderBaseId + Interlocked.Increment(ref folderIdCounter);
    }

    /// <summary>
    /// Generates a new unique segment block ID.
    /// </summary>
    /// <returns>A unique ID for a segment block.</returns>
    public long GetNextSegmentId()
    {
        return SegmentBaseId + Interlocked.Increment(ref segmentIdCounter);
    }

    /// <summary>
    /// Generates a new unique cleanup block ID.
    /// </summary>
    /// <returns>A unique ID for a cleanup block.</returns>
    public long GetNextCleanupId()
    {
        return CleanupBaseId + Interlocked.Increment(ref cleanupIdCounter);
    }

    /// <summary>
    /// Generates a new unique block ID for a custom block type.
    /// </summary>
    /// <returns>A unique ID for a custom block.</returns>
    public long GetNextCustomBlockId()
    {
        return CustomBlockBaseId + Interlocked.Increment(ref customBlockIdCounter);
    }

    /// <summary>
    /// Generates a new unique block ID based on the block type.
    /// </summary>
    /// <param name="blockType">The type of block.</param>
    /// <returns>A unique ID appropriate for the specified block type.</returns>
    public long GetNextBlockId(BlockType blockType)
    {
        return blockType switch
        {
            BlockType.Folder => GetNextFolderId(),
            BlockType.Segment => GetNextSegmentId(),
            BlockType.Cleanup => GetNextCleanupId(),
            BlockType.Metadata => MetadataBlockId,
            BlockType.WAL => WalBlockId,
            BlockType.FolderTree => FolderTreeBlockId,
            _ => GetNextCustomBlockId()
        };
    }

    /// <summary>
    /// Generates a new unique block ID for a specific custom category.
    /// </summary>
    /// <param name="category">The category name.</param>
    /// <returns>A unique ID for the specified category.</returns>
    public long GetNextCustomBlockId(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return GetNextCustomBlockId();

        // Get or initialize the counter for this category
        var counter = customCategoryCounters.AddOrUpdate(
            category,
            _ => 1,
            (_, current) => current + 1);

        // Use the custom block base with the category counter
        return CustomBlockBaseId + counter;
    }

    /// <summary>
    /// Registers an external ID as used to prevent future collisions.
    /// Use this when importing IDs from external sources.
    /// </summary>
    /// <param name="type">The block type.</param>
    /// <param name="id">The ID being registered.</param>
    public void RegisterExistingId(BlockType type, long id)
    {
        // Skip system block IDs
        if (id <= WalBlockId)
            return;

        switch (type)
        {
            case BlockType.Folder:
                // Extract the local counter value from the ID by removing the base
                var folderCounter = id - FolderBaseId;
                // Only update if the ID is in the correct range and higher than current
                if (folderCounter > 0 && folderCounter > folderIdCounter)
                {
                    Interlocked.CompareExchange(ref folderIdCounter, folderCounter, folderIdCounter);
                }
                break;

            case BlockType.Segment:
                var segmentCounter = id - SegmentBaseId;
                if (segmentCounter > 0 && segmentCounter > segmentIdCounter)
                {
                    Interlocked.CompareExchange(ref segmentIdCounter, segmentCounter, segmentIdCounter);
                }
                break;

            case BlockType.Cleanup:
                var cleanupCounter = id - CleanupBaseId;
                if (cleanupCounter > 0 && cleanupCounter > cleanupIdCounter)
                {
                    Interlocked.CompareExchange(ref cleanupIdCounter, cleanupCounter, cleanupIdCounter);
                }
                break;
        }
    }

    /// <summary>
    /// Determines the block type from a block ID.
    /// </summary>
    /// <param name="blockId">The block ID to check.</param>
    /// <returns>The block type corresponding to the ID range.</returns>
    public BlockType GetBlockTypeFromId(long blockId)
    {
        if (blockId == HeaderBlockId)
            return BlockType.Metadata; // Header is a special metadata block
        if (blockId == MetadataBlockId)
            return BlockType.Metadata;
        if (blockId == WalBlockId)
            return BlockType.WAL;
        if (blockId == FolderTreeBlockId)
            return BlockType.FolderTree;

        // Check ranges
        if (blockId >= FolderBaseId && blockId < SegmentBaseId)
            return BlockType.Folder;
        if (blockId >= SegmentBaseId && blockId < CleanupBaseId)
            return BlockType.Segment;
        if (blockId >= CleanupBaseId && blockId < CustomBlockBaseId)
            return BlockType.Cleanup;

        // Default to a generic type if unknown
        return BlockType.Metadata;
    }

    /// <summary>
    /// Verifies that a block ID is valid for its declared type.
    /// </summary>
    /// <param name="blockId">The block ID to verify.</param>
    /// <param name="declaredType">The declared block type.</param>
    /// <returns>True if the ID is valid for the declared type, false otherwise.</returns>
    public bool IsValidBlockIdForType(long blockId, BlockType declaredType)
    {
        // Special case for system blocks
        if (declaredType == BlockType.Metadata)
        {
            return blockId == HeaderBlockId || blockId == MetadataBlockId;
        }
        if (declaredType == BlockType.WAL)
        {
            return blockId == WalBlockId;
        }
        if (declaredType == BlockType.FolderTree)
        {
            return blockId == FolderTreeBlockId;
        }

        // For other block types, check that the ID falls within the correct range
        var actualType = GetBlockTypeFromId(blockId);
        return actualType == declaredType;
    }

    /// <summary>
    /// Resets all counters - primarily for testing purposes.
    /// </summary>
    public void Reset()
    {
        folderIdCounter = 0;
        segmentIdCounter = 0;
        cleanupIdCounter = 0;
        customBlockIdCounter = 0;
        customCategoryCounters.Clear();
    }
}