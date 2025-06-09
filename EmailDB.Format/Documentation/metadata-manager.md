# MetadataManager

## Overview

The `MetadataManager` is responsible for managing system-wide metadata in the EmailDB system. It maintains critical pointers to system structures and provides a centralized approach to system-wide configuration and state management.

## Responsibilities

The `MetadataManager` is responsible for:

1. **Metadata Management**:
   - Storing and retrieving system-wide metadata
   - Tracking critical system offsets (FolderTree, WAL, etc.)
   - Maintaining segment metadata and relationships
   - Providing structured access to configuration settings

2. **File Structure Initialization**:
   - Initializing new database files with proper structure
   - Writing initial metadata blocks
   - Setting up the WAL (Write-Ahead Log) system
   - Establishing core data structures

3. **Segment Tracking**:
   - Maintaining a registry of segments and their offsets
   - Tracking outdated segments for cleanup
   - Providing segment location services
   - Supporting segment lifecycle management

4. **System Recovery Support**:
   - Facilitating system state recovery
   - Maintaining version information
   - Tracking critical system blocks

5. **Metadata Persistence**:
   - Serializing metadata to storage blocks
   - Reading and updating metadata atomically
   - Ensuring metadata consistency

## Metadata Structure

The primary metadata structure (`MetadataContent`) includes:

- **WALOffset**: Pointer to the Write-Ahead Log
- **FolderTreeOffset**: Pointer to the folder hierarchy
- **SegmentOffsets**: Dictionary mapping segment IDs to their offsets
- **OutdatedOffsets**: List of blocks marked for cleanup

## Key Methods

- `InitializeFile()`: Sets up a new file with proper structure
- `GetFolderTree()`: Retrieves the folder hierarchy structure
- `UpdateFolderTreeOffset(long offset)`: Updates the folder tree location
- `WriteMetadata()`: Persists metadata changes to storage
- `AddOrUpdateSegmentOffset(string segmentId, long offset)`: Updates segment locations

## Usage Guidelines

### Best Practices

1. **Atomic Updates**: Use locking for all metadata updates
2. **Ensure Consistency**: Keep metadata and actual data in sync
3. **Handle Recovery**: Support proper recovery processes
4. **Version Compatibility**: Maintain version information
5. **Minimize Frequency**: Batch metadata updates when possible

### Avoiding Common Pitfalls

1. **Race Conditions**: Always use locks for metadata updates
2. **Missing Initialization**: Ensure new files are properly initialized
3. **Stale Metadata**: Invalidate caches after metadata changes
4. **Lost Updates**: Use proper concurrency control
5. **Orphaned Resources**: Maintain proper references to avoid leaks

## Integration with Adjacent Layers

The `MetadataManager` interacts with:

- **BlockManager/RawBlockManager**: For reading/writing metadata blocks
- **FolderManager**: Providing folder structure location
- **CacheManager**: Coordinating with the caching system
- **SegmentManager**: Supporting segment tracking

## Initialization Process

The metadata initialization process includes:

1. Creating an initial metadata block with empty structures
2. Setting up the WAL system for transaction safety
3. Updating the metadata with WAL offset
4. Preparing for folder tree creation

## Metadata Updates

Metadata updates follow this pattern:

1. Acquire a lock to prevent concurrent modifications
2. Read the current metadata if needed
3. Make the necessary changes
4. Write the updated metadata
5. Release the lock

## Thread Safety

The `MetadataManager` ensures thread safety through:
- A dedicated metadata lock object
- Atomic read/update operations
- Proper lock acquisition and release
- Consistent locking order to prevent deadlocks

## Recovery Support

The metadata system supports recovery by:
- Maintaining consistent references to critical structures
- Supporting scanning and rebuild operations
- Tracking version information
- Providing access to historical metadata when needed
