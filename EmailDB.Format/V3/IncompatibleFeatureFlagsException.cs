namespace EmailDB.Format.V3;

/// <summary>
/// Typed refusal error thrown when a v3 file's superblock carries
/// <see cref="Superblock.IncompatFlags"/> bits this implementation does not
/// understand (EmailDB_FileFormat_Spec.md Sections 3.1 and 10.2): the reader
/// MUST refuse to open the file. This is not corruption — the file is valid but
/// requires a newer implementation.
/// </summary>
public sealed class IncompatibleFeatureFlagsException : IOException
{
    /// <summary>The unknown IncompatFlags bits that caused the refusal.</summary>
    public uint UnknownIncompatFlags { get; }

    public IncompatibleFeatureFlagsException(uint unknownIncompatFlags)
        : base($"Refusing to open: superblock IncompatFlags contains unknown bits 0x{unknownIncompatFlags:X8}. " +
               "This file requires a newer EmailDB implementation.")
    {
        UnknownIncompatFlags = unknownIncompatFlags;
    }
}
