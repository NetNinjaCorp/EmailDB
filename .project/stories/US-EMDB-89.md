---
acceptance_criteria:
- Compacted file contains exactly the live blocks with identical BlockIds and content
- BlockLocationIndex rebuilt for the new layout and verifies
- Kill at every step of the swap yields either the complete old file or the complete
  new file
- Leftover .compact file is deleted on next open
- FolderVersions and CheckpointSequence continue across the swap so sync is unaffected
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-18
id: US-EMDB-89
points: 8
priority: must
status: done
tags:
- v3
- compaction
- swap
title: Side-file compaction and atomic swap
updated: '2026-07-10'
---

As an operator, I want full-file compaction via side-file + atomic rename (docs/Compaction.md Section 2, spec Section 11.2) so that space reclamation is crash-safe: copy live blocks, rebuild BlockLocationIndex, same FileId with continued sequences, rename + directory fsync.