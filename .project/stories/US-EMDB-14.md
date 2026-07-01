---
acceptance_criteria:
- Integration test for add email → retrieve email round trip with two-tier model
- Integration test for folder create → add email → list emails via metadata blocks
- Integration test for move email between folders (BTree untouched)
- Integration test for soft-delete email (move to Dead folder and verify BTree entry
  persists)
- Seeded fuzzing integration tests covering randomised workloads
- All tests use real file I/O (no mocks) with temp files
created: '2026-02-22'
epic_id: EPIC-EMDB-6
id: US-EMDB-14
points: 5
priority: should
status: backlog
tags: []
title: Add integration tests for end-to-end email workflows
updated: '2026-02-25'
---

As a developer, I want integration tests that exercise the full stack so that I can verify the system works end-to-end using the two-tier storage model.

**Workflows to test:**
- Email addition: EmailManager writes content block + metadata block → BTree insert → folder add
- Folder listing: folder → BTree lookup each ID → read metadata blocks only (no content blocks)
- Email retrieval: metadata block → content block via pointer
- Email move: folder membership update only, BTree and blocks untouched
- Soft-delete: move to Dead folder, verify BTree still has entry, verify email in Dead folder
- Seeded fuzzing: randomised workloads of add/delete/move/compact with state verification

All tests use real file I/O (no mocks) with temp files.