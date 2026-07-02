---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-102-5
points: 2
status: todo
story_id: US-EMDB-102
tags: []
title: Triage existing test suite
updated: '2026-07-02'
---

Classify all ~140 tests: keep (crypto primitives, BLAKE3, Argon2), rewrite (behavioral tests worth porting to v3 APIs), delete (v1 format specifics: 84B overhead, PrevChainHash, epoch-in-flags, offset BTree). Produce the triage list.