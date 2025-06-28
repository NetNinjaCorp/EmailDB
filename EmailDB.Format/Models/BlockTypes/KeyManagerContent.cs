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
    /// Block ID this key belongs to.
    /// </summary>
    [JsonPropertyName("block_id")]
    public long BlockId { get; set; }
    
    /// <summary>
    /// The encryption algorithm this key is for.
    /// </summary>
    [JsonPropertyName("algorithm")]
    public EncryptionAlgorithm Algorithm { get; set; }
    
    /// <summary>
    /// The encrypted encryption key (encrypted by master key).
    /// </summary>
    [JsonPropertyName("encrypted_key")]
    public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// When this key was created.
    /// </summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
    
    /// <summary>
    /// Block type this key is intended for (optional metadata).
    /// </summary>
    [JsonPropertyName("block_type")]
    public BlockType BlockType { get; set; }
    
    /// <summary>
    /// Whether this key is currently active.
    /// </summary>
    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;
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

