namespace EmailDB.Format.Models.BlockTypes;

/// <summary>
/// Payload for the KeyStore block, which holds a table of KeyEpoch-to-DEK mappings.
/// The key store is encrypted with the KEK (Key Encryption Key) using AES-256-GCM.
/// Supports multi-key encryption with O(1) password changes and key rotation
/// without re-encrypting data blocks.
/// </summary>
public class KeyStoreContent
{
    /// <summary>The currently active key epoch used for encrypting new blocks.</summary>
    public int ActiveEpoch { get; set; }

    /// <summary>Table of key entries indexed by epoch.</summary>
    public List<KeyStoreEntry> Entries { get; set; } = new();
}

/// <summary>
/// A single entry in the key store representing one DEK (Data Encryption Key)
/// associated with a key epoch.
/// </summary>
public class KeyStoreEntry
{
    /// <summary>Key epoch identifier. Monotonically increasing.</summary>
    public int Epoch { get; set; }

    /// <summary>Data Encryption Key bytes (32 bytes for AES-256).</summary>
    public byte[] DEK { get; set; } = Array.Empty<byte>();

    /// <summary>Timestamp when this key epoch was created.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Whether this key epoch has been retired (no longer used for new encryptions).</summary>
    public bool Retired { get; set; }
}
