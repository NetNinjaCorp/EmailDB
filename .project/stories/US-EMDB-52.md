---
acceptance_criteria:
- ADR-014 exists in DECISIONS.md with full rationale
- ADR-003 updated to note checksum portion superseded
- EmailDB_FileFormat_Spec.md shows 16-byte checksums and 81-byte overhead
- ARCHITECTURE.md references BLAKE3-128 not CRC32
- PROJECT.md references BLAKE3-128 not CRC32
- VISION.md references BLAKE3-128 not CRC32
- EPIC-EMDB-9 description updated
- Crc32.NET removed from all csproj files
- No remaining Force.Crc32 references outside ZonetreeRef/
created: '2026-02-24'
epic_id: EPIC-EMDB-10
id: US-EMDB-52
points: 3
priority: must
status: done
tags: []
title: Remove Crc32.NET dependency and add ADR for BLAKE3-128 migration
updated: '2026-02-24'
---

As a developer, I want the CRC32→BLAKE3-128 decision documented as an ADR and all Crc32.NET package references removed so the codebase has no vestigial CRC32 dependencies.

Changes:
- Add ADR-014 to DECISIONS.md: CRC32 → BLAKE3-128 Block Checksums. Document the rationale (32-bit keyspace insufficient, no tamper resistance, inconsistent with BLAKE3 hash chains, prerequisite for encryption epic).
- Update ADR-003 status to "Superseded by ADR-014" for the checksum portion.
- Update EmailDB_FileFormat_Spec.md: checksum fields 4→16 bytes, total fixed overhead 57→81, checksum algorithm line.
- Update ARCHITECTURE.md: CRC32 integrity → BLAKE3-128 integrity.
- Update PROJECT.md: CRC32 checksums → BLAKE3-128 checksums.
- Update VISION.md: CRC32 integrity verification → BLAKE3-128 integrity verification.
- Update EPIC-EMDB-9 description: replace "CRC32 checksums (computed on ciphertext)" with "BLAKE3-128 checksums (computed on ciphertext)".
- Remove Crc32.NET from all .csproj files (EmailDB.Format, EmailDB.UnitTests, EmailDB.Benchmark.SQLite).
- Verify no remaining references to Force.Crc32, Crc32Algorithm, or Crc32.NET anywhere in the codebase (excluding ZonetreeRef/ which is third-party reference code).