# RawBlockManager

## Overview

The `RawBlockManager` is the foundation of the EmailDB storage system, providing low-level block storage operations with robust error handling and data integrity guarantees.

## Responsibilities

The `RawBlockManager` is responsible for:

1. **Raw Block I/O**:
   - Writing blocks to storage with proper headers, footers, and checksums
   - Reading blocks from storage with integrity verification
   - Managing block locations and file positions

2. **Data Integrity**:
   - Generating checksums for headers and payloads
   - Verifying checksums during read operations
   - Ensuring block boundaries are respected
   - Handling corrupted or incomplete blocks gracefully

3. **Concurrency Control**:
   - Providing thread-safe access to the underlying storage file
   - Using reader-writer locks to maximize throughput
   - Ensuring atomic write operations

4. **Block Structure**:
   - Implementing the block format with magic numbers, version info, timestamps
   - Managing block IDs and consistency
   - Supporting various block types through the BlockType enum

5. **File Management**:
   - Opening and maintaining the storage file
   - Supporting file scanning and recovery
   - Finding blocks by magic bytes and positions

## Block Format

Each block follows this structure:

```
┌─────────────────────────────┐
│ HEADER_MAGIC     (8 bytes)  │
│ Version          (2 bytes)  │
│ BlockType        (1 byte)   │
│ Flags            (1 byte)   │
│ Timestamp        (8 bytes)  │
│ BlockId          (8 bytes)  │
│ PayloadLength    (8 bytes)  │
├─────────────────────────────┤
│ Header Checksum  (4 bytes)  │
├─────────────────────────────┤
│ Payload          (variable) │
├─────────────────────────────┤
│ Payload Checksum (4 bytes)  │
├─────────────────────────────┤
│ FOOTER_MAGIC     (8 bytes)  │
│ Total Block Length (8 bytes)│
└─────────────────────────────┘
```

## Key Methods

- `WriteBlockAsync(Block block, CancellationToken)`: Writes a block to storage
- `ReadBlockAsync(long blockId, CancellationToken)`: Reads a block by ID
- `GetBlockLocations()`: Returns all tracked block locations
- `ScanFile()`: Scans the file to identify valid blocks
- `CompactAsync()`: Compacts the file by removing outdated blocks

## Usage Guidelines

### Best Practices

1. **Result Pattern**: All operations return a `Result<T>` to properly handle errors
2. **Async Operations**: Use async methods for I/O-bound operations
3. **Checksums**: Always verify checksums when reading blocks
4. **Error Handling**: Handle read/write errors gracefully, especially for recovery
5. **Concurrency**: Use appropriate locks for concurrent access

### Avoiding Common Pitfalls

1. **Avoid Direct File Access**: Don't bypass RawBlockManager to access the file directly
2. **Handle Disposal**: Always dispose properly to release file handles
3. **Check Results**: Always check IsSuccess before using results
4. **Mind Block Size**: Be aware of maximum block size limitations
5. **Thread Safety**: Remember that block locations are shared resources

## Integration with Higher Layers

The `RawBlockManager` interacts primarily with:

- **CacheManager**: For efficient block caching and retrieval
- **Testing components**: For validating data integrity

It should not directly interact with higher-level components like `FolderManager` or `EmailManager`.

## Error Handling

All operations follow the Result pattern:

```csharp
var result = await blockManager.ReadBlockAsync(blockId);
if (result.IsSuccess)
{
    var block = result.Value;
    // Process the block...
}
else
{
    // Handle the error...
    string errorMessage = result.Error;
}
```

## Thread Safety

The `RawBlockManager` uses an `AsyncReaderWriterLock` to ensure thread safety. Multiple readers can access blocks simultaneously, while writers receive exclusive access.
