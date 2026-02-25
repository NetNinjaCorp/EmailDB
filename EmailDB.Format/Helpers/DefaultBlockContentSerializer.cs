using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Helpers;
public class DefaultBlockContentSerializer : iBlockContentSerializer
{
    public byte[] Serialize<T>(T obj)
    {
        // Use a serialization method like JSON or Protobuf
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    public T Deserialize<T>(byte[] payload)
    {
        return System.Text.Json.JsonSerializer.Deserialize<T>(payload);
    }
}