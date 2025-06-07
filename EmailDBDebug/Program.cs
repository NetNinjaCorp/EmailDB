using System;
using System.IO;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

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
            Console.WriteLine($"Expected total length: {61 + payload.Length} bytes");
            
            var writeResult = await blockManager.WriteBlockAsync(block);
            Console.WriteLine($"Write result - Success: {writeResult.IsSuccess}");
            if (!writeResult.IsSuccess)
            {
                Console.WriteLine($"Write error: {writeResult.Error}");
            }
            else
            {
                Console.WriteLine($"Write successful, position: {writeResult.Value.Position}, length: {writeResult.Value.Length}");
                
                // Check actual file size
                var fileInfo = new FileInfo(testFile);
                Console.WriteLine($"Actual file size: {fileInfo.Length} bytes");
                
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
            
            // Let's analyze the actual file content
            using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read))
            {
                Console.WriteLine($"File length: {fs.Length}");
                
                byte[] allBytes = new byte[fs.Length];
                fs.Read(allBytes, 0, (int)fs.Length);
                
                Console.WriteLine("File contents (hex):");
                for (int i = 0; i < Math.Min(allBytes.Length, 100); i += 16)
                {
                    var line = "";
                    for (int j = 0; j < 16 && i + j < allBytes.Length; j++)
                    {
                        line += $"{allBytes[i + j]:X2} ";
                    }
                    Console.WriteLine($"{i:X4}: {line}");
                }
            }
            
            blockManager.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
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
