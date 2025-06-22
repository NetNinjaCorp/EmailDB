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
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Comprehensive persistence tests for EmailDB with seeded, reproducible data.
/// Ensures that all data is ALWAYS saved correctly and retrievable.
/// </summary>
public class EmailDBPersistenceTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;
    private readonly Random _random;
    private const int DEFAULT_SEED = 42;

    public EmailDBPersistenceTest(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"emaildb_test_{Guid.NewGuid()}");
        _random = new Random(DEFAULT_SEED);
    }

    [Theory]
    [InlineData(1, 10)]     // 10 emails
    [InlineData(2, 50)]     // 50 emails
    [InlineData(3, 100)]    // 100 emails
    [InlineData(4, 500)]    // 500 emails
    public async Task TestPersistenceWithSeed(int testRun, int emailCount)
    {
        _output.WriteLine($"=== Test Run {testRun}: Testing persistence with {emailCount} emails ===");
        
        // Generate test data with seed
        var testEmails = GenerateSeededEmails(emailCount, testRun);
        var expectedHashes = new Dictionary<string, string>();
        
        // Step 1: Create database and import emails
        _output.WriteLine("\nStep 1: Creating database and importing emails...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            foreach (var (email, expectedId) in testEmails)
            {
                var importedId = await db.ImportEMLAsync(email.ToString());
                expectedHashes[expectedId] = ComputeContentHash(email);
                
                // Verify email was imported correctly
                var retrieved = await db.GetEmailAsync(importedId);
                Assert.NotNull(retrieved);
                Assert.Equal(email.Subject, retrieved.Subject);
            }
            
            // Verify count
            var allIds = await db.GetAllEmailIDsAsync();
            Assert.Equal(emailCount, allIds.Count);
            _output.WriteLine($"✓ Imported {emailCount} emails successfully");
        }
        
        // Step 2: Reopen database and verify all data persists
        _output.WriteLine("\nStep 2: Reopening database to verify persistence...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            var allIds = await db.GetAllEmailIDsAsync();
            Assert.Equal(emailCount, allIds.Count);
            _output.WriteLine($"✓ Found {allIds.Count} emails after reopening");
            
            // Verify each email's content
            int verified = 0;
            foreach (var emailId in allIds)
            {
                var email = await db.GetEmailAsync(emailId);
                Assert.NotNull(email);
                
                // Reconstruct MimeMessage for hash verification
                var reconstructed = ReconstructMimeMessage(email);
                var currentHash = ComputeContentHash(reconstructed);
                
                // Find matching hash
                Assert.Contains(currentHash, expectedHashes.Values);
                verified++;
            }
            _output.WriteLine($"✓ Verified content integrity for all {verified} emails");
        }
        
        // Step 3: Test search functionality after reopen
        _output.WriteLine("\nStep 3: Testing search functionality...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            // Search for common terms
            var searchTerms = new[] { "test", "email", "subject", "important", "meeting" };
            foreach (var term in searchTerms)
            {
                var results = await db.SearchAsync(term);
                _output.WriteLine($"  Search '{term}': {results.Count} results");
                Assert.True(results.Count >= 0); // Should not throw
            }
        }
        
        // Step 4: Multiple open/close cycles
        _output.WriteLine("\nStep 4: Testing multiple open/close cycles...");
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            using (var db = new EmailDatabase(_testDbPath))
            {
                var allIds = await db.GetAllEmailIDsAsync();
                Assert.Equal(emailCount, allIds.Count);
                _output.WriteLine($"  Cycle {cycle}: ✓ {allIds.Count} emails");
                
                // Add one more email
                var newEmail = GenerateSingleEmail($"Cycle {cycle} Email", cycle * 1000);
                await db.ImportEMLAsync(newEmail.ToString());
            }
            
            // Verify new count
            using (var db = new EmailDatabase(_testDbPath))
            {
                var allIds = await db.GetAllEmailIDsAsync();
                Assert.Equal(emailCount + cycle, allIds.Count);
            }
        }
        
        _output.WriteLine($"\n✓ Test completed successfully for {emailCount} emails");
    }

    [Fact]
    public async Task TestConcurrentWritesAndReads()
    {
        _output.WriteLine("=== Testing concurrent writes and reads ===");
        
        const int threadCount = 5;
        const int emailsPerThread = 20;
        
        // Initialize database
        using (var db = new EmailDatabase(_testDbPath))
        {
            await db.ImportEMLAsync(GenerateSingleEmail("Initial", 0).ToString());
        }
        
        // Concurrent writes
        var tasks = new List<Task>();
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(async () =>
            {
                using var db = new EmailDatabase(_testDbPath);
                for (int i = 0; i < emailsPerThread; i++)
                {
                    var email = GenerateSingleEmail($"Thread{threadId}-Email{i}", threadId * 100 + i);
                    await db.ImportEMLAsync(email.ToString());
                }
            }));
        }
        
        await Task.WhenAll(tasks);
        _output.WriteLine($"✓ Completed {threadCount} concurrent threads");
        
        // Verify all emails
        using (var db = new EmailDatabase(_testDbPath))
        {
            var allIds = await db.GetAllEmailIDsAsync();
            var expectedCount = 1 + (threadCount * emailsPerThread); // Initial + all threads
            Assert.Equal(expectedCount, allIds.Count);
            _output.WriteLine($"✓ Verified {allIds.Count} total emails");
        }
    }

    [Fact]
    public async Task TestDataIntegrityWithChecksums()
    {
        _output.WriteLine("=== Testing data integrity with checksums ===");
        
        var checksums = new Dictionary<string, string>();
        const int emailCount = 25;
        
        // Import with checksums
        using (var db = new EmailDatabase(_testDbPath))
        {
            for (int i = 0; i < emailCount; i++)
            {
                var email = GenerateSingleEmail($"Checksum Test {i}", i);
                var emailId = await db.ImportEMLAsync(email.ToString());
                checksums[emailId.ToString()] = ComputeContentHash(email);
            }
        }
        
        // Verify checksums after multiple reopens
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            _output.WriteLine($"\nVerification cycle {cycle}:");
            using (var db = new EmailDatabase(_testDbPath))
            {
                foreach (var (idStr, expectedChecksum) in checksums)
                {
                    var emailId = EmailHashedID.FromBase32String(idStr);
                    var email = await db.GetEmailAsync(emailId);
                    var reconstructed = ReconstructMimeMessage(email);
                    var actualChecksum = ComputeContentHash(reconstructed);
                    
                    Assert.Equal(expectedChecksum, actualChecksum);
                }
                _output.WriteLine($"  ✓ All {checksums.Count} checksums verified");
            }
        }
    }

    [Fact]
    public async Task TestEdgeCases()
    {
        _output.WriteLine("=== Testing edge cases ===");
        
        using (var db = new EmailDatabase(_testDbPath))
        {
            // Empty email
            var emptyEmail = new MimeMessage();
            emptyEmail.From.Add(new MailboxAddress("Empty", "empty@test.com"));
            emptyEmail.To.Add(new MailboxAddress("Nobody", "nobody@test.com"));
            var id1 = await db.ImportEMLAsync(emptyEmail.ToString());
            _output.WriteLine("✓ Imported empty email");
            
            // Very large email
            var largeEmail = GenerateSingleEmail("Large Email", 999);
            largeEmail.Body = new TextPart("plain") 
            { 
                Text = new string('X', 1_000_000) // 1MB of X's
            };
            var id2 = await db.ImportEMLAsync(largeEmail.ToString());
            _output.WriteLine("✓ Imported large email (1MB body)");
            
            // Unicode and special characters
            var unicodeEmail = GenerateSingleEmail("Unicode 测试 🚀 ñáéíóú", 1001);
            var id3 = await db.ImportEMLAsync(unicodeEmail.ToString());
            _output.WriteLine("✓ Imported unicode email");
        }
        
        // Verify all persist correctly
        using (var db = new EmailDatabase(_testDbPath))
        {
            var allIds = await db.GetAllEmailIDsAsync();
            Assert.Equal(3, allIds.Count);
            
            foreach (var id in allIds)
            {
                var email = await db.GetEmailAsync(id);
                Assert.NotNull(email);
            }
            _output.WriteLine("✓ All edge case emails persisted correctly");
        }
    }

    private List<(MimeMessage email, string expectedId)> GenerateSeededEmails(int count, int seed)
    {
        var emails = new List<(MimeMessage, string)>();
        var seededRandom = new Random(seed);
        
        for (int i = 0; i < count; i++)
        {
            var email = GenerateSingleEmail($"Seeded Email {i}", i, seededRandom);
            var expectedId = new EmailHashedID(email).ToString();
            emails.Add((email, expectedId));
        }
        
        return emails;
    }

    private MimeMessage GenerateSingleEmail(string subject, int index, Random? random = null)
    {
        random ??= _random;
        
        var message = new MimeMessage();
        message.MessageId = $"<{Guid.NewGuid()}@test.emaildb>";
        message.Date = DateTimeOffset.UtcNow.AddDays(-random.Next(0, 365));
        
        // Random from addresses
        var fromNames = new[] { "Alice", "Bob", "Charlie", "Diana", "Eve" };
        var fromDomains = new[] { "example.com", "test.org", "email.net" };
        message.From.Add(new MailboxAddress(
            fromNames[random.Next(fromNames.Length)],
            $"sender{index}@{fromDomains[random.Next(fromDomains.Length)]}"
        ));
        
        // Random to addresses
        var toCount = random.Next(1, 4);
        for (int i = 0; i < toCount; i++)
        {
            message.To.Add(new MailboxAddress(
                $"Recipient{i}",
                $"recipient{i}@{fromDomains[random.Next(fromDomains.Length)]}"
            ));
        }
        
        message.Subject = subject;
        
        // Random body content
        var bodyTypes = new[] { "meeting", "report", "update", "request", "notification" };
        var bodyType = bodyTypes[random.Next(bodyTypes.Length)];
        var priority = random.Next(100) < 20 ? "important" : "normal";
        
        var body = $@"This is a {bodyType} email with {priority} priority.

Generated at: {DateTime.UtcNow}
Index: {index}
Random value: {random.Next(10000)}

Lorem ipsum dolor sit amet, consectetur adipiscing elit. 
Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.

Best regards,
{message.From.First()}";

        message.Body = new TextPart("plain") { Text = body };
        
        return message;
    }

    private string ComputeContentHash(MimeMessage message)
    {
        // Create a canonical representation for hashing
        var canonical = new StringBuilder();
        canonical.AppendLine($"FROM:{message.From}");
        canonical.AppendLine($"TO:{message.To}");
        canonical.AppendLine($"SUBJECT:{message.Subject}");
        canonical.AppendLine($"DATE:{message.Date:O}");
        canonical.AppendLine($"BODY:{message.TextBody}");
        
        using (var sha256 = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }
    }

    private MimeMessage ReconstructMimeMessage(EmailContent content)
    {
        var message = new MimeMessage();
        message.MessageId = content.MessageId;
        message.Subject = content.Subject;
        
        if (!string.IsNullOrEmpty(content.From))
            message.From.Add(MailboxAddress.Parse(content.From));
            
        if (!string.IsNullOrEmpty(content.To))
        {
            foreach (var to in content.To.Split(';', StringSplitOptions.RemoveEmptyEntries))
                message.To.Add(MailboxAddress.Parse(to.Trim()));
        }
        
        message.Date = content.Date;
        message.Body = new TextPart("plain") { Text = content.TextBody };
        
        return message;
    }

    public void Dispose()
    {
        // Clean up test database
        try
        {
            if (Directory.Exists(_testDbPath))
            {
                Directory.Delete(_testDbPath, true);
            }
        }
        catch { }
    }
}

