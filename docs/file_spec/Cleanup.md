# Cleanup Block (BlockType = 5)

Marks blocks that have been superseded or deleted. Used during compaction to identify dead blocks that can be reclaimed.

## Block ID

Range-allocated: `30,000,000,000,000` + counter.

## Payload

The Cleanup block payload format is not explicitly structured in the current codebase. It is used in conjunction with `MetadataContent.OutdatedOffsets` to track which blocks are no longer live.

## Role in Compaction

During compaction, the system:
1. Reads all block locations from the file
2. Identifies live blocks (those not listed in OutdatedOffsets or referenced by Cleanup blocks)
3. Copies only live blocks to a new file
4. Replaces the original file with the compacted version

## Encryption Policy

**Not encrypted** under the Default policy. Cleanup blocks contain only internal bookkeeping data (block references), no email content.

## Source Reference

- `RawBlockManager.CompactAsync()`
- `MetadataContent.OutdatedOffsets`
