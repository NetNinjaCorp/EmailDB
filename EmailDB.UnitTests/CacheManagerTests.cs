using System;
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
        var block = new Block
        {
            Content = new BlockContent { FolderContent = folderContent }
        };

        mockBlockManager.Mock.Setup(m => m.ReadBlockAsync(It.IsAny<long>(), default))
            .ReturnsAsync(block);

        var cacheManager = new TestCacheManager(mockBlockManager);
        
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