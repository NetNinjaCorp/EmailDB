using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace EmailDB.Format;

/// <summary>
/// Manages segment storage operations, providing methods for reading, writing, and managing 
/// content segments within the EmailDB storage system.
/// </summary>
public class SegmentManager
{
    private readonly CacheManager cacheManager;
    private readonly MetadataManager metadataManager;
    private readonly object segmentLock = new object();

    /// <summary>
    /// Initializes a new instance of the SegmentManager class.
    /// </summary>
    /// <param name="cacheManager">The cache manager for efficient segment caching and retrieval.</param>
    /// <param name="metadataManager">The metadata manager for system-wide metadata operations.</param>
    public SegmentManager(CacheManager cacheManager, MetadataManager metadataManager)
    {
        this.cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        this.metadataManager = metadataManager ?? throw new ArgumentNullException(nameof(metadataManager));
    }

    /// <summary>
    /// Gets a segment by its ID.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to retrieve.</param>
    /// <returns>The segment content, or null if not found.</returns>
    public async Task<SegmentContent> GetSegmentAsync(long segmentId)
    {
        // Use the string version of segmentId for cache key
        var segment = await cacheManager.GetSegmentAsync(segmentId);
        return segment;
    }

    /// <summary>
    /// Gets a segment by its string identifier.
    /// </summary>
    /// <param name="segmentIdStr">The string identifier of the segment to retrieve.</param>
    /// <returns>The segment content, or null if not found.</returns>
    public async Task<SegmentContent> GetSegmentAsync(string segmentIdStr)
    {
        if (!long.TryParse(segmentIdStr, out var segmentId))
            return null;

        return await GetSegmentAsync(segmentId);
    }

