namespace EmailDB.Format.V3;

/// <summary>
/// The specific authentication failure a <see cref="WrongKeyOrTamperError"/>
/// represents. All are <see cref="VerificationFailureKind.WrongKeyOrTamper"/>: the
/// bytes verified against their checksum but failed an authenticity check, so this
/// is wrong key or tampering, never "corruption" (spec Section 13). This enum only
/// refines the cause for logging and for the per-failure handlers (US-EMDB-75-6).
/// </summary>
public enum TamperCause
{
    /// <summary>
    /// AES-GCM authentication tag / AAD verification failed after the PayloadChecksum
    /// verified. Wrong key or tampering; never brute other epochs beyond the header's
    /// KeyEpoch (spec Sections 4.6, 13).
    /// </summary>
    GcmTag = 0,

    /// <summary>
    /// The KeyVerificationToken did not match: the supplied key/password does not open
    /// this file's current epoch (spec Section 13).
    /// </summary>
    TokenMismatch = 1,
}
