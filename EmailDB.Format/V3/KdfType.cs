namespace EmailDB.Format.V3;

/// <summary>
/// Identifies the key-derivation function recorded in the superblock's <c>KdfType</c> byte
/// (EmailDB_FileFormat_Spec.md Section 3.1). The value is stored in the file, so opening
/// honours whatever the file declares rather than assuming a single algorithm.
/// </summary>
public enum KdfType : byte
{
    /// <summary>No KDF (plaintext file, or key supplied out-of-band).</summary>
    None = 0,

    /// <summary>Argon2id with parameters packed per <see cref="Argon2idParams"/> (spec Section 3.2).</summary>
    Argon2id = 1,
}
