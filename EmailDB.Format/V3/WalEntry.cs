namespace EmailDB.Format.V3;

/// <summary>
/// One logged mutation inside a WAL block (EmailDB_FileFormat_Spec.md Section
/// 10.4): <c>{ Op, Key (32), BlockId (Ulid), aux }</c>. A WAL block replays its
/// entries in order to reconstruct exactly the uncommitted operations after the
/// fenced Checkpoint.
///
/// <para>Layout of the serialized entry (all multi-byte integers little-endian,
/// spec Section 4):</para>
///
///   Op (1) + Key (<see cref="KeySize"/>) + BlockId (<see cref="BlockIdSize"/>) +
///   AuxLength (4) + Aux (AuxLength)
///
/// <para><see cref="Key"/> is a fixed 32-byte opaque key (the index key, or a
/// folder identity for folder ops). <see cref="BlockId"/> is the 16-byte ULID the
/// op binds or produces (all-zero when the op has no target, e.g. a delete).
/// <see cref="Aux"/> is variable-length op-specific data (empty for a plain
/// index insert/delete).</para>
/// </summary>
public sealed class WalEntry
{
    /// <summary>Fixed width of <see cref="Key"/>: 32 bytes (spec Section 10.4).</summary>
    public const int KeySize = 32;

    /// <summary>Width of <see cref="BlockId"/>: a 16-byte ULID.</summary>
    public const int BlockIdSize = UlidGenerator.UlidSize;

    /// <summary>The logged operation kind.</summary>
    public required WalOpKind Op { get; init; }

    /// <summary>The 32-byte operand key (index key or folder identity).</summary>
    public required byte[] Key { get; init; }

    /// <summary>
    /// The 16-byte ULID the op binds or produces; all-zero when the op has no
    /// target block (e.g. a delete).
    /// </summary>
    public required byte[] BlockId { get; init; }

    /// <summary>Op-specific auxiliary bytes; empty when the op needs none.</summary>
    public required byte[] Aux { get; init; }

    /// <summary>An all-zero 16-byte ULID: the "no target block" sentinel.</summary>
    public static byte[] NoBlockId => new byte[BlockIdSize];

    /// <summary>
    /// Creates an index-upsert entry binding <paramref name="key"/> to
    /// <paramref name="blockId"/>.
    /// </summary>
    public static WalEntry Insert(byte[] key, byte[] blockId) => new()
    {
        Op = WalOpKind.Insert,
        Key = key,
        BlockId = blockId,
        Aux = Array.Empty<byte>(),
    };

    /// <summary>Creates an index-delete entry for <paramref name="key"/>.</summary>
    public static WalEntry Delete(byte[] key) => new()
    {
        Op = WalOpKind.Delete,
        Key = key,
        BlockId = NoBlockId,
        Aux = Array.Empty<byte>(),
    };

    /// <summary>
    /// Creates a folder-op entry over <paramref name="key"/> carrying
    /// <paramref name="aux"/> detail and an optional produced <paramref name="blockId"/>.
    /// </summary>
    public static WalEntry FolderOp(byte[] key, byte[]? aux = null, byte[]? blockId = null) => new()
    {
        Op = WalOpKind.FolderOp,
        Key = key,
        BlockId = blockId ?? NoBlockId,
        Aux = aux ?? Array.Empty<byte>(),
    };
}
