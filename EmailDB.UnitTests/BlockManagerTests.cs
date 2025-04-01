using System;
using System.Threading.Tasks;
using EmailDB.UnitTests.Helpers;
using EmailDB.UnitTests.Models;
using Moq;
using Xunit;

namespace EmailDB.UnitTests
{
    public class BlockManagerTests
    {
        private readonly MockRawBlockManager mockRawBlockManager;
        private readonly Mock<TestCacheManager> mockCacheManager;

        public BlockManagerTests()
        {
            mockRawBlockManager = new MockRawBlockManager();
            mockCacheManager = new Mock<TestCacheManager>(mockRawBlockManager);
        }

        [Fact]
        public async Task GetSegmentAsync_WhenSegmentExists_ReturnsSegment()
        {
            // Arrange
            var segmentId = 123L;
            var segmentContent = new SegmentContent 
            { 
                SegmentId = segmentId, 
                SegmentData = new byte[] { 1, 2, 3, 4 } 
            };
            var block = new Block
            {
                BlockId = segmentId,
                Content = new BlockContent { SegmentContent = segmentContent }
            };

            mockRawBlockManager.Mock.Setup(m => m.ReadBlockAsync(segmentId, default))
                .ReturnsAsync(block);

            var blockManager = new TestBlockManager(mockRawBlockManager, mockCacheManager.Object);

            // Act
            var result = await blockManager.GetSegmentAsync(segmentId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(segmentId, result.SegmentId);
            Assert.Equal(4, result.SegmentData.Length);
        }

        [Fact]
        public async Task GetSegmentAsync_WhenSegmentDoesNotExist_ReturnsNull()
        {
            // Arrange
            var segmentId = 456L;

            mockRawBlockManager.Mock.Setup(m => m.ReadBlockAsync(segmentId, default))
                .ReturnsAsync((Block)null);

            var blockManager = new TestBlockManager(mockRawBlockManager, mockCacheManager.Object);

            // Act
            var result = await blockManager.GetSegmentAsync(segmentId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetFolderAsync_WhenFolderExists_ReturnsFolder()
        {
            // Arrange
            var folderName = "TestFolder";
            var folderContent = new FolderContent { Name = folderName };
            var folderTree = new FolderTreeContent();
            folderTree.FolderHierarchy.Add(new FolderHierarchyItem { Name = folderName });

            mockCacheManager.Setup(m => m.GetCachedFolderTree())
                .ReturnsAsync(folderTree);
            mockCacheManager.Setup(m => m.GetCachedFolder(folderName))
                .ReturnsAsync(folderContent);

            var blockManager = new TestBlockManager(mockRawBlockManager, mockCacheManager.Object);

            // Act
            var result = await blockManager.GetFolderAsync(folderName);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(folderName, result.Name);
        }

        [Fact]
        public async Task GetFolderAsync_WhenFolderTreeIsNull_ReturnsNull()
        {
            // Arrange
            var folderName = "TestFolder";

            mockCacheManager.Setup(m => m.GetCachedFolderTree())
                .ReturnsAsync((FolderTreeContent)null);

            var blockManager = new TestBlockManager(mockRawBlockManager, mockCacheManager.Object);

            // Act
            var result = await blockManager.GetFolderAsync(folderName);

            // Assert
            Assert.Null(result);
        }
    }

    // Test implementation of BlockManager
    public class TestBlockManager
    {
        private readonly IRawBlockManager rawBlockManager;
        private readonly TestCacheManager cacheManager;
        private FolderTreeContent folderTree;

        public TestBlockManager(IRawBlockManager rawBlockManager, TestCacheManager cacheManager)
        {
            this.rawBlockManager = rawBlockManager;
            this.cacheManager = cacheManager;
        }

        public async Task<SegmentContent> GetSegmentAsync(long segmentId)
        {
            var segment = await cacheManager.GetSegmentAsync(segmentId);
            return segment;
        }

        public async Task<FolderContent> GetFolderAsync(string folderName)
        {
            if (folderTree == null)
            {
                folderTree = await cacheManager.GetCachedFolderTree();
            }
            
            if (folderTree == null)
            {
                return null;
            }
            
            var folder = await cacheManager.GetCachedFolder(folderName);
            return folder;
        }
    }
}