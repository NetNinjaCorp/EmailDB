# EmailDB Architecture Overview

## Core Architecture

EmailDB is built on a layered storage architecture that provides reliability, performance, and flexibility. The system is composed of several key components, with clear demarcation of duties between them.

```
┌─────────────────────┐
│     EmailManager    │ High-level email operations
└─────────┬───────────┘
          │
┌─────────┴───────────┐
│    FolderManager    │ Folder operations & hierarchy
└─────────┬───────────┘
          │
┌─────────┴───────────┐
│   MetadataManager   │ System metadata tracking
└─────────┬───────────┘
          │
┌─────────┴───────────┐
│    CacheManager     │ In-memory caching layer
└─────────┬───────────┘
          │
┌─────────┴───────────┐
│   RawBlockManager   │ Low-level block storage
└─────────────────────┘
```

## Layer Responsibilities

### RawBlockManager

The foundation of the storage system. This component:
- Writes and reads raw blocks to/from the storage file
- Ensures data integrity through checksums
- Handles block headers and footers
- Manages basic file I/O operations
- Provides access to blocks by ID or position

### CacheManager

Sits on top of RawBlockManager and:
- Caches frequently accessed blocks in memory
- Reduces disk I/O for common operations
- Manages cache size and eviction policies
- Provides typed access to block content
- Handles cache invalidation when data changes

### MetadataManager

Manages system-level metadata:
- Tracks the location of key system blocks (WAL, FolderTree, etc.)
- Handles metadata versioning and updates
- Provides structured access to system-wide configuration
- Maintains references to segments and their offsets
- Tracks outdated segments for cleanup

### FolderManager

Manages folder operations:
- Maintains the folder hierarchy
- Handles folder creation, deletion, and renaming
- Associates emails with folders
- Manages folder content serialization/deserialization
- Provides abstracted folder path handling

### EmailManager

Provides high-level email operations:
- Adds, retrieves, and deletes emails
- Indexes email content for searching
- Handles email caching and retrieval
- Manages email attachments and content
- Supports moving emails between folders

## Content Types

The system uses several content types that are serialized into block payloads:

- **FolderContent**: Information about a specific folder, including emails it contains
- **FolderTreeContent**: The overall folder hierarchy and relationships
- **MetadataContent**: System-wide metadata and configuration
- **SegmentContent**: Chunks of data for storage and indexing
- **WALContent**: Write-ahead log entries for data integrity

## Key Architectural Decisions

1. **Block-based Storage**: All data is stored in blocks with headers, checksums, and structured content
2. **Layered Responsibility**: Each component has clear, limited responsibilities
3. **Caching Strategy**: Frequently accessed data is cached for performance
4. **Content Abstraction**: Data is abstracted into specific content types
5. **Metadata Tracking**: System-wide metadata enables consistency and recovery

This architecture provides a solid foundation for building a reliable email storage and indexing system with clear separation of concerns.
