# FolderManager

## Overview

The `FolderManager` provides a comprehensive system for managing the folder hierarchy and email organization within the EmailDB system. It handles all folder-related operations, maintains the folder structure, and manages email-to-folder associations.

## Responsibilities

The `FolderManager` is responsible for:

1. **Folder Hierarchy Management**:
   - Creating, deleting, and renaming folders
   - Maintaining parent-child relationships
   - Supporting nested folder structures
   - Managing folder paths and naming

2. **Email Organization**:
   - Associating emails with folders
   - Moving emails between folders
   - Retrieving emails within folders
   - Tracking email-folder relationships

3. **Folder Content Persistence**:
   - Serializing folder information to storage
   - Reading folder data from storage
   - Managing folder content updates
   - Ensuring folder data consistency

4. **Path Handling**:
   - Validating folder paths
   - Converting between paths and folder IDs
   - Handling path separators and special characters
   - Supporting path-based operations

5. **Folder Cache Coordination**:
   - Working with CacheManager for folder data caching
   - Managing folder content invalidation
   - Optimizing folder access patterns
   - Supporting efficient folder lookups

## Folder Structure

The folder system uses these key structures:

1. **FolderTreeContent**: System-wide folder hierarchy containing:
   - Root folder ID
   - Folder hierarchy relationships
   - Mapping between folder paths and IDs
   - Folder offset locations

2. **FolderContent**: Individual folder information including:
   - Folder ID and parent ID
   - Folder name (full path)
   - List of email IDs in the folder

## Key Methods

- `CreateFolder(string path)`: Creates a new folder
- `DeleteFolder(string path, bool deleteContents)`: Deletes a folder
- `MoveFolder(string sourcePath, string targetPath)`: Relocates a folder
- `GetEmails(string path)`: Retrieves emails in a folder
- `AddEmailToFolder(string path, EmailHashedID emailId)`: Associates an email with a folder
- `RemoveEmailFromFolder(string path, EmailHashedID emailId)`: Removes an email from a folder
- `MoveEmail(EmailHashedID emailId, string sourcePath, string targetPath)`: Moves an email between folders
- `GetSubfolders(string path)`: Lists subfolders of a folder

## Usage Guidelines

### Best Practices

1. **Path Validation**: Always validate folder paths before operations
2. **Hierarchy Integrity**: Maintain proper parent-child relationships
3. **Transaction Safety**: Consider transaction boundaries for multi-step operations
4. **Cache Awareness**: Invalidate caches after structure changes
5. **Path Consistency**: Use consistent path separators and formatting

### Avoiding Common Pitfalls

1. **Circular References**: Prevent folder cycles in the hierarchy
2. **Orphaned Folders**: Maintain proper connections to parent folders
3. **Path Collisions**: Handle duplicate folder names appropriately
4. **Delete Cascades**: Be careful with recursive deletions
5. **Thread Safety**: Use proper synchronization for concurrent access

## Integration with Adjacent Layers

The `FolderManager` interacts with:

- **CacheManager**: For efficient folder content caching
- **MetadataManager**: For folder tree location management
- **EmailManager**: For email-folder relationship coordination
- **BlockManager**: For folder content persistence

## Path Handling

The FolderManager uses a path system with these characteristics:

- Backslash (`\`) as the path separator
- No trailing separators allowed
- Validation against invalid path characters
- Support for both absolute and relative paths
- Path component validation

## Performance Considerations

For optimal performance:

- Cache the folder tree for frequent operations
- Batch folder updates where possible
- Use path-based lookups consistently
- Consider folder structure depth and breadth
- Optimize access patterns for commonly used folders

## Thread Safety

The `FolderManager` ensures thread safety through:
- Locking for folder tree modifications
- Atomic folder write operations
- Coordinated cache invalidation
- Safe reference handling

## Folder Tree Persistence

The folder tree persistence process:

1. Creates a new FolderTreeContent block
2. Writes it to storage via BlockManager
3. Updates the metadata to point to the new block
4. Updates the cache with the new folder tree

## Folder Operations Flow

A typical folder operation follows this pattern:

1. Validate input paths/parameters
2. Retrieve the current folder tree
3. Perform the operation (create, move, delete, etc.)
4. Update relevant folder content blocks
5. Update the folder tree if necessary
6. Persist changes to storage
7. Update caches accordingly
