namespace EmailDB.Format.V3;

/// <summary>
/// A <see cref="VerificationFailureKind.WrongKeyOrTamper"/> failure
/// (EmailDB_FileFormat_Spec.md Section 13): the PayloadChecksum verified — the bytes
/// are intact — but the authenticity check failed. Either the AES-GCM tag / AAD did
/// not validate, or the KeyVerificationToken did not match. The spec is explicit that
/// this is a <b>distinct error class, not "corruption"</b>: it means the wrong key or
/// deliberate tampering, and the reader MUST NOT brute-force other key epochs beyond
/// the header's KeyEpoch.
///
/// <para>Verify order is checksum → GCM tag, so reaching this error implies the
/// checksum already passed: distinguishing it from <see cref="CorruptionError"/> is
/// exactly the point of the taxonomy.</para>
/// </summary>
public sealed class WrongKeyOrTamperError : VerificationError
{
    /// <inheritdoc/>
    public override VerificationFailureKind Kind => VerificationFailureKind.WrongKeyOrTamper;

    /// <summary>Which authenticity check failed (spec Section 13 row).</summary>
    public TamperCause Cause { get; }

    /// <summary>
    /// The KeyEpoch the failed authentication was attempted under, when known. Recorded
    /// so callers can honor "never brute other epochs beyond the header's KeyEpoch".
    /// </summary>
    public int? KeyEpoch { get; }

    /// <summary>
    /// Constructs a wrong-key/tamper error. Prefer the static factories for the common
    /// Section 13 rows.
    /// </summary>
    public WrongKeyOrTamperError(
        string message,
        TamperCause cause,
        long? offset = null,
        byte[]? blockId = null,
        int? keyEpoch = null,
        Exception? innerException = null)
        : base(message, offset, blockId, damagedRange: null, innerException)
    {
        Cause = cause;
        KeyEpoch = keyEpoch;
    }

    /// <summary>
    /// AES-GCM tag / AAD verification failed after the checksum verified: wrong key or
    /// tampering (spec Section 13). Names the block and, when known, the KeyEpoch it
    /// was attempted under.
    /// </summary>
    public static WrongKeyOrTamperError GcmTagFailure(byte[]? blockId = null, long? offset = null, int? keyEpoch = null, Exception? innerException = null)
        => new($"AES-GCM authentication failed for block {(blockId is null ? "(unknown)" : Convert.ToHexStringLower(blockId))}" +
               $"{(keyEpoch is null ? "" : $" at KeyEpoch {keyEpoch}")}: wrong key or tampering, not corruption (spec Section 13).",
            TamperCause.GcmTag, offset, blockId, keyEpoch, innerException);

    /// <summary>
    /// The KeyVerificationToken did not match: the supplied key/password does not open
    /// this file's epoch (spec Section 13).
    /// </summary>
    public static WrongKeyOrTamperError TokenMismatch(int? keyEpoch = null)
        => new($"KeyVerificationToken mismatch{(keyEpoch is null ? "" : $" at KeyEpoch {keyEpoch}")}: wrong key or password, not corruption (spec Section 13).",
            TamperCause.TokenMismatch, offset: null, blockId: null, keyEpoch);
}
