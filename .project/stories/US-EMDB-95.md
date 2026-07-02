---
acceptance_criteria:
- Sidecar round-trips embeddings and index nodes
- Stale sidecar detected via CheckpointSequence mismatch and rebuilt from the main
  file
- Deleting the sidecar never loses email data
- Sidecar encrypted at rest when the main file is encrypted
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-20
id: US-EMDB-95
points: 8
priority: could
status: backlog
tags:
- v3
- vectors
- sidecar
title: .emdb.vec sidecar format
updated: '2026-07-02'
---

As the search layer, I want the vector sidecar file format (docs/Search.md Phase 4, spec Section 15): block types 19-21, header echoing the CheckpointSequence it was built from, staleness detection and rebuild, derived-data contract (deletable and regenerable).