# Folder Block (BlockType = 3)

Stores per-folder data: the folder's identity and the list of emails it contains.

## Block ID

Range-allocated: `10,000,000,000,000` + counter. Each folder gets its own dynamically assigned ID.

## Payload (Protobuf)

| Field | Type | Description |
|-------|------|-------------|
| FolderId | int64 | Unique folder identifier. |
| ParentFolderId | int64 | ID of the parent folder in the hierarchy. |
| Name | string | Folder display name (e.g., "Inbox", "Sent"). |
| EmailIds | List\<EmailHashedID\> | Ordered list of emails in this folder. Each entry is 32 bytes (SHA3-256). |

## Role in Three-Tier Model

`EmailIds` is the **source of truth** for folder membership. The paginated folder listing pages (designed but not yet in the block type enum) are a derived, denormalized structure built from this list plus BTree lookups. If listing pages are lost, they are regenerated from `EmailIds`.

## Encryption Policy

**Encrypted** under the Default policy. Folder contents reveal which emails belong to which folders.

## Source Reference

- `FolderContent.cs`
- `FolderManager.cs`
