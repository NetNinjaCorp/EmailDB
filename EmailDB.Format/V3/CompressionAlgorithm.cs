namespace EmailDB.Format.V3;

/// <summary>
/// v3 payload compression registry (EmailDB_FileFormat_Spec.md Section 4.4).
/// Applied after serialization, before encryption. Values 0x05-0xFF are reserved.
/// </summary>
public enum CompressionAlgorithm : byte
{
    /// <summary>No compression.</summary>
    None = 0x00,

    /// <summary>LZ4 frame format.</summary>
    Lz4 = 0x01,

    /// <summary>Zstandard frame format.</summary>
    Zstd = 0x02,

    /// <summary>Brotli.</summary>
    Brotli = 0x03,

    /// <summary>Deflate.</summary>
    Deflate = 0x04,
}
