using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using EmailDB.Format;
using MimeKit;

namespace EmailDB.Console;

/// <summary>
/// Simple demonstration of EmailDB functionality using the actual EmailDatabase API
/// Shows how emails are stored with ZoneTree indexing and metadata management
/// </summary>
public class EmailDBSimpleDemo
{
    private readonly string _dbPath;
    private EmailDatabase? _emailDb;

    public EmailDBSimpleDemo(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task RunDemoAsync()
    {
        System.Console.WriteLine("EmailDB Demo - Using ZoneTree for Indexing");
        System.Console.WriteLine("==========================================\n");

        try
        {
            // Step 1: Initialize the database
            InitializeDatabase();

            // Step 2: Create and import sample emails
            await CreateAndImportSampleEmailsAsync();

            // Step 3: Demonstrate search functionality
            await DemonstrateSearchAsync();

            // Step 4: Show folder organization
            await DemonstrateFolderOrganizationAsync();

            // Step 5: Display database statistics
            await ShowDatabaseStatsAsync();

            System.Console.WriteLine("\nDemo completed successfully!");
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
        System.Console.WriteLine("1. Initializing EmailDB with ZoneTree indexing...");
        
        _emailDb = new EmailDatabase(_dbPath);
        
        System.Console.WriteLine($"   ✓ Database created at: {_dbPath}");
        System.Console.WriteLine("   ✓ ZoneTree indexes initialized:");
        System.Console.WriteLine("     - Email storage (KV store)");
        System.Console.WriteLine("     - Full-text search index");
        System.Console.WriteLine("     - Folder organization index");
        System.Console.WriteLine("     - Metadata store");
        System.Console.WriteLine();
    }

    private async Task CreateAndImportSampleEmailsAsync()
    {
        System.Console.WriteLine("2. Creating and importing sample emails...\n");

        // Sample email data
        var emails = new[]
        {
            new
            {
                MessageId = "msg001@example.com",
                From = "alice@example.com",
                To = "you@example.com",
                Subject = "Welcome to EmailDB!",
                Body = "This email demonstrates how EmailDB stores emails using ZoneTree for efficient indexing. " +
                       "The system automatically creates full-text search indexes for subject, from, to, and body fields.",
                Folder = "inbox"
            },
            new
            {
                MessageId = "msg002@example.com",
                From = "bob@company.com",
                To = "you@example.com",
                Subject = "Project Update - EmailDB Integration",
                Body = "The EmailDB integration is working perfectly! The ZoneTree indexing provides fast search " +
                       "capabilities while the block storage ensures efficient space usage.",
                Folder = "inbox"
            },
            new
            {
                MessageId = "msg003@example.com",
                From = "you@example.com",
                To = "charlie@example.com",
                Subject = "Re: Database Architecture Question",
                Body = "Yes, EmailDB uses a hybrid approach: RawBlockManager for storage and ZoneTree for indexing. " +
                       "This provides both efficient storage and fast search capabilities.",
                Folder = "sent"
            },
            new
            {
                MessageId = "msg004@example.com",
                From = "system@emaildb.com",
                To = "you@example.com",
                Subject = "EmailDB System Architecture Overview",
                Body = "EmailDB combines several technologies:\n" +
                       "- RawBlockManager: Low-level block storage with checksums\n" +
                       "- ZoneTree: LSM-tree based indexing for fast searches\n" +
                       "- Hash chains: Optional integrity verification\n" +
                       "- Metadata management: Track email properties and statistics",
                Folder = "important"
            },
            new
            {
                MessageId = "msg005@example.com",
                From = "newsletter@techweekly.com",
                To = "you@example.com",
                Subject = "This Week in Database Technology",
                Body = "Featured this week: How modern email systems handle large-scale storage. " +
                       "Learn about block storage, indexing strategies, and search optimization.",
                Folder = "newsletters"
            }
        };

        foreach (var emailData in emails)
        {
            // Create a MimeMessage (standard email format)
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

            // Convert to EML format
            using var stream = new MemoryStream();
            message.WriteTo(stream);
            var emlContent = Encoding.UTF8.GetString(stream.ToArray());

            // Import the email
            var emailId = await _emailDb!.ImportEMLAsync(emlContent, $"{emailData.MessageId}.eml");
            
            System.Console.WriteLine($"   ✓ Imported: {emailData.Subject}");
            System.Console.WriteLine($"     - Email ID: {emailId}");
            System.Console.WriteLine($"     - Message ID: {emailData.MessageId}");
            System.Console.WriteLine($"     - Stored in RawBlockManager as block");
            System.Console.WriteLine($"     - Indexed in ZoneTree for search");
            
            // Add to folder
            await _emailDb.AddToFolderAsync(emailId, emailData.Folder);
            System.Console.WriteLine($"     - Added to folder: {emailData.Folder}");
            
            // Update the email IDs index (simplified implementation)
            var metadataKey = "email_ids_index";
            var emailIds = new List<string>();
            if (_emailDb.GetType().GetField("_metadataStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_emailDb) is Tenray.ZoneTree.IZoneTree<string, string> metadataStore)
            {
                if (metadataStore.TryGet(metadataKey, out var existingJson))
                {
                    emailIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(existingJson) ?? new List<string>();
                }
                emailIds.Add(emailId.ToString());
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(emailIds);
                metadataStore.Upsert(metadataKey, updatedJson);
            }
            
            System.Console.WriteLine();
        }
    }

    private async Task DemonstrateSearchAsync()
    {
        System.Console.WriteLine("3. Demonstrating search capabilities (powered by ZoneTree)...\n");

        // Search example 1: Search for "EmailDB"
        System.Console.WriteLine("   Searching for 'EmailDB':");
        var results1 = await _emailDb!.SearchAsync("EmailDB");
        foreach (var result in results1)
        {
            System.Console.WriteLine($"   → Found: {result.Subject}");
            System.Console.WriteLine($"     - Relevance: {result.RelevanceScore:F1}");
            System.Console.WriteLine($"     - Matched in: {string.Join(", ", result.MatchedFields)}");
        }

        // Search example 2: Search for "storage"
        System.Console.WriteLine("\n   Searching for 'storage':");
        var results2 = await _emailDb!.SearchAsync("storage");
        foreach (var result in results2)
        {
            System.Console.WriteLine($"   → Found: {result.Subject}");
            System.Console.WriteLine($"     - From: {result.From}");
        }

        // Search example 3: Search for "ZoneTree"
        System.Console.WriteLine("\n   Searching for 'ZoneTree':");
        var results3 = await _emailDb!.SearchAsync("ZoneTree");
        foreach (var result in results3)
        {
            System.Console.WriteLine($"   → Found: {result.Subject}");
        }
        
        System.Console.WriteLine();
    }

    private async Task DemonstrateFolderOrganizationAsync()
    {
        System.Console.WriteLine("4. Demonstrating folder organization...\n");

        var allEmailIds = await _emailDb!.GetAllEmailIDsAsync();
        
        System.Console.WriteLine($"   Total emails in database: {allEmailIds.Count}");
        System.Console.WriteLine("\n   Email organization by folder:");
        
        // Group emails by folder (simplified demonstration)
        var folderGroups = new Dictionary<string, int>();
        
        foreach (var emailId in allEmailIds)
        {
            var folders = await _emailDb.GetEmailFoldersAsync(emailId);
            foreach (var folder in folders)
            {
                if (!folderGroups.ContainsKey(folder))
                    folderGroups[folder] = 0;
                folderGroups[folder]++;
            }
        }

        foreach (var (folder, count) in folderGroups.OrderBy(f => f.Key))
        {
            System.Console.WriteLine($"   - {folder}: {count} email(s)");
        }
        
        System.Console.WriteLine();
    }

    private async Task ShowDatabaseStatsAsync()
    {
        System.Console.WriteLine("5. Database Statistics...\n");

        var stats = await _emailDb!.GetDatabaseStatsAsync();
        
        System.Console.WriteLine($"   Storage Statistics:");
        System.Console.WriteLine($"   - Total Emails: {stats.TotalEmails}");
        System.Console.WriteLine($"   - Storage Blocks: {stats.StorageBlocks}");
        System.Console.WriteLine($"   - Search Indexes: {stats.SearchIndexes}");
        System.Console.WriteLine($"   - Total Folders: {stats.TotalFolders}");
        
        System.Console.WriteLine("\n   How the data is stored:");
        System.Console.WriteLine("   - Email content → RawBlockManager blocks");
        System.Console.WriteLine("   - Search indexes → ZoneTree LSM segments");
        System.Console.WriteLine("   - Folder mappings → ZoneTree KV pairs");
        System.Console.WriteLine("   - Metadata → ZoneTree KV pairs");
        
        System.Console.WriteLine("\n   ZoneTree Integration:");
        System.Console.WriteLine("   - Each ZoneTree segment is stored as a block in RawBlockManager");
        System.Console.WriteLine("   - Block type: ZoneTreeSegment_KV");
        System.Console.WriteLine("   - Segments are compressed and deduplicated");
        System.Console.WriteLine("   - RandomAccessDevice provides the storage abstraction");
        
        System.Console.WriteLine();
    }
}