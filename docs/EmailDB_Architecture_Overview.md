# EmailDB Architecture Overview

## Introduction

EmailDB is a high-performance, append-only email storage system designed for reliability, searchability, and scalability. It uses a custom block-based file format that can store millions of emails in a single file while maintaining fast access times and data integrity.

## Core Design Principles

1. **Append-Only Architecture**: New data is always appended, never overwritten
2. **Block-Based Storage**: All data is stored in self-contained, checksummed blocks
3. **Layered Design**: Clear separation of concerns between storage, caching, and application logic
4. **Pluggable Serialization**: Support for multiple serialization formats (Protobuf, JSON, etc.)
5. **Thread-Safe Operations**: Concurrent read/write access with proper synchronization
6. **Data Integrity**: CRC32 checksums on all data blocks
7. **Recovery-Oriented**: Can recover from partial writes and corruption

## System Architecture

```
┌─────────────────────────────────────────────────────┐
│                 Application Layer                    │
├─────────────────────────────────────────────────────┤
│                  EmailManager                        │  High-level email operations
├─────────────────────────────────────────────────────┤
│     FolderManager    │    SegmentManager            │  Domain-specific managers
├─────────────────────────────────────────────────────┤
│                MetadataManager                       │  System metadata
├─────────────────────────────────────────────────────┤
│                 CacheManager                         │  In-memory caching
├─────────────────────────────────────────────────────┤
│                 BlockManager                         │  Block serialization
├─────────────────────────────────────────────────────┤
│               RawBlockManager                        │  Low-level block I/O
├─────────────────────────────────────────────────────┤
│                 File System                          │
└─────────────────────────────────────────────────────┘
```

## Component Responsibilities

### RawBlockManager
- **Purpose**: Low-level block I/O and file management
- **Key Features**:
  - Reads and writes complete blocks with headers/footers
  - Validates checksums and magic numbers
  - Maintains in-memory block location index
  - Handles file compaction
  - Thread-safe via AsyncReaderWriterLock

### BlockManager (Planned Enhancement)
- **Purpose**: Serialization layer between objects and blocks
- **Key Features**:
  - Implements IPayloadEncoding for pluggable serialization
  - Constructs blocks with proper headers
  - Manages different payload encodings (Protobuf, JSON, etc.)
  - Type-safe generic interface

### CacheManager
- **Purpose**: In-memory caching of frequently accessed blocks
- **Key Features**:
  - LRU eviction policy
  - Configurable cache size
  - Write-through or write-back modes
  - Cache invalidation on updates

### MetadataManager
- **Purpose**: System-wide metadata and configuration
- **Key Features**:
  - Tracks root folder tree location
  - Maintains segment mappings
  - Handles metadata versioning
  - Tracks blocks for cleanup

### FolderManager
- **Purpose**: Email folder hierarchy and organization
- **Key Features**:
  - Manages folder tree structure
  - Associates emails with folders
  - Handles folder operations (create, delete, rename)
  - Maintains folder content blocks

### SegmentManager
- **Purpose**: Data segmentation for large content
- **Key Features**:
  - Breaks large data into manageable segments
  - Handles segment allocation and tracking
  - Manages segment lifecycle

### EmailManager
- **Purpose**: High-level email storage and retrieval
- **Key Features**:
  - Email CRUD operations
  - Search functionality (via ZoneTree)
  - Email indexing
  - Attachment handling

## Data Flow

### Write Path
1. Application calls EmailManager.AddEmail()
2. EmailManager creates EmailHashedID and EnhancedEmailContent
3. Data is indexed in ZoneTree (which uses BlockManager)
4. BlockManager serializes content using configured encoding
5. RawBlockManager writes block to file with checksums
6. Block location is indexed in memory
7. Metadata is updated to reference new blocks

### Read Path
1. Application requests email by ID
2. EmailManager queries ZoneTree index
3. ZoneTree requests block from CacheManager
4. Cache hit: Return cached data
5. Cache miss: CacheManager requests from RawBlockManager
6. RawBlockManager reads block from file
7. Checksums are validated
8. Block is deserialized and cached
9. Email content is returned to application

## Block Format

See [Block Format Detailed Specification](./Block_Format_Detailed_Specification.md) for complete details.

Key aspects:
- 61-byte fixed overhead per block
- CRC32 checksums on header and payload
- Magic numbers for block boundary detection
- Support for multiple payload encodings
- Extensible block type system

## ZoneTree Integration

EmailDB uses ZoneTree for high-performance indexing:

- **Key-Value Store**: For email metadata and content
- **Full-Text Search**: For email search capabilities
- **LSM Architecture**: Optimized for write-heavy workloads
- **Custom Storage Provider**: All ZoneTree data stored as EmailDB blocks

## Concurrency Model

- **Multiple Readers**: Unlimited concurrent read operations
- **Single Writer**: Write operations are serialized
- **Lock Hierarchy**: RawBlockManager -> CacheManager -> Higher layers
- **Async Operations**: All I/O operations are async

## Error Handling

- **Result Pattern**: All operations return Result<T> for explicit error handling
- **Corruption Detection**: CRC32 validation on all reads
- **Recovery**: File scanning can rebuild block index
- **Graceful Degradation**: Partial failures don't compromise entire file

## Performance Characteristics

- **Write Performance**: Sequential append for optimal throughput
- **Read Performance**: O(1) block lookup via in-memory index
- **Memory Usage**: Configurable cache size + block index
- **Scalability**: Tested to multi-TB file sizes

## Future Enhancements

1. **Encryption**: Block-level encryption support
2. **Compression**: Per-block compression options
3. **Replication**: Multi-file replication support
4. **Cloud Storage**: S3-compatible backend
5. **Streaming API**: For very large emails/attachments

## Testing Strategy

See [Development TODO](./Development_TODO.md) for comprehensive testing plans including:
- Unit tests for every component
- Fuzz testing for corruption scenarios
- Performance benchmarks
- Cross-platform compatibility tests
- Stress tests with large files

This architecture provides a solid foundation for a production-ready email storage system with the flexibility to evolve into a general-purpose storage format.