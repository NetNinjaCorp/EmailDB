---
created: '2026-02-25'
id: EPIC-EMDB-11
points: null
priority: must
status: archived
tags:
- storage
- email
- metadata
- architecture
- core
target_date: null
title: Two-Tier Email Storage (Metadata + Content Blocks)
updated: '2026-07-01'
---

Introduce a two-tier email storage model that separates lightweight email metadata from heavy email content, enabling fast folder listings without reading full email payloads.

**Architecture:**
- Each email stored as two blocks: EmailMetadata (small, ~200-500 bytes) and EmailContent (variable, potentially megabytes with attachments).
- BTree indexes EmailHashedID → metadata block location. Metadata block carries a ContentBlockId pointer to the heavy content block.
- Folder listing reads only metadata blocks (subject, from, to, date, flags). Opening an email follows the content pointer.

**Soft-Delete Model:**
- Emails are never removed from the BTree. The BTree is a permanent append-once index.
- "Deleting" an email moves it to a Dead/Deleted system folder. BTree and blocks untouched.
- Hard delete (optional, rare) marks a tombstone or removes from BTree for explicit purge.

**Move = Folder Only:**
- Moving an email changes folder membership (add/remove EmailHashedID from folder blocks). BTree, metadata block, and content block are all untouched.

**New Block Type:**
- BlockType.EmailMetadata (value 11): structured block with subject, from, to, cc, date, attachment count, content size, flags, content block pointer, folder path.

**Success Criteria:**
- Folder listing of 1000 emails reads only metadata blocks (no content blocks touched)
- Add email writes exactly 2 blocks + 1 BTree insert
- Move/delete are folder-only operations (no BTree mutation)
- All existing BTree, encryption, and checksum infrastructure works unchanged
- Compaction handles both metadata and content blocks correctly

**Dependencies:** EPIC-EMDB-8 (B+-Tree, done), EPIC-EMDB-10 (BLAKE3 checksums, done), EPIC-EMDB-9 (Encryption, mostly done)

**Prerequisite for:** EPIC-EMDB-4 (Email Management API) — EmailManager needs two-tier model before implementation.