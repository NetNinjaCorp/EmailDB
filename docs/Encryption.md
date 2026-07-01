# Encryption (v3)

Operational design for at-rest encryption. Byte layouts and the corruption contract are normative in the [File Format Spec](../EmailDB_FileFormat_Spec.md) Sections 3, 9, 13.

## 1. Key hierarchy

```
Password (NFC-normalized, UTF-8)
    │
    ▼  Argon2id (params from superblock KdfParams; default 64 MB / 3 iter / 4 lanes)
KEK (32 B) ── verifies KeyVerificationToken ("EMDB" under GCM)
    │
    ▼  AES-256-GCM
KeyStore block (type 8): { ActiveEpoch, Entries[]: epoch → DEK (32 B), Retired }
    │
    ▼  AES-256-GCM per block, epoch stamped in the 2-byte header KeyEpoch field
Data block payloads
```

Two facts make the whole design work: **old blocks are never re-encrypted unless compaction chooses to**, and **the KEK never touches data blocks** — it only wraps the DEK table.

## 2. Bootstrap (open sequence)

1. Superblock → `EncryptionEnabled`, `KdfType`, `KdfParams`, `Salt`, `KeyVerificationToken`, KeyStore pointer
2. Password → NFC normalize → Argon2id(stored params, stored salt) → KEK
3. Decrypt `KeyVerificationToken`: failure = wrong password **or tampered KDF fields** (they're implicitly authenticated — altering Salt/KdfParams changes the derived KEK, which breaks the token). Fail fast, distinct error.
4. Decrypt KeyStore with KEK → DEK table in memory
5. Construct the block encryption provider: encrypt with `DEK[ActiveEpoch]`, decrypt by each block's header `KeyEpoch`

Files carry their own KDF configuration — implementations MUST never rely on compiled-in KDF constants, or parameter upgrades brick existing files.

## 3. Per-block encryption

- AES-256-GCM; on-disk payload `Nonce (12) ‖ Ciphertext ‖ Tag (16)` = +28 B/block
- **Nonce: 12 random CSPRNG bytes per operation** (never derived from BlockId — re-encryption of the same block under the same DEK would collapse uniqueness)
- **AAD = `FileId ‖ BlockId ‖ BlockType ‖ KeyEpoch`** (35 B), mandatory both directions. A ciphertext moved to a different block, type, epoch, or file fails the tag even though every checksum passes — transplant attacks are dead
- `PayloadChecksum` covers ciphertext, so scrubbing/scan integrity checks need no keys. Verify order: checksum (corruption) → tag (wrong key / tamper) — distinct error classes

## 4. Policy — what gets encrypted

| Blocks | Default | Full | Why |
|--------|---------|------|-----|
| EmailContent, EmailMetadata, FolderPage, FolderPageDirectory, FolderDeltaLog, FolderTree, WAL | ✔ | ✔ | Content and content-derived |
| FTS (14–17), BloomFilter (18) | ✔ **always** | ✔ | Trigrams and filter bits reverse to the indexed text |
| BTree nodes + IndexRoots (4–6) | ✘ | ✔ | Keys are opaque hashes; plaintext enables keyless integrity verification |
| Metadata, Cleanup, Checkpoint | ✘ | ✘ | Needed for recovery before keys exist |
| KeyStore | KEK-encrypted always | | |

The `Encrypted` flag is stamped per block, so files mixing policies (after a policy change) read correctly block-by-block.

## 5. Operations

**Password change — O(1), ~one block + superblock:** decrypt KeyStore with old KEK → new Salt (optionally upgraded KdfParams) → new KEK → append re-encrypted KeyStore block → superblock write (new Salt/KdfParams/token/pointer). Crash before the superblock write leaves the old password fully functional. Zero data blocks touched.

**Key rotation — O(1):** append KeyStore block with fresh DEK at `ActiveEpoch + 1`; superblock update. Existing blocks keep their epoch and DEK. Rotation MUST fail rather than exceed epoch 65535; compaction re-encryption consolidates epochs long before that.

**Compaction re-encryption (optional):** rewrite copied payloads under the active epoch, then prune unreferenced DEKs — the blast-radius cleanup mechanism. See [Compaction](Compaction.md) Section 4.

## 6. Threat model — what this does and doesn't protect

Protected:
- Content confidentiality at rest (stolen file/disk) — everything content-derived is ciphertext under Default
- Tamper evidence: block corruption (checksums), payload swaps (AAD), index manipulation (Merkle + RootHash), KDF-parameter downgrade (token)
- Old-epoch exposure containment: a leaked DEK exposes only its epoch's blocks

Not protected (by design, documented honestly):
- **Traffic-shape metadata**: block sizes, counts, ULID timestamps, and folder structure sizes are visible in a Default-policy file. Full policy hides index keys but sizes/timing remain.
- **A live, unlocked process**: DEKs are in memory while open. Implementations MUST zeroize password bytes, KEK, and DEKs when scope ends, but memory-dump attacks on a running process are out of scope.
- **Availability**: an attacker who can write to the file can destroy data (append-only + checksums detect, don't prevent).
- **Superblock destruction**: if both slots are destroyed and no backup of Salt/KdfParams exists, encrypted data is unrecoverable — by design (that's what "encrypted" means). Users should be told plainly: password + intact superblock, or a backup.

## 7. Implementation requirements checklist

- NFC-normalize passwords before KDF (cross-platform lockout bug otherwise)
- Random nonces from a CSPRNG only; never counters, never IDs
- Always pass AAD; never expose a decrypt path that skips it
- Zeroize key material buffers; prefer pinned/`fixed` buffers for DEKs
- Read KDF params from the superblock, never constants
- Fail rotation at epoch exhaustion; never wrap
- Wrong-password, corruption, and tamper must surface as three distinguishable errors
