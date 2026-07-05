namespace EmailDB.Format.V3;

/// <summary>
/// Base type for the v3 corruption-handling contract's error taxonomy
/// (EmailDB_FileFormat_Spec.md Section 13). The three concrete subtypes —
/// <see cref="CorruptionError"/>, <see cref="WrongKeyOrTamperError"/>, and
/// <see cref="IntegrityError"/> — keep the spec's three distinct failure classes
/// catchably apart while carrying the structured context Section 13 mandates:
/// the <see cref="Offset"/> where the failure was observed, the affected
/// <see cref="BlockId"/> (for "data-loss error naming the BlockId"), and the
/// <see cref="DamagedRange"/> of bytes (for "log damaged range").
///
/// <para>Derives from <see cref="IOException"/> (as
/// <see cref="IncompatibleFeatureFlagsException"/> does) so it composes with the
/// throw-based paths, and exposes <see cref="ToResult{T}"/> / <see cref="ToResult"/>
/// so it also composes with the v3 <see cref="Result{T}"/> convention used across
/// the block-manager / opener code: a detection site can either throw the typed
/// error or lower it into a failed result while preserving the message.</para>
///
/// <para>This class is the taxonomy itself; wiring each Section 13 row's handler to
/// raise the right subtype is the follow-up work (US-EMDB-75-6).</para>
/// </summary>
public abstract class VerificationError : IOException
{
    /// <summary>
    /// Which of the three distinct Section 13 categories this failure belongs to.
    /// Fixed per concrete subtype; branch on it instead of the exception type.
    /// </summary>
    public abstract VerificationFailureKind Kind { get; }

    /// <summary>
    /// File offset where the failure was observed (e.g. the first header byte of the
    /// dead block, or the offset a bad length was read at), or <see langword="null"/>
    /// when no single offset applies.
    /// </summary>
    public long? Offset { get; }

    /// <summary>
    /// The affected block's ULID (16 raw bytes, big-endian), or <see langword="null"/>
    /// when the failure was detected before a trustworthy BlockId was known. Set
    /// whenever Section 13 requires "naming the BlockId" (referenced-live data loss,
    /// a Merkle mismatch on an identified node). A defensive copy is stored.
    /// </summary>
    public byte[]? BlockId { get; }

    /// <summary>
    /// The damaged byte range (start/end offsets), when the failure covers a span the
    /// reader had to resynchronize across (spec Section 13, "log damaged range"), or
    /// <see langword="null"/> when a single <see cref="Offset"/> already describes it.
    /// Reuses <see cref="V3.DamagedRange"/>, the same struct the forward scan records.
    /// </summary>
    public DamagedRange? DamagedRange { get; }

    /// <summary>
    /// <see cref="BlockId"/> rendered as lowercase hex for logs, or <c>"(none)"</c>
    /// when no BlockId is attached.
    /// </summary>
    public string BlockIdHex => BlockId is null ? "(none)" : Convert.ToHexStringLower(BlockId);

    private protected VerificationError(
        string message,
        long? offset = null,
        byte[]? blockId = null,
        DamagedRange? damagedRange = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Offset = offset;
        // Defensive copy: a caught error must not expose a caller's mutable buffer.
        BlockId = blockId is null ? null : (byte[])blockId.Clone();
        DamagedRange = damagedRange;
    }

    /// <summary>
    /// Lowers this typed error into a failed <see cref="Result{T}"/> carrying its
    /// <see cref="Exception.Message"/> AND the typed error itself (via
    /// <see cref="Result{T}.VerificationError"/>), for detection sites that report
    /// via the v3 result convention rather than throwing. Callers branch on
    /// <see cref="Kind"/> without string-matching, yet <see cref="Result{T}.Error"/>
    /// still equals <see cref="Exception.Message"/>.
    /// </summary>
    public Result<T> ToResult<T>() => Result<T>.Failure(this);

    /// <summary>
    /// Lowers this typed error into a failed non-generic <see cref="Result"/> carrying
    /// its <see cref="Exception.Message"/> and the typed error itself.
    /// </summary>
    public Result ToResult() => Result.Failure(this);
}
