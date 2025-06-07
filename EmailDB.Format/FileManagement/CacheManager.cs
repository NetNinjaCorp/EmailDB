using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using System.Collections.Concurrent;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Provides an intelligent caching layer on top of the RawBlockManager,
/// optimizing performance by reducing disk I/O operations and providing
/// typed access to frequently used block content.
/// </summary>
public class CacheManager : IDisposable
{
    // Raw Block Manager for low-level operations
    private readonly RawBlockManager rawBlockManager;


    // Main index - by offset
    private readonly ConcurrentDictionary<long, BlockIndexEntry> blocksByOffset;

    // Secondary indices
    private readonly ConcurrentDictionary<long, BlockIndexEntry> blocksById;
    private readonly ConcurrentDictionary<string, BlockIndexEntry> blocksByKey;
    private readonly ConcurrentDictionary<BlockType, ConcurrentBag<BlockIndexEntry>> blocksByType;

    // Caches for different block types - Singular caches
    private HeaderContent cachedHeader;
    private FolderTreeContent cachedFolderTree;

    // Concurrent Cache Lock
    private readonly ReaderWriterLockSlim cacheLock;

    private readonly int maxCacheSize;
    private readonly TimeSpan cacheTimeout;
    private readonly Timer cacheCleanupTimer;
    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the CacheManager class.
    /// </summary>
    /// <param name="rawBlockManager">The underlying raw block manager.</param>
    /// <param name="serializer">The serializer for block content.</param>
    /// <param name="maxCacheSize">Maximum number of items to cache.</param>
    /// <param name="cacheTimeout">Time before cached items are expired.</param>
    public CacheManager(
        RawBlockManager rawBlockManager,
        iBlockContentSerializer serializer,
        int maxCacheSize = 1000,
        TimeSpan? cacheTimeout = null)
    {
        this.rawBlockManager = rawBlockManager ?? throw new ArgumentNullException(nameof(rawBlockManager));
        this.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.maxCacheSize = maxCacheSize;
        this.cacheTimeout = cacheTimeout ?? TimeSpan.FromMinutes(30);

        // Initialize the unified cache structure
        blocksByOffset = new ConcurrentDictionary<long, BlockIndexEntry>();
        blocksById = new ConcurrentDictionary<long, BlockIndexEntry>();
        blocksByKey = new ConcurrentDictionary<string, BlockIndexEntry>();
        blocksByType = new ConcurrentDictionary<BlockType, ConcurrentBag<BlockIndexEntry>>();

        cacheLock = new ReaderWriterLockSlim();

        // Start periodic cache cleanup
        cacheCleanupTimer = new Timer(CleanupCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        // Load initial header content
        LoadHeaderContent();
    }

    /// <summary>
    /// Loads the file header content from storage.
    /// </summary>
    public async Task<HeaderContent> LoadHeaderContent()
    {
        ThrowIfDisposed();

        cacheLock.EnterUpgradeableReadLock();
        try
        {
            if (cachedHeader != null)
                return cachedHeader;

            // Read the header block from position 0
            var headerResult = await rawBlockManager.ReadBlockAsync(0);
            if (headerResult.IsSuccess)
            {
                var block = headerResult.Value;
                if (block.Payload != null && block.Type == BlockType.Metadata)
                {
                    var headerContent = Serializer.Deserialize<HeaderContent>(block.Payload);

                    cacheLock.EnterWriteLock();
                    try
                    {
                        cachedHeader = headerContent;

                        // Cache the header in the unified cache
                        var entry = new BlockIndexEntry
                        {
                            BlockId = block.BlockId,
                            Offset = 0, // Header is always at offset 0
                            Type = BlockType.Metadata,
                            Content = headerContent,
                            LastAccess = DateTime.UtcNow,
                            Key = "header" // Special key for header
                        };

                        blocksByOffset[0] = entry;
                        blocksById[block.BlockId] = entry;
                        blocksByKey["header"] = entry;

                        AddToTypeIndex(entry);
                    }
                    finally
                    {
                        cacheLock.ExitWriteLock();
                    }

                    return headerContent;
                }
            }

            // If we can't find or read the header, create a default one
            var defaultHeader = new HeaderContent
            {
                FileVersion = 1,
                FirstMetadataOffset = -1,
                FirstFolderTreeOffset = -1,
                FirstCleanupOffset = -1
            };

            cacheLock.EnterWriteLock();
            try
            {
                cachedHeader = defaultHeader;

                // Cache the default header
                var entry = new BlockIndexEntry
                {
                    BlockId = 0, // Default ID for header
                    Offset = 0,
                    Type = BlockType.Metadata,
                    Content = defaultHeader,
                    LastAccess = DateTime.UtcNow,
                    Key = "header"
                };

                blocksByOffset[0] = entry;
                blocksById[0] = entry;
                blocksByKey["header"] = entry;

                AddToTypeIndex(entry);
            }
            finally
            {
                cacheLock.ExitWriteLock();
            }

            return defaultHeader;
        }
        finally
        {
            cacheLock.ExitUpgradeableReadLock();
        }
    }


    /// <summary>
    /// Gets a cached folder by name.
    /// </summary>
    /// <param name="folderName">Name of the folder to retrieve.</param>
    /// <returns>The folder content, or null if not found or cache miss.</returns>
    public async Task<FolderContent> GetCachedFolder(string folderName)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be null or empty", nameof(folderName));

        // Try to get from cache first
        if (blocksByKey.TryGetValue(folderName, out var entry))
        {
            // Update last access time
            entry.LastAccess = DateTime.UtcNow;
            return entry.Content as FolderContent;
        }

        // Get folder tree to find the folder's offset
        var folderTree = await GetCachedFolderTree();
        if (folderTree == null)
            return null;

        // Find folder ID by name
        if (!folderTree.FolderIDs.TryGetValue(folderName, out var folderId))
            return null;

        // Find offset by folder ID
        if (!folderTree.FolderOffsets.TryGetValue(folderId, out var folderOffset))
            return null;

        // Read the folder from storage
        var blockResult = await rawBlockManager.ReadBlockAsync(folderOffset);
        if (!blockResult.IsSuccess)
            return null;

        var block = blockResult.Value;
        if (block.Type != BlockType.Folder)
            return null;

        // Deserialize and cache
        try
        {
            var folder = Serializer.Deserialize<FolderContent>(block.Payload);
            if (folder != null)
            {
                // Cache the folder
                var newEntry = new BlockIndexEntry
                {
                    BlockId = block.BlockId,
                    Offset = folderOffset,
                    Type = BlockType.Folder,
                    Content = folder,
                    LastAccess = DateTime.UtcNow,
                    Key = folderName
                };

                blocksByOffset[folderOffset] = newEntry;
                blocksById[block.BlockId] = newEntry;
                blocksByKey[folderName] = newEntry;

                AddToTypeIndex(newEntry);

                return folder;
            }
        }
        catch (Exception)
        {
            // Log exception if needed
            // Failed to deserialize, don't cache
        }

        return null;
    }

