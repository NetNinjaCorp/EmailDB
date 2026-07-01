---
created: '2026-02-24'
id: EPIC-EMDB-9
points: null
priority: should
status: archived
tags:
- security
- encryption
target_date: null
title: Block-Level Encryption
updated: '2026-07-01'
---

Add AES-256-GCM block-level payload encryption to EmailDB using a two-tier KEK/DEK key-wrapping architecture (ADR-015). A Key Encryption Key (KEK) derived from the user's password via Argon2id encrypts a Key Store Block containing Data Encryption Keys (DEKs). Each data block's Flags byte encodes the key epoch identifying which DEK was used. This design enables: (1) password changes by re-encrypting only the key store block, (2) key rotation by adding a new DEK without re-encrypting old blocks, and (3) optional re-encryption during compaction to prune retired DEKs. Sensitive block payloads (email content, folders, WAL) are encrypted while headers remain unencrypted for scanning/recovery. Integrates with existing BLAKE3 hash chains (hash plaintext before encryption) and BLAKE3-128 checksums (computed on ciphertext).