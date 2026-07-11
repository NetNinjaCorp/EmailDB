---
acceptance_criteria:
- Users can create/rename/delete tags in a persisted tag registry
- An email can hold multiple tags and tag/untag does not rewrite folder listing rows
  or bump FolderVersion
- Single-tag and multi-tag (AND/OR) queries return correct results through the Search()
  planner and combine with text/date phases
- Tag registry and assignments survive close/reopen via Checkpoint registration and
  survive compaction
- Tag blocks are always encrypted under every policy
- Design decision folders-as-tags vs first-class recorded in docs with the chosen
  approach
created: '2026-07-11'
depends_on: []
epic_id: EPIC-EMDB-17
id: US-EMDB-105
points: null
priority: could
status: backlog
tags:
- v3
- tags
- mailbox
- search
title: First-class email tags
updated: '2026-07-11'
---

As a user, I want to tag emails with arbitrary user-defined labels (multiple tags per email, tag/untag without moving mail) so that I can organize and query my mailbox across dimensions independent of folder structure.

## Call-out: folders-as-tags vs first-class tags

**Current state — folders already approximate tags.** An email dedupes globally by EmailHashedID and can be listed in multiple folders (dual-listing, pinned by test `SearchMailbox_returns_a_hit_per_folder_for_a_message_listed_in_two_folders`). Each "tag" could simply be a folder: membership via listing rows, delta-log mutations, per-folder bloom filters, folder-scoped search, FolderVersion-based sync — all crash-safe, encrypted, and compaction-ready today for free.

**Why that stays second-class:**
1. **Row duplication** — every folder membership copies the full Tier-1 listing row (Subject/From/Preview/DateTicks). Tag churn rewrites listing rows, bumps FolderVersion, triggers page compiles and full-folder sync retransfer (docs/Sync.md transfers changed folders wholesale). Tags are high-churn; folders are not.
2. **No tag namespace** — nothing enumerates "all tags" or carries tag metadata (color, description). Folder ULIDs have no name registry usable as a tag registry.
3. **ListingRecord flags are a fixed system bitset** (read/starred/etc. via FlagChange deltas) — not user-extensible; can't piggyback arbitrary tags there.
4. **Planner has folder scope but no tag-shaped queries** — no `tag:foo`, no multi-tag AND/OR intersection.
5. **Semantics conflate** — untagging = folder delete delta, indistinguishable from "remove from folder"; a tag spanning "all mail" has no natural folder home.

**First-class sketch (for scoping):**
- **Tag registry block** (new block type): tag id → name/metadata; always encrypted like FTS/bloom types 14-18.
- **Tag-assignment secondary index** (checkpoint-registered, IndexKind 5): EmailHashedID → tag set, plus reverse posting lists (tag id → EmailHashedIDs) reusing the FTS posting-list machinery (FtsPostingList pattern) for O(candidates) multi-tag AND/OR intersection.
- **Tag deltas in the group-commit pipeline** (like FlagChange), so tag/untag is one WAL entry + checkpoint, no listing-row rewrite, no FolderVersion bump — cheap churn and near-zero sync retransfer.
- **Planner integration**: `tag:` term in SearchQuery routes to the tag index and intersects with text/date phases (same merge/dedupe/ordering rules as Sprint 8's Search()).
- **Compaction/sync**: blocks ride the existing live-block copy and ULID replication; KeyStore ordering (US-EMDB-100) applies unchanged.

**Scoping decision to record:** thin variant (system folders under a reserved `tag:` namespace + registry + planner sugar — fastest, inherits duplication costs) vs the full assignment-index variant above (more blocks, but tags become cheap and queryable). Recommendation: full variant; the posting-list and secondary-index machinery from Sprint 8 makes it mostly assembly.