    /// <summary>
    /// Gets the cached folder tree.
    /// </summary>
    /// <returns>The folder tree content, or null if not found or cache miss.</returns>
    public async Task<FolderTreeContent> GetCachedFolderTree()
    {
        ThrowIfDisposed();

        // Check cache for folder tree by special key
        if (blocksByKey.TryGetValue("folderTree", out var cachedEntry))
        {
            cachedEntry.LastAccess = DateTime.UtcNow;
            return cachedEntry.Content as FolderTreeContent;
        }

        // Get header to find folder tree offset
        var header = await LoadHeaderContent();
        if (header.FirstFolderTreeOffset == -1)
            return null;

        // Read folder tree from storage
        var blockResult = await rawBlockManager.ReadBlockAsync(header.FirstFolderTreeOffset);
        if (!blockResult.IsSuccess)
            return null;

        var block = blockResult.Value;
        if (block.Type != BlockType.FolderTree)
            return null;

        try
        {
            var folderTree = Serializer.Deserialize<FolderTreeContent>(block.Payload);
            if (folderTree != null)
            {
                // Cache the folder tree
                var newEntry = new BlockIndexEntry
                {
                    BlockId = block.BlockId,
                    Offset = header.FirstFolderTreeOffset,
                    Type = BlockType.FolderTree,
                    Content = folderTree,
                    LastAccess = DateTime.UtcNow,
                    Key = "folderTree" // Special key for folder tree
                };

                blocksByOffset[header.FirstFolderTreeOffset] = newEntry;
                blocksById[block.BlockId] = newEntry;
                blocksByKey["folderTree"] = newEntry;

                AddToTypeIndex(newEntry);

                return folderTree;
            }
        }
        catch (Exception)
        {
            // Log exception if needed
            // Failed to deserialize, don't cache
        }

        return null;
    }

