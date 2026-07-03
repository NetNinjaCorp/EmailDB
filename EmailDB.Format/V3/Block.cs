namespace EmailDB.Format.V3;

/// <summary>
/// A fully verified v3 block: its 48-byte header (already checksum-verified) and
/// its on-disk payload bytes (ciphertext when encrypted; still compressed if a
/// compression algorithm is set). Produced by <see cref="BlockSerializer.Deserialize"/>.
/// Decrypt/decompress/deserialize of the payload happens in higher layers.
/// </summary>
public sealed class Block
{
    /// <summary>The verified block header.</summary>
    public required BlockHeader Header { get; init; }

    /// <summary>The on-disk payload bytes (checksum-verified; may be empty).</summary>
    public required byte[] Payload { get; init; }
}
