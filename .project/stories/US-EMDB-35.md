---
acceptance_criteria:
- HeaderContent has WALRegionOffset and WALRegionSize fields
- IndexRoot has LastFlushedWALSequence field with PayloadSize=98
- MetadataContent has BTreeRootOffset field
- WALContent.cs contains WALRegionHeader struct replacing old models
- BTreeNodeSerializer round-trips 98-byte IndexRoot correctly
- BTreeNodeSerializer handles old 90-byte IndexRoot payloads with LastFlushedWALSequence=0
- RawBlockManager.WriteRawBytesAsync writes at specified offset using fileLock
- RawBlockManager.ReadRawBytesAsync reads from specified offset using fileLock
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-35
points: 5
priority: must
status: backlog
tags: []
title: WAL region model changes and RawBlockManager raw byte I/O
updated: '2026-02-23'
---

As a developer, I want the foundational model changes in place so that the WAL region, BTree bulk operations, and crash recovery can be built on top.

This is the additive-only Phase 1 — no behavior changes, just new fields, structs, and methods.

Changes:
- HeaderContent: add WALRegionOffset (long) and WALRegionSize (int) fields
- IndexRoot: add LastFlushedWALSequence (ulong), PayloadSize 90→98 bytes
- MetadataContent: add BTreeRootOffset (long) field
- WALContent.cs: replace old WALContent/WALEntry with WALRegionHeader struct (22 bytes: Version, Flags, EntryCount, Reserved, FlushSequence, HeaderCRC)
- BTreeNodeSerializer: update SerializeIndexRoot/DeserializeIndexRoot for 98-byte payload (backward-compatible with 90-byte)
- RawBlockManager: add WriteRawBytesAsync(long fileOffset, byte[] data, int offset, int count) and ReadRawBytesAsync(long fileOffset, int count) using existing fileLock