using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format;
using MimeKit;

namespace EmailDB.Console;

public class ZoneTreePersistenceTest
{
    public static async Task RunAsync()
    {
        System.Console.WriteLine("ZoneTree Persistence Test");
        System.Console.WriteLine("=========================\n");

        var dbPath = Path.Combine(Path.GetTempPath(), $"zonetree_test_{Guid.NewGuid():N}");
        
        try
        {
            // Phase 1: Create and populate
            System.Console.WriteLine("Phase 1: Creating database and adding emails");
            System.Console.WriteLine("-------------------------------------------");
            
            using (var db = new EmailDatabase(dbPath))
            {
                // Add test emails
                for (int i = 1; i <= 3; i++)
                {
                    var message = new MimeMessage();
                    message.MessageId = $"test{i:D3}@example.com";
                    message.From.Add(new MailboxAddress($"User {i}", $"user{i}@example.com"));
                    message.To.Add(MailboxAddress.Parse("recipient@example.com"));
                    message.Subject = $"Test Email {i}";
                    message.Date = DateTimeOffset.Now;
                    
                    var bodyBuilder = new BodyBuilder { TextBody = $"This is test email number {i}." };
                    message.Body = bodyBuilder.ToMessageBody();

                    using var stream = new MemoryStream();
                    message.WriteTo(stream);
                    var emlContent = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                    var emailId = await db.ImportEMLAsync(emlContent, $"test{i}.eml");
                    System.Console.WriteLine($"  ✓ Added email {i}: ID={emailId}");
                    
                    await db.AddToFolderAsync(emailId, "inbox");
                }
                
                // Force metadata save on all stores
                var fields = new[] { "_emailStore", "_searchIndex", "_folderIndex", "_metadataStore" };
                foreach (var fieldName in fields)
                {
                    var field = db.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    dynamic? store = field?.GetValue(db);
                    if (store != null)
                    {
                        store.Maintenance.SaveMetaData();
                        System.Console.WriteLine($"  ✓ Saved metadata for {fieldName}");
                    }
                }
            }
            
            System.Console.WriteLine($"\nDatabase closed. Path: {dbPath}");
            
            // Wait to ensure all writes complete
            await Task.Delay(500);
            
            // Phase 2: Reopen and verify
            System.Console.WriteLine("\nPhase 2: Reopening database");
            System.Console.WriteLine("---------------------------");
            
            using (var db = new EmailDatabase(dbPath))
            {
                var allIds = await db.GetAllEmailIDsAsync();
                System.Console.WriteLine($"  Found {allIds.Count} emails");
                
                if (allIds.Count == 0)
                {
                    System.Console.WriteLine("\n❌ FAILURE: No emails found after reopen!");
                    
                    // Debug: Check what's in the raw block storage
                    var blockManagerField = db.GetType().GetField("_blockManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (blockManagerField?.GetValue(db) is EmailDB.Format.FileManagement.RawBlockManager blockManager)
                    {
                        var locations = blockManager.GetBlockLocations();
                        System.Console.WriteLine($"\n  Debug: {locations.Count} blocks in storage");
                        foreach (var loc in locations)
                        {
                            System.Console.WriteLine($"    Block {loc.Key}: Type={loc.Value}");
                        }
                    }
                }
                else
                {
                    System.Console.WriteLine("\n✅ SUCCESS: Emails persisted correctly!");
                    
                    foreach (var id in allIds)
                    {
                        var email = await db.GetEmailAsync(id);
                        System.Console.WriteLine($"  • {email.Subject} (from {email.From})");
                    }
                    
                    // Test search
                    var searchResults = await db.SearchAsync("test");
                    System.Console.WriteLine($"\n  Search for 'test': {searchResults.Count} results");
                }
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n❌ Error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(dbPath))
            {
                try { Directory.Delete(dbPath, true); } catch { }
            }
        }
    }
}