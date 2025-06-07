using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

public class HybridStoreFolderSearchTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public HybridStoreFolderSearchTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"HybridFolderTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Folder_Index_Accuracy_And_Performance()
    {
        var dataPath = Path.Combine(_testDir, "emails.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        var resultsPath = Path.Combine(_testDir, "test_results.txt");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSizeThreshold: 256 * 1024); // 256KB blocks
        
        var results = new StringBuilder();
        results.AppendLine("🔷 HYBRID STORE FOLDER SEARCH TEST");
        results.AppendLine("==================================\n");
        
        // Test setup
        const int emailCount = 1000;
        const int folderCount = 10;
        var folders = new[] { "inbox", "sent", "drafts", "archive", "spam", "trash", "work", "personal", "projects", "receipts" };
        
        // Track emails per folder
        var folderEmails = new Dictionary<string, List<(EmailId id, string messageId)>>();
        foreach (var folder in folders)
        {
            folderEmails[folder] = new List<(EmailId, string)>();
        }
        
        // Generate and store emails
        results.AppendLine("📝 PHASE 1: STORING EMAILS");
        results.AppendLine("==========================");
        
        var totalSize = 0L;
        var sw = Stopwatch.StartNew();
        
        for (int i = 0; i < emailCount; i++)
        {
            var folder = folders[i % folderCount];
            var messageId = $"msg-{i:D6}@example.com";
            var subject = $"Email {i} in {folder}";
            var body = $"This is the body of email {i} stored in folder {folder}. " +
                      $"Some additional content to make it realistic: Lorem ipsum dolor sit amet, " +
                      $"consectetur adipiscing elit. Email number: {i}";
            var data = Encoding.UTF8.GetBytes(body);
            totalSize += data.Length;
            
            var emailId = await store.StoreEmailAsync(
                messageId, folder, data,
                subject: subject,
                from: $"sender{i}@example.com",
                to: $"recipient{i}@example.com",
                body: body,
                date: DateTime.UtcNow.AddDays(-_random.Next(365))
            );
            
            folderEmails[folder].Add((emailId, messageId));
        }
        
        await store.FlushAsync();
        var storeTime = sw.ElapsedMilliseconds;
        
        results.AppendLine($"  Emails stored: {emailCount}");
        results.AppendLine($"  Total size: {FormatBytes(totalSize)}");
        results.AppendLine($"  Store time: {storeTime}ms");
        results.AppendLine($"  Throughput: {emailCount / (storeTime / 1000.0):F0} emails/sec");
        
        // Get storage stats
        var stats = store.GetStats();
        var efficiency = (double)totalSize / stats.TotalSize * 100;
        
        results.AppendLine($"\n📊 STORAGE EFFICIENCY:");
        results.AppendLine($"  Data file: {FormatBytes(stats.DataFileSize)}");
        results.AppendLine($"  Index size: {FormatBytes(stats.IndexSize)}");
        results.AppendLine($"  Total size: {FormatBytes(stats.TotalSize)}");
        results.AppendLine($"  Efficiency: {efficiency:F1}%");
        results.AppendLine($"  Overhead: {(100 - efficiency):F1}%");
        
        // Test folder listings
        results.AppendLine("\n📂 PHASE 2: FOLDER INDEX VERIFICATION");
        results.AppendLine("=====================================");
        
        var allCorrect = true;
        sw.Restart();
        
        foreach (var folder in folders)
        {
            var listedEmails = store.ListFolder(folder).ToList();
            var expectedCount = folderEmails[folder].Count;
            var actualCount = listedEmails.Count;
            
            results.AppendLine($"\n  Folder: {folder}");
            results.AppendLine($"    Expected: {expectedCount} emails");
            results.AppendLine($"    Found: {actualCount} emails");
            
            if (expectedCount != actualCount)
            {
                results.AppendLine($"    ❌ COUNT MISMATCH!");
                allCorrect = false;
            }
            else
            {
                // Verify each email
                var expectedMessageIds = folderEmails[folder].Select(e => e.messageId).OrderBy(x => x).ToList();
                var actualMessageIds = listedEmails.Select(e => e.MessageId).OrderBy(x => x).ToList();
                
                var missingIds = expectedMessageIds.Except(actualMessageIds).ToList();
                var extraIds = actualMessageIds.Except(expectedMessageIds).ToList();
                
                if (missingIds.Any() || extraIds.Any())
                {
                    results.AppendLine($"    ❌ CONTENT MISMATCH!");
                    if (missingIds.Any())
                        results.AppendLine($"    Missing: {string.Join(", ", missingIds.Take(5))}...");
                    if (extraIds.Any())
                        results.AppendLine($"    Extra: {string.Join(", ", extraIds.Take(5))}...");
                    allCorrect = false;
                }
                else
                {
                    results.AppendLine($"    ✅ All emails correctly indexed");
                }
            }
        }
        
        var verifyTime = sw.ElapsedMilliseconds;
        results.AppendLine($"\n  Verification time: {verifyTime}ms");
        results.AppendLine($"  Overall result: {(allCorrect ? "✅ PASS" : "❌ FAIL")}");
        
        // Test folder search performance
        results.AppendLine("\n⚡ PHASE 3: FOLDER SEARCH PERFORMANCE");
        results.AppendLine("====================================");
        
        sw.Restart();
        for (int i = 0; i < 100; i++)
        {
            var folder = folders[_random.Next(folders.Length)];
            var emails = store.ListFolder(folder).ToList();
        }
        var searchTime = sw.ElapsedMilliseconds;
        
        results.AppendLine($"  100 folder searches: {searchTime}ms");
        results.AppendLine($"  Average per search: {searchTime / 100.0:F2}ms");
        results.AppendLine($"  Searches/second: {100.0 / (searchTime / 1000.0):F0}");
        
        // Test moving emails between folders
        results.AppendLine("\n📤 PHASE 4: FOLDER MOVE OPERATIONS");
        results.AppendLine("=================================");
        
        sw.Restart();
        var moveCount = 50;
        var movedEmails = new List<(EmailId id, string oldFolder, string newFolder)>();
        
        for (int i = 0; i < moveCount; i++)
        {
            var oldFolder = folders[_random.Next(folders.Length)];
            var newFolder = folders[_random.Next(folders.Length)];
            if (oldFolder == newFolder) continue;
            
            var emails = folderEmails[oldFolder];
            if (emails.Count == 0) continue;
            
            var emailToMove = emails[_random.Next(emails.Count)];
            await store.MoveEmailAsync(emailToMove.id, newFolder);
            
            movedEmails.Add((emailToMove.id, oldFolder, newFolder));
            
            // Update our tracking
            emails.Remove(emailToMove);
            folderEmails[newFolder].Add(emailToMove);
        }
        
        var moveTime = sw.ElapsedMilliseconds;
        results.AppendLine($"  Moves performed: {movedEmails.Count}");
        results.AppendLine($"  Move time: {moveTime}ms");
        results.AppendLine($"  Moves/second: {movedEmails.Count / (moveTime / 1000.0):F0}");
        
        // Verify moves
        results.AppendLine("\n  Verifying moves...");
        var moveErrors = 0;
        
        foreach (var (id, oldFolder, newFolder) in movedEmails)
        {
            var (_, metadata) = await store.GetEmailAsync(id);
            if (metadata.Folder != newFolder)
            {
                results.AppendLine($"    ❌ Email {id} should be in {newFolder} but is in {metadata.Folder}");
                moveErrors++;
            }
        }
        
        if (moveErrors == 0)
        {
            results.AppendLine($"    ✅ All moves verified correctly");
        }
        else
        {
            results.AppendLine($"    ❌ {moveErrors} move errors found");
        }
        
        // Test full-text search
        results.AppendLine("\n🔍 PHASE 5: FULL-TEXT SEARCH TEST");
        results.AppendLine("=================================");
        
        var searchWords = new[] { "email", "folder", "inbox", "body", "lorem" };
        foreach (var word in searchWords)
        {
            sw.Restart();
            var searchResults = store.SearchFullText(word).ToList();
            var searchMs = sw.ElapsedMilliseconds;
            
            results.AppendLine($"  Search '{word}': {searchResults.Count} results in {searchMs}ms");
        }
        
        // Final summary
        results.AppendLine("\n📈 SUMMARY");
        results.AppendLine("==========");
        results.AppendLine($"  Storage efficiency: {efficiency:F1}%");
        results.AppendLine($"  Folder index accuracy: {(allCorrect ? "✅ 100%" : "❌ Failed")}");
        results.AppendLine($"  Average folder search: {searchTime / 100.0:F2}ms");
        results.AppendLine($"  Email throughput: {emailCount / (storeTime / 1000.0):F0} emails/sec");
        
        // Write results to file and output
        await File.WriteAllTextAsync(resultsPath, results.ToString());
        _output.WriteLine(results.ToString());
        _output.WriteLine($"\nResults written to: {resultsPath}");
        
        // Assert all tests passed
        Assert.True(allCorrect, "Folder index verification failed");
        Assert.True(moveErrors == 0, "Move operations had errors");
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
}