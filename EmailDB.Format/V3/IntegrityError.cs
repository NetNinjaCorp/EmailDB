namespace EmailDB.Format.V3;

/// <summary>
/// An <see cref="VerificationFailureKind.Integrity"/> failure
/// (EmailDB_FileFormat_Spec.md Section 13): a Merkle <c>ChildHash</c> / content-hash
/// mismatch in the index tree — a node's recomputed BLAKE3 content hash did not match
/// the hash the parent (or IndexRoot) committed for it. This is index corruption or
/// tampering: the required behavior is to fail the lookup and fall back to the
/// previous Checkpoint's root, re-replaying the WAL if that root verifies.
///
/// <para>Distinct from <see cref="CorruptionError"/> (damaged raw bytes at the block
/// layer) and <see cref="WrongKeyOrTamperError"/> (failed decryption authenticity):
/// an integrity error is a <em>structural</em> mismatch between what a node contains
/// and what its parent vouched for, detected after the block itself read and
/// checksummed fine.</para>
/// </summary>
public sealed class IntegrityError : VerificationError
{
    /// <inheritdoc/>
    public override VerificationFailureKind Kind => VerificationFailureKind.Integrity;

    /// <summary>The content hash the parent/root committed for the node, when captured.</summary>
    public byte[]? ExpectedHash { get; }

    /// <summary>The hash actually recomputed over the node's serialized bytes, when captured.</summary>
    public byte[]? ActualHash { get; }

    /// <summary>
    /// Constructs an integrity error. Prefer <see cref="MerkleChildHashMismatch"/> for
    /// the Section 13 Merkle row.
    /// </summary>
    public IntegrityError(
        string message,
        byte[]? blockId = null,
        long? offset = null,
        byte[]? expectedHash = null,
        byte[]? actualHash = null,
        Exception? innerException = null)
        : base(message, offset, blockId, damagedRange: null, innerException)
    {
        // Defensive copies: hashes may come from caller-owned buffers.
        ExpectedHash = expectedHash is null ? null : (byte[])expectedHash.Clone();
        ActualHash = actualHash is null ? null : (byte[])actualHash.Clone();
    }

    /// <summary>
    /// A node's recomputed content hash did not match the ChildHash the parent (or
    /// IndexRoot) committed for it (spec Section 6, Section 13). Names the node's block
    /// and, when captured, the expected/actual hashes.
    /// </summary>
    public static IntegrityError MerkleChildHashMismatch(
        byte[]? blockId = null, long? offset = null, byte[]? expectedHash = null, byte[]? actualHash = null)
        => new($"Merkle ChildHash mismatch for node {(blockId is null ? "(unknown)" : Convert.ToHexStringLower(blockId))}" +
               $"{(offset is null ? "" : $" at offset {offset}")}: index corruption or tampering; failing lookup and falling back to the previous Checkpoint root (spec Section 13).",
            blockId, offset, expectedHash, actualHash);
}
