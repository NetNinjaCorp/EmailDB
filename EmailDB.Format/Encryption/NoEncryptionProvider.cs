using System;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// No-op encryption provider that passes data through unchanged.
/// </summary>
public class NoEncryptionProvider : EncryptionProviderBase
{
    public override EncryptionAlgorithm Algorithm => EncryptionAlgorithm.None;
    public override int KeySizeBytes => 0;
    
    public override Task<Result<byte[]>> EncryptAsync(byte[] payload, byte[] key, long blockId)
    {
        // No encryption - return payload as-is
        return Task.FromResult(Result<byte[]>.Success(payload));
    }
    
    public override Task<Result<byte[]>> DecryptAsync(byte[] encryptedPayload, byte[] key, long blockId)
    {
        // No decryption - return payload as-is
        return Task.FromResult(Result<byte[]>.Success(encryptedPayload));
    }
    
    public override byte[] GenerateKey()
    {
        // No key needed for no encryption
        return Array.Empty<byte>();
    }
}