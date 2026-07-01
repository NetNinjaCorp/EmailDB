---
acceptance_criteria:
- BTreeWALManager.InsertAsync adds entry to in-memory buffer and writes to WAL region
  on disk
- BTreeWALManager.LookupAsync returns buffered entry before flush
- WAL entries are 48 bytes each densely packed after 22-byte header
- Duplicate key insert upserts in buffer without creating duplicate WAL entry
- Auto-flush triggers at configurable threshold
- FlushAsync writes all entries to BTree and clears WAL
- WAL dirty flag set on insert and cleared on flush
- FlushSequence increments on each flush
- Concurrent inserts are thread-safe via AsyncReaderWriterLock
- Batch of 100 inserts produces fewer node writes than 100 individual BTreeIndex.InsertAsync
  calls
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-30
points: 8
priority: must
status: done
tags: []
title: Implement WAL-buffered writes with batch flush to B+-tree
updated: '2026-02-23'
---

As a developer, I want a BTreeWALManager class that buffers index inserts into the reserved 16MB WAL region and batch-flushes to the BTree so that write amplification is minimized.

New file: `EmailDB.Format/FileManagement/BTreeWALManager.cs`

**Constructor**: Takes RawBlockManager, BTreeIndex, walPayloadFileOffset (from HeaderContent.WALRegionOffset), autoFlushThreshold (default 82), autoFlushEnabled flag. On init, reads WAL header from disk and populates in-memory buffer from any unflushed entries (crash recovery path).

**InsertAsync(EmailHashedID key, long blockOffset, long blockId)**:
1. Add/update in Dictionary&lt;EmailHashedID, (long BlockOffset, long BlockId)&gt; (in-memory buffer)
2. Write 48-byte entry at walPayloadFileOffset + 22 + (entryCount * 48) via RawBlockManager.WriteRawBytesAsync
3. Update on-disk WAL header (EntryCount++, set dirty flag)
4. If autoFlush enabled and count >= threshold → trigger FlushAsync()

**LookupAsync(EmailHashedID key)**: Check in-memory buffer first. If not found, fall through to BTreeIndex.LookupAsync.

**FlushAsync()**: Sort buffered entries by key. For small batches, loop BTreeIndex.InsertAsync. For large batches, call BTreeIndex.BulkInsertAsync. Clear WAL region on disk (rewrite 22-byte header: EntryCount=0, dirty=0, FlushSequence++). Clear in-memory buffer.

**Locking**: AsyncReaderWriterLock. Lock ordering: WAL lock first, then RawBlockManager.fileLock (acquired internally by BTree operations).

**In-memory buffer**: Dictionary&lt;EmailHashedID, (long, long)&gt; for O(1) dedup, sorted at flush time. Handles both ~82-entry normal case and 349K bulk import without O(n^2) overhead.

Depends on: US-EMDB-35 (model changes), US-EMDB-36 (WAL region init)