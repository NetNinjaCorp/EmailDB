using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmailDB.UnitTests.Helpers;
using EmailDB.UnitTests.Models;
using Moq;
using Xunit;

namespace EmailDB.UnitTests;

public class CacheManagerTests
{
    private readonly MockRawBlockManager mockBlockManager;

    public CacheManagerTests()
    {
        mockBlockManager = new MockRawBlockManager();
    }

    [Fact]
    public async Task GetCachedFolder_WhenFolderExists_ReturnsFolderContent()
    {
        // Arrange
        var folderName = "TestFolder";
        var folderContent = new FolderContent { Name = folderName };

        var cacheManager = new TestCacheManager(mockBlockManager);
        cacheManager.CacheFolder(folderName, 0, folderContent);

        // Act
        var result = await cacheManager.GetCachedFolder(folderName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(folderName, result.Name);
    }

    [Fact]
    public async Task GetCachedFolder_WhenFolderDoesNotExist_ReturnsNull()
    {
        // Arrange
        var folderName = "NonExistentFolder";
        
        mockBlockManager.Mock.Setup(m => m.ReadBlockAsync(It.IsAny<long>(), default))
            .ReturnsAsync((Block)null);

        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act
        var result = await cacheManager.GetCachedFolder(folderName);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CacheFolder_WithValidParameters_AddsFolderToCache()
    {
        // Arrange
        var folderName = "TestFolder";
        var folderContent = new FolderContent { Name = folderName };
        var offset = 100L;

        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act & Assert (no exception should be thrown)
        var exception = Record.Exception(() => cacheManager.CacheFolder(folderName, offset, folderContent));
        Assert.Null(exception);
    }

    [Fact]
    public void CacheFolder_WithNullFolder_ThrowsArgumentNullException()
    {
        // Arrange
        var folderName = "TestFolder";
        var offset = 100L;
        FolderContent folderContent = null;

        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => cacheManager.CacheFolder(folderName, offset, folderContent));
    }

    [Fact]
    public void CacheFolder_WithEmptyFolderName_ThrowsArgumentException()
    {
        // Arrange
        var folderName = "";
        var folderContent = new FolderContent { Name = "TestFolder" };
        var offset = 100L;

        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() => cacheManager.CacheFolder(folderName, offset, folderContent));
    }

    [Fact]
    public void CacheFolder_WithNegativeOffset_ThrowsArgumentException()
    {
        // Arrange
        var folderName = "TestFolder";
        var folderContent = new FolderContent { Name = folderName };
        var offset = -1L;

        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() => cacheManager.CacheFolder(folderName, offset, folderContent));
    }

    [Fact]
    public void OnError_DefaultsToNull()
    {
        // Arrange
        var cacheManager = new TestCacheManager(mockBlockManager);

        // Assert - OnError should be null by default (no handler configured)
        Assert.Null(cacheManager.OnError);
    }

    [Fact]
    public void OnError_CanBeSetToHandler()
    {
        // Arrange
        var cacheManager = new TestCacheManager(mockBlockManager);
        string capturedContext = null;
        Exception capturedException = null;

        // Act
        cacheManager.OnError = (context, ex) =>
        {
            capturedContext = context;
            capturedException = ex;
        };

        // Simulate an error callback
        cacheManager.OnError?.Invoke("test context", new InvalidOperationException("test error"));

        // Assert
        Assert.NotNull(capturedContext);
        Assert.Equal("test context", capturedContext);
        Assert.NotNull(capturedException);
        Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Equal("test error", capturedException.Message);
    }

    [Fact]
    public void OnError_HandlerReceivesContextAndException()
    {
        // Arrange
        var errors = new List<(string Context, Exception Error)>();
        var cacheManager = new TestCacheManager(mockBlockManager);
        cacheManager.OnError = (context, ex) => errors.Add((context, ex));

        // Act - simulate multiple error callbacks
        cacheManager.OnError?.Invoke("Failed to deserialize folder 'Inbox' at offset 100", new FormatException("Bad data"));
        cacheManager.OnError?.Invoke("Failed to deserialize metadata at offset 200", new InvalidOperationException("Corrupt"));

        // Assert
        Assert.Equal(2, errors.Count);
        Assert.Contains("folder 'Inbox'", errors[0].Context);
        Assert.Contains("offset 100", errors[0].Context);
        Assert.IsType<FormatException>(errors[0].Error);
        Assert.Contains("metadata", errors[1].Context);
        Assert.Contains("offset 200", errors[1].Context);
        Assert.IsType<InvalidOperationException>(errors[1].Error);
    }

    [Fact]
    public void OnError_NullHandler_DoesNotThrow()
    {
        // Arrange
        var cacheManager = new TestCacheManager(mockBlockManager);
        // OnError is null by default

        // Act & Assert - invoking null OnError should not throw
        var exception = Record.Exception(() =>
            cacheManager.OnError?.Invoke("test", new Exception("test")));
        Assert.Null(exception);
    }

    [Fact]
    public void InvalidateCache_ClearsAllCaches()
    {
        // Arrange
        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act & Assert (no exception should be thrown)
        var exception = Record.Exception(() => cacheManager.InvalidateCache());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_DisposesResources()
    {
        // Arrange
        var cacheManager = new TestCacheManager(mockBlockManager);
        
        // Act & Assert (no exception should be thrown)
        var exception = Record.Exception(() => cacheManager.Dispose());
        Assert.Null(exception);
    }
}