using System;
using EmailDB.Format.Models;

namespace EmailDB.Format;

/// <summary>
/// Interface for payload encoding/decoding strategies.
/// Implementations handle serialization of block payloads to/from byte arrays.
/// </summary>
public interface IPayloadEncoding
{
    /// <summary>
    /// Serializes an object to a byte array.
    /// </summary>
    /// <typeparam name="T">The type of object to serialize</typeparam>
    /// <param name="payload">The object to serialize</param>
    /// <returns>Result containing the serialized byte array or error</returns>
    Result<byte[]> Serialize<T>(T payload);

    /// <summary>
    /// Deserializes a byte array to an object.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize to</typeparam>
    /// <param name="data">The byte array to deserialize</param>
    /// <returns>Result containing the deserialized object or error</returns>
    Result<T> Deserialize<T>(byte[] data);
    
    /// <summary>
    /// Gets the encoding type this implementation handles.
    /// </summary>
    PayloadEncoding EncodingType { get; }
}