using BenchmarkDotNet.Attributes;
using System;
using System.IO;
using Capnp;
using EmailDB.Format.CapnProto;
using EmailDB.Format.CapnProto.Models; // Add using for model types
namespace CapnpBenchmarkTest;

[MemoryDiagnoser]
public class CapnpBenchmark
{
    private Block sampleBlock; // Keep for original benchmarks
    private byte[] serializedData; // Keep for original benchmarks
    private RawBlockManager rawBlockManager;
    private string tempFilePath;
    private const int BlocksToWrite = 100; // Number of blocks for the compaction test
    [GlobalSetup]
    public void Setup()
    {
        // --- Setup for original benchmarks ---
        sampleBlock = new Block { /* ... existing initialization ... */ }; // Simplified for brevity
        // (Keep existing sampleBlock initialization as it was)
        sampleBlock = new Block
        {
            Magic = 0xEE411DBBD114EE,
            Header = new BlockHeader
            {
                Type = BlockType.metadata,  // Using one of the enum values
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Version = 1,
                Checksum = 0 // Checksum will be calculated by RawBlockManager
            },
            Content = new BlockContent
            {
                MetadataContent = new MetadataContent
                {
                    WalOffset = 0,
                    FolderTreeOffset = 0,
                    SegmentOffsets = new List<KeyValueTextLong>(),
                    OutdatedOffsets = new List<long>()
                }
            }
        };
        // Serialize sampleBlock to byte array for CapNPDeserializeBenchmark
        using (var ms = new MemoryStream())
        {
             var msg = MessageBuilder.Create();
             var root =  msg.BuildRoot<Block.WRITER>();
             sampleBlock.serialize(root); // Assuming Block has a serialize method
             var pump = new FramePump(ms);
             pump.Send(msg.Frame);
             serializedData = ms.ToArray();
        }


        // --- Setup for new WriteAndCompactBenchmark ---
        tempFilePath = Path.Combine(Path.GetTempPath(), $"benchmark_{Guid.NewGuid()}.emdb");
        // Ensure clean state for each run if necessary, or handle file existence
        if (File.Exists(tempFilePath))
        {
            File.Delete(tempFilePath);
        }
         if (File.Exists(tempFilePath + ".temp"))
        {
            File.Delete(tempFilePath + ".temp");
        }
         if (File.Exists(tempFilePath + ".bak"))
        {
            File.Delete(tempFilePath + ".bak");
        }
        rawBlockManager = new RawBlockManager(tempFilePath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        rawBlockManager?.Dispose();
        // Attempt to delete files, ignore errors if they occur (e.g., file lock)
        try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
        try { if (File.Exists(tempFilePath + ".temp")) File.Delete(tempFilePath + ".temp"); } catch { }
        try { if (File.Exists(tempFilePath + ".bak")) File.Delete(tempFilePath + ".bak"); } catch { }
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
    // Removed misplaced closing brace from here

    [Benchmark]
    public async Task<long> WriteAndCompactBenchmark()
    {
        // Ensure manager is ready (re-create if needed between iterations by BenchmarkDotNet)
        // Note: BenchmarkDotNet typically calls Setup once per run, not per iteration.
        // If cleanup/setup per iteration is needed, use [IterationSetup] and [IterationCleanup].
        // For this case, GlobalSetup/Cleanup should be sufficient if the manager state persists correctly.

        // Phase 1: Write initial blocks
        for (int i = 0; i < BlocksToWrite; i++)
        {
            var block = CreateSampleBlockForTest(i + 1); // Use unique IDs
            await rawBlockManager.WriteBlockAsync(block);
        }

        // Phase 2: Update some blocks (simulates outdated data)
        for (int i = 0; i < BlocksToWrite / 2; i++)
        {
            var block = CreateSampleBlockForTest(i + 1); // Reuse IDs to update
            block.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000; // Modify slightly
            // Potentially modify content payload here if needed
            await rawBlockManager.WriteBlockAsync(block);
        }

        // Phase 3: Compact the file
        await rawBlockManager.CompactAsync();

        // Phase 4: Return the size of the compacted file
        // Note: This measures the size *after* the operation. BenchmarkDotNet measures the *time* of the operation.
        // The file size isn't directly measured by BDN but is a result of the benchmarked operation.
        rawBlockManager.Dispose(); // Need to dispose to release file lock before getting length
        long fileSize = new FileInfo(tempFilePath).Length;
        // Re-initialize for potential next iteration if needed (depends on BDN execution strategy)
        rawBlockManager = new RawBlockManager(tempFilePath);
        return fileSize;
    }

    // Helper method to create sample blocks for the compaction test
    private Block CreateSampleBlockForTest(long blockId)
    {
        // Create a basic block structure. Adapt as needed.
        // Ensure payload is generated correctly if your Block class expects raw bytes.
        // For simplicity, using a small placeholder payload.
        byte[] payload = System.Text.Encoding.UTF8.GetBytes($"Payload for block {blockId}");

        return new Block
        {
            // Magic and Checksum are handled by RawBlockManager's WriteBlockToStream
            Version = 1,
            Type = BlockType.genericData, // Or another appropriate type
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = blockId,
            Payload = payload // Assign the raw byte payload
            // Content field might not be directly used if Payload is the raw data storage
        };
    }
} // Added closing brace for the class here
