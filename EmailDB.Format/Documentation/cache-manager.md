# CacheManager

## Overview

The `CacheManager` provides an intelligent caching layer on top of the `RawBlockManager`, optimizing performance by reducing disk I/O operations and providing typed access to frequently used block content.

## Responsibilities

The `CacheManager` is responsible for:

1. **Block Caching**:
   - Maintaining in-memory caches for different content types
   - Implementing cache eviction policies (LRU, timeout-based)
   - Tracking access patterns to optimize cache efficiency

2. **Content Deserialization**:
   - Converting raw block payloads to typed content objects
   - Handling serialization format conversions
   - Providing strongly-typed access to content

3. **Cache Management**:
   - Limiting cache size to prevent memory issues
   - Cleaning up stale cache entries
   - Providing cache invalidation mechanisms

4. **Performance Optimization**:
   - Prioritizing frequently accessed content
   - Prefetching related content where appropriate
   - Batch loading for improved throughput

5. **Thread Safety**:
   - Ensuring concurrent cache access is safe
   - Using appropriate locking mechanisms
   - Maintaining cache consistency

## Cache Structure

The `CacheManager` maintains several specialized caches:

1. **Folder Cache**: Maps folder names to folder content
2. **Metadata Cache**: Caches system metadata blocks
3. **Header Cache**: Maintains the file header information
4. **FolderTree Cache**: Caches the folder hierarchy

Each cache employs:
- Concurrent collections for thread safety
- Timestamping for LRU eviction
- Size limits to prevent memory issues

## Key Methods

- `GetCachedFolder(string folderName)`: Retrieves a cached folder
- `GetCachedFolderTree()`: Retrieves the cached folder tree
- `GetCachedMetadata()`: Retrieves the cached metadata
- `GetSegmentAsync(long segmentId)`: Retrieves a segment by ID
- `InvalidateCache()`: Clears all caches
- `CacheFolder(...)`: Adds or updates a folder in the cache

## Usage Guidelines

### Best Practices

1. **Check for null**: Cache methods may return null if an item isn't cached
2. **Handle async properly**: Most methods are async and should be awaited
3. **Use invalidation**: Call InvalidateCache when necessary for consistency
4. **Set reasonable limits**: Configure appropriate cache size limits
5. **Handle cleanup**: Ensure proper disposal to prevent memory leaks

### Avoiding Common Pitfalls

1. **Don't bypass**: Never bypass CacheManager to access RawBlockManager directly
2. **Mind concurrency**: Be aware of concurrent access patterns
3. **Check cache hits**: Don't assume items are always cached
4. **Handle serialization errors**: Be prepared for deserialization failures
5. **Consider memory impact**: Very large caches can impact system performance

## Integration with Adjacent Layers

The `CacheManager` interacts with:

- **RawBlockManager**: To retrieve blocks when cache misses occur
- **MetadataManager**: To provide cached metadata for system operations
- **FolderManager**: To provide cached folder content

## Caching Strategy

The CacheManager employs these caching strategies:

1. **Time-based eviction**: Items unused for a configurable period are removed
2. **Size-limited caching**: The cache has a maximum item count
3. **Write-through caching**: Updates are written to storage and cache simultaneously
4. **Lazy loading**: Items are loaded into cache on first access

## Performance Considerations

- Cache hit rates should be monitored for optimal performance
- Adjust cache sizes based on memory availability and usage patterns
- Consider prefetching for predictable access patterns
- Use batch operations where possible to improve throughput

## Thread Safety

The `CacheManager` uses:
- ConcurrentDictionary for thread-safe collections
- ReaderWriterLockSlim for cache-wide operations
- Atomic operations for cache updates
