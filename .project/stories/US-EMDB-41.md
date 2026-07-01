---
acceptance_criteria:
- DeriveFromPassword uses Argon2id with 64MB memory 3 iterations 4 parallelism
- Same password + salt always produces same 32-byte key
- Different salt produces different key
- LoadFromKeyFile reads exactly 32 bytes
- GenerateSalt produces 16 cryptographically random bytes
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-41
points: 3
priority: must
status: done
tags: []
title: Implement KeyDerivation with Argon2id and key file support
updated: '2026-02-24'
---

As a user, I want strong key derivation from passwords using Argon2id so that my encryption keys are resistant to brute-force attacks, with key file support as an alternative.