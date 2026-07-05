---
acceptance_criteria:
- Checkpoint written last in every mutation batch after fsync of its contents
- Offset hints verified by BlockId match on use and re-resolved on mismatch without
  error
- Secondary index table round-trips arbitrary IndexKind entries
- CheckpointSequence monotonic and previous-checkpoint chain walkable
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-14
id: US-EMDB-72
points: 5
priority: must
status: done
tags:
- v3
- checkpoint
- commit
title: Checkpoint writer and reader
updated: '2026-07-04'
---

As the storage engine, I want Checkpoint blocks as the single commit point (spec Section 10.1): root table with ULID+offset hint pairs (folder tree, primary index, location index, metadata, KeyStore, previous checkpoint), generic secondary-index table, live/dead byte accounting, FileId cross-check.