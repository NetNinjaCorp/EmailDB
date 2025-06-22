using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// AES-256-GCM encryption provider with authenticated encryption.
/// </summary>
public class Aes256GcmEncryptionProvider : EncryptionProviderBase
{
    public override EncryptionAlgorithm Algorithm => EncryptionAlgorithm.AES256_GCM;
    public override int KeySizeBytes => 32; // 256 bits
    
    private const int NonceSize = 12; // 96 bits for GCM
    private const int TagSize = 16;   // 128 bits for authentication tag
    
    public override async Task<Result<byte[]>> EncryptAsync(byte[] payload, byte[] key, long blockId)
    {
        try
        {
            ValidateKey(key);
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            
            // Derive nonce from blockId for deterministic but unique nonces
            var nonce = DeriveNonce(blockId, NonceSize);
            
            using var aesGcm = new AesGcm(key, TagSize);
            
            // Allocate buffer for encrypted data + nonce + tag
            var encrypted = new byte[payload.Length];
            var tag = new byte[TagSize];
            
            // Encrypt the payload
            aesGcm.Encrypt(nonce, payload, encrypted, tag);
            
            // Combine nonce + encrypted data + tag
            var result = new byte[NonceSize + encrypted.Length + TagSize];
            Array.Copy(nonce, 0, result, 0, NonceSize);
            Array.Copy(encrypted, 0, result, NonceSize, encrypted.Length);
            Array.Copy(tag, 0, result, NonceSize + encrypted.Length, TagSize);
            
            return Result<byte[]>.Success(result);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"AES-256-GCM encryption failed: {ex.Message}");
        }
    }
    
    public override async Task<Result<byte[]>> DecryptAsync(byte[] encryptedPayload, byte[] key, long blockId)
    {
        try
        {
            ValidateKey(key);
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            
            // Must have at least nonce + tag
            if (encryptedPayload.Length < NonceSize + TagSize)
                return Result<byte[]>.Failure("Encrypted payload too small for AES-256-GCM");
            
            // Extract components
            var nonce = new byte[NonceSize];
            var ciphertext = new byte[encryptedPayload.Length - NonceSize - TagSize];
            var tag = new byte[TagSize];
            
            Array.Copy(encryptedPayload, 0, nonce, 0, NonceSize);
            Array.Copy(encryptedPayload, NonceSize, ciphertext, 0, ciphertext.Length);
            Array.Copy(encryptedPayload, NonceSize + ciphertext.Length, tag, 0, TagSize);
            
            // Verify nonce matches expected for this blockId
            var expectedNonce = DeriveNonce(blockId, NonceSize);
            if (!nonce.AsSpan().SequenceEqual(expectedNonce))
                return Result<byte[]>.Failure("Nonce mismatch - possible tampering or corruption");
            
            using var aesGcm = new AesGcm(key, TagSize);
            
            // Decrypt the data
            var decrypted = new byte[ciphertext.Length];
            aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);
            
            return Result<byte[]>.Success(decrypted);
        }
        catch (CryptographicException ex)
        {
            return Result<byte[]>.Failure($"AES-256-GCM decryption failed (authentication or key error): {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"AES-256-GCM decryption failed: {ex.Message}");
        }
    }
}