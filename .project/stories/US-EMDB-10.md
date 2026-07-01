---
acceptance_criteria:
- StorageManager implements IStorageManager with two-tier model
- Proper initialization order (RawBlockManager → CacheManager → MetadataManager →
  FolderManager → EmailManager + BTreeIndex)
- All IStorageManager methods delegate to correct managers
- Soft-delete moves to Dead folder without BTree mutation
- File can be opened from existing or created new
- Proper disposal of all managed resources
created: '2026-02-22'
epic_id: EPIC-EMDB-4
id: US-EMDB-10
points: 5
priority: must
status: backlog
tags: []
title: Implement IStorageManager with two-tier storage and soft-delete
updated: '2026-02-25'
---

As a developer, I want a concrete StorageManager implementing IStorageManager so that there's a single entry point for all email operations using the two-tier storage model.

IStorageManager interface must reflect: two-tier storage (metadata + content blocks), soft-delete via folder move (no BTree deletion by default), and the updated initialization stack.

**Initialization order:** RawBlockManager → CacheManager → MetadataManager → FolderManager → EmailManager + BTreeIndex

**Key interface methods:**
- AddEmailToFolder: writes content block + metadata block + BTree insert + folder add
- GetEmailMetadata: BTree lookup → metadata block (for listings)
- GetEmailContent: metadata → content block (for opening)
- SoftDeleteEmail: move to Dead folder
- MoveEmail: folder membership update only
- CreateFolder / DeleteFolder
- Compact: live tree rewrite + dead block reclamation
- InvalidateCache