---
acceptance_criteria:
- Substring query over addresses returns correct results at 100K emails in under 10ms
  warm
- FTS blocks are encrypted under every policy
- Index updates on add and delete
- Candidates verified against Tier 1 records to remove trigram false positives
- Root recoverable from the Checkpoint secondary index table
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-19
id: US-EMDB-91
points: 13
priority: should
status: backlog
tags:
- v3
- search
- fts
- trigram
title: Address trigram FTS index
updated: '2026-07-02'
---

As a user, I want instant substring search on From/To/Cc addresses (docs/Search.md Phase 1): FTS block types 14-17, always encrypted, built on ingest, queried via trigram posting-list intersection with Tier 1 verification, root registered in the Checkpoint secondary table.