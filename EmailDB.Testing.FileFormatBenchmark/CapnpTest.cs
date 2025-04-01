using BenchmarkDotNet.Attributes;
using System;
using System.IO;
using Capnp;
using EmailDB.Format.CapnProto;  // Adjust this to the actual namespace of your generated classes

namespace CapnpBenchmarkTest;

[MemoryDiagnoser]
public class CapnpBenchmark
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
            Magic = 0xEE411DBBD114EE,
            Header = new BlockHeader
            {                
                Type = BlockType.metadata,  // Using one of the enum values
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Version = 1,
                Checksum = 0
            },
            Content = new BlockContent
            {                 
                MetadataContent = new MetadataContent
                {
                    WalOffset = 0,
                    FolderTreeOffset = 0,
                    SegmentOffsets =  new List<KeyValueTextLong>(),                    
                    OutdatedOffsets = new List<long>()
                }
            }
        };

        // Preserialize the block so that we can use the same data for deserialization tests.
        using (var ms = new MemoryStream())
        {
            var msg = MessageBuilder.Create();
            var root =  msg.BuildRoot<Block.WRITER>();
            sampleBlock.serialize(root);
            var pump = new FramePump(ms);
            pump.Send(msg.Frame);
            serializedData = ms.ToArray();
        }
    }

    [Benchmark]
    public byte[] CapNPSerializeBenchmark()
    {
        using (var ms = new MemoryStream())
        {
            var msg = MessageBuilder.Create(); 
            var pump = new FramePump(ms);
            pump.Send(msg.Frame);           
            return ms.ToArray();
        }
    }

    [Benchmark]
    public Block.READER CapNPDeserializeBenchmark()
    {
        using (var ms = new MemoryStream(serializedData))
        {
            var frame = Framing.ReadSegments(ms);
            var deserializer = DeserializerState.CreateRoot(frame);
            var reader = new Block.READER(deserializer);
            
            return reader;
        }
    }
}
