using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

public class HybridStoreDebugTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;

    public HybridStoreDebugTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"HybridDebug_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Basic_Store_And_Retrieve()
    {
        var dataPath = Path.Combine(_testDir, "emails.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSizeThreshold: 10 * 1024); // 10KB blocks
        
        // Store a few emails
        _output.WriteLine("Storing emails...");
        var emailIds = new EmailId[5];
        
        for (int i = 0; i < 5; i++)
        {
            var messageId = $"msg-{i}@example.com";
            var folder = "inbox";
            var data = Encoding.UTF8.GetBytes($"This is email {i} with some content to make it larger. Lorem ipsum dolor sit amet.");
            
            _output.WriteLine($"Storing email {i}: {data.Length} bytes");
            emailIds[i] = await store.StoreEmailAsync(messageId, folder, data);
            _output.WriteLine($"  Stored with ID: {emailIds[i]}");
        }
        
        // Flush to ensure everything is written
        await store.FlushAsync();
        _output.WriteLine("\nFlushed store");
        
        // Try to read them back
        _output.WriteLine("\nReading emails back...");
        for (int i = 0; i < 5; i++)
        {
            try
            {
                _output.WriteLine($"Reading email ID: {emailIds[i]}");
                var (data, metadata) = await store.GetEmailAsync(emailIds[i]);
                var content = Encoding.UTF8.GetString(data);
                _output.WriteLine($"  Success! Size: {data.Length}, Content: {content.Substring(0, Math.Min(50, content.Length))}...");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Failed: {ex.Message}");
                throw;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }
}