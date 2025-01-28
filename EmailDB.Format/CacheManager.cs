using EmailDB.Format.Models;
using System.Collections.Concurrent;

namespace EmailDB.Format;

public class CacheManager : IDisposable
{
    private readonly BlockManager blockManager;
    private volatile HeaderContent cachedHeader;
    private readonly ConcurrentDictionary<string, (long Offset, FolderContent Content, DateTime LastAccess)> folderCache;
    private readonly ConcurrentDictionary<string, (MetadataContent Content, DateTime LastAccess)> metadataCache;
    private volatile FolderTreeContent cachedFolderTree;
    private readonly ReaderWriterLockSlim cacheLock;
    private readonly int maxCacheSize;
    private readonly TimeSpan cacheTimeout;
    private readonly Timer cacheCleanupTimer;
    private bool isDisposed;

    public CacheManager(BlockManager blockManager, int maxCacheSize = 1000, TimeSpan? cacheTimeout = null)
    {
        this.blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        this.maxCacheSize = maxCacheSize;
        this.cacheTimeout = cacheTimeout ?? TimeSpan.FromMinutes(30);

        folderCache = new ConcurrentDictionary<string, (long, FolderContent, DateTime)>();
        metadataCache = new ConcurrentDictionary<string, (MetadataContent, DateTime)>();
        cacheLock = new ReaderWriterLockSlim();

        // Start periodic cache cleanup
        cacheCleanupTimer = new Timer(CleanupCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        LoadHeaderContent();
    }

    public void LoadHeaderContent()
    {
        try
        {
            cacheLock.EnterWriteLock();
            try
            {
                var headerBlock = blockManager.ReadBlock(0);
                if (headerBlock?.Content is HeaderContent header)
                {
                    ValidateHeader(header);
                    cachedHeader = header;
                }
                else
                {
                    throw new InvalidDataException("Invalid or missing header block");
                }
            }
            finally
            {
                cacheLock.ExitWriteLock();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load header content", ex);
        }
    }

    private void ValidateHeader(HeaderContent header)
    {
        if (header.FileVersion <= 0)
            throw new InvalidDataException("Invalid file version in header");

        if (header.FirstMetadataOffset < -1)
            throw new InvalidDataException("Invalid metadata offset in header");

        if (header.FirstFolderTreeOffset < -1)
            throw new InvalidDataException("Invalid folder tree offset in header");

        if (header.FirstCleanupOffset < -1)
            throw new InvalidDataException("Invalid cleanup offset in header");
    }

    public HeaderContent GetHeader()
    {
        ThrowIfDisposed();
        cacheLock.EnterReadLock();
        try
        {
            return cachedHeader ?? throw new InvalidOperationException("Header not loaded");
        }
        finally
        {
            cacheLock.ExitReadLock();
        }
    }

    public void UpdateHeader(HeaderContent header)
    {
        ThrowIfDisposed();
        if (header == null) throw new ArgumentNullException(nameof(header));

        ValidateHeader(header);

        cacheLock.EnterWriteLock();
        try
        {
            cachedHeader = header;
            var headerBlock = new Block
            {
                Header = new BlockHeader
                {
                    Type = BlockType.Header,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Version = 1
                },
                Content = header
            };
            blockManager.WriteBlock(headerBlock, 0);
        }
        finally
        {
            cacheLock.ExitWriteLock();
        }
    }

    public FolderContent GetCachedFolder(string folderName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be null or empty", nameof(folderName));

        if (folderCache.TryGetValue(folderName, out var cachedFolder))
        {
            try
            {
                var block = blockManager.ReadBlock(cachedFolder.Offset);
                if (block?.Content is FolderContent folder && folder.Name == folderName)
                {
                    // Update last access time
                    folderCache.TryUpdate(folderName,
                        (cachedFolder.Offset, folder, DateTime.UtcNow),
                        cachedFolder);
                    return folder;
                }
            }
            catch (Exception ex)
            {
                // Log error if needed
                folderCache.TryRemove(folderName, out _);
            }
        }
        return null;
    }

    public void CacheFolder(string folderName, long offset, FolderContent folder)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be null or empty", nameof(folderName));
        if (folder == null)
            throw new ArgumentNullException(nameof(folder));
        if (offset < 0)
            throw new ArgumentException("Offset cannot be negative", nameof(offset));

        // Implement LRU eviction if cache is full
        if (folderCache.Count >= maxCacheSize)
        {
            var oldestEntry = folderCache
                .OrderBy(x => x.Value.LastAccess)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(oldestEntry.Key))
            {
                folderCache.TryRemove(oldestEntry.Key, out _);
            }
        }

        folderCache.AddOrUpdate(folderName,
            (offset, folder, DateTime.UtcNow),
            (_, _) => (offset, folder, DateTime.UtcNow));
    }

