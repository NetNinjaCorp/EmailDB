# EmailContent Block (BlockType = 9)

Stores the content of a single email. This is the primary data block — one EmailContent block per email.

## Block ID

Range-allocated: `80,000,000,000,000` + counter.

## Payload (Protobuf or JSON)

The payload is the serialized email content. The exact structure depends on the serialization format used (Protobuf or JSON), but represents the full email data including headers and body.

The `EnhancedEmailContent` model provides the rich email representation.

## Relationship to Other Structures

- **BTree index**: Maps `EmailHashedID` → `(BlockOffset, BlockId)` pointing to this block.
- **Folder block**: `EmailIds` list contains the `EmailHashedID` of emails in each folder.
- **WAL**: Inserts are buffered in WAL entries before being flushed to the BTree.

### Write Path

1. Serialize email content
2. Append EmailContent block to file (sequential write)
3. Add WAL entry: `EmailHashedID → (BlockOffset, BlockId)`
4. When WAL reaches 82 entries, flush to BTree

### Read Path

1. Look up `EmailHashedID` in BTree → get `(BlockOffset, BlockId)`
2. Read block at `BlockOffset` using `BlockId`
3. Verify checksums, decrypt if necessary
4. Deserialize payload

## Encryption Policy

**Always encrypted** under both Default and Full policies. Email content is the most sensitive data in the file.

## Source Reference

- `EnhancedEmailContent.cs` — email content model
- `EmailManager.cs` — high-level email operations
- `BTreeIndex.cs` — index lookups
