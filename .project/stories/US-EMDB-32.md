---
acceptance_criteria:
- Rebuild produces valid file with Header WAL FolderTree Metadata and all data blocks
- All EmailContent blocks from source exist in rebuilt file with identical payloads
- BTree in rebuilt file has EntryCount matching number of EmailContent blocks
- All lookups that worked on source file work on rebuilt file
- Rebuilt file has zero dead blocks (file size is minimal)
- New hash chain starts fresh in rebuilt file
- File swap is atomic (no data loss on failure during swap)
- Rebuild works on files with WAL entries (flushes WAL first or includes WAL entries
  in rebuild)
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-32
points: 5
priority: should
status: done
tags: []
title: Implement B+-tree compaction (live tree rewrite)
updated: '2026-02-24'
---

As a developer, I want a BTreeRebuildManager that can rebuild the entire file from scratch — copying data blocks and building a fresh BTree — so that years of accumulated dead blocks can be reclaimed.

New file: `EmailDB.Format/FileManagement/BTreeRebuildManager.cs`

**RebuildToNewFileAsync(string outputPath, iBlockContentSerializer serializer, CancellationToken)**:
1. Create new RawBlockManager for output file
2. Call CacheManager.InitializeNewFile() on new file (creates header + 16MB WAL + FolderTree + Metadata)
3. Scan source file's blockLocations for all EmailContent blocks
4. For each EmailContent block: read from source, write to output (gets new offset), extract EmailHashedID from payload, record (EmailHashedID, newOffset, blockId)
5. Also copy Folder, FolderTree, Metadata blocks (non-index data)
6. Sort all recorded entries by EmailHashedID
7. Create BTreeIndex on output file, call BulkInsertAsync with sorted entries — produces optimally packed fresh tree
8. Update output file's Header and Metadata with final offsets
9. Swap files (same pattern as existing RawBlockManager.CompactAsync: temp file → File.Replace)

**ExtractIndexEntriesAsync(CancellationToken)**:
- Scans data blocks and extracts (EmailHashedID, blockOffset, blockId) tuples
- Used as input for BulkInsertAsync in rebuild scenarios

Result: zero dead blocks, contiguous data blocks, optimally packed BTree, clean WAL.

Depends on: US-EMDB-36 (WAL region init), US-EMDB-37 (BulkInsertAsync)