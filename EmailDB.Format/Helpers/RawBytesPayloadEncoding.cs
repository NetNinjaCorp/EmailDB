using System;
using EmailDB.Format.Models;

namespace EmailDB.Format.Helpers;

/// <summary>
/// Raw bytes payload encoding - no serialization, just passes through byte arrays.
/// </summary>
public class RawBytesPayloadEncoding : IPayloadEncoding
{
    public PayloadEncoding EncodingType => PayloadEncoding.RawBytes;

    public Result<byte[]> Serialize<T>(T payload)
    {
        if (payload is byte[] bytes)
        {
            return Result<byte[]>.Success(bytes);
        }

        return Result<byte[]>.Failure($"RawBytesPayloadEncoding can only serialize byte arrays, not {typeof(T).Name}");
    }

    public Result<T> Deserialize<T>(byte[] data)
    {
        if (typeof(T) == typeof(byte[]))
        {
            return Result<T>.Success((T)(object)data);
        }

        return Result<T>.Failure($"RawBytesPayloadEncoding can only deserialize to byte arrays, not {typeof(T).Name}");
    }
}