namespace EmailDB.Format.V3;

/// <summary>
/// v3 payload encoding registry (EmailDB_FileFormat_Spec.md Section 4.3).
/// Value 2 is reserved.
/// </summary>
public enum PayloadEncoding : byte
{
    /// <summary>Custom binary (BTree nodes).</summary>
    Custom = 0,

    /// <summary>protobuf-net.</summary>
    Protobuf = 1,

    // 2 is reserved.

    /// <summary>Debug/interchange JSON.</summary>
    Json = 3,

    /// <summary>Unstructured bytes.</summary>
    RawBytes = 4,
}
