using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// AES-256-CBC with HMAC-SHA256 encryption provider (encrypt-then-MAC).
/// </summary>
public class Aes256CbcHmacEncryptionProvider : EncryptionProviderBase
{
    public override EncryptionAlgorithm Algorithm => EncryptionAlgorithm.AES256_CBC_HMAC;
    public override int KeySizeBytes => 64; // 32 bytes for AES + 32 bytes for HMAC
    
    private const int AesKeySize = 32;   // 256 bits for AES
    private const int HmacKeySize = 32;  // 256 bits for HMAC
    private const int IvSize = 16;       // 128 bits for AES-CBC IV
    private const int HmacSize = 32;     // 256 bits for HMAC-SHA256
    
    public override async Task<Result<byte[]>> EncryptAsync(byte[] payload, byte[] key, long blockId)
    {
        try
        {
            ValidateKey(key);
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            
            // Split the key into AES and HMAC keys
            var aesKey = new byte[AesKeySize];
            var hmacKey = new byte[HmacKeySize];
            Array.Copy(key, 0, aesKey, 0, AesKeySize);
            Array.Copy(key, AesKeySize, hmacKey, 0, HmacKeySize);
            
            // Derive IV from blockId for deterministic but unique IVs
            var iv = DeriveNonce(blockId, IvSize);
            
            // Encrypt with AES-CBC
            byte[] encrypted;
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                
                using var encryptor = aes.CreateEncryptor();
                encrypted = encryptor.TransformFinalBlock(payload, 0, payload.Length);
            }
            
            // Create data to authenticate: IV + encrypted data
            var dataToAuthenticate = new byte[IvSize + encrypted.Length];
            Array.Copy(iv, 0, dataToAuthenticate, 0, IvSize);
            Array.Copy(encrypted, 0, dataToAuthenticate, IvSize, encrypted.Length);
            
            // Compute HMAC over IV + encrypted data
            byte[] hmac;
            using (var hmacSha256 = new HMACSHA256(hmacKey))
            {
                hmac = hmacSha256.ComputeHash(dataToAuthenticate);
            }
            
            // Combine IV + encrypted data + HMAC
            var result = new byte[IvSize + encrypted.Length + HmacSize];
            Array.Copy(iv, 0, result, 0, IvSize);
            Array.Copy(encrypted, 0, result, IvSize, encrypted.Length);
            Array.Copy(hmac, 0, result, IvSize + encrypted.Length, HmacSize);
            
            // Clear sensitive data
            Array.Clear(aesKey);
            Array.Clear(hmacKey);
            
            return Result<byte[]>.Success(result);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"AES-256-CBC-HMAC encryption failed: {ex.Message}");
        }
    }
    
    public override async Task<Result<byte[]>> DecryptAsync(byte[] encryptedPayload, byte[] key, long blockId)
    {
        try
        {
            ValidateKey(key);
            if (encryptedPayload == null) throw new ArgumentNullException(nameof(encryptedPayload));
            
            // Must have at least IV + HMAC
            if (encryptedPayload.Length < IvSize + HmacSize)
                return Result<byte[]>.Failure("Encrypted payload too small for AES-256-CBC-HMAC");
            
            // Split the key into AES and HMAC keys
            var aesKey = new byte[AesKeySize];
            var hmacKey = new byte[HmacKeySize];
            Array.Copy(key, 0, aesKey, 0, AesKeySize);
            Array.Copy(key, AesKeySize, hmacKey, 0, HmacKeySize);
            
            // Extract components
            var iv = new byte[IvSize];
            var ciphertext = new byte[encryptedPayload.Length - IvSize - HmacSize];
            var receivedHmac = new byte[HmacSize];
            
            Array.Copy(encryptedPayload, 0, iv, 0, IvSize);
            Array.Copy(encryptedPayload, IvSize, ciphertext, 0, ciphertext.Length);
            Array.Copy(encryptedPayload, IvSize + ciphertext.Length, receivedHmac, 0, HmacSize);
            
            // Verify IV matches expected for this blockId
            var expectedIv = DeriveNonce(blockId, IvSize);
            if (!iv.AsSpan().SequenceEqual(expectedIv))
                return Result<byte[]>.Failure("IV mismatch - possible tampering or corruption");
            
            // Verify HMAC first (authenticate before decrypt)
            var dataToAuthenticate = new byte[IvSize + ciphertext.Length];
            Array.Copy(iv, 0, dataToAuthenticate, 0, IvSize);
            Array.Copy(ciphertext, 0, dataToAuthenticate, IvSize, ciphertext.Length);
            
            byte[] computedHmac;
            using (var hmacSha256 = new HMACSHA256(hmacKey))
            {
                computedHmac = hmacSha256.ComputeHash(dataToAuthenticate);
            }
            
            // Constant-time HMAC comparison to prevent timing attacks
            if (!CryptographicOperations.FixedTimeEquals(receivedHmac, computedHmac))
            {
                Array.Clear(aesKey);
                Array.Clear(hmacKey);
                return Result<byte[]>.Failure("HMAC verification failed - possible tampering or wrong key");
            }
            
            // Decrypt with AES-CBC
            byte[] decrypted;
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                
                using var decryptor = aes.CreateDecryptor();
                decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
            
            // Clear sensitive data
            Array.Clear(aesKey);
            Array.Clear(hmacKey);
            
            return Result<byte[]>.Success(decrypted);
        }
        catch (CryptographicException ex)
        {
            return Result<byte[]>.Failure($"AES-256-CBC-HMAC decryption failed (authentication or key error): {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"AES-256-CBC-HMAC decryption failed: {ex.Message}");
        }
    }
}