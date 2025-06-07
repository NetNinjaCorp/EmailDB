using EmailDB.Format;
using EmailDB.Format.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Testing.Tests;

public static class  BasicFileTests
{

    public static async Task TestBasicFileOperations(this EmailDBTestSuite suite, string TestFilePath)
    {      
        using StorageManager storage = new StorageManager(TestFilePath, createNew: true);
        storage.InitializeNewFile();
        storage.Dispose();

        using FileStream fileStream = new FileStream(TestFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Initialize components
        var blockManager = new BlockManager(fileStream);
        var cacheManager = new CacheManager(blockManager);
        var metadataManager = new MetadataManager(blockManager);
        var folderManager = new FolderManager(blockManager, metadataManager);

        // Test file creation
        suite.AssertTrue(File.Exists(TestFilePath), "Storage file should be created");

        // Test header initialization
        var header = cacheManager.GetHeader();
        suite.AssertNotNull(header, "Header should be initialized");
        suite.AssertEquals(1, header.FileVersion, "File version should be 1");

        // Test basic folder operations
        var folder = folderManager.GetSubfolders("/");

        suite.AssertNotNull(folder, "Root folder should exist");
    }

    public static async Task TestConcurrentAccess(this EmailDBTestSuite suite, string TestFilePath)
    {
        using var storage = new StorageManager(TestFilePath);
        var tasks = new List<Task>();
        var errors = new ConcurrentBag<Exception>();

        // Create multiple concurrent operations
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    storage.CreateFolder($"TestFolder_{i}");
                    suite.AddSampleEmail(storage, $"TestFolder_{i}");
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }));
        }

        await Task.WhenAll(tasks);
        suite.AssertEquals(0, errors.Count, "No errors should occur during concurrent access");
    }

   
}
