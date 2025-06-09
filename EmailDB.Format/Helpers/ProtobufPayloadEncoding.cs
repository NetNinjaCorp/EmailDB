using System;
using System.IO;
using ProtoBuf;
using EmailDB.Format.Models;

namespace EmailDB.Format.Helpers;

public class ProtobufPayloadEncoding : IPayloadEncoding
{
    public PayloadEncoding EncodingType => PayloadEncoding.Protobuf;

    public Result<T> Deserialize<T>(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var result = Serializer.Deserialize<T>(stream);
            return Result<T>.Success(result);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure($"Protobuf deserialization failed: {ex.Message}");
        }
    }

    public Result<byte[]> Serialize<T>(T data)
    {
        try
        {
            using var stream = new MemoryStream();
            Serializer.Serialize(stream, data);
            return Result<byte[]>.Success(stream.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"Protobuf serialization failed: {ex.Message}");
        }
    }
}