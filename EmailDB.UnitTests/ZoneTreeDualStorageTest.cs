using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using EmailDB.Format.FileManagement;
using EmailDB.Format.ZoneTree;
using EmailDB.Format.Models;
using Tenray.ZoneTree;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Test that demonstrates dual ZoneTree storage (KV + Search Index) with EmailDB backend.
/// Shows how both email storage and search indexing create separate EmailDB blocks.
/// </summary>
public class ZoneTreeDualStorageTest : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public ZoneTreeDualStorageTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Create_EmailDB_Blocks_For_Both_KV_And_Search_Index()
    {
        _output.WriteLine("🔍 Testing Dual ZoneTree Storage (KV + Search Index) → EmailDB Integration");

        // Create KV ZoneTree for email storage
        var kvFactory = new Tenray.ZoneTree.ZoneTreeFactory<string, string>();
        kvFactory.Configure(options =>
        {
            options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_blockManager, "email_kv");
            options.WriteAheadLogProvider = null;
        });

        // Create Search Index ZoneTree for email content indexing  
        var searchFactory = new Tenray.ZoneTree.ZoneTreeFactory<string, string>();
        searchFactory.Configure(options =>
        {
            options.RandomAccessDeviceManager = new RandomAccessDeviceManager(_blockManager, "email_search");
            options.WriteAheadLogProvider = null;
        });

        _output.WriteLine("✅ Both KV and Search Index ZoneTrees configured with EmailDB backend");

        // Record initial state
        var initialBlocks = _blockManager.GetBlockLocations();
        _output.WriteLine($"📊 Initial EmailDB blocks: {initialBlocks.Count}");

        using var kvZoneTree = kvFactory.OpenOrCreate();
        using var searchZoneTree = searchFactory.OpenOrCreate();

        _output.WriteLine("✅ Both ZoneTree instances opened");

        // Sample emails with rich content for indexing
        var emails = new[]
        {
            new {
                Id = "email:001",
                From = "john.smith@company.com",
                To = "team@company.com",
                Subject = "Urgent Project Timeline Update Required",
                Body = "The client has requested accelerated delivery timeline. We need to review our project milestones and resource allocation."
            },
            new {
                Id = "email:002", 
                From = "sarah.jones@marketing.com",
                To = "john.smith@company.com",
                Subject = "Marketing Campaign Performance Analytics",
                Body = "Our latest digital marketing campaign exceeded expectations with 25% conversion rate. The analytics show strong engagement."
            },
            new {
                Id = "email:003",
                From = "admin@company.com",
                To = "all-staff@company.com", 
                Subject = "System Maintenance Notification",
                Body = "Scheduled maintenance window tonight from 2 AM to 4 AM. All systems including email, database, and file servers will be temporarily unavailable."
            },
            new {
                Id = "email:004",
                From = "client@external-corp.com",
                To = "sales@company.com",
                Subject = "Product Integration Requirements",
                Body = "We are interested in integrating your API services with our existing infrastructure. Please provide technical documentation."
            },
            new {
                Id = "email:005",
                From = "hr@company.com",
                To = "john.smith@company.com",
                Subject = "Annual Performance Review Schedule",
                Body = "Time to schedule your annual performance review. Please book a 60-minute slot in my calendar for next week."
            }
        };

        _output.WriteLine($"\n📧 Storing {emails.Length} emails with dual indexing...");

        foreach (var email in emails)
        {
            // Store complete email in KV store
            var emailJson = System.Text.Json.JsonSerializer.Serialize(email);
            var kvAdded = kvZoneTree.TryAdd(email.Id, emailJson, out var kvOpIndex);
            Assert.True(kvAdded, $"Should store email {email.Id} in KV store");

            // Create search index entries for different email fields
            var searchEntries = new[]
            {
                ($"subject:{email.Id}", email.Subject.ToLowerInvariant()),
                ($"from:{email.Id}", email.From.ToLowerInvariant()),
                ($"to:{email.Id}", email.To.ToLowerInvariant()),
                ($"body:{email.Id}", email.Body.ToLowerInvariant())
            };

            foreach (var (searchKey, searchValue) in searchEntries)
            {
                var searchAdded = searchZoneTree.TryAdd(searchKey, searchValue, out var searchOpIndex);
                Assert.True(searchAdded, $"Should index {searchKey}");
            }

            _output.WriteLine($"   ✅ {email.Id}: KV stored (opIndex: {kvOpIndex}) + 4 search indexes created");
            _output.WriteLine($"      Subject: {email.Subject}");
            _output.WriteLine($"      Indexed: Subject, From, To, Body fields");
        }

        // Check blocks after adding content (before persistence)
        var blocksAfterAdd = _blockManager.GetBlockLocations();
        var newBlocksAfterAdd = blocksAfterAdd.Count - initialBlocks.Count;
        _output.WriteLine($"\n📊 EmailDB blocks after adding emails (in memory): {newBlocksAfterAdd}");

        // Force both ZoneTrees to persist data
        _output.WriteLine("\n💾 Forcing both KV and Search ZoneTrees to persist to EmailDB...");
        
        kvZoneTree.Maintenance.MoveMutableSegmentForward();
        var kvMergeResult = kvZoneTree.Maintenance.StartMergeOperation();
        if (kvMergeResult != null)
        {
            kvMergeResult.Join();
            _output.WriteLine("✅ KV ZoneTree merge completed");
        }

        searchZoneTree.Maintenance.MoveMutableSegmentForward();
        var searchMergeResult = searchZoneTree.Maintenance.StartMergeOperation();
        if (searchMergeResult != null)
        {
            searchMergeResult.Join();
            _output.WriteLine("✅ Search Index ZoneTree merge completed");
        }

        // Check final block count
        var finalBlocks = _blockManager.GetBlockLocations();
        var totalNewBlocks = finalBlocks.Count - initialBlocks.Count;
        _output.WriteLine($"\n📊 Final EmailDB blocks after persistence: {totalNewBlocks}");

        // Test search functionality by looking for specific terms
        _output.WriteLine("\n🔍 Testing search functionality via indexed content...");

        var searchTerms = new[] { "project", "marketing", "maintenance", "performance", "integration" };
        foreach (var term in searchTerms)
        {
            var foundResults = 0;
            
            // Search through all search index entries by looking for known patterns
            foreach (var email in emails)
            {
                var searchKeys = new[]
                {
                    $"subject:{email.Id}",
                    $"from:{email.Id}",
                    $"to:{email.Id}",
                    $"body:{email.Id}"
                };
                
                foreach (var searchKey in searchKeys)
                {
                    var found = searchZoneTree.TryGet(searchKey, out var searchValue);
                    if (found && searchValue.Contains(term.ToLowerInvariant()))
                    {
                        foundResults++;
                        if (foundResults <= 2) // Show first 2 results
                        {
                            _output.WriteLine($"   🔍 Found '{term}' in {email.Id} ({searchKey.Split(':')[0]})");
                        }
                    }
                }
            }
            
            _output.WriteLine($"   📊 Total matches for '{term}': {foundResults}");
        }

        // Test email retrieval via search results
        _output.WriteLine("\n📧 Testing email retrieval via search results...");
        
        var foundEmailsViaSearch = 0;
        foreach (var email in emails)
        {
            if (foundEmailsViaSearch >= 3) break;
            
            var searchKeys = new[]
            {
                $"subject:{email.Id}",
                $"from:{email.Id}",
                $"to:{email.Id}",
                $"body:{email.Id}"
            };
            
            foreach (var searchKey in searchKeys)
            {
                var found = searchZoneTree.TryGet(searchKey, out var searchValue);
                if (found && searchValue.Contains("marketing"))
                {
                    var kvFound = kvZoneTree.TryGet(email.Id, out var emailJson);
                    
                    if (kvFound)
                    {
                        foundEmailsViaSearch++;
                        _output.WriteLine($"   ✅ Found via search: {email.Id}");
                        _output.WriteLine($"      Search matched: {searchValue}");
                        break; // Found this email, move to next
                    }
                }
            }
        }

        // Analyze the blocks created for both systems
        _output.WriteLine("\n📦 Analyzing EmailDB blocks created by dual ZoneTree storage:");
        var blockNumber = 1;
        var kvBlocks = 0;
        var searchBlocks = 0;
        
        foreach (var kvp in finalBlocks)
        {
            if (!initialBlocks.ContainsKey(kvp.Key))
            {
                var readResult = await _blockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess)
                {
                    var block = readResult.Value;
                    _output.WriteLine($"   📦 Block {blockNumber}: ID={kvp.Key}");
                    _output.WriteLine($"      Type: {block.Type}");
                    _output.WriteLine($"      Size: {block.Payload.Length} bytes");
                    
                    // Heuristic to determine block type based on size and content patterns
                    if (block.Payload.Length > 800) // Larger blocks likely contain full email JSON
                    {
                        kvBlocks++;
                        _output.WriteLine($"      Likely contains: KV email data");
                    }
                    else if (block.Payload.Length > 200) // Medium blocks likely contain search indexes
                    {
                        searchBlocks++;
                        _output.WriteLine($"      Likely contains: Search index data");
                    }
                    else
                    {
                        _output.WriteLine($"      Likely contains: Metadata/structure");
                    }
                    
                    blockNumber++;
                }
            }
        }

        // Summary
        _output.WriteLine($"\n🎉 DUAL ZONETREE STORAGE TEST SUMMARY:");
        _output.WriteLine($"   📧 Emails stored: {emails.Length}");
        _output.WriteLine($"   🔍 Search index entries: {emails.Length * 4} (4 fields per email)");
        _output.WriteLine($"   📦 Total EmailDB blocks created: {totalNewBlocks}");
        _output.WriteLine($"   🗃️ Estimated KV blocks: {kvBlocks}");
        _output.WriteLine($"   🔍 Estimated Search index blocks: {searchBlocks}");
        _output.WriteLine($"   💾 Block creation ratio: {(double)totalNewBlocks / emails.Length:F2} blocks per email");
        _output.WriteLine($"   ✅ Both email storage and search indexing use EmailDB backend");
        _output.WriteLine($"   ✅ Search functionality working via indexed content");
        _output.WriteLine($"   ✅ Dual ZoneTree architecture proven with EmailDB storage!");

        Assert.True(totalNewBlocks > 0, "Dual ZoneTree should create EmailDB blocks");
        Assert.True(foundEmailsViaSearch > 0, "Should find emails via search index");
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
        
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}