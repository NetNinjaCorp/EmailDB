using System;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Interface for encryption providers that can encrypt and decrypt block payloads.
/// </summary>
public interface IEncryptionProvider
{
    /// <summary>
    /// The encryption algorithm this provider implements.
    /// </summary>
    EncryptionAlgorithm Algorithm { get; }
    
    /// <summary>
    /// Encrypts the given payload with the specified key.
    /// </summary>
    /// <param name="payload">The data to encrypt</param>
    /// <param name="key">The encryption key</param>
    /// <param name="blockId">The block ID for additional entropy</param>
    /// <returns>Encrypted payload</returns>
    Task<Result<byte[]>> EncryptAsync(byte[] payload, byte[] key, long blockId);
    
    /// <summary>
    /// Decrypts the given encrypted payload with the specified key.
    /// </summary>
    /// <param name="encryptedPayload">The encrypted data</param>
    /// <param name="key">The decryption key</param>
    /// <param name="blockId">The block ID for additional entropy</param>
    /// <returns>Decrypted payload</returns>
    Task<Result<byte[]>> DecryptAsync(byte[] encryptedPayload, byte[] key, long blockId);
    
    /// <summary>
    /// Generates a new encryption key for this algorithm.
    /// </summary>
    /// <returns>A new encryption key</returns>
    byte[] GenerateKey();
    
    /// <summary>
    /// Gets the expected key size for this algorithm in bytes.
    /// </summary>
    int KeySizeBytes { get; }
}

/// <summary>
/// Base class for encryption providers with common functionality.
/// </summary>
public abstract class EncryptionProviderBase : IEncryptionProvider
{
    public abstract EncryptionAlgorithm Algorithm { get; }
    public abstract int KeySizeBytes { get; }
    
    public abstract Task<Result<byte[]>> EncryptAsync(byte[] payload, byte[] key, long blockId);
    public abstract Task<Result<byte[]>> DecryptAsync(byte[] encryptedPayload, byte[] key, long blockId);
    
    public virtual byte[] GenerateKey()
    {
        var key = new byte[KeySizeBytes];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(key);
        return key;
    }
    
    protected void ValidateKey(byte[] key)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"Key must be exactly {KeySizeBytes} bytes for {Algorithm}");
    }
    
    protected byte[] DeriveNonce(long blockId, int nonceSize)
    {
        // Derive a deterministic nonce from blockId for additional security
        var nonce = new byte[nonceSize];
        var blockIdBytes = BitConverter.GetBytes(blockId);
        
        // Fill nonce with blockId bytes repeated as needed
        for (int i = 0; i < nonceSize; i++)
        {
            nonce[i] = blockIdBytes[i % 8];
        }
        
        // XOR with a constant to avoid all-zero nonces
        var constant = System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(0x1337DEADBEEF1337L));
        for (int i = 0; i < nonceSize; i++)
        {
            nonce[i] ^= constant[i % 32];
        }
        
        return nonce;
    }
}