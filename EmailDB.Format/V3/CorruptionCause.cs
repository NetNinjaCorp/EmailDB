namespace EmailDB.Format.V3;

/// <summary>
/// The specific damage a <see cref="CorruptionError"/> represents. Every value maps
/// to a Section 13 row whose required behavior is "block dead; resynchronize" (or,
/// for a referenced-live block, a data-loss error). All are
/// <see cref="VerificationFailureKind.Corruption"/> — this enum only refines the
/// cause for logging and for the per-failure handlers (US-EMDB-75-6).
/// </summary>
public enum CorruptionCause
{
    /// <summary>HeaderChecksum mismatch — corrupt/torn header (spec Section 13).</summary>
    HeaderChecksum = 0,

    /// <summary>PayloadChecksum mismatch — corrupt payload (spec Section 13).</summary>
    PayloadChecksum = 1,

    /// <summary>
    /// PayloadChecksum mismatch on a block the live index still references: a genuine
    /// data-loss event that must name the BlockId (spec Section 13).
    /// </summary>
    ReferencedLiveDataLoss = 2,

    /// <summary>
    /// PayloadLength insane — greater than MaxPayloadLength or extending past EOF; a
    /// corrupt header that passed no checks yet, so never allocate on it (spec
    /// Section 13, Section 4).
    /// </summary>
    InsaneLength = 3,

    /// <summary>
    /// Decompressed size exceeded the bomb guard (MaxPayloadLength × 16); treated as
    /// payload corruption (spec Sections 4.4, 13).
    /// </summary>
    DecompressionBomb = 4,

    /// <summary>
    /// Footer magic missing at EOF — a torn final append; logical truncation at the
    /// last valid block (spec Section 13).
    /// </summary>
    TornTail = 5,

    /// <summary>Corruption that does not fit a more specific cause above.</summary>
    Other = 6,
}
