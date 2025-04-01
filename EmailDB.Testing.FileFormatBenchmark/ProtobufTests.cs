using BenchmarkDotNet.Attributes;
using EmailDB.Format.Protobuf.Models;
using System;
using System.IO;

namespace ProtobufTests;

[MemoryDiagnoser]
public class ProtobufTests
{
    private Block sampleBlock;
    private byte[] serializedData;

    [GlobalSetup]
    public void Setup()
    {
        // Create a sample Block instance.
        // Adjust the initialization as needed for your actual schema.
        sampleBlock = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Metadata,  // Using one of the enum values
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Version = 1,
                Checksum = 0
            },
            Content = new HeaderContent
            { 
               
                    FileVersion = 1,
                    FirstMetadataOffset = 100,
                    FirstFolderTreeOffset = 200,
                    FirstCleanupOffset = 300
                }
            
        };

        // Preserialize the block so that we can use the same data for deserialization tests.
        using (var ms = new MemoryStream())
        {
            ProtoBuf.Serializer.Serialize(ms, sampleBlock);
            serializedData = ms.ToArray();
        }
    }

    [Benchmark]
    public byte[] ProtobufSerializeBenchmark()
    {
        using (var ms = new MemoryStream())
        {
            ProtoBuf.Serializer.Serialize(ms, sampleBlock);
            return ms.ToArray();
        }
    }

    [Benchmark]
    public Block ProtobufDeserializeBenchmark()
    {
        using (var ms = new MemoryStream(serializedData))
        {
            return ProtoBuf.Serializer.Deserialize<Block>(ms);
        }
    }
}