    /// <summary>
    /// Caches a folder tree.
    /// </summary>
    public void CacheFolderTree(FolderTreeContent folderTree)
    {
        ThrowIfDisposed();

        if (folderTree == null)
            throw new ArgumentNullException(nameof(folderTree));

        cacheLock.EnterWriteLock();
        try
        {
            cachedFolderTree = folderTree;
        }
        finally
        {
            cacheLock.ExitWriteLock();
        }
    }

    /// Gets the cached metadata content.
    /// </summary>
    /// <returns>The metadata content, or null if not found or cache miss.</returns>
    public async Task<MetadataContent> GetCachedMetadata()
    {
        ThrowIfDisposed();

        // Get header to find metadata offset
        var header = await LoadHeaderContent();
        if (header.FirstMetadataOffset == -1)
            return null;

        var metadataKey = $"metadata:{header.FirstMetadataOffset}";

        // Try to get from cache first
        if (blocksByKey.TryGetValue(metadataKey, out var entry))
        {
            entry.LastAccess = DateTime.UtcNow;
            return entry.Content as MetadataContent;
        }

        // Read metadata from storage
        var blockResult = await rawBlockManager.ReadBlockAsync(header.FirstMetadataOffset);
        if (!blockResult.IsSuccess)
            return null;

        var block = blockResult.Value;
        if (block.Type != BlockType.Metadata)
            return null;

        try
        {
            var metadata = Serializer.Deserialize<MetadataContent>(block.Payload);
            if (metadata != null)
            {
                // Cache the metadata
                var newEntry = new BlockIndexEntry
                {
                    BlockId = block.BlockId,
                    Offset = header.FirstMetadataOffset,
                    Type = BlockType.Metadata,
                    Content = metadata,
                    LastAccess = DateTime.UtcNow,
                    Key = metadataKey
                };

                blocksByOffset[header.FirstMetadataOffset] = newEntry;
                blocksById[block.BlockId] = newEntry;
                blocksByKey[metadataKey] = newEntry;

                AddToTypeIndex(newEntry);

                return metadata;
            }
        }
        catch (Exception)
        {
            // Log exception if needed
            // Failed to deserialize, don't cache
        }

        return null;
    }

    /// <summary>
    /// Gets a cached segment by ID.
    /// </summary>
    /// <param name="segmentId">ID of the segment to retrieve.</param>
    /// <returns>The segment content, or null if not found or cache miss.</returns>
    public async Task<SegmentContent> GetSegmentAsync(long segmentId)
    {
        ThrowIfDisposed();

        string segmentKey = $"segment:{segmentId}";

        // Try to get from cache first
        if (blocksByKey.TryGetValue(segmentKey, out var entry))
        {
            entry.LastAccess = DateTime.UtcNow;
            return entry.Content as SegmentContent;
        }

        // Get metadata to find segment offset
        var metadata = await GetCachedMetadata();
        if (metadata == null)
            return null;

        var segmentOffsetKey = segmentId.ToString();
        if (!metadata.SegmentOffsets.TryGetValue(segmentOffsetKey, out var offset))
            return null;

        // Check if segment is outdated
        if (metadata.OutdatedOffsets.Contains(offset))
            return null;

        // Read segment from storage
        var blockResult = await rawBlockManager.ReadBlockAsync(offset);
        if (!blockResult.IsSuccess)
            return null;

        var block = blockResult.Value;
        if (block.Type != BlockType.Segment)
            return null;

        try
        {
            var segment = Serializer.Deserialize<SegmentContent>(block.Payload);
            if (segment != null)
            {
                // Cache the segment
                var newEntry = new BlockIndexEntry
                {
                    BlockId = block.BlockId,
                    Offset = offset,
                    Type = BlockType.Segment,
                    Content = segment,
                    LastAccess = DateTime.UtcNow,
                    Key = segmentKey
                };

                blocksByOffset[offset] = newEntry;
                blocksById[block.BlockId] = newEntry;
                blocksByKey[segmentKey] = newEntry;

                AddToTypeIndex(newEntry);

                return segment;
            }
        }
        catch (Exception)
        {
            // Log exception if needed
            // Failed to deserialize, don't cache
        }

        return null;
    }

