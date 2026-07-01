---
acceptance_criteria:
- 'Text preparation function: email → embedding-ready string documented and implemented'
- Truncation strategy decided and tested (256 token limit handling)
- HTML stripping for HTML-body emails
- Query embedding latency measured and added to end-to-end search budget
- Distance metric documented as cosine similarity in ADR
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-24
points: 3
priority: must
status: backlog
tags: []
title: Design email text preparation pipeline for embedding
updated: '2026-02-22'
---

As a developer, I want a well-defined text preparation pipeline that converts raw email content into embedding-ready text so that search quality is consistent and predictable. Critical design decisions needed: (1) MiniLM-L6-v2 has a 256 token limit (~200 words) but most emails are longer — truncation vs chunking strategy, (2) What fields to embed — subject only, subject+body, subject+body+from, (3) How to handle HTML emails — strip tags first, (4) Query-time embedding adds ~1-15ms to search latency — must be factored into end-to-end targets, (5) Distance metric — cosine similarity (equivalent to dot product for normalized MiniLM outputs).