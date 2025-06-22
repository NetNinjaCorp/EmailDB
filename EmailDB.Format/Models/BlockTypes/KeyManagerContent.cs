using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.Models.BlockTypes;

/// <summary>
/// Content for KeyManager blocks that store encryption keys for other blocks.
/// The KeyManager block itself is encrypted with a master key.
/// </summary>
public class KeyManagerContent : BlockContent
{
    public override BlockType GetBlockType() => BlockType.KeyManager;
    /// <summary>
    /// Version of the key manager format.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    
    /// <summary>
    /// Timestamp when this key manager was created.
    /// </summary>
    [JsonPropertyName("created_timestamp")]
    public long CreatedTimestamp { get; set; }
    
    /// <summary>
    /// Timestamp when this key manager was last updated.
    /// </summary>
    [JsonPropertyName("updated_timestamp")]
    public long UpdatedTimestamp { get; set; }
    
    /// <summary>
    /// Maps block IDs to their encryption keys.
    /// Key: Block ID
    /// Value: Encryption key data
    /// </summary>
    [JsonPropertyName("block_keys")]
    public Dictionary<long, BlockKeyInfo> BlockKeys { get; set; } = new();
    
    /// <summary>
    /// Key derivation settings used for this key manager.
    /// </summary>
    [JsonPropertyName("key_derivation")]
    public KeyDerivationSettings KeyDerivation { get; set; } = new();
    