    /// <summary>
    /// Writes a segment to storage.
    /// </summary>
    /// <param name="segment">The segment content to write.</param>
    /// <returns>The offset where the segment was written.</returns>
    public async Task<long> WriteSegmentAsync(SegmentContent segment)
    {
        if (segment == null)
            throw new ArgumentNullException(nameof(segment));

        try
        {
            // Ensure the segment has metadata
            if (segment.Metadata == null)
                segment.Metadata = new Dictionary<string, string>();

            // Update segment timestamp
            segment.SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Write segment using the cache manager
            var offset = await cacheManager.UpdateSegment(segment.SegmentId.ToString(), segment);

            // Update metadata with the new segment offset
            await metadataManager.AddOrUpdateSegmentOffsetAsync(segment.SegmentId.ToString(), offset);

            return offset;
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to write segment {segment.SegmentId}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a new segment with the provided data.
    /// </summary>
    /// <param name="data">The data to store in the segment.</param>
    /// <param name="metadata">Optional metadata for the segment.</param>
    /// <returns>The created segment content.</returns>
    public async Task<SegmentContent> CreateSegmentAsync(byte[] data, Dictionary<string, string> metadata = null)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        // Generate a new segment ID
        var segmentId = BlockIdGenerator.Instance.GetNextSegmentId();

        // Create a new segment
        var segment = new SegmentContent
        {
            SegmentId = segmentId,
            SegmentData = data,
            ContentLength = data.Length,
            SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Version = 1,
            Metadata = metadata ?? new Dictionary<string, string>()
        };

        // Write the segment
        await WriteSegmentAsync(segment);

        return segment;
    }

    /// <summary>
    /// Deletes a segment by its ID.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> DeleteSegmentAsync(long segmentId)
    {
        try
        {
            // Mark the segment as outdated in metadata
            await metadataManager.MarkSegmentOutdatedAsync(segmentId.ToString());

            // Invalidate cache
            cacheManager.InvalidateCache();

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to delete segment {segmentId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing segment with new data.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to update.</param>
    /// <param name="data">The new data for the segment.</param>
    /// <param name="updateMetadata">Optional metadata updates.</param>
    /// <returns>The updated segment content.</returns>
    public async Task<SegmentContent> UpdateSegmentAsync(long segmentId, byte[] data, Dictionary<string, string> updateMetadata = null)
    {
        var segment = await GetSegmentAsync(segmentId);
        if (segment == null)
            throw new KeyNotFoundException($"Segment {segmentId} not found");

        // Update segment data and increment version
        segment.SegmentData = data ?? throw new ArgumentNullException(nameof(data));
        segment.ContentLength = data.Length;
        segment.SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        segment.Version++;

        // Update metadata if provided
        if (updateMetadata != null)
        {
            foreach (var kvp in updateMetadata)
            {
                segment.Metadata[kvp.Key] = kvp.Value;
            }
        }

        // Write the updated segment
        await WriteSegmentAsync(segment);

        return segment;
    }

    /// <summary>
    /// Checks if a segment is outdated.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to check.</param>
    /// <returns>True if the segment is outdated, false otherwise.</returns>
    public async Task<bool> IsSegmentOutdatedAsync(long segmentId)
    {
        var outdatedOffsets = await metadataManager.GetOutdatedSegmentOffsetsAsync();
        var allSegmentOffsets = await metadataManager.GetAllSegmentOffsetsAsync();

        if (allSegmentOffsets.TryGetValue(segmentId.ToString(), out var offset))
        {
            return outdatedOffsets.Contains(offset);
        }

        // If segment doesn't exist, it's not outdated
        return false;
    }

    /// <summary>
    /// Gets all segments.
    /// </summary>
    /// <returns>A dictionary mapping segment IDs to segment content.</returns>
    public async Task<Dictionary<string, SegmentContent>> GetAllSegmentsAsync()
    {
        var result = new Dictionary<string, SegmentContent>();
        var allSegmentOffsets = await metadataManager.GetAllSegmentOffsetsAsync();
        var outdatedOffsets = await metadataManager.GetOutdatedSegmentOffsetsAsync();

        foreach (var kvp in allSegmentOffsets)
        {
            // Skip outdated segments
            if (outdatedOffsets.Contains(kvp.Value))
                continue;

            if (long.TryParse(kvp.Key, out var segmentId))
            {
                var segment = await GetSegmentAsync(segmentId);
                if (segment != null)
                {
                    result[kvp.Key] = segment;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the segment offset by segment ID.
    /// </summary>
    /// <param name="segmentId">The ID of the segment.</param>
    /// <returns>The offset of the segment, or -1 if not found.</returns>
    public async Task<long> GetSegmentOffsetAsync(long segmentId)
    {
        var allSegmentOffsets = await metadataManager.GetAllSegmentOffsetsAsync();
        return allSegmentOffsets.TryGetValue(segmentId.ToString(), out var offset) ? offset : -1;
    }

    /// <summary>
    /// Cleans up outdated segments.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> CleanupOutdatedSegmentsAsync()
    {
        try
        {
            await metadataManager.CleanupOutdatedSegmentsAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to clean up outdated segments: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes segment data from the cache.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to remove from cache.</param>
    public void InvalidateSegmentCache(long segmentId)
    {
        // This could be a more targeted cache invalidation in the future
        cacheManager.InvalidateCache();
    }

    /// <summary>
    /// Updates a segment's metadata without changing its data.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to update.</param>
    /// <param name="metadata">The new metadata to apply.</param>
    /// <returns>The updated segment content.</returns>
    public async Task<SegmentContent> UpdateSegmentMetadataAsync(long segmentId, Dictionary<string, string> metadata)
    {
        if (metadata == null)
            throw new ArgumentNullException(nameof(metadata));

        var segment = await GetSegmentAsync(segmentId);
        if (segment == null)
            throw new KeyNotFoundException($"Segment {segmentId} not found");

        // Update metadata and increment version
        segment.Metadata = metadata;
        segment.SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        segment.Version++;

        // Write the updated segment
        await WriteSegmentAsync(segment);

        return segment;
    }

    /// <summary>
    /// Adds or updates a specific metadata entry for a segment.
    /// </summary>
    /// <param name="segmentId">The ID of the segment to update.</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The metadata value.</param>
    /// <returns>The updated segment content.</returns>
    public async Task<SegmentContent> AddOrUpdateSegmentMetadataItemAsync(long segmentId, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Metadata key cannot be null or empty", nameof(key));

        var segment = await GetSegmentAsync(segmentId);
        if (segment == null)
            throw new KeyNotFoundException($"Segment {segmentId} not found");

        // Update specific metadata entry
        segment.Metadata[key] = value;
        segment.SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        segment.Version++;

        // Write the updated segment
        await WriteSegmentAsync(segment);

        return segment;
    }
}