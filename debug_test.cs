using System;
using System.IO;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Helpers;

class Program
{
    static async Task Main(string[] args)
    {
        var testFile = Path.GetTempFileName();
        Console.WriteLine($"Test file: {testFile}");
        
        try
        {
            var blockManager = new RawBlockManager(testFile);
            
            var testData = "Hello EmailDB World!";
            var payload = Encoding.UTF8.GetBytes(testData);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1001,
                Payload = payload
            };

            Console.WriteLine($"Writing block with {payload.Length} bytes");
            
            var writeResult = await blockManager.WriteBlockAsync(block);
            Console.WriteLine($"Write result - Success: {writeResult.IsSuccess}");
            if (!writeResult.IsSuccess)
            {
                Console.WriteLine($"Write error: {writeResult.Error}");
            }
            else
            {
                Console.WriteLine($"Write successful, position: {writeResult.Value.Position}, length: {writeResult.Value.Length}");
                
                var readResult = await blockManager.ReadBlockAsync(block.BlockId);
                Console.WriteLine($"Read result - Success: {readResult.IsSuccess}");
                if (!readResult.IsSuccess)
                {
                    Console.WriteLine($"Read error: {readResult.Error}");
                }
                else
                {
                    var readData = Encoding.UTF8.GetString(readResult.Value.Payload);
                    Console.WriteLine($"Read data: '{readData}'");
                    Console.WriteLine($"Match: {testData == readData}");
                }
            }
            
            blockManager.Dispose();
        }
        finally
        {
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }
}