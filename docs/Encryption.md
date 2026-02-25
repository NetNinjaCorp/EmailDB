# Encryption

EmailDB provides optional per-block AES-256-GCM encryption with multi-epoch key rotation. Encryption is transparent to upper layers -- the cache and serialization systems handle encrypt/decrypt automatically.

## Architecture

```
Password/KeyFile
      |
      v
  KeyDerivation (Argon2id) --> KEK (Key Encryption Key)
      |
      v
  KeyStoreManager (encrypt/decrypt DEK table with KEK)
      |
      v
  KeyWrappingEncryptionProvider (multi-epoch DEK lookup)
      |
      v
  AesGcmBlockEncryptionProvider (per-block encrypt/decrypt)
      |
      v
  CacheManager / RawBlockManager (transparent integration)
```

## Key Hierarchy

| Key | Purpose | Storage |
|-----|---------|---------|
| **Password / Key File** | User credential | External (never stored in file) |
| **KEK** (Key Encryption Key) | Wraps/unwraps DEKs | Derived at runtime via Argon2id, never stored |
| **DEK** (Data Encryption Key) | Encrypts block payloads | Stored in KeyStore block, wrapped with KEK |

## Key Derivation

- **Algorithm:** Argon2id
- **Parameters:** 65,536 KB memory, 3 iterations, 4-way parallelism
- **Salt:** 16 bytes, random, stored in the file's encryption header
- **Output:** 32-byte KEK

Alternative: 32-byte key loaded directly from a key file (no derivation).

## Encryption Header

Written at the start of encrypted files, before the first block:

```
Magic           4 bytes     Identifies file as encrypted
SchemeVersion   1 byte      Encryption scheme version
AlgorithmId     1 byte      Algorithm identifier
KdfType         1 byte      Key derivation function type
Salt            16 bytes    KDF salt
TokenLength     4 bytes     Length of verification token
Token           variable    Encrypted verification token (validates correct password)
```

## KeyStore Block (BlockType = 10)

Contains the encrypted DEK table:

| Field | Description |
|-------|-------------|
| ActiveEpoch | Current epoch used for new encryptions |
| Entries[] | Array of `{ Epoch, DEK[32B], Timestamp, Retired }` |

The KeyStore is encrypted with the KEK. Each DEK is 32 bytes (AES-256).

## Per-Block Encryption

- **Algorithm:** AES-256-GCM
- **Nonce construction:** `[BlockId bytes (8B little-endian)] + [Random (4B)]` = 12 bytes
- **Output format:** `[Nonce (12B)] [Ciphertext (N)] [AuthTag (16B)]`
- **Overhead:** 28 bytes per encrypted block (12B nonce + 16B auth tag)
- **Key selection:** The block header's `KeyEpoch` field identifies which DEK to use

## Encryption Policy

Controls which block types are encrypted:

| Policy | Encrypted | Plaintext |
|--------|-----------|-----------|
| **Default** | EmailContent, Folder, FolderTree, Segment, WAL, FolderMeta, FolderDeltaLog | Metadata, BTreeLeaf, BTreeInternal, IndexRoot, Cleanup |
| **Full** | All except Metadata | Metadata |

BTree nodes remain plaintext because their keys are SHA3-256 hashes (opaque). The `KeyEpoch` in the block header ensures correct DEK lookup at read time regardless of policy.

## Key Rotation

1. Generate new 32-byte random DEK
2. Assign it the next epoch number
3. Mark it as the active epoch in the KeyStore
4. Write updated KeyStore block (wrapped with current KEK)
5. All future writes use the new DEK
6. Existing blocks remain readable with their original DEK (old epochs are retained, not retired)

**No re-encryption of existing blocks.** Old epochs stay valid indefinitely.

## Password Change

1. Derive old KEK from old password + existing salt
2. Decrypt KeyStore with old KEK
3. Generate new salt, derive new KEK from new password + new salt
4. Re-encrypt KeyStore with new KEK
5. Update encryption header with new salt
6. Write new KeyStore block

Data blocks are unaffected -- they are encrypted with DEKs, not the KEK.

## Sync Considerations

- **Same encryption domain (recommended):** Both replicas share the same password/key file. KeyStore block syncs directly. Both sides decrypt any block with the same key material.
- **KeyStore sync ordering:** If the active rotates keys, the updated KeyStore must arrive at the backup before any blocks encrypted with the new epoch.
- **Separate encryption domains:** Would require decrypt-on-source, re-encrypt-on-destination. Not recommended for v1.
