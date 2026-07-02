---
created: '2026-07-01'
id: EPIC-EMDB-19
points: null
priority: should
status: draft
tags:
- v3
- search
- fts
- index
target_date: null
title: Search Phases 1-3
updated: '2026-07-01'
---

First three search phases per docs/Search.md: address trigram FTS index (block types 14-17, always encrypted, posting-list intersection with Tier 1 verification), Tier 1 listing page scan (folder-scoped ~15ms/50K), date BTree (IndexKind 2, composite DateTicks|BlockId keys), and a simple query planner routing across phases. Success: address substring search returns in <10ms at 100K emails; date-range queries avoid scans.