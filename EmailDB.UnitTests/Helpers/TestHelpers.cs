using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.UnitTests.Models;
using EmailDB.Format.Models;
using Moq;

namespace EmailDB.UnitTests.Helpers;

public class MockRawBlockManager : IRawBlockManager
{
    private readonly Mock<IRawBlockManager> _mock;

    public MockRawBlockManager()
    {
        _mock = new Mock<IRawBlockManager>();
    }

    public Mock<IRawBlockManager> Mock => _mock;

    public Task<Block> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default)
    {
        return _mock.Object.ReadBlockAsync(blockId, cancellationToken);
    }

    public Task<BlockLocation> WriteBlockAsync(Block block, CancellationToken cancellationToken = default)
    {
        return _mock.Object.WriteBlockAsync(block, cancellationToken);
    }

    public IReadOnlyDictionary<long, BlockLocation> GetBlockLocations()
    {
        return _mock.Object.GetBlockLocations();
    }

    public List<long> ScanFile()
    {
        return _mock.Object.ScanFile();
    }

    public Task CompactAsync(CancellationToken cancellationToken = default)
    {
        return _mock.Object.CompactAsync(cancellationToken);
    }

    public void Dispose()
    {
        _mock.Object.Dispose();
    }
}

public interface IRawBlockManager : IDisposable
{
    Task<Block> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default);
    Task<BlockLocation> WriteBlockAsync(Block block, CancellationToken cancellationToken = default);
    IReadOnlyDictionary<long, BlockLocation> GetBlockLocations();
    List<long> ScanFile();
    Task CompactAsync(CancellationToken cancellationToken = default);
}

// Mock implementation of CacheManager for testing
public class TestCacheManager
{
    private readonly IRawBlockManager _blockManager;
    private readonly Dictionary<string, FolderContent> _folderCache = new Dictionary<string, FolderContent>();
    private FolderTreeContent _folderTree;
    private MetadataContent _metadata;
    private HeaderContent _header = new HeaderContent { FileVersion = 1 };

    public TestCacheManager(IRawBlockManager blockManager)
    {
        _blockManager = blockManager;
    }

    public async Task<FolderContent> GetCachedFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be null or empty", nameof(folderName));

        if (_folderCache.TryGetValue(folderName, out var folder))
        {
            return folder;
        }

        return null;
    }

    public void CacheFolder(string folderName, long offset, FolderContent folder)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be null or empty", nameof(folderName));
        
        if (folder == null)
            throw new ArgumentNullException(nameof(folder));
        
        if (offset < 0)
            throw new ArgumentException("Offset cannot be negative", nameof(offset));

        _folderCache[folderName] = folder;
    }

    public async Task<FolderTreeContent> GetCachedFolderTree()
    {
        return _folderTree;
    }

    public void CacheFolderTree(FolderTreeContent tree)
    {
        if (tree == null)
            throw new ArgumentNullException(nameof(tree));

        _folderTree = tree;
    }

    public async Task<MetadataContent> GetCachedMetadata()
    {
        return _metadata;
    }

    public async Task<SegmentContent> GetSegmentAsync(long segmentID)
    {
        var block = await _blockManager.ReadBlockAsync(segmentID);
        // This is a mock implementation, return null for now
        return null;
    }

    public void InvalidateCache()
    {
        _folderCache.Clear();
        _folderTree = null;
        _metadata = null;
    }

    public void Dispose()
    {
        // Cleanup resources
    }
}