/// <summary>
/// Performance and stress tests for EmailDB persistence
/// </summary>
public class EmailDBStressTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;

    public EmailDBStressTest(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"emaildb_stress_{Guid.NewGuid()}");
    }

    [Theory]
    [InlineData(1000)]  // 1K emails
    [InlineData(5000)]  // 5K emails
    public async Task StressTestLargeDataset(int emailCount)
    {
        _output.WriteLine($"=== Stress Test: {emailCount} emails ===");
        
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(42);
        
        // Import phase
        _output.WriteLine($"\nImporting {emailCount} emails...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            for (int i = 0; i < emailCount; i++)
            {
                var email = GenerateEmail(i, random);
                await db.ImportEMLAsync(email.ToString());
                
                if ((i + 1) % 100 == 0)
                {
                    _output.WriteLine($"  Imported {i + 1}/{emailCount} emails...");
                }
            }
        }
        
        stopwatch.Stop();
        _output.WriteLine($"✓ Import completed in {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Rate: {emailCount / stopwatch.Elapsed.TotalSeconds:F2} emails/sec");
        
        // Verify persistence
        stopwatch.Restart();
        _output.WriteLine($"\nVerifying persistence...");
        using (var db = new EmailDatabase(_testDbPath))
        {
            var allIds = await db.GetAllEmailIDsAsync();
            Assert.Equal(emailCount, allIds.Count);
            
            // Sample verification (check 1% of emails)
            var sampleSize = Math.Max(10, emailCount / 100);
            var sampled = allIds.OrderBy(x => Guid.NewGuid()).Take(sampleSize).ToList();
            
            foreach (var id in sampled)
            {
                var email = await db.GetEmailAsync(id);
                Assert.NotNull(email);
                Assert.NotEmpty(email.Subject);
            }
        }
        
        stopwatch.Stop();
        _output.WriteLine($"✓ Verification completed in {stopwatch.ElapsedMilliseconds}ms");
        
        // Measure database size
        var dbSize = GetDirectorySize(_testDbPath);
        _output.WriteLine($"\nDatabase size: {dbSize / 1024.0 / 1024.0:F2} MB");
        _output.WriteLine($"Average per email: {dbSize / emailCount:F0} bytes");
    }

    private MimeMessage GenerateEmail(int index, Random random)
    {
        var message = new MimeMessage();
        message.MessageId = $"<stress-{index}-{Guid.NewGuid()}@test.emaildb>";
        message.From.Add(new MailboxAddress($"Sender{index}", $"sender{index}@stress.test"));
        message.To.Add(new MailboxAddress($"Recipient{index}", $"recipient{index}@stress.test"));
        message.Subject = $"Stress Test Email {index} - {random.Next(1000)}";
        message.Date = DateTimeOffset.UtcNow.AddSeconds(-random.Next(86400));
        
        var bodySize = 100 + random.Next(900); // 100-1000 chars
        message.Body = new TextPart("plain") 
        { 
            Text = $"Email {index}\n" + new string('X', bodySize) 
        };
        
        return message;
    }

    private long GetDirectorySize(string path)
    {
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDbPath))
            {
                Directory.Delete(_testDbPath, true);
            }
        }
        catch { }
    }
}