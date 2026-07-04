---
acceptance_criteria:
- Second writer fails fast with a clear error while readers open shared
- fsync means flush-to-disk not stream flush
- fsync failure poisons the handle and forces recovery on reopen
- Directory fsync on file create and rename
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-12
id: US-EMDB-65
points: 5
priority: must
status: done
tags:
- v3
- concurrency
- durability
title: Single-writer lock and fsync discipline
updated: '2026-07-04'
---

As the storage engine, I want enforced single-writer access and strict fsync semantics (spec Sections 10.3, 12) so that concurrent writers cannot corrupt the file and durability failures are never papered over.