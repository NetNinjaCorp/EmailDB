using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages system-wide metadata in the EmailDB system, maintaining critical pointers to system structures
/// and providing a centralized approach to system-wide configuration and state management.
/// </summary>
public class MetadataManager
{
    private readonly CacheManager cacheManager;
    private readonly object metadataLock = new object();

    /// <summary>
    /// Initializes a new instance of the MetadataManager class.
    /// </summary>
    /// <param name="cacheManager">The cache manager for efficient metadata retrieval.</param>
    public MetadataManager(CacheManager cacheManager)
    {
        this.cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
    }

    /// <summary>
    /// Initializes a new database file with proper structure.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> InitializeFileAsync()
    {
        try
        {
            // Create initial metadata content
            var metadata = new MetadataContent
            {
                WALOffset = -1,
                FolderTreeOffset = -1,
                SegmentOffsets = new Dictionary<string, long>(),
                OutdatedOffsets = new List<long>()
            };

            // Write initial metadata content
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Flags = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 0, // Special ID for metadata
                Payload = cacheManager.Serializer.Serialize(metadata)
            };

            var writeResult = await cacheManager.WriteBlockAsync(block);
            if (!writeResult.IsSuccess)
                return Result.Failure($"Failed to initialize file: {writeResult.Error}");

            // Create initial header content
            var header = new HeaderContent
            {
                FileVersion = 1,
                FirstMetadataOffset = writeResult.Value.Position,
                FirstFolderTreeOffset = -1,
                FirstCleanupOffset = -1
            };

            // Write header block
            var headerBlock = new Block
            {
                Version = 1,
                Type = BlockType.Metadata, // Header is stored as a special metadata block
                Flags = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 0, // Special ID for header
                Payload = cacheManager.Serializer.Serialize(header)
            };

            var headerResult = await cacheManager.WriteBlockAsync(headerBlock, 0);
            if (!headerResult.IsSuccess)
                return Result.Failure($"Failed to write header: {headerResult.Error}");

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Error initializing file: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the latest metadata content.
    /// </summary>
    /// <returns>The metadata content, or null if not found.</returns>
    public async Task<MetadataContent> GetMetadataAsync()
    {
        return await cacheManager.GetCachedMetadata();
    }

    /// <summary>
    /// Gets the folder tree content based on metadata reference.
    /// </summary>
    /// <returns>The folder tree content, or null if not found.</returns>
    public async Task<FolderTreeContent> GetFolderTreeAsync()
    {
        var metadata = await GetMetadataAsync();
        if (metadata == null || metadata.FolderTreeOffset == -1)
            return null;

        var folderTree = await cacheManager.GetCachedFolderTree();
        return folderTree;
    }

    /// <summary>
    /// Updates the folder tree offset in metadata.
    /// </summary>
    /// <param name="offset">The new folder tree offset.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> UpdateFolderTreeOffsetAsync(long offset)
    {
        if (offset < 0)
            return Result.Failure("Invalid offset value");

        try
        {
            await metadataLock.LockAsync(async () =>
            {
                var metadata = await GetMetadataAsync();
                if (metadata == null)
                    throw new InvalidOperationException("Metadata not found");

                metadata.FolderTreeOffset = offset;
                await WriteMetadataAsync(metadata);
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update folder tree offset: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the WAL offset in metadata.
    /// </summary>
    /// <param name="offset">The new WAL offset.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> UpdateWALOffsetAsync(long offset)
    {
        if (offset < 0)
            return Result.Failure("Invalid offset value");

        try
        {
            await metadataLock.LockAsync(async () =>
            {
                var metadata = await GetMetadataAsync();
                if (metadata == null)
                    throw new InvalidOperationException("Metadata not found");

                metadata.WALOffset = offset;
                await WriteMetadataAsync(metadata);
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update WAL offset: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds or updates a segment offset in metadata.
    /// </summary>
    /// <param name="segmentId">The segment ID.</param>
    /// <param name="offset">The segment offset.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> AddOrUpdateSegmentOffsetAsync(string segmentId, long offset)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return Result.Failure("Invalid segment ID");

        if (offset < 0)
            return Result.Failure("Invalid offset value");

        try
        {
            await metadataLock.LockAsync(async () =>
            {
                var metadata = await GetMetadataAsync();
                if (metadata == null)
                    throw new InvalidOperationException("Metadata not found");

                metadata.SegmentOffsets[segmentId] = offset;
                await WriteMetadataAsync(metadata);
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update segment offset: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a segment offset from metadata.
    /// </summary>
    /// <param name="segmentId">The segment ID to remove.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> RemoveSegmentOffsetAsync(string segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return Result.Failure("Invalid segment ID");

        try
        {
            await metadataLock.LockAsync(async () =>
            {
                var metadata = await GetMetadataAsync();
                if (metadata == null)
                    throw new InvalidOperationException("Metadata not found");

                if (metadata.SegmentOffsets.TryGetValue(segmentId, out var offset))
                {
                    metadata.SegmentOffsets.Remove(segmentId);
                    metadata.OutdatedOffsets.Add(offset);
                    await WriteMetadataAsync(metadata);
                }
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to remove segment offset: {ex.Message}");
        }
    }

    /// <summary>
    /// Marks a segment as outdated.
    /// </summary>
    /// <param name="segmentId">The segment ID to mark as outdated.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> MarkSegmentOutdatedAsync(string segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return Result.Failure("Invalid segment ID");

        try
        {
            await metadataLock.LockAsync(async () =>
            {
                var metadata = await GetMetadataAsync();
                if (metadata == null)
                    throw new InvalidOperationException("Metadata not found");

                if (metadata.SegmentOffsets.TryGetValue(segmentId, out var offset))
                {
                    metadata.OutdatedOffsets.Add(offset);
                    await WriteMetadataAsync(metadata);
                }
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to mark segment as outdated: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleans up outdated segments from metadata.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> CleanupOutdatedSegmentsAsync()
    {
        try
        {
            await metadataLock.LockAsync(async () =>
            {
                var metadata = await GetMetadataAsync();
                if (metadata == null)
                    throw new InvalidOperationException("Metadata not found");

                var validOffsets = new HashSet<long>(metadata.SegmentOffsets.Values);
                metadata.OutdatedOffsets.RemoveAll(offset => validOffsets.Contains(offset));
                await WriteMetadataAsync(metadata);
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to clean up outdated segments: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a list of outdated segment offsets.
    /// </summary>
    /// <returns>A list of outdated segment offsets.</returns>
    public async Task<List<long>> GetOutdatedSegmentOffsetsAsync()
    {
        var metadata = await GetMetadataAsync();
        return metadata?.OutdatedOffsets ?? new List<long>();
    }

    /// <summary>
    /// Gets all segment offsets.
    /// </summary>
    /// <returns>A dictionary of segment IDs to offsets.</returns>
    public async Task<Dictionary<string, long>> GetAllSegmentOffsetsAsync()
    {
        var metadata = await GetMetadataAsync();
        return metadata?.SegmentOffsets ?? new Dictionary<string, long>();
    }

    /// <summary>
    /// Writes metadata content to storage.
    /// </summary>
    /// <param name="metadata">The metadata content to write.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task WriteMetadataAsync(MetadataContent metadata)
    {
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockId = 0, // Special ID for metadata
            Payload = cacheManager.Serializer.Serialize(metadata)
        };

        var header = await cacheManager.LoadHeaderContent();
        var writeResult = await cacheManager.WriteBlockAsync(block);
        if (!writeResult.IsSuccess)
            throw new IOException($"Failed to write metadata: {writeResult.Error}");

        // Update header with new metadata offset
        header.FirstMetadataOffset = writeResult.Value.Position;
        var headerBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata, // Header is stored as a special metadata block
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockId = 0, // Special ID for header
            Payload = cacheManager.Serializer.Serialize(header)
        };

        var headerResult = await cacheManager.WriteBlockAsync(headerBlock, 0);
        if (!headerResult.IsSuccess)
            throw new IOException($"Failed to update header: {headerResult.Error}");

        // Invalidate cache to ensure fresh read
        cacheManager.InvalidateMetadataCache();
    }
}

// Extension method to provide async locking
public static class LockExtensions
{
    public static async Task LockAsync(this object lockObject, Func<Task> action)
    {
        bool lockTaken = false;
        try
        {
            Monitor.Enter(lockObject, ref lockTaken);
            await action();
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(lockObject);
        }
    }
}