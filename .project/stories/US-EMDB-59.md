---
acceptance_criteria:
- AddEmailAsync writes ContentBlock then MetadataBlock in correct order
- BTree insert points to metadata block location not content block
- MetadataBlock.ContentBlockId matches the written content block's BlockId
- MetadataBlock contains correct extracted fields (subject from to cc date attachment
  count)
- Email is added to target folder after all blocks written
- Content block payload matches original raw email bytes
- Duplicate EmailHashedID is rejected or handled as upsert
created: '2026-02-25'
epic_id: EPIC-EMDB-11
id: US-EMDB-59
points: 5
priority: must
status: backlog
tags: []
title: Implement two-tier email write flow (content block + metadata block + BTree
  insert)
updated: '2026-02-25'
---

As a developer, I want the email ingestion path to write both a content block and a metadata block so that email listings can be served without reading full email payloads.

**Write flow for AddEmailAsync:**
1. Parse email to extract metadata (subject, from, to, cc, date, attachment count)
2. Write EmailContent block (raw MIME bytes) → get ContentBlockId and content size
3. Build EmailMetadataContent with extracted fields + ContentBlockId + content size + initial folder
4. Write EmailMetadata block → get metadata block location
5. BTree insert: EmailHashedID → metadata block location (Position, BlockId)
6. Add EmailHashedID to target folder's EmailIds list

**Key invariants:**
- BTree always points to metadata blocks, never content blocks
- Metadata block always carries a valid ContentBlockId
- Both blocks are append-only (never overwritten)
- Email is not "visible" until folder membership is updated