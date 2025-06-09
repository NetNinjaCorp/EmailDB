using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.Helpers;

public class DefaultBlockContentSerializer : iBlockContentSerializer
{
    private readonly Dictionary<PayloadEncoding, IPayloadEncoding> _encodings;
    
    public DefaultBlockContentSerializer()
    {
        _encodings = new Dictionary<PayloadEncoding, IPayloadEncoding>
        {
            { PayloadEncoding.Protobuf, new ProtobufPayloadEncoding() },
            { PayloadEncoding.Json, new JsonPayloadEncoding() },
            { PayloadEncoding.RawBytes, new RawBytesPayloadEncoding() }
        };
    }
    
    public Result<byte[]> Serialize<T>(T content, PayloadEncoding encoding)
    {
        if (!_encodings.TryGetValue(encoding, out var encoder))
            return Result<byte[]>.Failure($"Unsupported encoding: {encoding}");
            
        return encoder.Serialize(content);
    }
    
    public Result<T> Deserialize<T>(byte[] data, PayloadEncoding encoding)
    {
        if (!_encodings.TryGetValue(encoding, out var encoder))
            return Result<T>.Failure($"Unsupported encoding: {encoding}");
            
        return encoder.Deserialize<T>(data);
    }
    
    // Legacy methods for compatibility
    public byte[] Serialize<T>(T obj)
    {
        var result = Serialize(obj, PayloadEncoding.Json);
        return result.IsSuccess ? result.Value : throw new Exception(result.Error);
    }

    public T Deserialize<T>(byte[] payload)
    {
        var result = Deserialize<T>(payload, PayloadEncoding.Json);
        return result.IsSuccess ? result.Value : throw new Exception(result.Error);
    }
}