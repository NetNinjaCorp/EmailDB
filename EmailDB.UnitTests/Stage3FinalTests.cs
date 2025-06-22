using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.Caching;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Indexing;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using MimeKit;
using MimeKit.Text;

namespace EmailDB.UnitTests;

/// <summary>
/// Stage 3 tests for Caching and Index Management components
/// </summary>
[TestCategory("Stage3")]
public class Stage3FinalTests : IDisposable
{
    private readonly string _testDirectory;
    
    public Stage3FinalTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"EmailDB_Stage3_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    #region LRU Cache Tests

    [Fact]
    public void Stage3_LRUCache_BasicOperations()
    {
        // Test basic LRU cache functionality
        var cache = new LRUCache<string, string>(3);

        // Add items
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        // Verify all items present
        Assert.True(cache.TryGet("a", out var val));
        Assert.Equal("1", val);
        Assert.Equal(3, cache.Count);

        // Add fourth item - should evict least recently used
        cache.Set("d", "4");
        Assert.False(cache.TryGet("b", out _)); // 'b' should be evicted
        Assert.True(cache.TryGet("a", out _));  // 'a' still there (was accessed)
    }

    [Fact]
    public void Stage3_LRUCache_ThreadSafety()
    {
        var cache = new LRUCache<int, int>(100);
        var tasks = new List<Task>();

        // Run concurrent operations
        for (int t = 0; t < 10; t++)
        {
            int thread = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    cache.Set(thread * 100 + i, i);
                    cache.TryGet(thread * 100 + i - 1, out _);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());
        Assert.Equal(100, cache.Count);
    }

    #endregion

    #region CacheManager Tests

    [Fact]
    public async Task Stage3_CacheManager_Initialization()
    {
        // Test CacheManager initialization
        var dbPath = Path.Combine(_testDirectory, "cache_init.edb");
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        using var cacheManager = new CacheManager(rawManager, serializer);

        var result = await cacheManager.InitializeNewFile();
        Assert.True(result.IsSuccess);

        // Verify header is loaded
        var header = await cacheManager.LoadHeaderContent();
        Assert.NotNull(header);
        Assert.Equal(1, header.FileVersion);
    }

