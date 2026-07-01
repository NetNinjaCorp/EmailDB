---
acceptance_criteria:
- Implements IBlockEncryptionProvider interface
- Encrypt uses current active DEK from key store and returns the active epoch
- Decrypt accepts key epoch parameter and looks up correct DEK from key store
- Wrong epoch or missing DEK returns clear error
- Round-trip encrypt/decrypt with multiple DEKs produces correct plaintext
- Works with EncryptionPolicy to determine which block types to encrypt
- Properly disposes all DEK key material on disposal
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-55
points: 5
priority: must
status: done
tags: []
title: Implement KeyWrappingEncryptionProvider
updated: '2026-02-24'
---

As a developer, I want a KeyWrappingEncryptionProvider that wraps AesGcmBlockEncryptionProvider with DEK lookup from the key store, so that encrypt uses the active DEK and decrypt looks up the correct DEK by key epoch from the block header.