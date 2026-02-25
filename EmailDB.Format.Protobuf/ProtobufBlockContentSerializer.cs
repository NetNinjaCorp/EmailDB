using ProtoBuf;

namespace EmailDB.Format.Protobuf;

public class ProtobufBlockContentSerializer : iBlockContentSerializer
{
    public byte[] Serialize<T>(T obj)
    {
        using var ms = new MemoryStream();
        Serializer.Serialize(ms, obj);
        return ms.ToArray();
    }

    public T Deserialize<T>(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        return Serializer.Deserialize<T>(ms);
    }
}
