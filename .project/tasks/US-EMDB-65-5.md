---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-65-5
points: 2
status: todo
story_id: US-EMDB-65
tags: []
title: Implement single-writer OS lock
updated: '2026-07-02'
---

Exclusive writer lock (FileShare semantics on .NET, flock/OFD on POSIX) held for the file lifetime; readers open shared; second writer fails fast with a typed error.