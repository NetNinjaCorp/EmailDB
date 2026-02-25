namespace EmailDB.Format.Models;

/// <summary>
/// Specifies the serialization format used for a block's payload.
/// </summary>
public enum PayloadEncoding : byte
{
    Protobuf = 1, // Payload is serialized using Protobuf serialization format. This is the default format for most blocks.
    Json = 2, // Payload is serialized using JSON serialization format. This format is used for human-readable data.
    RawBytes = 3 // Payload is stored as raw bytes. This format is used for binary data that does not require serialization.
}