namespace EmailDB.Format.V3;

/// <summary>
/// Feature-flag bits this implementation understands, per superblock mask
/// (EmailDB_FileFormat_Spec.md Section 3.1). The v3 spec currently defines no
/// flag bits, so all three known masks are zero and every set bit found on disk
/// is "unknown". Open-time policy for unknown bits:
/// <list type="bullet">
/// <item><see cref="Superblock.CompatFlags"/> — proceed normally.</item>
/// <item><see cref="Superblock.ReadOnlyCompatFlags"/> — open read-only, MUST NOT write.</item>
/// <item><see cref="Superblock.IncompatFlags"/> — refuse to open.</item>
/// </list>
/// </summary>
public static class SuperblockFeatureFlags
{
    /// <summary>CompatFlags bits this implementation understands.</summary>
    public const uint KnownCompatFlags = 0u;

    /// <summary>ReadOnlyCompatFlags bits this implementation understands.</summary>
    public const uint KnownReadOnlyCompatFlags = 0u;

    /// <summary>IncompatFlags bits this implementation understands.</summary>
    public const uint KnownIncompatFlags = 0u;

    /// <summary>IncompatFlags bits set in the superblock that this implementation does not understand.</summary>
    public static uint UnknownIncompatBits(Superblock superblock) =>
        superblock.IncompatFlags & ~KnownIncompatFlags;

    /// <summary>ReadOnlyCompatFlags bits set in the superblock that this implementation does not understand.</summary>
    public static uint UnknownReadOnlyCompatBits(Superblock superblock) =>
        superblock.ReadOnlyCompatFlags & ~KnownReadOnlyCompatFlags;
}
