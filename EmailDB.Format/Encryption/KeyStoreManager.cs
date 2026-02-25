using System.Security.Cryptography;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Manages encryption and decryption of the key store payload using a KEK (Key Encryption Key)
/// with AES-256-GCM. The key store holds the DEK table (epoch-to-DEK mappings).
/// </summary>
public class KeyStoreManager
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly iBlockContentSerializer _serializer;

    public KeyStoreManager(iBlockContentSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Serializes and encrypts a KeyStoreContent payload with the given KEK using AES-256-GCM.
    /// Output format: [Nonce (12 bytes)] [Ciphertext (N bytes)] [Auth Tag (16 bytes)]
    /// </summary>
    public byte[] EncryptKeyStore(KeyStoreContent content, byte[] kek)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (kek == null || kek.Length != KeySize)
            throw new ArgumentException($"KEK must be exactly {KeySize} bytes.", nameof(kek));

        var plaintext = _serializer.Serialize(content);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Output: Nonce(12) + Ciphertext(N) + AuthTag(16)
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result.AsSpan(0));
        ciphertext.CopyTo(result.AsSpan(NonceSize));
        tag.CopyTo(result.AsSpan(NonceSize + ciphertext.Length));

        return result;
    }

    /// <summary>
    /// Rotates the active DEK by generating a new 32-byte random DEK, assigning it the next
    /// epoch number, marking the previous active epoch as retained (not retired), and setting
    /// the new epoch as active. Returns the updated KeyStoreContent.
    /// </summary>
    public KeyStoreContent RotateKey(KeyStoreContent keyStore)
    {
        if (keyStore == null) throw new ArgumentNullException(nameof(keyStore));
        if (keyStore.Entries.Count == 0)
            throw new InvalidOperationException("Key store must contain at least one entry to rotate.");

        var previousEntry = keyStore.Entries.FirstOrDefault(e => e.Epoch == keyStore.ActiveEpoch);
        if (previousEntry == null)
            throw new InvalidOperationException($"Active epoch {keyStore.ActiveEpoch} not found in key store entries.");

        int newEpoch = keyStore.Entries.Max(e => e.Epoch) + 1;

        var newDek = new byte[KeySize];
        RandomNumberGenerator.Fill(newDek);

        keyStore.Entries.Add(new KeyStoreEntry
        {
            Epoch = newEpoch,
            DEK = newDek,
            Timestamp = DateTime.UtcNow,
            Retired = false
        });

        keyStore.ActiveEpoch = newEpoch;

        return keyStore;
    }

    /// <summary>
    /// Decrypts and deserializes a KeyStoreContent payload with the given KEK using AES-256-GCM.
    /// Throws <see cref="CryptographicException"/> if the KEK is wrong or data is tampered.
    /// </summary>
    public KeyStoreContent DecryptKeyStore(byte[] encrypted, byte[] kek)
    {
        if (encrypted == null) throw new ArgumentNullException(nameof(encrypted));
        if (kek == null || kek.Length != KeySize)
            throw new ArgumentException($"KEK must be exactly {KeySize} bytes.", nameof(kek));
        if (encrypted.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted data is too short to contain nonce and auth tag.");

        var nonce = encrypted.AsSpan(0, NonceSize);
        var ciphertext = encrypted.AsSpan(NonceSize, encrypted.Length - NonceSize - TagSize);
        var tag = encrypted.AsSpan(encrypted.Length - TagSize, TagSize);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return _serializer.Deserialize<KeyStoreContent>(plaintext);
    }
}
