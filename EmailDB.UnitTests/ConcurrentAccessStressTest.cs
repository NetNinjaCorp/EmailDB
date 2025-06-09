using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Stress tests for concurrent access patterns to ensure thread safety and data integrity.
/// </summary>
public class ConcurrentAccessStressTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public ConcurrentAccessStressTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"ConcurrentTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Concurrent_Write_Read_Operations()
    {
        var dataPath = Path.Combine(_testDir, "concurrent.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSizeThreshold: 512 * 1024);
        
        var results = new StringBuilder();
        results.AppendLine("🔷 CONCURRENT ACCESS STRESS TEST");
        results.AppendLine("================================\n");
        
        // Test parameters
        const int writerCount = 10;
        const int readerCount = 10;
        const int emailsPerWriter = 100;
        const int readsPerReader = 200;
        
        var writtenEmails = new ConcurrentBag<(EmailId id, string messageId, string folder)>();
        var readErrors = new ConcurrentBag<string>();
        var writeErrors = new ConcurrentBag<string>();
        
        results.AppendLine($"📊 Test Configuration:");
        results.AppendLine($"  Writers: {writerCount}");
        results.AppendLine($"  Readers: {readerCount}");
        results.AppendLine($"  Emails per writer: {emailsPerWriter}");
        results.AppendLine($"  Total emails: {writerCount * emailsPerWriter}");
        
        // Phase 1: Concurrent writes
        results.AppendLine($"\n📝 PHASE 1: CONCURRENT WRITES");
        results.AppendLine($"============================");
        
        var sw = Stopwatch.StartNew();
        var writeTasks = new List<Task>();
        
        for (int w = 0; w < writerCount; w++)
        {
            int writerId = w;
            writeTasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < emailsPerWriter; i++)
                {
                    try
                    {
                        var messageId = $"msg-w{writerId}-{i}@test.com";
                        var folder = $"folder-{writerId % 5}";
                        var subject = $"Email from writer {writerId}";
                        var body = GenerateEmailBody(writerId, i);
                        
                        var emailId = await store.StoreEmailAsync(
                            messageId, folder, Encoding.UTF8.GetBytes(body),
                            subject: subject,
                            from: $"writer{writerId}@test.com",
                            to: "recipient@test.com",
                            body: body,
                            date: DateTime.UtcNow
                        );
                        
                        writtenEmails.Add((emailId, messageId, folder));
                    }
                    catch (Exception ex)
                    {
                        writeErrors.Add($"Writer {writerId}: {ex.Message}");
                    }
                }
            }));
        }
        
        await Task.WhenAll(writeTasks);
        var writeTime = sw.ElapsedMilliseconds;
        
        results.AppendLine($"  Write time: {writeTime}ms");
        results.AppendLine($"  Successful writes: {writtenEmails.Count}");
        results.AppendLine($"  Write errors: {writeErrors.Count}");
        results.AppendLine($"  Writes/second: {writtenEmails.Count / (writeTime / 1000.0):F0}");
        
        // Phase 2: Concurrent reads while writing
        results.AppendLine($"\n📖 PHASE 2: CONCURRENT READS DURING WRITES");
        results.AppendLine($"=========================================");
        
        var emailList = writtenEmails.ToList();
        var moreWriteTasks = new List<Task>();
        var readTasks = new List<Task>();
        var readSuccesses = 0;
        
        sw.Restart();
        
        // Start more writers
        for (int w = 0; w < 5; w++)
        {
            int writerId = writerCount + w;
            moreWriteTasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        var messageId = $"msg-w{writerId}-{i}@test.com";
                        var folder = $"folder-{writerId % 5}";
                        var body = GenerateEmailBody(writerId, i);
                        
                        var emailId = await store.StoreEmailAsync(
                            messageId, folder, Encoding.UTF8.GetBytes(body),
                            subject: $"Concurrent write {writerId}",
                            body: body
                        );
                        
                        writtenEmails.Add((emailId, messageId, folder));
                    }
                    catch (Exception ex)
                    {
                        writeErrors.Add($"Concurrent writer {writerId}: {ex.Message}");
                    }
                }
            }));
        }
        
        // Start readers
        for (int r = 0; r < readerCount; r++)
        {
            int readerId = r;
            readTasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < readsPerReader; i++)
                {
                    try
                    {
                        if (emailList.Count > 0)
                        {
                            var email = emailList[_random.Next(emailList.Count)];
                            var (data, metadata) = await store.GetEmailAsync(email.id);
                            
                            if (data != null && metadata != null)
                            {
                                Interlocked.Increment(ref readSuccesses);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        readErrors.Add($"Reader {readerId}: {ex.Message}");
                    }
                }
            }));
        }
        
        await Task.WhenAll(moreWriteTasks.Concat(readTasks));
        var mixedTime = sw.ElapsedMilliseconds;
        
        results.AppendLine($"  Mixed operation time: {mixedTime}ms");
        results.AppendLine($"  Successful reads: {readSuccesses}");
        results.AppendLine($"  Read errors: {readErrors.Count}");
        results.AppendLine($"  Total emails written: {writtenEmails.Count}");
        
        // Phase 3: Folder operations under load
        results.AppendLine($"\n📂 PHASE 3: CONCURRENT FOLDER OPERATIONS");
        results.AppendLine($"=======================================");
        
        var folderTasks = new List<Task>();
        var folderOpCount = 0;
        
        sw.Restart();
        
        // Concurrent folder listings
        for (int i = 0; i < 20; i++)
        {
            folderTasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var folder = $"folder-{j % 5}";
                    var emails = store.ListFolder(folder).ToList();
                    Interlocked.Increment(ref folderOpCount);
                }
            }));
        }
        
        // Concurrent moves
        emailList = writtenEmails.ToList();
        for (int i = 0; i < 10; i++)
        {
            folderTasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < 20; j++)
                {
                    try
                    {
                        var email = emailList[_random.Next(emailList.Count)];
                        var newFolder = $"folder-{_random.Next(5)}";
                        await store.MoveEmailAsync(email.id, newFolder);
                        Interlocked.Increment(ref folderOpCount);
                    }
                    catch { }
                }
            }));
        }
        
        await Task.WhenAll(folderTasks);
        var folderTime = sw.ElapsedMilliseconds;
        
        results.AppendLine($"  Folder operations: {folderOpCount}");
        results.AppendLine($"  Time: {folderTime}ms");
        results.AppendLine($"  Operations/second: {folderOpCount / (folderTime / 1000.0):F0}");
        
        // Phase 4: Search operations under load
        results.AppendLine($"\n🔍 PHASE 4: CONCURRENT SEARCH OPERATIONS");
        results.AppendLine($"=======================================");
        
        var searchTasks = new List<Task>();
        var searchCount = 0;
        
        sw.Restart();
        
        var searchWords = new[] { "email", "writer", "concurrent", "test", "data" };
        
        for (int i = 0; i < 20; i++)
        {
            searchTasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var word = searchWords[j % searchWords.Length];
                    var results = store.SearchFullText(word).ToList();
                    Interlocked.Increment(ref searchCount);
                }
            }));
        }
        
        await Task.WhenAll(searchTasks);
        var searchTime = sw.ElapsedMilliseconds;
        
        results.AppendLine($"  Searches performed: {searchCount}");
        results.AppendLine($"  Time: {searchTime}ms");
        results.AppendLine($"  Searches/second: {searchCount / (searchTime / 1000.0):F0}");
        
        // Verify data integrity
        results.AppendLine($"\n✅ PHASE 5: DATA INTEGRITY VERIFICATION");
        results.AppendLine($"======================================");
        
        await store.FlushAsync();
        
        // Verify all written emails can be read
        var verifyErrors = 0;
        var sampleSize = Math.Min(100, writtenEmails.Count);
        var sample = writtenEmails.OrderBy(_ => Guid.NewGuid()).Take(sampleSize).ToList();
        
        foreach (var (id, messageId, folder) in sample)
        {
            try
            {
                var (data, metadata) = await store.GetEmailAsync(id);
                if (data == null || metadata == null || metadata.MessageId != messageId)
                {
                    verifyErrors++;
                }
            }
            catch
            {
                verifyErrors++;
            }
        }
        
        results.AppendLine($"  Sample size: {sampleSize}");
        results.AppendLine($"  Verification errors: {verifyErrors}");
        results.AppendLine($"  Integrity: {(verifyErrors == 0 ? "✅ PASS" : "❌ FAIL")}");
        
        // Summary
        results.AppendLine($"\n📊 SUMMARY");
        results.AppendLine($"==========");
        results.AppendLine($"  Total emails: {writtenEmails.Count}");
        results.AppendLine($"  Write errors: {writeErrors.Count}");
        results.AppendLine($"  Read errors: {readErrors.Count}");
        results.AppendLine($"  Data integrity: {(verifyErrors == 0 ? "✅ Verified" : $"❌ {verifyErrors} errors")}");
        
        _output.WriteLine(results.ToString());
        
        // Assertions
        Assert.True(writeErrors.Count < writtenEmails.Count * 0.01, "Write error rate too high");
        Assert.True(readErrors.Count < readSuccesses * 0.01, "Read error rate too high");
        Assert.Equal(0, verifyErrors);
    }

    [Fact]
    public async Task Test_Concurrent_Index_Consistency()
    {
        var dataPath = Path.Combine(_testDir, "index_consistency.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath);
        
        _output.WriteLine("\n🔍 INDEX CONSISTENCY UNDER CONCURRENT LOAD");
        _output.WriteLine("=========================================");
        
        const int threadCount = 10;
        const int operationsPerThread = 50;
        
        var tasks = new List<Task>();
        var operations = new ConcurrentBag<string>();
        
        // Mixed operations that stress index consistency
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    var op = _random.Next(4);
                    
                    try
                    {
                        switch (op)
                        {
                            case 0: // Store
                                var messageId = $"msg-{threadId}-{i}@test.com";
                                await store.StoreEmailAsync(
                                    messageId,
                                    $"folder-{threadId % 3}",
                                    Encoding.UTF8.GetBytes($"Content {threadId}-{i}"),
                                    body: $"Thread {threadId} operation {i}"
                                );
                                operations.Add($"Store:{messageId}");
                                break;
                                
                            case 1: // Search
                                var results = store.SearchFullText($"Thread").ToList();
                                operations.Add($"Search:Found {results.Count}");
                                break;
                                
                            case 2: // List folder
                                var folder = $"folder-{threadId % 3}";
                                var emails = store.ListFolder(folder).ToList();
                                operations.Add($"List:{folder}:{emails.Count}");
                                break;
                                
                            case 3: // Get by message ID
                                try
                                {
                                    var targetId = $"msg-{threadId}-{_random.Next(i + 1)}@test.com";
                                    var (data, meta) = await store.GetEmailByMessageIdAsync(targetId);
                                    operations.Add($"Get:{targetId}:Success");
                                }
                                catch (KeyNotFoundException)
                                {
                                    operations.Add($"Get:NotFound");
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        operations.Add($"Error:{ex.GetType().Name}");
                    }
                }
            }));
        }
        
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        sw.Stop();
        
        _output.WriteLine($"  Operations: {operations.Count}");
        _output.WriteLine($"  Time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Ops/second: {operations.Count / (sw.ElapsedMilliseconds / 1000.0):F0}");
        
        // Verify index consistency
        await store.FlushAsync();
        
        var stats = store.GetStats();
        _output.WriteLine($"\n  Final state:");
        _output.WriteLine($"    Emails: {stats.EmailCount}");
        _output.WriteLine($"    Folders: {stats.FolderCount}");
        _output.WriteLine($"    Indexed words: {stats.IndexedWords}");
        
        Assert.True(operations.Count > 0);
        Assert.True(stats.EmailCount > 0);
    }

    private string GenerateEmailBody(int writerId, int index)
    {
        var size = _random.Next(1000, 10000);
        return $"Email from writer {writerId}, index {index}. " + 
               new string('x', size) + 
               $" End of email data.";
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