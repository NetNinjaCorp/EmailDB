using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Tenray.ZoneTree.Serializers;

namespace EmailDB.Format.Indexing;

/// <summary>
/// Serializer for EmailLocation objects.
/// </summary>
public class EmailLocationSerializer : ISerializer<EmailLocation>
{
    public EmailLocation Deserialize(Memory<byte> bytes)
    {
        using var ms = new MemoryStream(bytes.ToArray());
        using var reader = new BinaryReader(ms);
        
        return new EmailLocation
        {
            BlockId = reader.ReadInt64(),
            LocalId = reader.ReadInt32()
        };
    }
    
    public Memory<byte> Serialize(in EmailLocation value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(value.BlockId);
        writer.Write(value.LocalId);
        
        return ms.ToArray();
    }
}

/// <summary>
/// Serializer for List<string> used in search indexes.
/// </summary>
public class StringListSerializer : ISerializer<List<string>>
{
    public List<string> Deserialize(Memory<byte> bytes)
    {
        using var ms = new MemoryStream(bytes.ToArray());
        using var reader = new BinaryReader(ms);
        
        var count = reader.ReadInt32();
        var list = new List<string>(count);
        
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadString());
        }
        
        return list;
    }
    
    public Memory<byte> Serialize(in List<string> value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(value.Count);
        foreach (var item in value)
        {
            writer.Write(item);
        }
        
        return ms.ToArray();
    }
}

/// <summary>
/// Serializer for IndexMetadata.
/// </summary>
public class IndexMetadataSerializer : ISerializer<IndexMetadata>
{
    public IndexMetadata Deserialize(Memory<byte> bytes)
    {
        var json = Encoding.UTF8.GetString(bytes.Span);
        return JsonSerializer.Deserialize<IndexMetadata>(json);
    }
    
    public Memory<byte> Serialize(in IndexMetadata value)
    {
        var json = JsonSerializer.Serialize(value);
        return Encoding.UTF8.GetBytes(json);
    }
}