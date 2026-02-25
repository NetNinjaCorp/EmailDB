using System.Security.Cryptography;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Encryption provider that wraps AesGcmBlockEncryptionProvider with DEK lookup
/// from a key store. Encrypt uses the current active DEK, and Decrypt looks up
/// the correct DEK by key epoch from the block header.
/// </summary>
public sealed class KeyWrappingEncryptionProvider : IBlockEncryptionProvider
{
    private readonly Dictionary<int, AesGcmBlockEncryptionProvider> _providers = new();
    private readonly int _activeEpoch;
    private readonly EncryptionPolicy _policy;
    private bool _disposed;

    public KeyWrappingEncryptionProvider(KeyStoreContent keyStore, EncryptionPolicy policy)
    {
        if (keyStore == null) throw new ArgumentNullException(nameof(keyStore));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _activeEpoch = keyStore.ActiveEpoch;

        foreach (var entry in keyStore.Entries)
        {
            _providers[entry.Epoch] = new AesGcmBlockEncryptionProvider(entry.DEK);
        }

        if (!_providers.ContainsKey(_activeEpoch))
            throw new ArgumentException($"Active epoch {_activeEpoch} not found in key store entries.", nameof(keyStore));
    }

    public int OverheadBytes => AesGcmBlockEncryptionProvider.NonceSize + AesGcmBlockEncryptionProvider.TagSize;

    public bool IsEnabled => true;

    /// <summary>
    /// Returns the active key epoch. Callers should write this into the block header
    /// so Decrypt can look up the correct DEK later.
    /// </summary>
    public int ActiveEpoch => _activeEpoch;

    public bool ShouldEncrypt(BlockType blockType) => _policy.ShouldEncrypt(blockType);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, BlockType blockType, long blockId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _providers[_activeEpoch].Encrypt(plaintext, blockType, blockId);
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, BlockType blockType, long blockId)
    {
        return Decrypt(ciphertext, blockType, blockId, _activeEpoch);
    }

    /// <summary>
    /// Decrypts using the DEK identified by the given key epoch.
    /// </summary>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, BlockType blockType, long blockId, int keyEpoch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_providers.TryGetValue(keyEpoch, out var provider))
            throw new CryptographicException($"No DEK found for key epoch {keyEpoch}.");

        return provider.Decrypt(ciphertext, blockType, blockId);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var provider in _providers.Values)
                provider.Dispose();
            _providers.Clear();
            _disposed = true;
        }
    }
}
