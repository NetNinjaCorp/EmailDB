using EmailDB.Format.Encryption;
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

    // Optional encryption provider for block-level encryption/decryption
    private readonly IBlockEncryptionProvider? encryptionProvider;


    // Main index - by offset
    private readonly ConcurrentDictionary<long, BlockIndexEntry> blocksByOffset;

    // Secondary indices
    private readonly ConcurrentDictionary<long, BlockIndexEntry> blocksById;
    private readonly ConcurrentDictionary<string, BlockIndexEntry> blocksByKey;
    private readonly ConcurrentDictionary<BlockType, ConcurrentBag<BlockIndexEntry>> blocksByType;

    // Cached file header
    private HeaderContent cachedHeader;

    // Async-safe cache lock (SemaphoreSlim supports both sync and async acquisition)
    private readonly SemaphoreSlim cacheLock;

    private readonly int maxCacheSize;
    private readonly TimeSpan cacheTimeout;
    private readonly Timer cacheCleanupTimer;
    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of the CacheManager class.
    /// </summary>
    /// <param name="rawBlockManager">The underlying raw block manager.</param>
    /// <param name="serializer">The serializer for block content.</param>
    /// <param name="encryptionProvider">Optional encryption provider for block-level encryption/decryption. Pass null to disable encryption.</param>
    /// <param name="maxCacheSize">Maximum number of items to cache.</param>
    /// <param name="cacheTimeout">Time before cached items are expired.</param>
    public CacheManager(
        RawBlockManager rawBlockManager,
        iBlockContentSerializer serializer,
        IBlockEncryptionProvider? encryptionProvider = null,
        int maxCacheSize = 1000,
        TimeSpan? cacheTimeout = null)
    {
        this.rawBlockManager = rawBlockManager ?? throw new ArgumentNullException(nameof(rawBlockManager));
        this.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.encryptionProvider = encryptionProvider;
        this.maxCacheSize = maxCacheSize;
        this.cacheTimeout = cacheTimeout ?? TimeSpan.FromMinutes(30);

        // Initialize the unified cache structure
        blocksByOffset = new ConcurrentDictionary<long, BlockIndexEntry>();
        blocksById = new ConcurrentDictionary<long, BlockIndexEntry>();
        blocksByKey = new ConcurrentDictionary<string, BlockIndexEntry>();
        blocksByType = new ConcurrentDictionary<BlockType, ConcurrentBag<BlockIndexEntry>>();

        cacheLock = new SemaphoreSlim(1, 1);

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

        await cacheLock.WaitAsync().ConfigureAwait(false);
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

            cachedHeader = defaultHeader;

            // Cache the default header
            var entry2 = new BlockIndexEntry
            {
                BlockId = 0, // Default ID for header
                Offset = 0,
                Type = BlockType.Metadata,
                Content = defaultHeader,
                LastAccess = DateTime.UtcNow,
                Key = "header"
            };

            blocksByOffset[0] = entry2;
            blocksById[0] = entry2;
            blocksByKey["header"] = entry2;

            AddToTypeIndex(entry2);

            return defaultHeader;
        }
        finally
        {
            cacheLock.Release();
        }
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

        var result = await rawBlockManager.ReadBlockAsync(offset);

        // Decrypt the payload on read if the block is encrypted and we have a provider
        if (result.IsSuccess && result.Value.IsEncrypted
            && encryptionProvider != null && encryptionProvider.IsEnabled)
        {
            var block = result.Value;
            block.Payload = encryptionProvider.Decrypt(
                block.Payload, block.Type, block.BlockId, block.KeyEpoch);
        }

        return result;
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

        // Encrypt the payload if an encryption provider is configured and enabled
        if (encryptionProvider != null && encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(block.Type))
        {
            block.Payload = encryptionProvider.Encrypt(block.Payload, block.Type, block.BlockId);
            block.Flags |= Block.FlagEncrypted;
            block.SetKeyEpoch((byte)encryptionProvider.ActiveEpoch);
        }

        var result = await rawBlockManager.WriteBlockAsync(block, CancellationToken.None);

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
            catch (Exception ex)
            {
                OnError?.Invoke($"Failed to cache block {block.BlockId} (type: {block.Type}) after write at offset {location.Position}", ex);
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

        cacheLock.Wait();
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
            cacheLock.Release();
        }
    }

    /// <summary>
    /// Invalidates the metadata cache.
    /// </summary>
    public void InvalidateMetadataCache()
    {
        ThrowIfDisposed();

        cacheLock.Wait();
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
            cacheLock.Release();
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
    /// The encryption provider used for block-level encryption/decryption, or null if encryption is disabled.
    /// </summary>
    public IBlockEncryptionProvider? EncryptionProvider => encryptionProvider;

    /// <summary>
    /// Optional error handler invoked when catch blocks handle exceptions.
    /// Receives a context string describing where the error occurred and the exception.
    /// </summary>
    public Action<string, Exception>? OnError { get; set; }
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
}
