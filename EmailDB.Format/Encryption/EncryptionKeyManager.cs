using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Manages encryption keys for blocks with master key protection.
/// </summary>
public class EncryptionKeyManager
{
    private byte[]? _masterKey;
    private readonly Dictionary<long, byte[]> _blockKeys;
    private readonly Dictionary<long, BlockKeyInfo> _keyMetadata;
    private bool _isUnlocked;

    public EncryptionKeyManager()
    {
        _blockKeys = new Dictionary<long, byte[]>();
        _keyMetadata = new Dictionary<long, BlockKeyInfo>();
        _isUnlocked = false;
    }

    /// <summary>
    /// Gets whether the key manager is currently unlocked.
    /// </summary>
    public bool IsUnlocked => _isUnlocked;

    /// <summary>
    /// Gets the number of managed keys.
    /// </summary>
    public int KeyCount => _blockKeys.Count;

    /// <summary>
    /// Unlocks the key manager with a master key and loads existing keys.
    /// </summary>
    /// <param name="masterKey">Master encryption key</param>
    /// <param name="keyManagerContent">Existing key manager content (if any)</param>
    /// <returns>Success result</returns>
    public Result<bool> Unlock(byte[] masterKey, KeyManagerContent keyManagerContent)
    {
        try
        {
            if (masterKey == null || masterKey.Length != 32)
                return Result<bool>.Failure("Master key must be 32 bytes");

            _masterKey = new byte[masterKey.Length];
            Array.Copy(masterKey, _masterKey, masterKey.Length);

            // For empty/new KeyManagerContent, just unlock without verification
            if (keyManagerContent == null || keyManagerContent.BlockKeys.Count == 0)
            {
                _isUnlocked = true;
                return Result<bool>.Success(true);
            }

            // Load existing keys if provided
            foreach (var kvp in keyManagerContent.BlockKeys)
            {
                var blockId = kvp.Key;
                var keyInfo = kvp.Value;

                // Decrypt the block key using master key
                var decryptedKey = DecryptBlockKey(keyInfo.EncryptedKey, _masterKey);
                if (decryptedKey != null)
                {
                    _blockKeys[blockId] = decryptedKey;
                    _keyMetadata[blockId] = keyInfo;
                }
            }

            _isUnlocked = true;
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to unlock key manager: {ex.Message}");
        }
    }

    /// <summary>
    /// Locks the key manager and clears sensitive data from memory.
    /// </summary>
    public void Lock()
    {
        if (_masterKey != null)
        {
            Array.Clear(_masterKey, 0, _masterKey.Length);
            _masterKey = null;
        }

        foreach (var key in _blockKeys.Values)
        {
            Array.Clear(key, 0, key.Length);
        }

        _blockKeys.Clear();
        _keyMetadata.Clear();
        _isUnlocked = false;
    }

    /// <summary>
    /// Generates a new encryption key for a block.
    /// </summary>
    /// <param name="blockId">Block ID</param>
    /// <param name="algorithm">Encryption algorithm</param>
    /// <param name="blockType">Block type (optional)</param>
    /// <returns>Generated key</returns>
    public Result<byte[]> GenerateBlockKey(long blockId, EncryptionAlgorithm algorithm, BlockType? blockType = null)
    {
        try
        {
            if (!_isUnlocked)
                return Result<byte[]>.Failure("Key manager is locked");

            if (_blockKeys.ContainsKey(blockId))
                return Result<byte[]>.Failure($"Key for block {blockId} already exists");

            // Generate new key based on algorithm
            var keySize = GetKeySizeForAlgorithm(algorithm);
            if (keySize == 0)
                return Result<byte[]>.Failure($"Unsupported algorithm: {algorithm}");

            var newKey = new byte[keySize];
            RandomNumberGenerator.Fill(newKey);

            // Encrypt the key with master key
            var encryptedKey = EncryptBlockKey(newKey, _masterKey!);
            if (encryptedKey == null)
                return Result<byte[]>.Failure("Failed to encrypt block key");

            // Store the key and metadata
            _blockKeys[blockId] = newKey;
            _keyMetadata[blockId] = new BlockKeyInfo
            {
                BlockId = blockId,
                Algorithm = algorithm,
                EncryptedKey = encryptedKey,
                BlockType = blockType ?? BlockType.EmailBatch,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            return Result<byte[]>.Success(newKey);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"Failed to generate block key: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets an existing encryption key for a block.
    /// </summary>
    /// <param name="blockId">Block ID</param>
    /// <returns>Encryption key if found</returns>
    public Result<byte[]> GetBlockKey(long blockId)
    {
        try
        {
            if (!_isUnlocked)
                return Result<byte[]>.Failure("Key manager is locked");

            if (!_blockKeys.TryGetValue(blockId, out var key))
                return Result<byte[]>.Failure($"No key found for block {blockId}");

            return Result<byte[]>.Success(key);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"Failed to get block key: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates KeyManagerContent for persistence.
    /// </summary>
    /// <returns>Key manager content</returns>
    public Result<KeyManagerContent> ToContent()
    {
        try
        {
            if (!_isUnlocked)
                return Result<KeyManagerContent>.Failure("Key manager is locked");

            var content = new KeyManagerContent
            {
                BlockKeys = new Dictionary<long, BlockKeyInfo>(_keyMetadata)
            };

            // Create verification hash
            using var sha256 = SHA256.Create();
            var hashInput = System.Text.Encoding.UTF8.GetBytes($"KeyManager_{_keyMetadata.Count}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            content.VerificationHash = sha256.ComputeHash(hashInput);

            return Result<KeyManagerContent>.Success(content);
        }
        catch (Exception ex)
        {
            return Result<KeyManagerContent>.Failure($"Failed to create key manager content: {ex.Message}");
        }
    }

    private static int GetKeySizeForAlgorithm(EncryptionAlgorithm algorithm)
    {
        return algorithm switch
        {
            EncryptionAlgorithm.AES256_GCM => 32,
            EncryptionAlgorithm.ChaCha20_Poly1305 => 32,
            EncryptionAlgorithm.AES256_CBC_HMAC => 64, // 32 for AES + 32 for HMAC
            EncryptionAlgorithm.None => 0,
            _ => 0
        };
    }

    private static byte[]? EncryptBlockKey(byte[] key, byte[] masterKey)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = masterKey;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(key, 0, key.Length);

            // Prepend IV to encrypted data
            var result = new byte[aes.IV.Length + encrypted.Length];
            Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
            Array.Copy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecryptBlockKey(byte[] encryptedKey, byte[] masterKey)
    {
        try
        {
            if (encryptedKey.Length < 16) // Must have at least IV
                return null;

            using var aes = Aes.Create();
            aes.Key = masterKey;

            // Extract IV and encrypted data
            var iv = new byte[16];
            var encrypted = new byte[encryptedKey.Length - 16];
            Array.Copy(encryptedKey, 0, iv, 0, 16);
            Array.Copy(encryptedKey, 16, encrypted, 0, encrypted.Length);

            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        }
        catch
        {
            return null;
        }
    }
}