    /// Reads a block using the underlying RawBlockManager.
    /// </summary>
    /// <param name="offset">The offset of the block.</param>
    /// <returns>The block, or null if not found or error.</returns>
    public async Task<Result<Block>> ReadBlockAsync(long offset)
    {
        ThrowIfDisposed();

        // Try to get from cache first if we have it
        if (blocksByOffset.TryGetValue(offset, out var entry))
        {
            entry.LastAccess = DateTime.UtcNow;

            // We need to create a Block from the cached entry content
            var block = new Block
            {
                BlockId = entry.BlockId,
                Type = entry.Type,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = Serializer.Serialize(entry.Content)
            };

            return Result<Block>.Success(block);
        }

        return await rawBlockManager.ReadBlockAsync(offset);
    }

    /// <summary>
    /// Writes a block using the underlying RawBlockManager.
    /// </summary>
    /// <param name="block">The block to write.</param>
    /// <param name="specificOffset">Optional specific offset to write at, or -1 to append.</param>
    /// <returns>The offset where the block was written.</returns>
    public async Task<Result<BlockLocation>> WriteBlockAsync(Block block, long specificOffset = -1)
    {
        ThrowIfDisposed();

        // Ensure the block has a valid ID
        block.EnsureBlockId();


        var result = specificOffset >= 0
            ? await rawBlockManager.WriteBlockAsync(block, CancellationToken.None)
            : await rawBlockManager.WriteBlockAsync(block, CancellationToken.None);

        if (result.IsSuccess)
        {
            var location = result.Value;

            // Try to deserialize and cache the content
            try
            {
                object content = null;
                string key = null;

                switch (block.Type)
                {
                    case BlockType.Metadata:
                        content = Serializer.Deserialize<MetadataContent>(block.Payload);
                        key = $"metadata:{location.Position}";
                        InvalidateMetadataCache(); // Clear old metadata
                        break;
                    case BlockType.FolderTree:
                        content = Serializer.Deserialize<FolderTreeContent>(block.Payload);
                        key = "folderTree";
                        RemoveFromCache("folderTree"); // Clear old folder tree
                        break;
                    case BlockType.Folder:
                        var folder = Serializer.Deserialize<FolderContent>(block.Payload);
                        content = folder;
                        key = folder.Name; // Folder path is the key
                        break;
                    case BlockType.Segment:
                        var segment = Serializer.Deserialize<SegmentContent>(block.Payload);
                        content = segment;
                        key = $"segment:{segment.SegmentId}";
                        break;
                }

                if (content != null && key != null)
                {
                    var entry = new BlockIndexEntry
                    {
                        BlockId = block.BlockId,
                        Offset = location.Position,
                        Type = block.Type,
                        Content = content,
                        LastAccess = DateTime.UtcNow,
                        Key = key
                    };

                    blocksByOffset[location.Position] = entry;
                    blocksById[block.BlockId] = entry;
                    blocksByKey[key] = entry;

                    AddToTypeIndex(entry);
                }
            }
            catch
            {
                // Failed to deserialize, don't cache
            }

            return result;
        }

        throw new IOException($"Failed to write block: {result.Error}");
    }


