---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-85-5
id: US-EMDB-86-5
points: 3
status: done
story_id: US-EMDB-86
tags: []
title: Implement GetEmail and metadata-only fetch
updated: '2026-07-10'
---

Primary index lookup -> location index -> verified block read -> decrypt/decompress/deserialize; GetMetadata variant stops at Tier 2; clean not-found.