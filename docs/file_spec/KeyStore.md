# KeyStore Block (BlockType = 10)

Holds the table of Data Encryption Keys (DEKs) used for per-block encryption. The KeyStore payload itself is encrypted with the KEK (Key Encryption Key), not with the normal block encryption mechanism.

## Block ID

Not a system block — uses standard block ID allocation.

## Payload Structure

The KeyStore payload is **double-encrypted**: the outer block uses standard block encryption (if enabled), and the inner content is AES-256-GCM encrypted with the KEK.

### Inner Content (Protobuf, after KEK decryption)

| Field | Type | Description |
|-------|------|-------------|
| ActiveEpoch | int32 | The epoch number currently used for encrypting new blocks. |
| Entries | List\<KeyStoreEntry\> | Table of all DEKs, indexed by epoch. |

### KeyStoreEntry

| Field | Type | Description |
|-------|------|-------------|
| Epoch | int32 | Monotonically increasing epoch identifier. |
| DEK | byte[32] | Data Encryption Key (AES-256). |
| Timestamp | DateTime | When this DEK was generated. |
| Retired | bool | If true, no new blocks should use this epoch. |

### KEK Encryption Format

The serialized `KeyStoreContent` is encrypted with AES-256-GCM using the KEK:

```
[Nonce (12 bytes, random)]
[Ciphertext (N bytes)]
[Auth Tag (16 bytes)]
```

The KEK is derived from the user's password via Argon2id (see [Encryption Header](Encryption_Header.md)), or loaded directly from a key file.

## Key Rotation

Performed by `KeyStoreManager.RotateKey()`:

1. Generate new 32-byte random DEK
2. Assign `newEpoch = max(existing epochs) + 1`
3. Add new `KeyStoreEntry` with `Retired = false`
4. Set `ActiveEpoch = newEpoch`
5. Re-encrypt and write updated KeyStore block

Existing blocks remain encrypted with their original DEK. The `KeyEpoch` in each block's Flags byte identifies which DEK to use for decryption.

## Password Change

1. Derive old KEK from old password + existing salt
2. Decrypt KeyStore with old KEK
3. Generate new salt, derive new KEK from new password + new salt
4. Re-encrypt KeyStore with new KEK
5. Update [Encryption Header](Encryption_Header.md) with new salt and verification token
6. Write new KeyStore block

**No data blocks are touched.** Password change is O(1).

## Encryption Policy

The KeyStore block is listed as **not encrypted** under the Default block encryption policy (the block payload encryption layer). Instead, the content is directly encrypted with the KEK via `KeyStoreManager`, which is a separate encryption path.

## Source Reference

- `KeyStoreContent.cs` — model
- `KeyStoreManager.cs` — encrypt/decrypt/rotate operations
- `KeyWrappingEncryptionProvider.cs` — DEK lookup by epoch
