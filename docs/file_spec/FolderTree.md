# FolderTree Block (BlockType = 2)

Stores the folder hierarchy — the tree structure of all folders and their relationships.

## Block ID

Fixed: `2` (system block).

## Payload (Protobuf)

| Field | Type | Description |
|-------|------|-------------|
| RootFolderId | int64 | ID of the root folder. |
| FolderHierarchy | Dictionary\<string, string\> | Parent-child relationships (child name → parent name). |
| FolderIDs | Dictionary\<string, int64\> | Folder name → folder ID mapping. |
| FolderOffsets | Dictionary\<int64, int64\> | Folder ID → file offset of the corresponding Folder block. |

## Encryption Policy

**Encrypted** under the Default policy. Folder names may reveal information about email organization.

## Source Reference

- `FolderTreeContent.cs`
- `FolderManager.cs`
