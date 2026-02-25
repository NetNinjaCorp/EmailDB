using System.Security.Cryptography;
using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// AES-256-GCM encryption provider for block payloads.
/// Output format: [Nonce (12 bytes)] [Ciphertext (N bytes)] [Auth Tag (16 bytes)]
/// Nonce is constructed as: [BlockId (8 bytes, little-endian)] [Random (4 bytes)]
/// </summary>
public sealed class AesGcmBlockEncryptionProvider : IBlockEncryptionProvider
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32; // AES-256

    private readonly byte[] _key;
    private bool _disposed;

    public AesGcmBlockEncryptionProvider(byte[] key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be exactly {KeySize} bytes for AES-256-GCM.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public int OverheadBytes => NonceSize + TagSize; // 28

    public bool IsEnabled => true;

    public int ActiveEpoch => 0;

    public bool ShouldEncrypt(BlockType blockType) => true;

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, BlockType blockType, long blockId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var nonce = new byte[NonceSize];
        // First 8 bytes: blockId in little-endian
        BitConverter.TryWriteBytes(nonce.AsSpan(0, 8), blockId);
        // Last 4 bytes: random
        RandomNumberGenerator.Fill(nonce.AsSpan(8, 4));

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Output: Nonce(12) + Ciphertext(N) + AuthTag(16)
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result.AsSpan(0));
        ciphertext.CopyTo(result.AsSpan(NonceSize));
        tag.CopyTo(result.AsSpan(NonceSize + ciphertext.Length));

        return result;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, BlockType blockType, long blockId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ciphertext.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to contain nonce and auth tag.");

        var nonce = ciphertext[..NonceSize];
        var encryptedData = ciphertext[NonceSize..^TagSize];
        var tag = ciphertext[^TagSize..];

        var plaintext = new byte[encryptedData.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, encryptedData, tag, plaintext);

        return plaintext;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}