    /// <summary>
    /// Invalidates all cached content.
    /// </summary>
    public void InvalidateCache()
    {
        ThrowIfDisposed();

        cacheLock.EnterWriteLock();
        try
        {
            blocksByOffset.Clear();
            blocksById.Clear();
            blocksByKey.Clear();
            blocksByType.Clear();
            cachedHeader = null;
        }
        finally
        {
            cacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Invalidates the metadata cache.
    /// </summary>
    public void InvalidateMetadataCache()
    {
        ThrowIfDisposed();

        cacheLock.EnterWriteLock();
        try
        {
            // Remove all metadata entries
            var metadataKeys = blocksByKey.Keys
                .Where(k => k.StartsWith("metadata:"))
                .ToList();

            foreach (var key in metadataKeys)
            {
                if (blocksByKey.TryRemove(key, out var entry))
                {
                    blocksByOffset.TryRemove(entry.Offset, out _);
                    blocksById.TryRemove(entry.BlockId, out _);

                    if (blocksByType.TryGetValue(BlockType.Metadata, out var typeList))
                    {
                        // Create a new list without the removed entry
                        var newList = new ConcurrentBag<BlockIndexEntry>(
                            typeList.Where(e => e.Key != key));

                        blocksByType[BlockType.Metadata] = newList;
                    }
                }
            }
        }
        finally
        {
            cacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Cleans up expired cache entries.
    /// </summary>
    private void CleanupCache(object state)
    {
        if (isDisposed) return;

        var expirationTime = DateTime.UtcNow - cacheTimeout;

        // Check if cache size exceeds the limit
        if (blocksByOffset.Count > maxCacheSize)
        {
            var candidatesForEviction = blocksByOffset.Values
                .Where(e => e.LastAccess < expirationTime)
                .OrderBy(e => e.LastAccess)
                .Take(blocksByOffset.Count - maxCacheSize)
                .ToList();

            foreach (var entry in candidatesForEviction)
            {
                RemoveFromCache(entry.Key);
            }
        }
    }

    /// <summary>
    /// Helper method to remove an entry from all cache indices.
    /// </summary>
    private void RemoveFromCache(string key)
    {
        if (blocksByKey.TryRemove(key, out var entry))
        {
            blocksByOffset.TryRemove(entry.Offset, out _);
            blocksById.TryRemove(entry.BlockId, out _);

            if (blocksByType.TryGetValue(entry.Type, out var typeList))
            {
                // Create a new list without the removed entry
                var newList = new ConcurrentBag<BlockIndexEntry>(
                    typeList.Where(e => e.Key != key));

                blocksByType[entry.Type] = newList;
            }
        }
    }

    /// <summary>
    /// Helper method to add an entry to the type index.
    /// </summary>
    private void AddToTypeIndex(BlockIndexEntry entry)
    {
        if (!blocksByType.TryGetValue(entry.Type, out var typeList))
        {
            typeList = new ConcurrentBag<BlockIndexEntry>();
            blocksByType[entry.Type] = typeList;
        }

        // Remove old entries with the same key
        var newList = new ConcurrentBag<BlockIndexEntry>(
            typeList.Where(e => e.Key != entry.Key));

        newList.Add(entry);
        blocksByType[entry.Type] = newList;
    }



    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(CacheManager));
        }
    }

    public iBlockContentSerializer Serializer { get; }
    /// <summary>
    /// Disposes resources used by the CacheManager.
    /// </summary>
    public void Dispose()
    {
        if (!isDisposed)
        {
            isDisposed = true;
            cacheCleanupTimer?.Dispose();
            cacheLock?.Dispose();

            blocksByOffset.Clear();
            blocksById.Clear();
            blocksByKey.Clear();
            blocksByType.Clear();
            cachedHeader = null;
        }
    }


    // Write Folder to Cache and storage (Will Write at end of File) returns the offset
    internal async Task<long> UpdateFolder(string folderPath, FolderContent content)
    {
        try
        {
            // Create a block for the folder content
            // Get Existing Block by FolderPath
            if (blocksByKey.TryGetValue(folderPath, out var existingBlockIndex))
            {
                // Remove old entry from cache
                RemoveFromCache(folderPath);
            }
            Block block = null;

            if (existingBlockIndex == null)
            {
                block = new Block
                {
                    Type = BlockType.Folder,
                    BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.Folder),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Payload = Serializer.Serialize(content)
                };
            }
            else
            {
                var existingBlock = await ReadBlockAsync(existingBlockIndex.Offset);
                if (!existingBlock.IsSuccess)
                    throw new IOException($"Failed to read existing folder block: {existingBlock.Error}");
                block = existingBlock.Value;
            }
            // Update the existing block

            block.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();


            // Write the block to storage
            var result = await rawBlockManager.WriteBlockAsync(block);
            if (!result.IsSuccess)
                throw new IOException($"Failed to write folder block: {result.Error}");

            long offset = result.Value.Position;

            // Update all indices atomically
            var entry = new BlockIndexEntry
            {
                BlockId = block.BlockId,
                Offset = offset,
                Type = BlockType.Folder,
                Content = content,
                LastAccess = DateTime.UtcNow,
                Key = folderPath
            };

            // Remove old folder entry if exists
            RemoveFromCache(folderPath);

            // Update caches
            blocksByOffset[offset] = entry;
            blocksById[block.BlockId] = entry;
            blocksByKey[folderPath] = entry;

            AddToTypeIndex(entry);

            return offset;
        }
        catch (Exception ex)
        {
            throw new IOException($"Error updating folder: {ex.Message}", ex);
        }
    }

