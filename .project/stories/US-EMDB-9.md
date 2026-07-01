---
acceptance_criteria:
- EmailManager compiles and implements two-tier write flow (content block + metadata
  block + BTree insert)
- AddEmailAsync writes both content and metadata blocks and indexes in BTree
- GetEmailMetadataAsync retrieves metadata via BTree lookup without reading content
  block
- GetEmailContentAsync retrieves full email content via metadata pointer
- SoftDeleteEmailAsync moves email to Dead folder without BTree mutation
- MoveEmailAsync updates folder membership only
- EmailHashedID deduplication prevents duplicate storage
created: '2026-02-22'
epic_id: EPIC-EMDB-4
id: US-EMDB-9
points: 8
priority: must
status: backlog
tags: []
title: Implement EmailManager with two-tier storage (metadata + content blocks)
updated: '2026-02-25'
---

As a developer, I want EmailManager to be a working high-level API using the two-tier storage model so that the application can store, retrieve, and manage emails efficiently.

**Two-Tier Model:**
- AddEmailAsync: writes ContentBlock (raw MIME) + MetadataBlock (subject/from/to/date/flags/content pointer) + BTree insert (EmailHashedID → metadata block location) + add to folder.
- GetEmailMetadataAsync: BTree lookup → read metadata block (small). Used for folder listings.
- GetEmailContentAsync: BTree lookup → metadata block → follow content pointer → read content block. Used when opening an email.
- SoftDeleteEmailAsync: Move email to Dead folder. BTree untouched, content untouched.
- MoveEmailAsync: Update folder membership only. BTree and blocks untouched.
- HardDeleteEmailAsync (optional): Tombstone or BTree removal for explicit purge.

**Initialization order:** RawBlockManager → CacheManager → MetadataManager → FolderManager → EmailManager + BTreeIndex