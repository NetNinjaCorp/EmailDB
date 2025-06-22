using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.ZoneTree;
using Tenray.ZoneTree;

namespace EmailDB.Console;

public class MetadataStoreTest
{
    public static async Task RunAsync()
    {
        System.Console.WriteLine("Metadata Store Test");
        System.Console.WriteLine("===================\n");

        var dbPath = Path.Combine(Path.GetTempPath(), $"metadata_test_{Guid.NewGuid():N}");
        
        try
        {
            Directory.CreateDirectory(dbPath);
            var blockFile = Path.Combine(dbPath, "blocks.db");
            
            // Test 1: Basic metadata persistence
            System.Console.WriteLine("Test 1: Basic ZoneTree persistence");
            System.Console.WriteLine("-----------------------------------");
            
            using (var blockManager = new RawBlockManager(blockFile))
            {
                IZoneTree<string, string>? metadataStore = null;
                
                // Create and populate
                {
                    var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
                    metadataStore = factory.OpenOrCreateDirect("test_metadata");
                    
                    metadataStore.Upsert("key1", "value1");
                    metadataStore.Upsert("key2", "value2");
                    metadataStore.Upsert("key3", "value3");
                    
                    // Force save
                    metadataStore.Maintenance.SaveMetaData();
                    
                    System.Console.WriteLine("✓ Added 3 key-value pairs");
                    
                    // Verify they exist
                    if (metadataStore.TryGet("key1", out var val1))
                        System.Console.WriteLine($"✓ key1 = {val1}");
                    else
                        System.Console.WriteLine("❌ key1 not found!");
                        
                    metadataStore.Dispose();
                }
                
                // Wait a bit
                await Task.Delay(100);
                
                // Reopen and verify
                {
                    var factory = new EmailDBZoneTreeFactory<string, string>(blockManager);
                    metadataStore = factory.OpenOrCreateDirect("test_metadata");
                    
                    System.Console.WriteLine("\nAfter reopening:");
                    
                    int found = 0;
                    for (int i = 1; i <= 3; i++)
                    {
                        if (metadataStore.TryGet($"key{i}", out var value))
                        {
                            System.Console.WriteLine($"✓ key{i} = {value}");
                            found++;
                        }
                        else
                        {
                            System.Console.WriteLine($"❌ key{i} not found!");
                        }
                    }
                    
                    if (found == 3)
                        System.Console.WriteLine("\n✅ SUCCESS: All metadata persisted correctly!");
                    else
                        System.Console.WriteLine($"\n❌ FAILURE: Only {found} of 3 keys found!");
                        
                    metadataStore.Dispose();
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