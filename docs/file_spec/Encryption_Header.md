# Encryption Header

File-level header written at the very start of an encrypted `.emdb` file, before the first block. Stores the cryptographic parameters needed to derive the KEK and verify the password.

## Binary Layout

### Fixed Portion (27 bytes)

```
Offset  Size  Field
──────  ────  ─────
0       4     Magic              ("EMDB" in ASCII: 0x45 0x4D 0x44 0x42)
4       1     SchemeVersion      (byte, current: 0x01)
5       1     AlgorithmId        (byte, block encryption algorithm)
6       1     KdfType            (byte, key derivation function)
7       16    Salt               (random bytes for KDF)
23      4     TokenLength        (int32 LE, length of verification token)
```

### Variable Portion

```
27      var   KeyVerificationToken  (TokenLength bytes, encrypted "EMDB" for password check)
```

**Total size: 27 + TokenLength bytes.**

## Field Details

### AlgorithmId

| Value | Algorithm | Description |
|-------|-----------|-------------|
| 0x01 | AES-256-GCM | 32-byte key, 12-byte nonce, 16-byte auth tag |

### KdfType

| Value | Algorithm | Description |
|-------|-----------|-------------|
| 0x01 | Argon2id | Memory-hard KDF. Parameters stored externally (see below). |

### Key Derivation Parameters (Argon2id)

Currently hardcoded in `KeyDerivation.cs`:

| Parameter | Value |
|-----------|-------|
| Memory | 65,536 KB (64 MB) |
| Iterations | 3 |
| Parallelism | 4 |
| Output key size | 32 bytes (AES-256) |
| Salt size | 16 bytes |

### Key Verification Token

On file creation:
1. Derive KEK from password + salt using Argon2id
2. Encrypt the known plaintext "EMDB" (4 bytes) with the KEK using AES-256-GCM
3. Store the ciphertext (nonce + encrypted data + auth tag) as the token

On file open:
1. Derive KEK from password + salt
2. Decrypt the token
3. Compare to "EMDB" — if it matches, the password is correct
4. This provides early key verification before attempting to decrypt any blocks

## Per-Block Encryption

Once the KEK is derived and the [KeyStore](KeyStore.md) is decrypted, individual block payloads are encrypted with DEKs using AES-256-GCM:

### Nonce Construction (12 bytes)

```
[BlockId (8 bytes, int64 LE)] [Random (4 bytes)]
```

The BlockId provides uniqueness across blocks; the random suffix provides uniqueness across re-encryptions of the same block.

### Encrypted Payload Format

```
[Nonce (12 bytes)]
[Ciphertext (N bytes)]
[Auth Tag (16 bytes)]
```

**Overhead per encrypted block: 28 bytes** (12 nonce + 16 tag).

### Encryption Policy

Controls which block types are encrypted:

| Policy | Encrypted | Plaintext |
|--------|-----------|-----------|
| **Default** | EmailContent, Folder, FolderTree, Segment, WAL | Metadata, BTreeLeaf, BTreeInternal, IndexRoot, Cleanup |
| **Full** | All except Metadata | Metadata only |

KeyStore blocks use a separate encryption path (KEK-encrypted via `KeyStoreManager`), not the block encryption policy.

## Detection

`EncryptionHeaderManager.HasEncryptionHeader()` checks whether a file begins with the "EMDB" magic bytes to determine if an encryption header is present. This check does not advance the stream position.

## Source Reference

- `EncryptionHeader.cs` — model and constants
- `EncryptionHeaderManager.cs` — read/write/detect
- `KeyDerivation.cs` — Argon2id parameters and key derivation
- `AesGcmBlockEncryptionProvider.cs` — per-block encryption
- `EncryptionPolicy.cs` — block type encryption rules
