namespace EmailDB.Format.Models;

/// <summary>
/// Specifies the serialization format used for a block's payload.
/// </summary>
public enum PayloadEncoding : byte
{
    Protobuf = 1,   // Google Protocol Buffers
    CapnProto = 2,  // Cap'n Proto serialization  
    Json = 3,       // JSON text encoding
    RawBytes = 4    // No encoding, raw binary data
}