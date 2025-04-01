namespace EmailDB.Format.Models
{
    /// <summary>
    /// Specifies the serialization format used for a block's payload.
    /// </summary>
    public enum PayloadEncoding : byte
    {
        /// <summary>
        /// Payload is serialized using Google Protocol Buffers.
        /// </summary>
        Protobuf = 1,

        /// <summary>
        /// Payload is serialized using Cap'n Proto.
        /// </summary>
        CapnProto = 2,

        /// <summary>
        /// Payload is serialized as JSON text.
        /// </summary>
        Json = 3,

        /// <summary>
        /// Payload is raw binary data with no specific serialization structure assumed by the core format.
        /// </summary>
        RawBytes = 4
    }
}