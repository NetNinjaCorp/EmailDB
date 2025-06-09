using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Comprehensive performance and efficiency tests for HybridEmailStore.
/// </summary>
public class HybridEmailStorePerformanceTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public HybridEmailStorePerformanceTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"HybridStore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Hybrid_Store_Performance_And_Efficiency()
    {
        _output.WriteLine("🔷 HYBRID EMAIL STORE PERFORMANCE TEST");
        _output.WriteLine("=====================================\n");

        // Test configuration
        const int emailCount = 100; // Reduced for testing
        const int searchQueries = 10;
        const int folderCount = 5;
        
        var dataPath = Path.Combine(_testDir, "emails.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSizeThreshold: 512 * 1024);
        
        // Generate test data
        var emails = GenerateTestEmails(emailCount, folderCount);
        var totalEmailSize = emails.Sum(e => (long)e.data.Length);
        
        _output.WriteLine($"📊 Test Configuration:");
        _output.WriteLine($"  Emails: {emailCount:N0}");
        _output.WriteLine($"  Folders: {folderCount}");
        _output.WriteLine($"  Total email data: {FormatBytes(totalEmailSize)}");
        _output.WriteLine($"  Average email size: {FormatBytes(totalEmailSize / emailCount)}");
        
        // Phase 1: Write Performance
        _output.WriteLine($"\n📝 PHASE 1: WRITE PERFORMANCE");
        _output.WriteLine($"============================");
        
        var sw = Stopwatch.StartNew();
        var emailIds = new List<EmailId>();
        
        foreach (var email in emails)
        {
            var emailId = await store.StoreEmailAsync(
                email.messageId,
                email.folder,
                email.data,
                email.subject,
                email.from,
                email.to,
                email.body,
                email.date
            );
            emailIds.Add(emailId);
        }
        
        await store.FlushAsync();
        var writeTime = sw.ElapsedMilliseconds;
        
        _output.WriteLine($"  Write time: {writeTime:N0}ms");
        _output.WriteLine($"  Emails/second: {emailCount / (writeTime / 1000.0):N0}");
        _output.WriteLine($"  MB/second: {totalEmailSize / 1024.0 / 1024.0 / (writeTime / 1000.0):F2}");
        
        // Phase 2: Storage Efficiency
        _output.WriteLine($"\n📁 PHASE 2: STORAGE EFFICIENCY");
        _output.WriteLine($"=============================");
        
        var stats = store.GetStats();
        var totalSize = stats.TotalSize;
        var efficiency = (double)totalEmailSize / totalSize * 100;
        
        _output.WriteLine($"  Data file size: {FormatBytes(stats.DataFileSize)}");
        _output.WriteLine($"  Index size: {FormatBytes(stats.IndexSize)}");
        _output.WriteLine($"  Total size: {FormatBytes(totalSize)}");
        _output.WriteLine($"  Storage efficiency: {efficiency:F1}%");
        _output.WriteLine($"  Overhead: {FormatBytes(totalSize - totalEmailSize)} ({(totalSize - totalEmailSize) * 100.0 / totalEmailSize:F1}%)");
        _output.WriteLine($"  Indexed words: {stats.IndexedWords:N0}");
        
        // Phase 3: Read Performance
        _output.WriteLine($"\n📖 PHASE 3: READ PERFORMANCE");
        _output.WriteLine($"===========================");
        
        // Random reads by EmailId
        sw.Restart();
        var readCount = Math.Min(50, emailCount);
        for (int i = 0; i < readCount; i++)
        {
            var randomId = emailIds[_random.Next(emailIds.Count)];
            var (data, metadata) = await store.GetEmailAsync(randomId);
        }
        var randomReadTime = sw.ElapsedMilliseconds;
        
        // Sequential reads by MessageId
        sw.Restart();
        for (int i = 0; i < readCount; i++)
        {
            var email = emails[i % emails.Length];
            var (data, metadata) = await store.GetEmailByMessageIdAsync(email.messageId);
        }
        var messageIdReadTime = sw.ElapsedMilliseconds;
        
        _output.WriteLine($"  Random reads ({readCount}): {randomReadTime}ms ({readCount / (randomReadTime / 1000.0):N0} reads/sec)");
        _output.WriteLine($"  MessageId lookups ({readCount}): {messageIdReadTime}ms ({readCount / (messageIdReadTime / 1000.0):N0} lookups/sec)");
        
        // Phase 4: Folder Operations
        _output.WriteLine($"\n📂 PHASE 4: FOLDER OPERATIONS");
        _output.WriteLine($"============================");
        
        sw.Restart();
        var folderListings = new Dictionary<string, int>();
        for (int i = 0; i < folderCount; i++)
        {
            var folder = $"folder_{i}";
            var count = store.ListFolder(folder).Count();
            folderListings[folder] = count;
        }
        var folderListTime = sw.ElapsedMilliseconds;
        
        _output.WriteLine($"  Folder listing time: {folderListTime}ms");
        _output.WriteLine($"  Average emails per folder: {folderListings.Values.Average():F1}");
        _output.WriteLine($"  Folders/second: {folderCount / (folderListTime / 1000.0):N0}");
        
        // Phase 5: Full-Text Search
        _output.WriteLine($"\n🔍 PHASE 5: FULL-TEXT SEARCH");
        _output.WriteLine($"===========================");
        
        // Generate search queries
        var searchWords = new[] { "important", "meeting", "project", "update", "urgent", "review", "proposal", "schedule" };
        var searchTimes = new List<long>();
        var searchResults = new List<int>();
        
        foreach (var word in searchWords.Take(searchQueries))
        {
            sw.Restart();
            var results = store.SearchFullText(word).ToList();
            searchTimes.Add(sw.ElapsedMilliseconds);
            searchResults.Add(results.Count);
        }
        
        _output.WriteLine($"  Search queries: {searchWords.Length}");
        _output.WriteLine($"  Average search time: {searchTimes.Average():F2}ms");
        _output.WriteLine($"  Min search time: {searchTimes.Min()}ms");
        _output.WriteLine($"  Max search time: {searchTimes.Max()}ms");
        _output.WriteLine($"  Average results: {searchResults.Average():F1}");
        
        // Phase 6: Move Operations
        _output.WriteLine($"\n📤 PHASE 6: MOVE OPERATIONS");
        _output.WriteLine($"==========================");
        
        sw.Restart();
        var moveCount = Math.Min(20, emailCount);
        for (int i = 0; i < moveCount; i++)
        {
            var emailId = emailIds[_random.Next(emailIds.Count)];
            var newFolder = $"folder_{_random.Next(folderCount)}";
            await store.MoveEmailAsync(emailId, newFolder);
        }
        var moveTime = sw.ElapsedMilliseconds;
        
        _output.WriteLine($"  Moves performed: {moveCount}");
        _output.WriteLine($"  Total time: {moveTime}ms");
        _output.WriteLine($"  Moves/second: {moveCount / (moveTime / 1000.0):N0}");
        
        // Phase 7: Delete Operations
        _output.WriteLine($"\n🗑️ PHASE 7: DELETE OPERATIONS");
        _output.WriteLine($"============================");
        
        sw.Restart();
        var deleteCount = Math.Min(20, emailCount);
        for (int i = 0; i < deleteCount; i++)
        {
            var emailId = emailIds[_random.Next(emailIds.Count)];
            try
            {
                await store.DeleteEmailAsync(emailId);
            }
            catch (KeyNotFoundException)
            {
                // Already deleted in a previous iteration
            }
        }
        var deleteTime = sw.ElapsedMilliseconds;
        
        _output.WriteLine($"  Deletes performed: {deleteCount}");
        _output.WriteLine($"  Total time: {deleteTime}ms");
        _output.WriteLine($"  Deletes/second: {deleteCount / (deleteTime / 1000.0):N0}");
        
        // Summary
        _output.WriteLine($"\n📊 PERFORMANCE SUMMARY");
        _output.WriteLine($"=====================");
        _output.WriteLine($"  Storage efficiency: {efficiency:F1}%");
        _output.WriteLine($"  Write throughput: {totalEmailSize / 1024.0 / 1024.0 / (writeTime / 1000.0):F2} MB/s");
        _output.WriteLine($"  Random read latency: {randomReadTime / 1000.0:F2}ms average");
        _output.WriteLine($"  Search latency: {searchTimes.Average():F2}ms average");
        _output.WriteLine($"  Index overhead: {stats.IndexSize * 100.0 / stats.DataFileSize:F1}% of data size");
        
        // Recommendations
        _output.WriteLine($"\n💡 ANALYSIS");
        _output.WriteLine($"===========");
        if (efficiency > 90)
        {
            _output.WriteLine($"  ✅ Excellent storage efficiency ({efficiency:F1}%)");
        }
        else if (efficiency > 80)
        {
            _output.WriteLine($"  ⚠️ Good storage efficiency ({efficiency:F1}%), consider larger block sizes");
        }
        else
        {
            _output.WriteLine($"  ❌ Poor storage efficiency ({efficiency:F1}%), review configuration");
        }
        
        var indexOverhead = stats.IndexSize * 100.0 / stats.DataFileSize;
        if (indexOverhead < 10)
        {
            _output.WriteLine($"  ✅ Low index overhead ({indexOverhead:F1}%)");
        }
        else if (indexOverhead < 20)
        {
            _output.WriteLine($"  ⚠️ Moderate index overhead ({indexOverhead:F1}%)");
        }
        else
        {
            _output.WriteLine($"  ❌ High index overhead ({indexOverhead:F1}%), consider index optimization");
        }
    }

    [Theory]
    [InlineData(1000, 128 * 1024)]   // 1K emails, 128KB blocks
    [InlineData(1000, 256 * 1024)]   // 1K emails, 256KB blocks
    [InlineData(1000, 512 * 1024)]   // 1K emails, 512KB blocks
    [InlineData(1000, 1024 * 1024)]  // 1K emails, 1MB blocks
    public async Task Test_Block_Size_Impact(int emailCount, int blockSize)
    {
        _output.WriteLine($"\n🔧 BLOCK SIZE IMPACT TEST");
        _output.WriteLine($"========================");
        _output.WriteLine($"  Emails: {emailCount}");
        _output.WriteLine($"  Block size: {FormatBytes(blockSize)}");
        
        var dataPath = Path.Combine(_testDir, $"emails_{blockSize}.data");
        var indexPath = Path.Combine(_testDir, $"indexes_{blockSize}");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSize);
        
        var emails = GenerateTestEmails(emailCount, 5);
        var totalSize = emails.Sum(e => (long)e.data.Length);
        
        var sw = Stopwatch.StartNew();
        foreach (var email in emails)
        {
            await store.StoreEmailAsync(
                email.messageId, email.folder, email.data,
                email.subject, email.from, email.to, email.body, email.date
            );
        }
        await store.FlushAsync();
        var writeTime = sw.ElapsedMilliseconds;
        
        var stats = store.GetStats();
        var efficiency = totalSize * 100.0 / stats.TotalSize;
        
        _output.WriteLine($"\n  Results:");
        _output.WriteLine($"    Write time: {writeTime}ms");
        _output.WriteLine($"    File size: {FormatBytes(stats.DataFileSize)}");
        _output.WriteLine($"    Index size: {FormatBytes(stats.IndexSize)}");
        _output.WriteLine($"    Efficiency: {efficiency:F1}%");
        _output.WriteLine($"    Throughput: {totalSize / 1024.0 / 1024.0 / (writeTime / 1000.0):F2} MB/s");
    }

    private TestEmail[] GenerateTestEmails(int count, int folderCount)
    {
        var emails = new TestEmail[count];
        var words = new[] { "important", "meeting", "project", "update", "urgent", "review", 
                            "proposal", "schedule", "deadline", "budget", "report", "analysis",
                            "discussion", "planning", "strategy", "implementation" };
        
        for (int i = 0; i < count; i++)
        {
            var size = GetRealisticEmailSize();
            var folder = $"folder_{i % folderCount}";
            
            // Generate subject with searchable words
            var subjectWords = Enumerable.Range(0, _random.Next(2, 5))
                .Select(_ => words[_random.Next(words.Length)])
                .Distinct();
            var subject = $"Email {i}: {string.Join(" ", subjectWords)}";
            
            // Generate body with searchable content
            var bodyWords = Enumerable.Range(0, _random.Next(10, 30))
                .Select(_ => words[_random.Next(words.Length)]);
            var body = $"This is the body of email {i}. Keywords: {string.Join(" ", bodyWords)}. " +
                      $"Additional content to reach target size...";
            
            // Pad body to reach target size
            while (Encoding.UTF8.GetByteCount(body) < size - 200)
            {
                body += " Lorem ipsum dolor sit amet, consectetur adipiscing elit.";
            }
            
            var data = Encoding.UTF8.GetBytes(body);
            
            emails[i] = new TestEmail
            {
                messageId = $"msg-{i:D8}@example.com",
                folder = folder,
                subject = subject,
                from = $"sender{i % 100}@example.com",
                to = $"recipient{(i + 1) % 100}@example.com",
                body = body,
                date = DateTime.UtcNow.AddDays(-_random.Next(365)),
                data = data
            };
        }
        
        return emails;
    }

    private int GetRealisticEmailSize()
    {
        var rand = _random.NextDouble();
        
        if (rand < 0.4) // 40% small emails
            return _random.Next(500, 2000);
        else if (rand < 0.75) // 35% medium emails
            return _random.Next(2000, 10000);
        else if (rand < 0.95) // 20% large emails
            return _random.Next(10000, 50000);
        else // 5% very large emails
            return _random.Next(50000, 200000);
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:F2} {sizes[order]}";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    private class TestEmail
    {
        public string messageId;
        public string folder;
        public string subject;
        public string from;
        public string to;
        public string body;
        public DateTime date;
        public byte[] data;
    }
}