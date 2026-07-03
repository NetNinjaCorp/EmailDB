---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-63-7
id: US-EMDB-63-8
points: 3
status: done
story_id: US-EMDB-63
tags: []
title: Implement open validation, feature flags, and CleanShutdown transitions
updated: '2026-07-03'
---

Slot validation (magic + checksum), Incompat refusal / ReadOnlyCompat read-only enforcement, CleanShutdown=0 on first write after open and =1 on graceful close, MaxPayloadLength exposure to the block reader.