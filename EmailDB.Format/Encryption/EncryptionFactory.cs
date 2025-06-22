using System;
using System.Collections.Generic;
using System.Linq;
using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Factory for creating encryption providers based on algorithm type.
/// </summary>
public static class EncryptionFactory
{
    private static readonly Dictionary<EncryptionAlgorithm, Func<IEncryptionProvider>> _providers = new()
    {
        [EncryptionAlgorithm.None] = () => new NoEncryptionProvider(),
        [EncryptionAlgorithm.AES256_GCM] = () => new Aes256GcmEncryptionProvider(),
        [EncryptionAlgorithm.ChaCha20_Poly1305] = () => new ChaCha20Poly1305EncryptionProvider(),
        [EncryptionAlgorithm.AES256_CBC_HMAC] = () => new Aes256CbcHmacEncryptionProvider()
    };
    
    /// <summary>
    /// Creates an encryption provider for the specified algorithm.
    /// </summary>
    /// <param name="algorithm">The encryption algorithm</param>
    /// <returns>The encryption provider</returns>
    /// <exception cref="NotSupportedException">If the algorithm is not supported</exception>
    public static IEncryptionProvider CreateProvider(EncryptionAlgorithm algorithm)
    {
        if (_providers.TryGetValue(algorithm, out var factory))
        {
            return factory();
        }
        
        throw new NotSupportedException($"Encryption algorithm {algorithm} is not supported");
    }
    
    /// <summary>
    /// Gets all supported encryption algorithms.
    /// </summary>
    /// <returns>Array of supported algorithms</returns>
    public static EncryptionAlgorithm[] GetSupportedAlgorithms()
    {
        return _providers.Keys.ToArray();
    }
    
    /// <summary>
    /// Checks if an encryption algorithm is supported.
    /// </summary>
    /// <param name="algorithm">The algorithm to check</param>
    /// <returns>True if supported</returns>
    public static bool IsSupported(EncryptionAlgorithm algorithm)
    {
        return _providers.ContainsKey(algorithm);
    }
    
    /// <summary>
    /// Gets the key size in bytes for a specific encryption algorithm.
    /// </summary>
    /// <param name="algorithm">The encryption algorithm</param>
    /// <returns>Key size in bytes</returns>
    public static int GetKeySizeBytes(EncryptionAlgorithm algorithm)
    {
        var provider = CreateProvider(algorithm);
        return provider.KeySizeBytes;
    }
    
    /// <summary>
    /// Generates a new key for the specified algorithm.
    /// </summary>
    /// <param name="algorithm">The encryption algorithm</param>
    /// <returns>A new encryption key</returns>
    public static byte[] GenerateKey(EncryptionAlgorithm algorithm)
    {
        var provider = CreateProvider(algorithm);
        return provider.GenerateKey();
    }
}