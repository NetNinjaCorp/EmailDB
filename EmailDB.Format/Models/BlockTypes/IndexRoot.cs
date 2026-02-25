namespace EmailDB.Format.Models.BlockTypes;

/// <summary>
/// Payload for the IndexRoot block, which stores the current state of the B+-tree index.
/// Fixed size: 90 bytes (8 + 8 + 2 + 32 + 32 + 8).
/// </summary>
public class IndexRoot
{
    public const int PayloadSize = 90;

    /// <summary>File offset of the root B+-tree node block (8 bytes).</summary>
    public long RootNodeBlockOffset { get; set; }

    /// <summary>Total number of entries in the B+-tree (8 bytes).</summary>
    public long EntryCount { get; set; }

    /// <summary>Height of the B+-tree (2 bytes).</summary>
    public ushort TreeHeight { get; set; }

    /// <summary>BLAKE3 hash of the root node content (32 bytes).</summary>
    public byte[] RootNodeHash { get; set; } = new byte[32];

    /// <summary>BLAKE3 hash of the previous root node (32 bytes). Zero-filled for the first root.</summary>
    public byte[] PreviousRootHash { get; set; } = new byte[32];

    /// <summary>File offset of the previous IndexRoot block (8 bytes). -1 if no previous root.</summary>
    public long PreviousRootOffset { get; set; }
}
