# Content Models

## Overview

The EmailDB system uses a structured approach to content modeling, with specialized content types that are serialized into block payloads. These content models provide a clean separation between different data types and enable type-safe operations throughout the system.

## Content Type Hierarchy

Content types in the system fall into these major categories:

```
Block
├── BlockContent (abstract)
│   ├── FolderContent
│   ├── FolderTreeContent
│   ├── HeaderContent
│   ├── MetadataContent
│   ├── SegmentContent
│   └── WALContent
└── EmailContent
    ├── EmailHashedID
    └── EnhancedEmailContent
```

## Block Content Types

### MetadataContent

Stores system-wide metadata including:
- WAL offset
- Folder tree offset
- Segment offsets dictionary
- Outdated offsets list

Used by: `MetadataManager`

### FolderTreeContent

Represents the entire folder hierarchy:
- Root folder ID
- Folder hierarchy relationships (parent-child)
- Folder path to ID mapping
- Folder ID to offset mapping

Used by: `FolderManager`

### FolderContent

Represents a single folder:
- Folder ID
- Parent folder ID
- Folder name (path)
- List of email IDs contained in the folder

Used by: `FolderManager`

### HeaderContent

Stores file-level header information:
- File version
- First metadata offset
- First folder tree offset
- First cleanup offset

Used by: `StorageManager`

### SegmentContent

Stores a segment of data:
- Segment ID
- Segment data (byte array)
- File location information
- Content length
- Timestamp and version
- Metadata dictionary

Used by: `SegmentManager`

### WALContent

Stores write-ahead log information:
- WAL entries by category
- Next WAL offset
- Category offsets

Used by: Write-ahead log components

## Email Content Types

### EmailHashedID

A specialized identifier for emails:
- Based on SHA3-256 hash of email metadata
- Efficiently comparable and serializable
- Guaranteed uniqueness for emails
- Split into four 64-bit components

Used by: `EmailManager`, `FolderContent`

### EnhancedEmailContent

Stores comprehensive email information:
- Subject, from, to, cc, bcc fields
- Date metadata
- Text content
- Attachment information
- Raw email content

Used by: `EmailManager`

## Serialization

Content models are serialized into block payloads using a flexible serialization system:

1. `iBlockContentSerializer`: Interface for block content serialization
2. Serialization to byte arrays for storage
3. Deserialization from byte arrays to typed objects
4. Support for different serialization formats (protobuf, JSON, etc.)

## Block Types

The system uses an enumeration (`BlockType`) to identify block content types:

```csharp
public enum BlockType : byte
{
    Metadata = 0,
    WAL = 1,
    FolderTree = 2,
    Folder = 3,
    Segment = 4,
    Cleanup = 5
}
```

## Key Design Principles

1. **Clear Separation**: Each content type has a distinct purpose
2. **Type Safety**: Strong typing for content models
3. **Efficient Serialization**: Optimized binary serialization
4. **Reference Integrity**: Proper relationships between content types
5. **Versioning Support**: Content evolution over time

## Usage Guidelines

### Best Practices

1. **Content Immutability**: Treat content models as immutable when possible
2. **Proper Initialization**: Always fully initialize content models
3. **Validation**: Validate content before serialization
4. **Version Compatibility**: Handle content evolution gracefully
5. **Memory Efficiency**: Be mindful of large content objects

### Avoiding Common Pitfalls

1. **Type Confusion**: Use the right content type for each purpose
2. **Missing Fields**: Ensure all required fields are populated
3. **Serialization Errors**: Handle serialization edge cases
4. **Deep Copies**: Make proper copies when needed
5. **Circular References**: Avoid problematic circular references

## Extending Content Models

When adding new content types:

1. Define a clear purpose and responsibility
2. Add to the appropriate BlockType enum
3. Implement proper serialization support
4. Update the relevant managers
5. Consider backward compatibility
6. Add appropriate caching support

## Content Access Pattern

Content models are typically accessed through this pattern:

1. Request content from the appropriate manager
2. The manager checks the cache via CacheManager
3. If not in cache, the manager requests the block from RawBlockManager
4. The block payload is deserialized into the content model
5. The content is cached for future use
6. The content model is returned to the caller

## Performance Considerations

For optimal performance with content models:

- Use proper caching strategies
- Minimize deep object graphs
- Use efficient binary serialization
- Be mindful of content size
- Consider lazy loading for large content
