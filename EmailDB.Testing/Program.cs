using System.Diagnostics;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models.BlockTypes;

class Program
{
    const string filePath = "test_email_store.dat";
    const string compactedFilePath = "test_email_store_compacted.dat";

    static void Main()
    {
        // Clean up any existing test files
        CleanupTestFiles();

        // Run core functionality tests
        TestBasicOperations();
        TestEmailOperations();
        TestFolderOperations();

        // Run performance tests
        TestPerformance();

        // Run robustness tests
        TestRobustness();

        // Test maintenance operations
        TestCompaction();

        // Test error handling
        TestErrorHandling();
    }

    static void CleanupTestFiles()
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        if (File.Exists(compactedFilePath))
            File.Delete(compactedFilePath);
    }

    static void TestBasicOperations()
    {
        Console.WriteLine("\n=== Testing Basic Operations ===");

        using var storage = new StorageManager(filePath, createNew: true);

        // Create basic folder structure
        Console.WriteLine("Creating initial folder structure...");
        storage.CreateFolder("Root");
        storage.CreateFolder("Inbox", "Root");
        storage.CreateFolder("Sent", "Root");
        storage.CreateFolder("Archive", "Root");

        // Add some initial emails
        Console.WriteLine("Adding initial emails...");
        AddSampleEmails(storage, "Inbox", 3);
        AddSampleEmails(storage, "Sent", 2);

        DumpFileStructure(storage.blockManager);
    }

    static void TestEmailOperations()
    {
        Console.WriteLine("\n=== Testing Email Operations ===");

        using var storage = new StorageManager(filePath);

        // Add new emails and measure performance
        Console.WriteLine("Adding new emails to multiple folders...");
        var stopwatch = Stopwatch.StartNew();

        AddSampleEmails(storage, "Inbox", 5);
        AddSampleEmails(storage, "Sent", 3);

        stopwatch.Stop();
        Console.WriteLine($"Time taken to add emails: {stopwatch.ElapsedMilliseconds}ms");

        // Test email operations with cache
        Console.WriteLine("\nTesting cached folder access...");
        stopwatch.Restart();

        // First access (uncached)
        var folder = GetFolder(storage.blockManager, "Inbox");
        var firstAccessTime = stopwatch.ElapsedMilliseconds;

        // Second access (should be cached)
        stopwatch.Restart();
        folder = GetFolder(storage.blockManager, "Inbox");
        var secondAccessTime = stopwatch.ElapsedMilliseconds;

        Console.WriteLine($"First folder access: {firstAccessTime}ms");
        Console.WriteLine($"Second folder access (cached): {secondAccessTime}ms");

        // Test email movement
        Console.WriteLine("\nTesting email movement between folders...");
        if (folder?.EmailIds.Count > 0)
        {
            var emailToMove = folder.EmailIds[0];
            storage.MoveEmail(emailToMove, "Inbox", "Archive");
            Console.WriteLine($"Moved email {emailToMove} from Inbox to Archive");
        }

        DumpFileStructure(storage.blockManager);
    }

    static void TestPerformance()
    {
        Console.WriteLine("\n=== Testing Performance ===");

        using var storage = new StorageManager(filePath);
        var stopwatch = Stopwatch.StartNew();

        // Test folder tree access performance
        Console.WriteLine("Testing folder tree access times...");
        for (int i = 0; i < 5; i++)
        {
            stopwatch.Restart();
            var tree = GetLatestFolderTree(storage.blockManager);
            Console.WriteLine($"Folder tree access #{i + 1}: {stopwatch.ElapsedMilliseconds}ms");
        }

        // Test folder access with cache
        Console.WriteLine("\nTesting folder access with cache...");
        string[] folders = { "Inbox", "Sent", "Archive" };

        foreach (var folderName in folders)
        {
            stopwatch.Restart();
            var folder = GetFolder(storage.blockManager, folderName);
            var firstAccess = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            folder = GetFolder(storage.blockManager, folderName);
            var secondAccess = stopwatch.ElapsedMilliseconds;

            Console.WriteLine($"Folder '{folderName}':");
            Console.WriteLine($"  First access: {firstAccess}ms");
            Console.WriteLine($"  Second access (cached): {secondAccess}ms");
        }

        // Test cache invalidation
        Console.WriteLine("\nTesting cache invalidation...");
        storage.InvalidateCache();

        stopwatch.Restart();
        var folderAfterInvalidation = GetFolder(storage.blockManager, "Inbox");
        Console.WriteLine($"Folder access after cache invalidation: {stopwatch.ElapsedMilliseconds}ms");
    }

    static void TestRobustness()
    {
        Console.WriteLine("\n=== Testing Robustness ===");

        using var storage = new StorageManager(filePath);

        // Test recovery from invalid offsets
        Console.WriteLine("Testing recovery from invalid metadata...");
        try
        {
            // Force cache invalidation to test recovery
            storage.InvalidateCache();
            var folder = GetFolder(storage.blockManager, "Inbox");
            Console.WriteLine("Successfully recovered folder information after cache invalidation");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during recovery: {ex.Message}");
        }

        // Test concurrent operations
        Console.WriteLine("\nTesting concurrent operations...");
        var tasks = new List<Task>();

        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(() => {
                try
                {
                    AddSampleEmails(storage, "Inbox", 1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Concurrent operation error: {ex.Message}");
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());
        Console.WriteLine("Completed concurrent operation testing");
    }

    static void TestFolderOperations()
    {
        Console.WriteLine("\n=== Testing Folder Operations ===");

        using var storage = new StorageManager(filePath);
        var stopwatch = Stopwatch.StartNew();

        // Create new folders
        Console.WriteLine("Creating new folders...");
        storage.CreateFolder("Important", "Root");
        storage.CreateFolder("Temporary", "Root");

        // Test folder access performance
        Console.WriteLine("\nTesting folder access performance...");
        stopwatch.Restart();
        var folder1 = GetFolder(storage.blockManager, "Important");
        var firstAccess = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        var folder2 = GetFolder(storage.blockManager, "Important");
        var cachedAccess = stopwatch.ElapsedMilliseconds;

        Console.WriteLine($"First folder access: {firstAccess}ms");
        Console.WriteLine($"Cached folder access: {cachedAccess}ms");

        // Add emails and test folder updates
        AddSampleEmails(storage, "Important", 2);
        AddSampleEmails(storage, "Temporary", 3);

        // Test folder deletion with cleanup
        Console.WriteLine("\nTesting folder deletion with cleanup...");
        storage.DeleteFolder("Temporary", deleteEmails: true);

        DumpFileStructure(storage.blockManager);
    }

    static void TestCompaction()
    {
        Console.WriteLine("\n=== Testing Compaction ===");

        using var storage = new StorageManager(filePath);

        // Create test data for compaction
        Console.WriteLine("Creating test data for compaction...");
        for (int i = 0; i < 3; i++)
        {
            AddSampleEmails(storage, "Inbox", 2);
            // Update some emails to create outdated content
            var folder = GetFolder(storage.blockManager, "Inbox");
            if (folder?.EmailIds.Count > 0)
            {
                var emailId = folder.EmailIds[0];
                var newContent = Encoding.UTF8.GetBytes($"Updated content {i} " + Guid.NewGuid());
                storage.UpdateEmailContent(emailId, newContent);
            }
        }

        Console.WriteLine("\nFile structure before compaction:");
        DumpFileStructure(storage.blockManager);

        // Perform compaction
        Console.WriteLine("\nPerforming compaction...");
        var stopwatch = Stopwatch.StartNew();
        storage.Compact(compactedFilePath);
        Console.WriteLine($"Compaction completed in {stopwatch.ElapsedMilliseconds}ms");

        // Compare files
        var originalSize = new FileInfo(filePath).Length;
        var compactedSize = new FileInfo(compactedFilePath).Length;
        var reduction = (1 - (double)compactedSize / originalSize) * 100;

        Console.WriteLine($"\nCompaction Results:");
        Console.WriteLine($"Original size: {originalSize / 1024.0:F2} KB");
        Console.WriteLine($"Compacted size: {compactedSize / 1024.0:F2} KB");
        Console.WriteLine($"Size reduction: {reduction:F1}%");

        // Verify compacted file
        Console.WriteLine("\nVerifying compacted file...");
        using (var compactedStorage = new StorageManager(compactedFilePath))
        {
            DumpFileStructure(compactedStorage.blockManager);
        }
    }

    static void TestErrorHandling()
    {
        Console.WriteLine("\n=== Testing Error Handling ===");

        using var storage = new StorageManager(filePath);

        // Test invalid operations
        Console.WriteLine("Testing invalid operations...");

        // Test invalid folder creation
        try
        {
            storage.CreateFolder("Root");
            Console.WriteLine("ERROR: Should not allow duplicate root folder");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Expected error: {ex.Message}");
        }

        // Test nonexistent folder operations
        try
        {
            storage.DeleteFolder("NonexistentFolder");
            Console.WriteLine("ERROR: Should not allow deleting nonexistent folder");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Expected error: {ex.Message}");
        }

        // Test invalid email operations
        try
        {            
            storage.MoveEmail(new EmailHashedID(), "Inbox", "Archive");
            Console.WriteLine("ERROR: Should not allow moving nonexistent email");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Expected error: {ex.Message}");
        }

        // Test cache behavior with invalid data
        Console.WriteLine("\nTesting cache behavior with invalid data...");
        storage.InvalidateCache();
        try
        {
            var folder = GetFolder(storage.blockManager, "Inbox");
            Console.WriteLine("Successfully recovered from cache invalidation");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during cache recovery: {ex.Message}");
        }
    }

    // Helper methods
    static void AddSampleEmails(StorageManager storage, string folderName, int count)
    {
        var random = new Random();
        for (int i = 0; i < count; i++)
        {
            var contentSize = random.Next(5 * 1024, 20 * 1024);
            var content = new byte[contentSize];
            random.NextBytes(content);

            try
            {
                storage.AddEmailToFolder(folderName, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding email to {folderName}: {ex.Message}");
            }
        }
    }

    static FolderContent GetFolder(BlockManager storage, string folderName)
    {
        foreach (var (_, block) in storage.WalkBlocks())
        {
            if (block.Content is FolderContent folder && folder.Name == folderName)
            {
                return folder;
            }
        }
        return null;
    }

    static FolderTreeContent GetLatestFolderTree(BlockManager storage)
    {
        FolderTreeContent latest = null;
        foreach (var (_, block) in storage.WalkBlocks())
        {
            if (block.Content is FolderTreeContent tree)
            {
                latest = tree;
            }
        }
        return latest;
    }

    static void DumpFileStructure(BlockManager storage)
    {
        Console.WriteLine("\n=== File Structure Summary ===");
        var stats = new Dictionary<BlockType, int>();
        var emailsByFolder = new Dictionary<string, int>();
        var totalEmails = 0;
        var totalEmailSize = 0L;
        var uniqueSegments = new HashSet<long>();

        foreach (var (_, block) in storage.WalkBlocks())
        {
            if (!stats.ContainsKey(block.Header.Type))
                stats[block.Header.Type] = 0;
            stats[block.Header.Type]++;

            switch (block.Content)
            {
                case FolderContent folder:
                    emailsByFolder[folder.Name] = folder.EmailIds.Count;
                    totalEmails += folder.EmailIds.Count;
                    break;
                case SegmentContent segment:
                    uniqueSegments.Add(segment.SegmentId);
                    totalEmailSize += segment.SegmentData.Length;
                    break;
            }
        }

        Console.WriteLine("Block counts by type:");
        foreach (var stat in stats.OrderBy(x => x.Key))
        {
            Console.WriteLine($"  {stat.Key}: {stat.Value}");
        }

        Console.WriteLine("\nEmails per folder:");
        foreach (var folder in emailsByFolder.OrderBy(x => x.Key))
        {
            Console.WriteLine($"  {folder.Key}: {folder.Value} emails");
        }

        Console.WriteLine($"\nTotal statistics:");
        Console.WriteLine($"  Total emails: {totalEmails:N0}");
        Console.WriteLine($"  Unique segments: {uniqueSegments.Count:N0}");
        Console.WriteLine($"  Total email content size: {totalEmailSize / 1024.0:F2} KB");
        Console.WriteLine($"  File size: {new FileInfo(filePath).Length / 1024.0:F2} KB");
    }
}