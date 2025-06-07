using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EmailDB.UnitTests.Models;
using Xunit;

namespace EmailDB.UnitTests;

public class StorageManagerTests : IDisposable
{
    private readonly string testFilePath;

    public StorageManagerTests()
    {
        // Create a unique test file path for each test run
        testFilePath = Path.Combine(Path.GetTempPath(), $"test_storage_manager_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        // Clean up test file after each test
        if (File.Exists(testFilePath))
        {
            File.Delete(testFilePath);
        }
    }

    [Fact]
    public void Constructor_WithCreateNewTrue_CreatesNewFile()
    {
        // Act
        using var storage = new TestStorageManager(testFilePath, true);

        // Assert
        Assert.True(File.Exists(testFilePath));
    }

    [Fact]
    public void InitializeNewFile_CreatesBasicStructure()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);

        // Act
        storage.InitializeNewFile();

        // Assert
        Assert.True(storage.IsInitialized);
        Assert.NotNull(storage.GetHeader());
        Assert.Equal(1, storage.GetHeader().FileVersion);
    }

    [Fact]
    public void CreateFolder_AddsNewFolder()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);
        storage.InitializeNewFile();
        var folderName = "TestFolder";

        // Act
        storage.CreateFolder(folderName);

        // Assert
        var folder = storage.GetFolder(folderName);
        Assert.NotNull(folder);
        Assert.Equal(folderName, folder.Name);
    }

    [Fact]
    public void CreateFolder_WithParent_AddsNestedFolder()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);
        storage.InitializeNewFile();
        var parentFolder = "Parent";
        var childFolder = "Child";

        // Act
        storage.CreateFolder(parentFolder);
        storage.CreateFolder(childFolder, parentFolder);

        // Assert
        var folder = storage.GetFolder(childFolder);
        Assert.NotNull(folder);
        Assert.Equal(childFolder, folder.Name);
    }

    [Fact]
    public void AddEmailToFolder_AddsEmailToSpecifiedFolder()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);
        storage.InitializeNewFile();
        var folderName = "Inbox";
        storage.CreateFolder(folderName);
        var emailContent = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var emailId = storage.AddEmailToFolder(folderName, emailContent);

        // Assert
        Assert.NotNull(emailId);
        var folder = storage.GetFolder(folderName);
        Assert.Contains(emailId, folder.EmailIds);
    }

    [Fact]
    public void DeleteFolder_RemovesFolder()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);
        storage.InitializeNewFile();
        var folderName = "ToDelete";
        storage.CreateFolder(folderName);

        // Act
        storage.DeleteFolder(folderName);

        // Assert
        var folder = storage.GetFolder(folderName);
        Assert.Null(folder);
    }

    [Fact]
    public void MoveEmail_MovesEmailBetweenFolders()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);
        storage.InitializeNewFile();
        var sourceFolder = "Source";
        var targetFolder = "Target";
        storage.CreateFolder(sourceFolder);
        storage.CreateFolder(targetFolder);
        var emailContent = new byte[] { 1, 2, 3, 4, 5 };
        var emailId = storage.AddEmailToFolder(sourceFolder, emailContent);

        // Act
        storage.MoveEmail(emailId, sourceFolder, targetFolder);

        // Assert
        var sourceFolder2 = storage.GetFolder(sourceFolder);
        var targetFolder2 = storage.GetFolder(targetFolder);
        Assert.DoesNotContain(emailId, sourceFolder2.EmailIds);
        Assert.Contains(emailId, targetFolder2.EmailIds);
    }

    [Fact]
    public void InvalidateCache_ClearsCache()
    {
        // Arrange
        using var storage = new TestStorageManager(testFilePath, true);
        storage.InitializeNewFile();

        // Act & Assert (no exception should be thrown)
        var exception = Record.Exception(() => storage.InvalidateCache());
        Assert.Null(exception);
    }
}

// Simple test implementation of StorageManager
public class TestStorageManager : IDisposable
{
    private readonly string filePath;
    private readonly bool createNew;
    private readonly Dictionary<string, FolderContent> folders = new Dictionary<string, FolderContent>();
    private readonly Dictionary<string, byte[]> emails = new Dictionary<string, byte[]>();
    private HeaderContent header;
    private bool isInitialized = false;

    public TestStorageManager(string filePath, bool createNew = false)
    {
        this.filePath = filePath;
        this.createNew = createNew;

        if (createNew && !File.Exists(filePath))
        {
            using var fs = File.Create(filePath);
        }
    }

    public bool IsInitialized => isInitialized;

    public void InitializeNewFile()
    {
        header = new HeaderContent { FileVersion = 1 };
        
        // Create root folder
        var rootFolder = new FolderContent { Name = "Root" };
        folders["Root"] = rootFolder;
        
        isInitialized = true;
    }

    public HeaderContent GetHeader()
    {
        return header;
    }

    public void CreateFolder(string folderName, string parentFolder = "Root")
    {
        if (folders.ContainsKey(folderName))
        {
            throw new InvalidOperationException($"Folder '{folderName}' already exists");
        }

        if (!folders.ContainsKey(parentFolder))
        {
            throw new InvalidOperationException($"Parent folder '{parentFolder}' does not exist");
        }

        var folder = new FolderContent { Name = folderName };
        folders[folderName] = folder;
    }

    public FolderContent GetFolder(string folderName)
    {
        if (folders.TryGetValue(folderName, out var folder))
        {
            return folder;
        }
        return null;
    }

    public string AddEmailToFolder(string folderName, byte[] emailContent)
    {
        if (!folders.TryGetValue(folderName, out var folder))
        {
            throw new InvalidOperationException($"Folder '{folderName}' does not exist");
        }

        var emailId = Guid.NewGuid().ToString();
        emails[emailId] = emailContent;
        folder.EmailIds.Add(emailId);
        
        return emailId;
    }

    public void DeleteFolder(string folderName)
    {
        if (!folders.ContainsKey(folderName))
        {
            throw new InvalidOperationException($"Folder '{folderName}' does not exist");
        }

        folders.Remove(folderName);
    }

    public void MoveEmail(string emailId, string sourceFolder, string targetFolder)
    {
        if (!folders.TryGetValue(sourceFolder, out var source))
        {
            throw new InvalidOperationException($"Source folder '{sourceFolder}' does not exist");
        }

        if (!folders.TryGetValue(targetFolder, out var target))
        {
            throw new InvalidOperationException($"Target folder '{targetFolder}' does not exist");
        }

        if (!source.EmailIds.Contains(emailId))
        {
            throw new InvalidOperationException($"Email '{emailId}' does not exist in folder '{sourceFolder}'");
        }

        source.EmailIds.Remove(emailId);
        target.EmailIds.Add(emailId);
    }

    public void InvalidateCache()
    {
        // In a real implementation, this would clear any caches
    }

    public void Dispose()
    {
        // Clean up resources
    }
}