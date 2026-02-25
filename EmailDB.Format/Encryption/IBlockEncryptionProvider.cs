using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Provides block-level encryption and decryption for EmailDB payloads.
/// Implementations encrypt payload bytes after serialization and decrypt
/// before deserialization, leaving block headers unencrypted.
/// </summary>
public interface IBlockEncryptionProvider : IDisposable
{
    /// <summary>
    /// Encrypts a plaintext payload. Returns ciphertext in the format:
    /// [Nonce (12 bytes)] [Ciphertext (N bytes)] [Auth Tag (16 bytes)]
    /// </summary>
    /// <param name="plaintext">The serialized payload to encrypt.</param>
    /// <param name="blockType">The type of block being encrypted (for policy checks).</param>
    /// <param name="blockId">The block ID (used in nonce generation).</param>
    /// <returns>Encrypted payload bytes.</returns>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, BlockType blockType, long blockId);

    /// <summary>
    /// Decrypts a ciphertext payload previously encrypted by <see cref="Encrypt"/>.
    /// Verifies the authentication tag and throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// on tamper or wrong key.
    /// </summary>
    /// <param name="ciphertext">The encrypted payload (nonce + ciphertext + auth tag).</param>
    /// <param name="blockType">The type of block being decrypted.</param>
    /// <param name="blockId">The block ID (used in nonce verification).</param>
    /// <returns>Decrypted plaintext bytes.</returns>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, BlockType blockType, long blockId);

    /// <summary>
    /// Determines whether a given block type should be encrypted according to the current policy.
    /// </summary>
    bool ShouldEncrypt(BlockType blockType);

    /// <summary>
    /// The number of overhead bytes added by encryption (nonce + auth tag).
    /// For AES-256-GCM: 12 (nonce) + 16 (tag) = 28 bytes.
    /// </summary>
    int OverheadBytes { get; }

    /// <summary>
    /// True if encryption is active; false for the null/pass-through provider.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// The active key epoch used for encryption. Callers should write this
    /// into the block header Flags (bits 1-7) so Decrypt can look up the
    /// correct DEK later. Returns 0 for providers that don't support key rotation.
    /// </summary>
    int ActiveEpoch { get; }

    /// <summary>
    /// Decrypts a ciphertext payload using the DEK identified by the given key epoch.
    /// Default implementation delegates to the standard <see cref="Decrypt(ReadOnlySpan{byte}, BlockType, long)"/>
    /// method, which is correct for single-key providers.
    /// </summary>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, BlockType blockType, long blockId, int keyEpoch)
        => Decrypt(ciphertext, blockType, blockId);
}
