---
acceptance_criteria:
- Footer length written and read as same type (both Int64 or both Int32)
- Blocks written by WriteBlockToStream can be read by ReadBlockFromStreamInternal
- 'Round-trip test passes: write block then read back with identical payload'
- Existing unit tests still pass
created: '2026-02-22'
epic_id: EPIC-EMDB-1
id: US-EMDB-21
points: 2
priority: must
status: done
tags: []
title: Fix RawBlockManager footer int32/int64 read/write mismatch
updated: '2026-02-22'
---

As a developer, I want RawBlockManager to correctly read blocks it writes so that block storage is reliable. Bug found during SQLite benchmarking: WriteBlockToStream writes footer length as int (4 bytes) via BinaryWriter.Write(int), but ReadBlockFromStreamInternal reads it as Int64 (8 bytes) via reader.ReadInt64(). This 4-byte mismatch causes "Unable to read beyond the end of the stream" on every read. Location: RawBlockManager.cs WriteBlockToStream (~line 547) and ReadBlockFromStreamInternal (~line 591).