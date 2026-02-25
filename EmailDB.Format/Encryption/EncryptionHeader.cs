namespace EmailDB.Format.Encryption;

/// <summary>
/// File-level encryption header that stores crypto metadata at the start of an encrypted
/// database file. Enables early key verification and stores KDF parameters so the system
/// can re-derive the encryption key from a password.
///
/// Layout:
///   Magic (4 bytes)  |  SchemeVersion (1 byte)  |  AlgorithmId (1 byte)
///   KdfType (1 byte) |  Salt (16 bytes)         |  KeyVerificationToken (variable)
/// </summary>
public sealed class EncryptionHeader
{
    /// <summary>Magic bytes identifying an encrypted EmailDB file: "EMDB" in ASCII.</summary>
    public static readonly byte[] MagicBytes = "EMDB"u8.ToArray();

    /// <summary>Current encryption scheme version.</summary>
    public const byte CurrentSchemeVersion = 1;

    /// <summary>Algorithm identifier: AES-256-GCM.</summary>
    public const byte AlgorithmAes256Gcm = 0x01;

    /// <summary>KDF identifier: Argon2id.</summary>
    public const byte KdfArgon2Id = 0x01;

    /// <summary>
    /// Magic bytes that identify this file as an encrypted EmailDB database.
    /// Must equal <see cref="MagicBytes"/> ("EMDB").
    /// </summary>
    public byte[] Magic { get; set; } = (byte[])MagicBytes.Clone();

    /// <summary>
    /// Encryption scheme version. Allows future upgrades to the header format.
    /// </summary>
    public byte SchemeVersion { get; set; } = CurrentSchemeVersion;

    /// <summary>
    /// Identifies the symmetric encryption algorithm used for block payloads.
    /// 0x01 = AES-256-GCM.
    /// </summary>
    public byte AlgorithmId { get; set; } = AlgorithmAes256Gcm;

    /// <summary>
    /// Identifies the key derivation function used to derive the encryption key.
    /// 0x01 = Argon2id.
    /// </summary>
    public byte KdfType { get; set; } = KdfArgon2Id;

    /// <summary>
    /// Salt used by the KDF to derive the encryption key from a password.
    /// Must be at least <see cref="KeyDerivation.SaltSize"/> bytes (16).
    /// </summary>
    public byte[] Salt { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Key verification token: the known plaintext "EMDB" encrypted with the derived key.
    /// Used for early key verification — decrypt this token and compare to "EMDB" to
    /// detect a wrong key immediately, before processing any blocks.
    /// </summary>
    public byte[] KeyVerificationToken { get; set; } = Array.Empty<byte>();
}
