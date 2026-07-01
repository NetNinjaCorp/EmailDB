---
acceptance_criteria:
- InitializeNewFile creates a file at least 16MB in size
- WAL block (BlockId=3) has exactly 16777216 bytes of payload
- WALRegionHeader at start of WAL payload has Version=1 EntryCount=0 dirty=0
- HeaderContent.WALRegionOffset points to correct payload start offset
- HeaderContent.WALRegionSize equals 16777216
- Block scanner sees exactly 4 blocks (Header WAL FolderTree Metadata) after init
- FolderTree and Metadata blocks are accessible at offsets after the WAL region
- Existing tests for InitializeNewFile still pass
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-36
points: 3
priority: must
status: backlog
tags: []
title: Reserve 16MB WAL region during file initialization
updated: '2026-02-23'
---

As a developer, I want CacheManager.InitializeNewFile() to reserve a 16MB WAL region at the start of the file so that BTree inserts can be buffered without scattered WAL blocks.

The WAL region is written as a standard block (BlockType.WAL, BlockId=3) with a 16MB payload initialized to zeros with a valid WALRegionHeader. This means the block scanner naturally handles it — it sees one big block and jumps past it. The payload is managed internally via raw byte writes.

File layout after init:
  [Header Block ~110 bytes] [WAL Block 16MB+60 bytes] [FolderTree] [Metadata]

Key changes to CacheManager.InitializeNewFile():
- Step 2 changes from writing a tiny WAL block to allocating a 16MB payload
- WAL payload starts with WALRegionHeader (22 bytes): Version=1, Flags=0, EntryCount=0, FlushSequence=0
- HeaderContent.WALRegionOffset = walBlock position + 40 (header+headerCRC = payload start)
- HeaderContent.WALRegionSize = 16,777,216

The scanner (ScanExistingBlocksAsync) requires no changes — it validates header magic/checksum, reads payload length, and jumps by total block length. The 16MB WAL block scans as a single block.