---
created: '2026-02-22'
id: EPIC-EMDB-4
points: null
priority: must
status: archived
tags:
- api
- email
target_date: null
title: Email Management API
updated: '2026-07-01'
---

Complete the EmailManager and IStorageManager interface using the two-tier email storage model (metadata blocks + content blocks). Wire up email add/get/soft-delete/move/search operations end-to-end through the full stack: EmailManager → BTreeIndex + RawBlockManager.

**Two-Tier Storage Model:**
- Each email is stored as two blocks: a lightweight EmailMetadata block (subject, from, to, date, flags, content pointer) and a heavy EmailContent block (raw MIME payload).
- The BTree indexes EmailHashedID → metadata block location. The metadata block references the content block.
- Listing a folder reads only metadata blocks (small, cacheable). Opening an email follows the content pointer for the full payload.

**Soft-Delete Model:**
- Emails are never removed from the BTree. "Deleting" an email moves it to a Dead/Deleted folder.
- The BTree is a permanent append-once index of every email ever ingested.
- Hard delete (optional, rare) could mark a tombstone flag or remove from BTree, but is the exception.

**Move = Folder Membership Only:**
- Moving an email updates folder blocks (remove from source, add to target). BTree and content blocks are untouched.

**Stack:** RawBlockManager → CacheManager → MetadataManager → FolderManager → EmailManager → BTreeIndex