    /// <summary>
    /// Salt used for key derivation from master password.
    /// </summary>
    [JsonPropertyName("key_salt")]
    public byte[] KeySalt { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// Verification hash to check if master key is correct.
    /// </summary>
    [JsonPropertyName("verification_hash")]
    public byte[] VerificationHash { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// Next available key ID for new blocks.
    /// </summary>
    [JsonPropertyName("next_key_id")]
    public long NextKeyId { get; set; } = 1;
    
    /// <summary>
    /// List of revoked key IDs that should no longer be used.
    /// </summary>
    [JsonPropertyName("revoked_keys")]
    public HashSet<long> RevokedKeys { get; set; } = new();
}

/// <summary>
/// Information about an encryption key for a specific block.
/// </summary>
public class BlockKeyInfo
{
    /// <summary>
    /// Unique key ID.
    /// </summary>
    [JsonPropertyName("key_id")]
    public long KeyId { get; set; }
    
    /// <summary>
    /// The encryption algorithm this key is for.
    /// </summary>
    [JsonPropertyName("algorithm")]
    public EncryptionAlgorithm Algorithm { get; set; }
    
    /// <summary>
    /// The actual encryption key (will be encrypted by master key).
    /// </summary>
    [JsonPropertyName("key_data")]
    public byte[] KeyData { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// When this key was created.
    /// </summary>
    [JsonPropertyName("created_timestamp")]
    public long CreatedTimestamp { get; set; }
    
    /// <summary>
    /// Block type this key is intended for (optional metadata).
    /// </summary>
    [JsonPropertyName("block_type")]
    public BlockType? BlockType { get; set; }
    
    /// <summary>
    /// Whether this key is currently active.
    /// </summary>
    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;
    
    /// <summary>
    /// Optional metadata about this key.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Key derivation settings for generating keys from master password.
/// </summary>
public class KeyDerivationSettings
{
    /// <summary>
    /// Key derivation function used (e.g., "PBKDF2", "Argon2", "SCRYPT").
    /// </summary>
    [JsonPropertyName("kdf")]
    public string Kdf { get; set; } = "PBKDF2";
    
    /// <summary>
    /// Number of iterations for the KDF.
    /// </summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; } = 100000;
    
    /// <summary>
    /// Hash algorithm used with the KDF.
    /// </summary>
    [JsonPropertyName("hash_algorithm")]
    public string HashAlgorithm { get; set; } = "SHA256";
    
    /// <summary>
    /// Key length in bytes to derive.
    /// </summary>
    [JsonPropertyName("key_length")]
    public int KeyLength { get; set; } = 32;
    
    /// <summary>
    /// Additional parameters for specific KDFs.
    /// </summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// Manager for encryption keys that handles key generation, storage, and retrieval.
/// </summary>
public class EncryptionKeyManager
{
    private readonly Dictionary<long, BlockKeyInfo> _blockKeys = new();
    private byte[] _masterKey = Array.Empty<byte>();
    private bool _isUnlocked = false;
    
    /// <summary>
    /// Whether the key manager is currently unlocked with master key.
    /// </summary>
    public bool IsUnlocked => _isUnlocked;
    
    /// <summary>
    /// Number of keys currently managed.
    /// </summary>
    public int KeyCount => _blockKeys.Count;
    
    /// <summary>
    /// Unlocks the key manager with the master key.
    /// </summary>
    /// <param name="masterKey">The master encryption key</param>
    /// <param name="keyManagerContent">The encrypted key manager content</param>
    /// <returns>Success if unlocked correctly</returns>
    public Result<bool> Unlock(byte[] masterKey, KeyManagerContent keyManagerContent)
    {
        try
        {
            // Verify master key by checking verification hash
            var verificationHash = System.Security.Cryptography.SHA256.HashData(masterKey.Concat(keyManagerContent.KeySalt).ToArray());
            if (!verificationHash.AsSpan().SequenceEqual(keyManagerContent.VerificationHash))
            {
                return Result<bool>.Failure("Invalid master key");
            }
            
            _masterKey = new byte[masterKey.Length];
            Array.Copy(masterKey, _masterKey, masterKey.Length);
            _isUnlocked = true;
            
            // Load all block keys
            _blockKeys.Clear();
            foreach (var kvp in keyManagerContent.BlockKeys)
            {
                _blockKeys[kvp.Key] = kvp.Value;
            }
            
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure($"Failed to unlock key manager: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Locks the key manager and clears sensitive data.
    /// </summary>
    public void Lock()
    {
        if (_masterKey.Length > 0)
        {
            Array.Clear(_masterKey);
        }
        _isUnlocked = false;
        _blockKeys.Clear();
    }
    
    /// <summary>
    /// Gets the encryption key for a specific block.
    /// </summary>
    /// <param name="blockId">The block ID</param>
    /// <returns>The encryption key if found</returns>
    public Result<byte[]> GetBlockKey(long blockId)
    {
        if (!_isUnlocked)
            return Result<byte[]>.Failure("Key manager is locked");
            
        if (!_blockKeys.TryGetValue(blockId, out var keyInfo))
            return Result<byte[]>.Failure($"No key found for block {blockId}");
            
        if (!keyInfo.IsActive)
            return Result<byte[]>.Failure($"Key for block {blockId} is inactive");
            
        return Result<byte[]>.Success(keyInfo.KeyData);
    }
    
    /// <summary>
    /// Generates and stores a new encryption key for a block.
    /// </summary>
    /// <param name="blockId">The block ID</param>
    /// <param name="algorithm">The encryption algorithm</param>
    /// <param name="blockType">Optional block type for metadata</param>
    /// <returns>The generated key</returns>
    public Result<byte[]> GenerateBlockKey(long blockId, EncryptionAlgorithm algorithm, BlockType? blockType = null)
    {
        if (!_isUnlocked)
            return Result<byte[]>.Failure("Key manager is locked");
            
        try
        {
            // Generate appropriate key for algorithm
            var keySize = algorithm switch
            {
                EncryptionAlgorithm.AES256_GCM => 32,
                EncryptionAlgorithm.ChaCha20_Poly1305 => 32,
                EncryptionAlgorithm.AES256_CBC_HMAC => 64,
                _ => 32
            };
            
            var key = new byte[keySize];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(key);
            
            var keyInfo = new BlockKeyInfo
            {
                KeyId = blockId,
                Algorithm = algorithm,
                KeyData = key,
                CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockType = blockType,
                IsActive = true
            };
            
            _blockKeys[blockId] = keyInfo;
            
            return Result<byte[]>.Success(key);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"Failed to generate block key: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Revokes a key making it inactive.
    /// </summary>
    /// <param name="blockId">The block ID to revoke</param>
    /// <returns>Success if revoked</returns>
    public Result<bool> RevokeBlockKey(long blockId)
    {
        if (!_isUnlocked)
            return Result<bool>.Failure("Key manager is locked");
            
        if (!_blockKeys.TryGetValue(blockId, out var keyInfo))
            return Result<bool>.Failure($"No key found for block {blockId}");
            
        keyInfo.IsActive = false;
        return Result<bool>.Success(true);
    }
    
    /// <summary>
    /// Creates a KeyManagerContent for serialization.
    /// </summary>
    /// <returns>The key manager content</returns>
    public Result<KeyManagerContent> ToContent()
    {
        if (!_isUnlocked)
            return Result<KeyManagerContent>.Failure("Key manager is locked");
            
        var content = new KeyManagerContent
        {
            Version = 1,
            CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockKeys = new Dictionary<long, BlockKeyInfo>(_blockKeys),
            KeyDerivation = new KeyDerivationSettings(),
            NextKeyId = _blockKeys.Count > 0 ? _blockKeys.Keys.Max() + 1 : 1
        };
        
        // Generate salt and verification hash
        content.KeySalt = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(content.KeySalt);
        
        content.VerificationHash = System.Security.Cryptography.SHA256.HashData(_masterKey.Concat(content.KeySalt).ToArray());
        
        return Result<KeyManagerContent>.Success(content);
    }
}