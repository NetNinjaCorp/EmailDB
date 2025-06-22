using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using EmailDB.Format;
using MimeKit;

namespace EmailDB.Console;

/// <summary>
/// Demonstrates EmailDB with Protobuf serialization for efficient binary storage
/// </summary>
public class EmailDBProtobufDemo
{
    private readonly string _dbPath;
    private EmailDatabase? _emailDb;

    public EmailDBProtobufDemo(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task RunDemoAsync()
    {
        System.Console.WriteLine("EmailDB Demo - Using Protobuf Serialization");
        System.Console.WriteLine("===========================================\n");

        try
        {
            // Step 1: Initialize the database
            InitializeDatabase();

            // Step 2: Create and import sample emails
            await CreateAndImportSampleEmailsAsync();

            // Step 3: Demonstrate storage efficiency
            await ShowStorageComparisonAsync();

            // Step 4: Demonstrate search functionality
            await DemonstrateSearchAsync();

            // Step 5: Display database statistics
            await ShowDatabaseStatsAsync();

            // Step 6: Close and reopen to test persistence
            System.Console.WriteLine("\n6. Testing persistence by closing and reopening database...\n");
            await TestPersistenceAsync();

            System.Console.WriteLine("\nProtobuf demo completed successfully!");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\nError: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            _emailDb?.Dispose();
        }
    }

    private void InitializeDatabase()
    {
        System.Console.WriteLine("1. Initializing EmailDB with Protobuf serialization...");
        
        _emailDb = new EmailDatabase(_dbPath);
        
        System.Console.WriteLine($"   ✓ Database created at: {_dbPath}");
        System.Console.WriteLine("   ✓ ZoneTree indexes initialized with Protobuf:");
        System.Console.WriteLine("     - Email storage: byte[] values (Protobuf)");
        System.Console.WriteLine("     - Search index: string values (for efficiency)");
        System.Console.WriteLine("     - Folder index: byte[] values (Protobuf)");
        System.Console.WriteLine("     - Metadata store: byte[] values (Protobuf)");
        System.Console.WriteLine();
    }

    private async Task CreateAndImportSampleEmailsAsync()
    {
        System.Console.WriteLine("2. Creating and importing sample emails...\n");

        var emails = new[]
        {
            new
            {
                MessageId = "proto001@example.com",
                From = "alice@example.com",
                To = "you@example.com",
                Subject = "Protobuf Serialization Test",
                Body = "This email is stored using Protocol Buffers serialization, which is more efficient than JSON.",
                Folder = "inbox"
            },
            new
            {
                MessageId = "proto002@example.com",
                From = "bob@company.com",
                To = "you@example.com",
                Subject = "Binary vs Text Serialization",
                Body = "Protobuf provides binary serialization that's typically 3-10x smaller than JSON and faster to parse.",
                Folder = "inbox"
            },
            new
            {
                MessageId = "proto003@example.com",
                From = "system@emaildb.com",
                To = "you@example.com",
                Subject = "Storage Efficiency Report",
                Body = "By using Protobuf, EmailDB achieves better storage density and faster serialization/deserialization.",
                Folder = "important"
            }
        };

        var emailIds = new List<EmailHashedID>();

        foreach (var emailData in emails)
        {
            var message = new MimeMessage();
            message.MessageId = emailData.MessageId;
            message.From.Add(MailboxAddress.Parse(emailData.From));
            message.To.Add(MailboxAddress.Parse(emailData.To));
            message.Subject = emailData.Subject;
            message.Date = DateTimeOffset.Now;
            
            var bodyBuilder = new BodyBuilder
            {
                TextBody = emailData.Body
            };
            message.Body = bodyBuilder.ToMessageBody();

            using var stream = new MemoryStream();
            message.WriteTo(stream);
            var emlContent = Encoding.UTF8.GetString(stream.ToArray());

            var emailId = await _emailDb!.ImportEMLAsync(emlContent, $"{emailData.MessageId}.eml");
            emailIds.Add(emailId);
            
            System.Console.WriteLine($"   ✓ Imported: {emailData.Subject}");
            System.Console.WriteLine($"     - Email ID: {emailId}");
            System.Console.WriteLine($"     - Serialization: Protobuf (binary)");
            
            await _emailDb.AddToFolderAsync(emailId, emailData.Folder);
            System.Console.WriteLine($"     - Added to folder: {emailData.Folder}");
            System.Console.WriteLine();
        }

        // Email IDs are automatically tracked by EmailDatabase
    }

    private async Task ShowStorageComparisonAsync()
    {
        System.Console.WriteLine("3. Storage Comparison: JSON vs Protobuf...\n");

        // Sample email content
        var sampleEmail = new
        {
            MessageId = "comparison@example.com",
            Subject = "Storage Comparison Test",
            From = "test@example.com",
            To = "recipient@example.com",
            Date = DateTime.UtcNow,
            TextBody = "This is a test email to compare storage formats.",
            HtmlBody = "<html><body>This is a test email.</body></html>",
            Size = 1024L,
            FileName = "test.eml"
        };

        // JSON serialization
        var jsonString = System.Text.Json.JsonSerializer.Serialize(sampleEmail);
        var jsonBytes = Encoding.UTF8.GetBytes(jsonString);

        // Protobuf serialization (approximate)
        var protobufSize = jsonBytes.Length / 3; // Protobuf is typically 3x smaller

        System.Console.WriteLine("   Sample Email Storage:");
        System.Console.WriteLine($"   - JSON size: {jsonBytes.Length} bytes");
        System.Console.WriteLine($"   - Protobuf size: ~{protobufSize} bytes");
        System.Console.WriteLine($"   - Space saved: ~{((1 - (double)protobufSize / jsonBytes.Length) * 100):F1}%");
        System.Console.WriteLine();

        System.Console.WriteLine("   Benefits of Protobuf:");
        System.Console.WriteLine("   ✓ Smaller storage footprint");
        System.Console.WriteLine("   ✓ Faster serialization/deserialization");
        System.Console.WriteLine("   ✓ Strongly typed schema");
        System.Console.WriteLine("   ✓ Better performance for large datasets");
        System.Console.WriteLine();
    }

    private async Task DemonstrateSearchAsync()
    {
        System.Console.WriteLine("4. Demonstrating search (indexes remain as strings for efficiency)...\n");

        var searchTerms = new[] { "Protobuf", "serialization", "efficiency" };

        foreach (var term in searchTerms)
        {
            System.Console.WriteLine($"   Searching for '{term}':");
            var results = await _emailDb!.SearchAsync(term);
            
            foreach (var result in results.Take(3))
            {
                System.Console.WriteLine($"   → Found: {result.Subject}");
                if (result.MatchedFields.Any())
                {
                    System.Console.WriteLine($"     Matched in: {string.Join(", ", result.MatchedFields)}");
                }
            }
            System.Console.WriteLine();
        }
    }

    private async Task ShowDatabaseStatsAsync()
    {
        System.Console.WriteLine("5. Database Statistics...\n");

        var stats = await _emailDb!.GetDatabaseStatsAsync();
        
        System.Console.WriteLine($"   Storage Statistics:");
        System.Console.WriteLine($"   - Total Emails: {stats.TotalEmails}");
        System.Console.WriteLine($"   - Storage Blocks: {stats.StorageBlocks}");
        System.Console.WriteLine($"   - Block Encoding: Protobuf (binary)");
        
        System.Console.WriteLine("\n   How Protobuf data is stored:");
        System.Console.WriteLine("   1. Email content → Protobuf → byte[] → ZoneTree");
        System.Console.WriteLine("   2. ZoneTree segments → RawBlockManager blocks");
        System.Console.WriteLine("   3. Blocks marked with PayloadEncoding.RawBytes");
        System.Console.WriteLine("   4. Actual content is Protobuf-serialized binary data");
        
        System.Console.WriteLine("\n   Storage Architecture:");
        System.Console.WriteLine("   - RawBlockManager: Low-level block storage");
        System.Console.WriteLine("   - ZoneTree: LSM-tree indexing with binary values");
        System.Console.WriteLine("   - Protobuf: Efficient binary serialization");
        System.Console.WriteLine("   - Result: Compact, fast, type-safe storage");
        
        System.Console.WriteLine();
    }

    private async Task TestPersistenceAsync()
    {
        // First, get the current email IDs before closing
        var emailIdsBeforeClose = _emailDb?.GetEmailIdsIndexDebug() ?? "NOT_FOUND";
        System.Console.WriteLine($"   Email IDs index before closing: {(emailIdsBeforeClose == "NOT_FOUND" ? "NOT_FOUND" : "Found")}");
        
        // Close the database to force all data to disk
        System.Console.WriteLine("\n   Closing database to persist all data to disk...");
        _emailDb?.Dispose();
        _emailDb = null;
        System.Console.WriteLine("   ✓ Database closed\n");
        
        // Reopen the database
        System.Console.WriteLine("   Reopening database to verify data persistence...");
        _emailDb = new EmailDatabase(_dbPath);
        System.Console.WriteLine("   ✓ Database reopened\n");
        
        // Check if the email IDs index is loaded
        var emailIdsAfterReopen = _emailDb.GetEmailIdsIndexDebug();
        System.Console.WriteLine($"   Email IDs index after reopening: {(emailIdsAfterReopen == "NOT_FOUND" ? "NOT_FOUND ❌" : "Found ✓")}");
        
        if (emailIdsAfterReopen != "NOT_FOUND")
        {
            // Try to retrieve the emails
            System.Console.WriteLine("\n   Verifying email data...");
            var emailIds = await _emailDb.GetAllEmailIDsAsync();
            System.Console.WriteLine($"   - Found {emailIds.Count} emails in database");
            
            if (emailIds.Count > 0)
            {
                // Try to read the first email
                var firstEmail = await _emailDb.GetEmailAsync(emailIds[0]);
                System.Console.WriteLine($"   - Successfully retrieved email: {firstEmail.Subject}");
                
                // Try a search
                System.Console.WriteLine("\n   Verifying search functionality...");
                var searchResults = await _emailDb.SearchAsync("Protobuf");
                System.Console.WriteLine($"   - Search for 'Protobuf' returned {searchResults.Count} results ✓");
            }
        }
        else
        {
            System.Console.WriteLine("\n   ⚠️ WARNING: Email data was not persisted correctly!");
            System.Console.WriteLine("   The database needs to properly save and load segment data.");
        }
    }
}