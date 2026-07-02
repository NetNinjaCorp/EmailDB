---
acceptance_criteria:
- Clean open performs zero scanning beyond superblock and checkpoint reads
- Dirty open scans only bytes written after the last checkpoint
- Crash between checkpoint and superblock update is healed by the forward scan
- No valid superblock falls back to full scan from 8192 and no valid checkpoint to
  full rebuild
- Open is O(log n) on every normal path
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-14
id: US-EMDB-74
points: 8
priority: must
status: backlog
tags:
- v3
- recovery
- open
title: Open protocol and crash recovery
updated: '2026-07-01'
---

As a user, I want opening a mailbox to be fast and crash-safe (spec Section 10.2): superblock selection, clean-shutdown fast path, bounded dirty scan for newer checkpoints and unreplayed WAL, location index load, and full-scan fallbacks only on disaster.