    // Write FolderTree to storage and update cache, returns the offset
    internal async Task<long> UpdateFolderTree(FolderTreeContent folderTree)
    {
        try
        {
            // Create a block for the folder tree content
            var block = new Block
            {
                Type = BlockType.FolderTree,
                BlockId = 2, // Special ID for folder tree
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = Serializer.Serialize(folderTree)
            };

            // Write the block to storage
            var result = await rawBlockManager.WriteBlockAsync(block);
            if (!result.IsSuccess)
                throw new IOException($"Failed to write folder tree block: {result.Error}");

            long offset = result.Value.Position;

            // Update header with new folder tree offset
            var header = await LoadHeaderContent();
            header.FirstFolderTreeOffset = offset;

            // Write updated header
            var headerBlock = new Block
            {
                Type = BlockType.Metadata,
                BlockId = 0, // Special ID for header
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = Serializer.Serialize(header)
            };

            await rawBlockManager.WriteBlockAsync(headerBlock, OverrideLocation: 0);

            // Update all indices atomically
            var entry = new BlockIndexEntry
            {
                BlockId = block.BlockId,
                Offset = offset,
                Type = BlockType.FolderTree,
                Content = folderTree,
                LastAccess = DateTime.UtcNow,
                Key = "folderTree" // Special key for folder tree
            };

            // Remove old folder tree from cache
            RemoveFromCache("folderTree");

            // Update caches
            blocksByOffset[offset] = entry;
            blocksById[block.BlockId] = entry;
            blocksByKey["folderTree"] = entry;

            AddToTypeIndex(entry);

            // Cache the folder tree instance
            cachedFolderTree = folderTree;

            return offset;
        }
        catch (Exception ex)
        {
            throw new IOException($"Error updating folder tree: {ex.Message}", ex);
        }
    }

