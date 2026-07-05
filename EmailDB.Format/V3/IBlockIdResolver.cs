namespace EmailDB.Format.V3;

/// <summary>
/// Read-side BlockId-to-location resolution (EmailDB_FileFormat_Spec.md
/// Section 7). Logical pointers in v3 are ULIDs, never offsets; anything that
/// must READ a block by BlockId resolves it through an implementation of this
/// interface. The spec's resolution precedence — runtime map first, then
/// BlockLocationIndex, then (disaster only) full scan — is realized by
/// composing resolvers; <see cref="RuntimeBlockOffsetMap"/> is the first link
/// in that chain.
/// </summary>
public interface IBlockIdResolver
{
    /// <summary>
    /// Resolves a BlockId to its latest known location. Returns false when
    /// this resolver does not know the block — the caller then falls back to
    /// the next resolver in the precedence chain (spec Section 7).
    /// </summary>
    /// <param name="blockId">ULID as 16 raw bytes, big-endian binary layout.</param>
    /// <param name="location">The block's latest location when found; null otherwise.</param>
    bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location);
}
