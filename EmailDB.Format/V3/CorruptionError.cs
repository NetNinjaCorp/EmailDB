namespace EmailDB.Format.V3;

/// <summary>
/// A <see cref="VerificationFailureKind.Corruption"/> failure (EmailDB_FileFormat_Spec.md
/// Section 13): the bytes are damaged — a header or payload checksum mismatch, an
/// insane declared length, a torn tail, or a decompression bomb. The block is dead;
/// the reader resynchronizes on the next valid block. When a corrupt payload belongs
/// to a block the live index still references, this is a data-loss event that names
/// the affected <see cref="VerificationError.BlockId"/>
/// (<see cref="CorruptionCause.ReferencedLiveDataLoss"/>).
///
/// <para>Distinct from <see cref="WrongKeyOrTamperError"/>: corruption is damaged
/// bytes, whereas a wrong-key/tamper failure is intact bytes that failed
/// authentication — the spec keeps these classes apart deliberately.</para>
/// </summary>
public sealed class CorruptionError : VerificationError
{
    /// <inheritdoc/>
    public override VerificationFailureKind Kind => VerificationFailureKind.Corruption;

    /// <summary>The specific damage this error represents (spec Section 13 row).</summary>
    public CorruptionCause Cause { get; }

    /// <summary>
    /// True when the corrupt block is still referenced by the live index, so the
    /// corruption is an actual loss of committed data (spec Section 13). Always paired
    /// with a non-null <see cref="VerificationError.BlockId"/>.
    /// </summary>
    public bool ReferencedLiveData => Cause == CorruptionCause.ReferencedLiveDataLoss;

    /// <summary>
    /// Constructs a corruption error. Prefer the static factories for the common
    /// Section 13 rows; this constructor stays public for causes without a factory.
    /// </summary>
    public CorruptionError(
        string message,
        CorruptionCause cause,
        long? offset = null,
        byte[]? blockId = null,
        DamagedRange? damagedRange = null,
        Exception? innerException = null)
        : base(message, offset, blockId, damagedRange, innerException)
    {
        Cause = cause;
    }

    /// <summary>HeaderChecksum mismatch at <paramref name="offset"/> — corrupt/torn header.</summary>
    public static CorruptionError HeaderChecksumMismatch(long offset, DamagedRange? damagedRange = null)
        => new($"Block header checksum mismatch at offset {offset} (torn or corrupt header); block is dead, resynchronizing (spec Section 13).",
            CorruptionCause.HeaderChecksum, offset, blockId: null, damagedRange);

    /// <summary>PayloadChecksum mismatch — corrupt payload of a block that is not (or not known to be) referenced live.</summary>
    public static CorruptionError PayloadChecksumMismatch(long offset, byte[]? blockId = null, DamagedRange? damagedRange = null)
        => new($"Block payload checksum mismatch at offset {offset} (corrupt payload); block is dead, resynchronizing (spec Section 13).",
            CorruptionCause.PayloadChecksum, offset, blockId, damagedRange);

    /// <summary>
    /// PayloadChecksum mismatch on a block the live index still references: committed
    /// data has been lost. Names the affected <paramref name="blockId"/> (spec Section 13).
    /// </summary>
    public static CorruptionError ReferencedDataLoss(byte[] blockId, long offset, DamagedRange? damagedRange = null)
    {
        ArgumentNullException.ThrowIfNull(blockId);
        return new($"Data loss: block {Convert.ToHexStringLower(blockId)} at offset {offset} has a corrupt payload but is still referenced live (spec Section 13).",
            CorruptionCause.ReferencedLiveDataLoss, offset, blockId, damagedRange);
    }

    /// <summary>
    /// PayloadLength insane: <paramref name="declaredLength"/> exceeds
    /// <paramref name="maxPayloadLength"/> (or would run past EOF). Never allocate on it.
    /// </summary>
    public static CorruptionError InsaneLength(long offset, long declaredLength, long maxPayloadLength)
        => new($"Insane PayloadLength {declaredLength} at offset {offset} exceeds MaxPayloadLength {maxPayloadLength} (corrupt header); never allocating on it (spec Sections 4, 13).",
            CorruptionCause.InsaneLength, offset);

    /// <summary>
    /// Decompressed output exceeded the bomb guard (<paramref name="bombGuardBytes"/>
    /// = MaxPayloadLength × 16); treated as payload corruption (spec Sections 4.4, 13).
    /// </summary>
    public static CorruptionError DecompressionBomb(long bombGuardBytes, long? offset = null, byte[]? blockId = null)
        => new($"Decompressed output exceeded the bomb guard of {bombGuardBytes} bytes (MaxPayloadLength × 16); treating payload as corrupt (spec Sections 4.4, 13).",
            CorruptionCause.DecompressionBomb, offset, blockId);
}
