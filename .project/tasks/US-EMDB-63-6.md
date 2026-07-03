---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-63-6
points: 2
status: done
story_id: US-EMDB-63
tags: []
title: Implement superblock slot serialization
updated: '2026-07-03'
---

Serialize/deserialize the 4096-byte slot layout per spec Section 3.1: all fields, reserved zero-fill, BLAKE3-128 slot checksum over bytes 0..4079. New Superblock model + SuperblockSerializer.