    public FolderTreeContent GetCachedFolderTree()
    {
        ThrowIfDisposed();
        cacheLock.EnterReadLock();
        try
        {
            if (cachedFolderTree != null)
            {
                return cachedFolderTree;
            }

            if (cachedHeader.FirstFolderTreeOffset != -1)
            {
                try
                {
                    var block = blockManager.ReadBlock(cachedHeader.FirstFolderTreeOffset);
                    if (block?.Content is FolderTreeContent tree)
                    {
                        cachedFolderTree = tree;
                        return tree;
                    }
                }
                catch (Exception ex)
                {
                    // Log error if needed
                }
            }
            return null;
        }
        finally
        {
            cacheLock.ExitReadLock();
        }
    }

    public void CacheFolderTree(FolderTreeContent tree)
    {
        ThrowIfDisposed();
        if (tree == null) throw new ArgumentNullException(nameof(tree));

        cacheLock.EnterWriteLock();
        try
        {
            cachedFolderTree = tree;
        }
        finally
        {
            cacheLock.ExitWriteLock();
        }
    }

    public MetadataContent GetCachedMetadata()
    {
        ThrowIfDisposed();
        if (cachedHeader.FirstMetadataOffset == -1)
        {
            return null;
        }

        var key = cachedHeader.FirstMetadataOffset.ToString();
        if (metadataCache.TryGetValue(key, out var cached))
        {
            metadataCache.TryUpdate(key,
                (cached.Content, DateTime.UtcNow),
                cached);
            return cached.Content;
        }

        try
        {
            var block = blockManager.ReadBlock(cachedHeader.FirstMetadataOffset);
            if (block?.Content is MetadataContent metadata)
            {
                metadataCache.TryAdd(key, (metadata, DateTime.UtcNow));
                return metadata;
            }
        }
        catch (Exception ex)
        {
            // Log error if needed
        }
        return null;
    }

    public void InvalidateCache()
    {
        ThrowIfDisposed();
        cacheLock.EnterWriteLock();
        try
        {
            folderCache.Clear();
            metadataCache.Clear();
            cachedFolderTree = null;
            LoadHeaderContent();
        }
        finally
        {
            cacheLock.ExitWriteLock();
        }
    }

    private void CleanupCache(object state)
    {
        if (isDisposed) return;

        var expirationTime = DateTime.UtcNow - cacheTimeout;

        // Clean up folder cache
        var expiredFolders = folderCache
            .Where(x => x.Value.LastAccess < expirationTime)
            .Select(x => x.Key)
            .ToList();

        foreach (var folder in expiredFolders)
        {
            folderCache.TryRemove(folder, out _);
        }

        // Clean up metadata cache
        var expiredMetadata = metadataCache
            .Where(x => x.Value.LastAccess < expirationTime)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expiredMetadata)
        {
            metadataCache.TryRemove(key, out _);
        }
    }

    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(CacheManager));
        }
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            isDisposed = true;
            cacheCleanupTimer?.Dispose();
            cacheLock?.Dispose();
            folderCache.Clear();
            metadataCache.Clear();
            cachedFolderTree = null;
            cachedHeader = null;
        }
    }
}