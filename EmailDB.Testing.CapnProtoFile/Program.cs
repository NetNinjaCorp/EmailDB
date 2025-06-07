// See https://aka.ms/new-console-template for more information

using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;
using System.Text;
const ulong ExpectedMagic = 0xEE411DBBD114EEUL;


var SW = Stopwatch.StartNew();

var blockManager = new RawBlockManager("data.blk");

// Write a block
var blockmd = new Block
{
    Version = 1,
    Type = BlockType.Metadata,
    BlockId = 1,
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    Payload = new byte[512]
};

var location = await blockManager.WriteBlockAsync(blockmd);
if (location.IsFailure) { Console.WriteLine("Location - " + location.Error); }
// Write a block
var blockwal = new Block
{
    Version = 1,
    Type = BlockType.WAL,
    BlockId = 2,
    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    Payload = new byte[512]
};
var wal = await blockManager.WriteBlockAsync(blockwal);
if (wal.IsFailure) { Console.WriteLine("WAL - " + wal.Error); }

var randBytes = new byte[16384];
for (int i = 0; i <= 1000; i++)
{
    randBytes = new byte[Random.Shared.Next(4096, 65536)];
    Random.Shared.NextBytes(randBytes);
    var blockseg = new Block
    {
        Version = 1,
        Type = BlockType.Segment,
        BlockId = 3+i,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Payload = randBytes
    };
    var res = await blockManager.WriteBlockAsync(blockwal);
    if (res.IsFailure) { Console.WriteLine(res.Error);    }
   
}
blockManager.Dispose();


SW.Stop();
Console.WriteLine($"Elapsed: {SW.ElapsedMilliseconds} ms");
SW.Restart();


var bm = new RawBlockManager("data.blk", false);
var count = await bm.ScanFile();

SW.Stop();

Console.WriteLine("Found {0} blocks", count.Count);
Console.WriteLine($"Elapsed: {SW.ElapsedMilliseconds} ms");














//var sampleBlock = new Block
//{
//    Magic = 0xEE411DBBD114EE,
//    Header = new BlockHeader
//    {

//        Type = BlockType.metadata,  // Using one of the enum values
//        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
//        Version = 1,
//        Checksum = 0
//    },
//    Content = new BlockContent
//    {
//        MetadataContent = new MetadataContent
//        {
//            WalOffset = 0,
//            FolderTreeOffset = 0,
//            SegmentOffsets = new List<KeyValueTextLong>() { new KeyValueTextLong() { Key = "test1", Value = 2381937891 } },
//            OutdatedOffsets = new List<long>()
//        }
//    }
//};

//var sampleBlock2 = new Block
//{
//    Magic = 0xEE411DBBD114EE,
//    Header = new BlockHeader
//    {
//        Type = BlockType.wal,  // Using one of the enum values
//        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
//        Version = 1,
//        Checksum = 0
//    },
//    Content = new BlockContent
//    {
//        WalContent = new WALContent
//        {
//            Entries = new List<KeyValueTextListWALEntry>(),
//            CategoryOffsets = new List<KeyValueTextLong>() { new KeyValueTextLong() { Key = "test", Value = 5 } },
//            NextWALOffset = 512
//        }

//    }
//};
//Random.Shared.NextBytes(randBytes);

//var SegmentData = new SegmentContent
//{
//    ContentLength = randBytes.Length,
//    SegmentId = Random.Shared.NextInt64(),
//    SegmentData = randBytes,
//    SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
//    Version = 1,
//    FileName = "test",
//    IsDeleted = false
//};

//var sampleBlock3 = new Block
//{
//    Magic = 0xEE411DBBD114EE,
//    Header = new BlockHeader
//    {
//        Type = BlockType.segment,  // Using one of the enum values
//        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
//        Version = 1
//    },
//    Content = new BlockContent
//    {
//        SegmentContent = SegmentData

//    }
//};


//using var file = File.Open("sample.capnp", FileMode.Create);
//{
//    var msg = MessageBuilder.Create();
//    var root = msg.BuildRoot<Block.WRITER>();
//    sampleBlock.serialize(root);
//    var pump = new FramePump(file);
//    pump.Send(msg.Frame);
//    file.Position = 10 * 1024 * 1024; // 10MB    
//    var msg2 = MessageBuilder.Create();
//    var root2 = msg2.BuildRoot<Block.WRITER>();
//    sampleBlock2.serialize(root2);
//    pump.Send(msg2.Frame);
//    var msg3 = MessageBuilder.Create();
//    var root3 = msg3.BuildRoot<Block.WRITER>();
//    sampleBlock3.serialize(root3);
//    for (int i = 0; i < 1000000; i++)
//    {
//        pump.Send(msg3.Frame);
//    }

//    file.Close();
//}
//var SW = Stopwatch.StartNew();
//List<long> magicPositions = FindMagicPositions("sample.capnp", BitConverter.GetBytes(ExpectedMagic));
//SW.Stop();
//Console.WriteLine($"Elapsed: {SW.ElapsedMilliseconds} ms");
//Console.WriteLine($"Found {magicPositions.Count} magic positions.");
//SW.Restart();
//ProcessBlocks("sample.capnp", magicPositions);
//SW.Stop();
//Console.WriteLine($"Elapsed: {SW.ElapsedMilliseconds} ms");


Console.WriteLine("Press any key to exit.");
