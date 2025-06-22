using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format;
using EmailDB.Format.Models;
using MimeKit;

namespace EmailDB.Console;

/// <summary>
/// Runs persistence tests for EmailDB with seeded, reproducible data
/// </summary>
public static class PersistenceTestRunner
{
    public static async Task RunAsync(string dbPath, int seed, int emailCount, int cycles)
    {
        System.Console.WriteLine("EmailDB Persistence Test");
        System.Console.WriteLine("========================\n");
        System.Console.WriteLine($"Configuration:");
        System.Console.WriteLine($"  Database Path: {dbPath}");
        System.Console.WriteLine($"  Seed: {seed}");
        System.Console.WriteLine($"  Email Count: {emailCount}");
        System.Console.WriteLine($"  Test Cycles: {cycles}\n");
        
        var random = new Random(seed);
        var stopwatch = new Stopwatch();
        var emailIds = new List<EmailHashedID>();
        var checksums = new Dictionary<string, string>();
        
        try
        {
            // Phase 1: Initial import
            System.Console.WriteLine("Phase 1: Initial Import");
            System.Console.WriteLine("-----------------------");
            stopwatch.Start();
            
            using (var db = new EmailDatabase(dbPath))
            {
                for (int i = 0; i < emailCount; i++)
                {
                    var email = GenerateTestEmail($"Test Email {i}", i, random);
                    var emailId = await db.ImportEMLAsync(email.ToString());
                    emailIds.Add(emailId);
                    checksums[emailId.ToString()] = ComputeChecksum(email);
                    
                    if ((i + 1) % 10 == 0)
                    {
                        System.Console.Write($"\r  Imported {i + 1}/{emailCount} emails...");
                    }
                }
                System.Console.WriteLine($"\r  ✓ Imported {emailCount} emails in {stopwatch.ElapsedMilliseconds}ms");
                
                // Test immediate retrieval
                var retrieved = await db.GetEmailAsync(emailIds[0]);
                System.Console.WriteLine($"  ✓ Successfully retrieved first email: {retrieved.Subject}");
            }
            
            stopwatch.Stop();
            System.Console.WriteLine($"  Import rate: {emailCount / stopwatch.Elapsed.TotalSeconds:F2} emails/sec\n");
            
            // Phase 2: Persistence cycles
            System.Console.WriteLine($"Phase 2: Testing {cycles} Persistence Cycles");
            System.Console.WriteLine("------------------------------------------");
            
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                System.Console.WriteLine($"\nCycle {cycle}:");
                stopwatch.Restart();
                
                using (var db = new EmailDatabase(dbPath))
                {
                    // Check email count
                    var allIds = await db.GetAllEmailIDsAsync();
                    System.Console.WriteLine($"  Found {allIds.Count} emails (expected {emailCount + cycle - 1})");
                    
                    if (allIds.Count != emailCount + cycle - 1)
                    {
                        System.Console.WriteLine($"  ❌ ERROR: Email count mismatch!");
                        System.Console.WriteLine($"  Debug info: {db.GetEmailIdsIndexDebug()}");
                    }
                    else
                    {
                        System.Console.WriteLine($"  ✓ Email count matches");
                    }
                    
                    // Verify checksums for a sample
                    var sampleSize = Math.Min(10, allIds.Count);
                    var verified = 0;
                    foreach (var id in allIds.Take(sampleSize))
                    {
                        try
                        {
                            var email = await db.GetEmailAsync(id);
                            if (checksums.ContainsKey(id.ToString()))
                            {
                                var checksum = ComputeChecksumFromContent(email);
                                if (checksum == checksums[id.ToString()])
                                {
                                    verified++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Console.WriteLine($"  ❌ Failed to retrieve email {id}: {ex.Message}");
                        }
                    }
                    System.Console.WriteLine($"  ✓ Verified {verified}/{sampleSize} sample emails");
                    
                    // Test search
                    var searchResults = await db.SearchAsync("test");
                    System.Console.WriteLine($"  Search for 'test': {searchResults.Count} results");
                    
                    // Add one more email for next cycle
                    var newEmail = GenerateTestEmail($"Cycle {cycle} Email", 1000 + cycle, random);
                    var newId = await db.ImportEMLAsync(newEmail.ToString());
                    checksums[newId.ToString()] = ComputeChecksum(newEmail);
                    System.Console.WriteLine($"  ✓ Added new email for cycle {cycle}");
                }
                
                stopwatch.Stop();
                System.Console.WriteLine($"  Cycle completed in {stopwatch.ElapsedMilliseconds}ms");
            }
            
            // Phase 3: Final verification
            System.Console.WriteLine("\nPhase 3: Final Verification");
            System.Console.WriteLine("---------------------------");
            
            using (var db = new EmailDatabase(dbPath))
            {
                var finalIds = await db.GetAllEmailIDsAsync();
                var expectedFinal = emailCount + cycles;
                
                if (finalIds.Count == expectedFinal)
                {
                    System.Console.WriteLine($"✅ SUCCESS: All {finalIds.Count} emails persisted correctly!");
                }
                else
                {
                    System.Console.WriteLine($"❌ FAILURE: Expected {expectedFinal} emails but found {finalIds.Count}");
                    System.Console.WriteLine($"Debug: {db.GetEmailIdsIndexDebug()}");
                }
                
                // Database stats
                var stats = await db.GetDatabaseStatsAsync();
                System.Console.WriteLine($"\nDatabase Statistics:");
                System.Console.WriteLine($"  Total Emails: {stats.TotalEmails}");
                System.Console.WriteLine($"  Storage Blocks: {stats.StorageBlocks}");
                System.Console.WriteLine($"  Search Indexes: {stats.SearchIndexes}");
            }
            
            // Calculate database size
            var dbSize = GetDirectorySize(dbPath);
            System.Console.WriteLine($"\nStorage Metrics:");
            System.Console.WriteLine($"  Database Size: {dbSize / 1024.0 / 1024.0:F2} MB");
            System.Console.WriteLine($"  Avg per Email: {dbSize / (emailCount + cycles):F0} bytes");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n❌ Test failed with error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // Optionally clean up
            System.Console.WriteLine($"\nTest database preserved at: {dbPath}");
            System.Console.WriteLine("Delete manually if no longer needed.");
        }
    }

    private static MimeMessage GenerateTestEmail(string subject, int index, Random random)
    {
        var message = new MimeMessage();
        message.MessageId = $"<test-{index}-{Guid.NewGuid()}@emaildb.test>";
        message.Date = DateTimeOffset.UtcNow.AddDays(-random.Next(0, 365));
        
        var senders = new[] { "alice", "bob", "charlie", "david", "eve" };
        var domains = new[] { "example.com", "test.org", "email.net" };
        
        message.From.Add(new MailboxAddress(
            senders[random.Next(senders.Length)].ToUpper(),
            $"{senders[random.Next(senders.Length)]}@{domains[random.Next(domains.Length)]}"
        ));
        
        message.To.Add(new MailboxAddress(
            "Recipient",
            $"recipient{index}@{domains[random.Next(domains.Length)]}"
        ));
        
        message.Subject = subject;
        
        var bodyTypes = new[] { "meeting", "report", "update", "notification" };
        var priority = random.Next(100) < 20 ? "important" : "normal";
        
        message.Body = new TextPart("plain")
        {
            Text = $@"This is a {bodyTypes[random.Next(bodyTypes.Length)]} email with {priority} priority.

Generated for persistence test:
- Index: {index}
- Random: {random.Next(10000)}
- Timestamp: {DateTime.UtcNow:O}

Lorem ipsum dolor sit amet, consectetur adipiscing elit."
        };
        
        return message;
    }

    private static string ComputeChecksum(MimeMessage message)
    {
        var data = $"{message.From}|{message.To}|{message.Subject}|{message.TextBody}";
        using (var md5 = MD5.Create())
        {
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }
    }

    private static string ComputeChecksumFromContent(EmailContent content)
    {
        var data = $"{content.From}|{content.To}|{content.Subject}|{content.TextBody}";
        using (var md5 = MD5.Create())
        {
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }
}