    // Write Metadata to storage and update cache
    internal async Task<long> UpdateMetadata(MetadataContent metadata)
    {
        try
        {
            // Create a block for the metadata content
            var block = new Block
            {
                Type = BlockType.Metadata,
                BlockId = 1, // Special ID for metadata (different from header)
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = Serializer.Serialize(metadata)
            };

            // Write the block to storage
            var result = await rawBlockManager.WriteBlockAsync(block);
            if (!result.IsSuccess)
                throw new IOException($"Failed to write metadata block: {result.Error}");

            long offset = result.Value.Position;

            // Update header with new metadata offset
            var header = await LoadHeaderContent();
            header.FirstMetadataOffset = offset;

            // Write updated header
            var headerBlock = new Block
            {
                Type = BlockType.Metadata,
                BlockId = 0, // Special ID for header
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = Serializer.Serialize(header)
            };

            await rawBlockManager.WriteBlockAsync(headerBlock, OverrideLocation: 0);

            // Update all indices atomically
            var metadataKey = $"metadata:{offset}";
            var entry = new BlockIndexEntry
            {
                BlockId = block.BlockId,
                Offset = offset,
                Type = BlockType.Metadata,
                Content = metadata,
                LastAccess = DateTime.UtcNow,
                Key = metadataKey
            };

            // Clear old metadata from cache
            InvalidateMetadataCache();

            // Update caches
            blocksByOffset[offset] = entry;
            blocksById[block.BlockId] = entry;
            blocksByKey[metadataKey] = entry;

            AddToTypeIndex(entry);

            return offset;
        }
        catch (Exception ex)
        {
            throw new IOException($"Error updating metadata: {ex.Message}", ex);
        }
    }


    // Write Segment to storage and update cache
    internal async Task<long> UpdateSegment(string segmentID, SegmentContent segment)
    {
        blocksByKey.TryGetValue(segmentID, out var existingBlockIndex);
        Block block = null;
        if (existingBlockIndex == null)
        {
            // Create a block for the segment content
            block = new Block
            {
                Type = BlockType.Segment,
                BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.Segment),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = Serializer.Serialize(segment)
            };
        }
        else
        {
            // Read the existing block
            var existingBlock = await ReadBlockAsync(existingBlockIndex.Offset);
            if (!existingBlock.IsSuccess)
                throw new IOException($"Failed to read existing segment block: {existingBlock.Error}");
            block = existingBlock.Value;
            // Remove old entry from cache
            RemoveFromCache(segmentID);
        }

        // Update the existing block
        block.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Write the block to storage
        var result = await rawBlockManager.WriteBlockAsync(block);
        if (!result.IsSuccess)
            throw new IOException($"Failed to write segment block: {result.Error}");

        long offset = result.Value.Position;

        // Update metadata with new segment offset
        var metadata = await GetCachedMetadata();
        if (metadata != null)
        {
            metadata.SegmentOffsets[segment.SegmentId.ToString()] = offset;
            await UpdateMetadata(metadata);
        }

        // Update all indices atomically
        string segmentKey = $"segment:{segment.SegmentId}";
        var entry = new BlockIndexEntry
        {
            BlockId = block.BlockId,
            Offset = offset,
            Type = BlockType.Segment,
            Content = segment,
            LastAccess = DateTime.UtcNow,
            Key = segmentKey
        };

        // Remove old segment from cache
        RemoveFromCache(segmentKey);

        // Update caches
        blocksByOffset[offset] = entry;
        blocksById[block.BlockId] = entry;
        blocksByKey[segmentKey] = entry;

        AddToTypeIndex(entry);

