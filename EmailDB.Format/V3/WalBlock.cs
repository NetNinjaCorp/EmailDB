namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of a WAL block payload (BlockType 1,
/// EmailDB_FileFormat_Spec.md Section 10.4): a monotonic
/// <see cref="WalSequence"/>, the <see cref="CheckpointBlockId"/> the block is
/// fenced to, and an ordered list of logged <see cref="Entries"/>.
///
/// <para><b>Checkpoint fence.</b> A WAL block written after Checkpoint N carries
/// N's BlockId in <see cref="CheckpointBlockId"/>. Recovery replays exactly the
/// WAL blocks whose <see cref="CheckpointBlockId"/> matches the last valid
/// Checkpoint (in <see cref="WalSequence"/> order), then writes a fresh
/// Checkpoint; WAL blocks referencing an older Checkpoint are committed history
/// and are ignored (spec Section 10.4). This block is a standard append-only
/// block — no raw regions, no in-place rewrites.</para>
///
/// <para>Byte serialization is <see cref="WalSerializer"/>. Instances are
/// produced by <see cref="WalWriter"/>, which stamps the current Checkpoint's
/// BlockId and the next monotonic sequence.</para>
/// </summary>
public sealed class WalBlock
{
    /// <summary>Width of <see cref="CheckpointBlockId"/>: a 16-byte ULID.</summary>
    public const int CheckpointBlockIdSize = UlidGenerator.UlidSize;

    /// <summary>
    /// Monotonic sequence stamped on this WAL block, strictly greater than every
    /// earlier WAL block's in the file. Orders replay after recovery.
    /// </summary>
    public required ulong WalSequence { get; init; }

    /// <summary>
    /// The 16-byte ULID of the Checkpoint this WAL block is fenced to: the
    /// current (last committed) Checkpoint at write time. Never all-zero — a WAL
    /// block always fences against a real Checkpoint.
    /// </summary>
    public required byte[] CheckpointBlockId { get; init; }

    /// <summary>The logged mutations, replayed in order.</summary>
    public required IReadOnlyList<WalEntry> Entries { get; init; }
}