    [Fact]
    public async Task Stage3_CacheManager_FolderOperations()
    {
        // Test folder caching
        var dbPath = Path.Combine(_testDirectory, "cache_folders.edb");
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        using var cacheManager = new CacheManager(rawManager, serializer);

        await cacheManager.InitializeNewFile();

        // Use reflection to access internal UpdateFolder method
        var updateFolderMethod = typeof(CacheManager).GetMethod("UpdateFolder", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        var folder = new FolderContent
        {
            Name = "TestFolder",
            FolderId = 1,
            ParentFolderId = 0,
            EmailIds = new List<EmailHashedID>()
        };

        if (updateFolderMethod != null)
        {
            var task = (Task<long>)updateFolderMethod.Invoke(cacheManager, new object[] { "TestFolder", folder });
            var offset = await task;
            
            // Update folder tree
            var folderTree = new FolderTreeContent
            {
                RootFolderId = 1,
                FolderHierarchy = new Dictionary<string, string> { { "TestFolder", "" } },
                FolderIDs = new Dictionary<string, long> { { "TestFolder", 1 } },
                FolderOffsets = new Dictionary<long, long> { { 1, offset } }
            };
            
            var updateTreeMethod = typeof(CacheManager).GetMethod("UpdateFolderTree",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (updateTreeMethod != null)
            {
                await (Task<long>)updateTreeMethod.Invoke(cacheManager, new object[] { folderTree });
            }
        }

        // Verify folder can be retrieved from cache
        var cached = await cacheManager.GetCachedFolder("TestFolder");
        Assert.NotNull(cached);
        Assert.Equal("TestFolder", cached.Name);
    }

    [Fact]
    public async Task Stage3_CacheManager_HeaderCaching()
    {
        // Test header caching
        var dbPath = Path.Combine(_testDirectory, "cache_header.edb");
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        using var cacheManager = new CacheManager(rawManager, serializer);

        await cacheManager.InitializeNewFile();

        // Load header multiple times
        var header1 = await cacheManager.LoadHeaderContent();
        var header2 = await cacheManager.LoadHeaderContent();

        // Should get the same cached instance
        Assert.Same(header1, header2);
    }

    [Fact]
    public async Task Stage3_CacheManager_CacheInvalidation()
    {
        // Test cache invalidation
        var dbPath = Path.Combine(_testDirectory, "cache_invalidate.edb");
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        using var cacheManager = new CacheManager(rawManager, serializer);

        await cacheManager.InitializeNewFile();

        var header1 = await cacheManager.LoadHeaderContent();
        cacheManager.InvalidateCache();
        var header2 = await cacheManager.LoadHeaderContent();

        // Should be different instances after invalidation
        Assert.NotSame(header1, header2);
    }

    #endregion

    #region IndexManager Tests

    [Fact]
    public async Task Stage3_IndexManager_BasicIndexing()
    {
        // Test basic email indexing
        var indexPath = Path.Combine(_testDirectory, "indexes_basic");
        using var indexManager = new IndexManager(indexPath);

        var emailId = new EmailHashedID
        {
            BlockId = 1,
            LocalId = 0,
            EnvelopeHash = new byte[32],
            ContentHash = new byte[32]
        };

        var message = new MimeMessage();
        message.MessageId = "test@example.com";
        message.Subject = "Test Email";
        message.From.Add(new MailboxAddress("Test", "test@test.com"));
        message.To.Add(new MailboxAddress("User", "user@test.com"));
        message.Body = new TextPart(TextFormat.Plain) { Text = "Test body" };

        var result = await indexManager.IndexEmailAsync(emailId, message, "Inbox", 100);
        Assert.True(result.IsSuccess);

        // Verify lookup
        var lookup = indexManager.GetEmailByMessageId("test@example.com");
        Assert.True(lookup.IsSuccess);
        Assert.Equal("1:0", lookup.Value);
    }

    [Fact]
    public async Task Stage3_IndexManager_SearchFunctionality()
    {
        // Test search term indexing
        var indexPath = Path.Combine(_testDirectory, "indexes_search");
        using var indexManager = new IndexManager(indexPath);

        // Index emails with different subjects
        for (int i = 0; i < 5; i++)
        {
            var emailId = new EmailHashedID
            {
                BlockId = 1,
                LocalId = i,
                EnvelopeHash = new byte[32],
                ContentHash = new byte[32]
            };

            var message = new MimeMessage();
            message.MessageId = $"email{i}@test.com";
            message.Subject = i % 2 == 0 ? "Important Meeting" : "Status Update";
            message.From.Add(new MailboxAddress("Sender", "sender@test.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@test.com"));
            message.Body = new TextPart(TextFormat.Plain) { Text = "Email content" };

            await indexManager.IndexEmailAsync(emailId, message, "Inbox", i * 100);
        }

        // Search for "important"
        var results = indexManager.GetEmailsBySearchTerm("important");
        Assert.True(results.IsSuccess);
        Assert.Equal(3, results.Value.Count); // Emails 0, 2, 4
    }

    [Fact]
    public async Task Stage3_IndexManager_FolderIndexing()
    {
        // Test folder path indexing
        var indexPath = Path.Combine(_testDirectory, "indexes_folders");
        using var indexManager = new IndexManager(indexPath);

        // Update folder indexes
        var folders = new[] { "Inbox", "Sent", "Drafts" };
        for (int i = 0; i < folders.Length; i++)
        {
            var result = await indexManager.UpdateFolderIndexAsync(folders[i], i * 1000);
            Assert.True(result.IsSuccess);
        }

        // Index emails in different folders
        for (int i = 0; i < 6; i++)
        {
            var emailId = new EmailHashedID
            {
                BlockId = 1,
                LocalId = i,
                EnvelopeHash = new byte[32],
                ContentHash = new byte[32]
            };

            var message = new MimeMessage();
            message.MessageId = $"folder_test_{i}@test.com";
            message.Subject = $"Test {i}";
            message.From.Add(new MailboxAddress("Sender", "sender@test.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@test.com"));
            message.Body = new TextPart(TextFormat.Plain) { Text = "Content" };

            var folder = folders[i % 3];
            await indexManager.IndexEmailAsync(emailId, message, folder, i * 100);
        }

        // All emails should be indexed
        for (int i = 0; i < 6; i++)
        {
            var result = indexManager.GetEmailByMessageId($"folder_test_{i}@test.com");
            Assert.True(result.IsSuccess);
        }
    }

    [Fact]
    public async Task Stage3_IndexManager_EnvelopeHashLookup()
    {
        // Test envelope hash lookup functionality
        var indexPath = Path.Combine(_testDirectory, "indexes_envelope");
        using var indexManager = new IndexManager(indexPath);

        // Index emails with specific envelope hashes
        for (int i = 0; i < 5; i++)
        {
            var emailId = new EmailHashedID
            {
                BlockId = 1,
                LocalId = i,
                EnvelopeHash = GenerateHash(i * 100),
                ContentHash = GenerateHash(i * 200)
            };

            var message = new MimeMessage();
            message.MessageId = $"envelope_test_{i}@test.com";
            message.Subject = $"Envelope Test {i}";
            message.From.Add(new MailboxAddress("Sender", "sender@test.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@test.com"));
            message.Body = new TextPart(TextFormat.Plain) { Text = "Content" };

            await indexManager.IndexEmailAsync(emailId, message, "Inbox", i * 100);
        }

        // Test envelope hash lookup
        var hash = GenerateHash(200); // Email at index 2
        var result = indexManager.GetEmailByEnvelopeHash(hash);
        
        Assert.True(result.IsSuccess);
        Assert.Equal("1:2", result.Value);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task Stage3_Integration_CacheAndIndexWorking()
    {
        // Test cache and index working together
        var dbPath = Path.Combine(_testDirectory, "integrated.edb");
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        using var cacheManager = new CacheManager(rawManager, serializer);
        
        var indexPath = Path.Combine(_testDirectory, "indexes_integrated");
        using var indexManager = new IndexManager(indexPath);

        // Initialize
        await cacheManager.InitializeNewFile();

        // Index emails
        for (int i = 0; i < 10; i++)
        {
            var emailId = new EmailHashedID
            {
                BlockId = 1,
                LocalId = i,
                EnvelopeHash = GenerateHash(i),
                ContentHash = GenerateHash(i + 100)
            };

            var message = new MimeMessage();
            message.MessageId = $"integrated_{i}@test.com";
            message.Subject = $"Integration Test {i}";
            message.From.Add(new MailboxAddress("Sender", "sender@test.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@test.com"));
            message.Body = new TextPart(TextFormat.Plain) { Text = $"Integration test email {i}" };

            await indexManager.IndexEmailAsync(emailId, message, "Inbox", i * 1000);
        }

        // Verify all emails are indexed
        for (int i = 0; i < 10; i++)
        {
            var result = indexManager.GetEmailByMessageId($"integrated_{i}@test.com");
            Assert.True(result.IsSuccess);
            Assert.Equal($"1:{i}", result.Value);
        }

        // Search functionality
        var searchResults = indexManager.GetEmailsBySearchTerm("integration");
        Assert.True(searchResults.IsSuccess);
        Assert.Equal(10, searchResults.Value.Count);
    }

    #endregion

    #region Helper Methods

    private byte[] GenerateHash(int seed)
    {
        var hash = new byte[32];
        new Random(seed).NextBytes(hash);
        return hash;
    }

    #endregion

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}