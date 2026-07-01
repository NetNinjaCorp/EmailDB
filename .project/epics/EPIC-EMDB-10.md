---
created: '2026-02-24'
id: EPIC-EMDB-10
points: null
priority: must
status: archived
tags:
- integrity
- security
- format
target_date: null
title: BLAKE3-128 Block Checksums
updated: '2026-07-01'
---

Replace CRC32 (4-byte) block checksums with BLAKE3-128 (16-byte) checksums on both header and payload. CRC32's 32-bit keyspace provides only basic bit-flip detection with no tamper resistance. BLAKE3-128 gives 128-bit collision resistance, cryptographic integrity, and unifies the hashing story with the existing BLAKE3 hash chains in the B+-tree index. This is a prerequisite for the encryption epic (EPIC-EMDB-9) where checksums on ciphertext need to resist intentional tampering. Format overhead increases from 57 to 81 bytes per block (24 bytes). Requires format version bump. Supersedes ADR-003's CRC32 decision.