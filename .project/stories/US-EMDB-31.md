---
acceptance_criteria:
- Clean shutdown followed by reopen loads correct IndexRoot and tree
- Crash with dirty WAL recovers unflushed entries on next startup
- Recovery flushes recovered entries into BTree correctly
- BeginBulkImport disables auto-flush
- EndBulkImportAsync flushes all pending entries via BulkInsertAsync
- WAL capacity of 349524 entries is respected during bulk import
- Dispose flushes pending entries if any exist
- Recovery completes in O(WAL_size) time not O(file_size)
- LastFlushedWALSequence in IndexRoot matches WAL FlushSequence after recovery
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-31
points: 8
priority: must
status: done
tags: []
title: Implement IndexRoot management and crash recovery
updated: '2026-02-23'
---

As a developer, I want WAL crash recovery and bulk import mode so that unflushed WAL entries survive crashes and large email imports can fill the WAL without per-batch flushing overhead.

Add to `EmailDB.Format/FileManagement/BTreeWALManager.cs`:

**RecoverAsync(CancellationToken)**:
- Called on startup after constructing BTreeWALManager
- Read 22-byte WAL header from walPayloadFileOffset
- If Flags.dirty == 1: read EntryCount entries from disk into in-memory buffer, call FlushAsync()
- If Flags.dirty == 0: WAL is clean, no recovery needed
- Can also cross-check IndexRoot.LastFlushedWALSequence against WAL header FlushSequence

**BeginBulkImport()**:
- Sets autoFlushEnabled = false
- Allows WAL to fill up to 349,524 entries (16MB capacity)

**EndBulkImportAsync(CancellationToken)**:
- Triggers FlushAsync() which uses BulkInsertAsync for the large batch
- Re-enables auto-flush
- Returns Result indicating success/failure

**Dispose/Shutdown**:
- If PendingEntryCount > 0, flush before disposing
- Ensures no data loss on clean shutdown

Depends on: US-EMDB-30 (WAL manager core), US-EMDB-37 (BulkInsertAsync for large flushes)