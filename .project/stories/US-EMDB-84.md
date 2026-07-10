---
acceptance_criteria:
- Create produces a file that reopens cleanly with and without encryption
- Open wires superblock checkpoint indexes folders and encryption into one ready instance
- Close writes final checkpoint sets CleanShutdown and releases the writer lock
- Kill -9 between operations always reopens via recovery to the last commit
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-17
id: US-EMDB-84
points: 8
priority: must
status: done
tags:
- v3
- api
- lifecycle
title: 'Lifecycle: create, open, close'
updated: '2026-07-10'
---

As a consuming application, I want EmailManager to own the full file lifecycle: create initializes superblocks + initial blocks + first Checkpoint (spec Section 11.1); open runs the full protocol including encryption bootstrap and WAL replay; close flushes, checkpoints, and sets CleanShutdown.