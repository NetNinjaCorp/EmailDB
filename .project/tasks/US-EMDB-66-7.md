---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-66-7
points: 2
status: todo
story_id: US-EMDB-66
tags: []
title: Implement backward EOF footer walk
updated: '2026-07-02'
---

Walk backward from EOF via footer magic + TotalBlockLength to find the last valid block after a torn tail append.