        return offset;
    }

    /// <summary>
    /// Initializes a new file with the core system blocks.
    /// </summary>
    public async Task<Result> InitializeNewFile()
    {
        try
        {
            // Create initial header content (will update after writing other blocks)
            var header = new HeaderContent
            {
                FileVersion = 1,
                FirstMetadataOffset = -1,
                FirstFolderTreeOffset = -1,
                FirstCleanupOffset = -1
            };

            // Create initial metadata content (will update after writing WAL and folder tree)
            var metadata = new MetadataContent
            {
                WALOffset = -1,
                FolderTreeOffset = -1,
                SegmentOffsets = new Dictionary<string, long>(),
                OutdatedOffsets = new List<long>()
            };

            // Create initial folder tree content
            var folderTree = new FolderTreeContent
            {
                RootFolderId = -1, // Set to -1 initially (no root folder yet)
                FolderHierarchy = new Dictionary<string, string>(),
                FolderIDs = new Dictionary<string, long>(),
                FolderOffsets = new Dictionary<long, long>()
            };

            // Create initial WAL content
            var walContent = new WALContent
            {
                NextWALOffset = -1,
                CategoryOffsets = new Dictionary<string, long>(),
                Entries = new Dictionary<string, List<WALEntry>>()
            };

            // Write header first (will update later)
            var headerBlock = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 0, // Special ID for header
                Payload = Serializer.Serialize(header)
            };

            var headerResult = await rawBlockManager.WriteBlockAsync(headerBlock, OverrideLocation: 0);
            if (!headerResult.IsSuccess)
                return Result.Failure($"Failed to write header block: {headerResult.Error}");

            // Write WAL block
            var walBlock = new Block
            {
                Version = 1,
                Type = BlockType.WAL,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 3, // Special ID for WAL
                Payload = Serializer.Serialize(walContent)
            };

            var walResult = await rawBlockManager.WriteBlockAsync(walBlock);
            if (!walResult.IsSuccess)
                return Result.Failure($"Failed to write WAL block: {walResult.Error}");

            long walOffset = walResult.Value.Position;

            // Write folder tree block
            var folderTreeBlock = new Block
            {
                Version = 1,
                Type = BlockType.FolderTree,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 2, // Special ID for folder tree
                Payload = Serializer.Serialize(folderTree)
            };

            var folderTreeResult = await rawBlockManager.WriteBlockAsync(folderTreeBlock);
            if (!folderTreeResult.IsSuccess)
                return Result.Failure($"Failed to write folder tree block: {folderTreeResult.Error}");

            long folderTreeOffset = folderTreeResult.Value.Position;

            // Update metadata with correct offsets
            metadata.WALOffset = walOffset;
            metadata.FolderTreeOffset = folderTreeOffset;

            // Write metadata block
            var metadataBlock = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 1, // Special ID for metadata
                Payload = Serializer.Serialize(metadata)
            };

            var metadataResult = await rawBlockManager.WriteBlockAsync(metadataBlock);
            if (!metadataResult.IsSuccess)
                return Result.Failure($"Failed to write metadata block: {metadataResult.Error}");

            long metadataOffset = metadataResult.Value.Position;

            // Update header with correct offsets
            header.FirstMetadataOffset = metadataOffset;
            header.FirstFolderTreeOffset = folderTreeOffset;
            header.FirstCleanupOffset = -1; // No cleanup block yet

            // Rewrite header with updated offsets
            var updatedHeaderBlock = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 0, // Special ID for header
                Payload = Serializer.Serialize(header)
            };

            var finalHeaderResult = await rawBlockManager.WriteBlockAsync(updatedHeaderBlock, OverrideLocation: 0);
            if (!finalHeaderResult.IsSuccess)
                return Result.Failure($"Failed to update header block: {finalHeaderResult.Error}");

            // Cache the header, metadata, and folder tree for immediate use
            var headerEntry = new BlockIndexEntry
            {
                BlockId = 0,
                Offset = 0,
                Type = BlockType.Metadata,
                Content = header,
                LastAccess = DateTime.UtcNow,
                Key = "header"
            };

            var metadataEntry = new BlockIndexEntry
            {
                BlockId = 1,
                Offset = metadataOffset,
                Type = BlockType.Metadata,
                Content = metadata,
                LastAccess = DateTime.UtcNow,
                Key = $"metadata:{metadataOffset}"
            };

            var folderTreeEntry = new BlockIndexEntry
            {
                BlockId = 2,
                Offset = folderTreeOffset,
                Type = BlockType.FolderTree,
                Content = folderTree,
                LastAccess = DateTime.UtcNow,
                Key = "folderTree"
            };

            // Update cache
            blocksByOffset[0] = headerEntry;
            blocksByOffset[metadataOffset] = metadataEntry;
            blocksByOffset[folderTreeOffset] = folderTreeEntry;

            blocksById[0] = headerEntry;
            blocksById[1] = metadataEntry;
            blocksById[2] = folderTreeEntry;

            blocksByKey["header"] = headerEntry;
            blocksByKey[$"metadata:{metadataOffset}"] = metadataEntry;
            blocksByKey["folderTree"] = folderTreeEntry;

            AddToTypeIndex(headerEntry);
            AddToTypeIndex(metadataEntry);
            AddToTypeIndex(folderTreeEntry);

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"File initialization failed: {ex.Message}");
        }
    }
}