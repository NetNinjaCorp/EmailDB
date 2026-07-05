namespace EmailDB.Format.V3;

/// <summary>
/// The three distinct verification-failure categories the v3 corruption-handling
/// contract (EmailDB_FileFormat_Spec.md Section 13) requires implementations to
/// keep apart — "implementations MUST NOT improvise". Every <see cref="VerificationError"/>
/// exposes exactly one of these so callers can branch on cause deterministically
/// (e.g. a <c>switch</c> on <see cref="VerificationError.Kind"/>) without depending
/// on the concrete exception type or reflection.
/// </summary>
public enum VerificationFailureKind
{
    /// <summary>
    /// The bytes are damaged: a checksum mismatch (header or payload), an insane
    /// declared length, a torn tail, or a decompression bomb. The data is dead and
    /// the reader resynchronizes (spec Section 13). Surfaced as
    /// <see cref="CorruptionError"/>.
    /// </summary>
    Corruption = 0,

    /// <summary>
    /// The bytes are intact (the checksum verified) but authentication failed: a GCM
    /// tag / AAD mismatch, or a key-verification token mismatch. This is wrong key or
    /// deliberate tampering — explicitly NOT "corruption" (spec Section 13). Surfaced
    /// as <see cref="WrongKeyOrTamperError"/>.
    /// </summary>
    WrongKeyOrTamper = 1,

    /// <summary>
    /// A structural integrity check failed: a Merkle <c>ChildHash</c> / content-hash
    /// mismatch in the index tree — index corruption or tampering (spec Section 13).
    /// Surfaced as <see cref="IntegrityError"/>.
    /// </summary>
    Integrity = 2,
}
