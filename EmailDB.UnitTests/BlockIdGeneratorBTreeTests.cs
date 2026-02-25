using EmailDB.Format.Helpers;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that BlockIdGenerator allocates IDs in dedicated B+-tree ranges without collisions.
/// </summary>
public class BlockIdGeneratorBTreeTests : IDisposable
{
    private readonly BlockIdGenerator _generator;

    public BlockIdGeneratorBTreeTests()
    {
        _generator = BlockIdGenerator.Instance;
        _generator.Reset();
    }

    public void Dispose()
    {
        _generator.Reset();
    }

    [Fact]
    public void GetNextBTreeLeafId_ReturnsIdInBTreeLeafRange()
    {
        var id = _generator.GetNextBTreeLeafId();

        Assert.Equal(BlockType.BTreeLeaf, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextBTreeInternalId_ReturnsIdInBTreeInternalRange()
    {
        var id = _generator.GetNextBTreeInternalId();

        Assert.Equal(BlockType.BTreeInternal, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextIndexRootId_ReturnsIdInIndexRootRange()
    {
        var id = _generator.GetNextIndexRootId();

        Assert.Equal(BlockType.IndexRoot, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextEmailContentId_ReturnsIdInEmailContentRange()
    {
        var id = _generator.GetNextEmailContentId();

        Assert.Equal(BlockType.EmailContent, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextBlockId_BTreeLeaf_ReturnsIdInCorrectRange()
    {
        var id = _generator.GetNextBlockId(BlockType.BTreeLeaf);

        Assert.Equal(BlockType.BTreeLeaf, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextBlockId_BTreeInternal_ReturnsIdInCorrectRange()
    {
        var id = _generator.GetNextBlockId(BlockType.BTreeInternal);

        Assert.Equal(BlockType.BTreeInternal, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextBlockId_IndexRoot_ReturnsIdInCorrectRange()
    {
        var id = _generator.GetNextBlockId(BlockType.IndexRoot);

        Assert.Equal(BlockType.IndexRoot, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void GetNextBlockId_EmailContent_ReturnsIdInCorrectRange()
    {
        var id = _generator.GetNextBlockId(BlockType.EmailContent);

        Assert.Equal(BlockType.EmailContent, _generator.GetBlockTypeFromId(id));
    }

    [Fact]
    public void BTreeLeafIds_AreUnique_AcrossMultipleAllocations()
    {
        var ids = new HashSet<long>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(ids.Add(_generator.GetNextBTreeLeafId()), $"Duplicate BTreeLeaf ID at iteration {i}");
        }
    }

    [Fact]
    public void BTreeInternalIds_AreUnique_AcrossMultipleAllocations()
    {
        var ids = new HashSet<long>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(ids.Add(_generator.GetNextBTreeInternalId()), $"Duplicate BTreeInternal ID at iteration {i}");
        }
    }

    [Fact]
    public void IndexRootIds_AreUnique_AcrossMultipleAllocations()
    {
        var ids = new HashSet<long>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(ids.Add(_generator.GetNextIndexRootId()), $"Duplicate IndexRoot ID at iteration {i}");
        }
    }

    [Fact]
    public void EmailContentIds_AreUnique_AcrossMultipleAllocations()
    {
        var ids = new HashSet<long>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(ids.Add(_generator.GetNextEmailContentId()), $"Duplicate EmailContent ID at iteration {i}");
        }
    }

    [Fact]
    public void BTreeRanges_DoNotOverlap_WithEachOther()
    {
        // Allocate IDs from all B+-tree types
        var leafId = _generator.GetNextBTreeLeafId();
        var internalId = _generator.GetNextBTreeInternalId();
        var rootId = _generator.GetNextIndexRootId();
        var contentId = _generator.GetNextEmailContentId();

        // Each should be in its own range
        var allIds = new[] { leafId, internalId, rootId, contentId };
        Assert.Equal(allIds.Length, allIds.Distinct().Count());

        // Each should map back to its own type
        Assert.Equal(BlockType.BTreeLeaf, _generator.GetBlockTypeFromId(leafId));
        Assert.Equal(BlockType.BTreeInternal, _generator.GetBlockTypeFromId(internalId));
        Assert.Equal(BlockType.IndexRoot, _generator.GetBlockTypeFromId(rootId));
        Assert.Equal(BlockType.EmailContent, _generator.GetBlockTypeFromId(contentId));
    }

    [Fact]
    public void BTreeRanges_DoNotOverlap_WithExistingRanges()
    {
        // Allocate IDs from all block types
        var folderId = _generator.GetNextFolderId();
        var segmentId = _generator.GetNextSegmentId();
        var cleanupId = _generator.GetNextCleanupId();
        var customId = _generator.GetNextCustomBlockId();
        var leafId = _generator.GetNextBTreeLeafId();
        var internalId = _generator.GetNextBTreeInternalId();
        var rootId = _generator.GetNextIndexRootId();
        var contentId = _generator.GetNextEmailContentId();

        var allIds = new[] { folderId, segmentId, cleanupId, customId, leafId, internalId, rootId, contentId };
        Assert.Equal(allIds.Length, allIds.Distinct().Count());

        // Verify each maps to the correct type
        Assert.Equal(BlockType.Folder, _generator.GetBlockTypeFromId(folderId));
        Assert.Equal(BlockType.Segment, _generator.GetBlockTypeFromId(segmentId));
        Assert.Equal(BlockType.Cleanup, _generator.GetBlockTypeFromId(cleanupId));
        Assert.Equal(BlockType.BTreeLeaf, _generator.GetBlockTypeFromId(leafId));
        Assert.Equal(BlockType.BTreeInternal, _generator.GetBlockTypeFromId(internalId));
        Assert.Equal(BlockType.IndexRoot, _generator.GetBlockTypeFromId(rootId));
        Assert.Equal(BlockType.EmailContent, _generator.GetBlockTypeFromId(contentId));
    }

    [Fact]
    public void IsValidBlockIdForType_BTreeLeaf_ValidatesCorrectly()
    {
        var id = _generator.GetNextBTreeLeafId();

        Assert.True(_generator.IsValidBlockIdForType(id, BlockType.BTreeLeaf));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.BTreeInternal));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.IndexRoot));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.EmailContent));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.Folder));
    }

    [Fact]
    public void IsValidBlockIdForType_BTreeInternal_ValidatesCorrectly()
    {
        var id = _generator.GetNextBTreeInternalId();

        Assert.True(_generator.IsValidBlockIdForType(id, BlockType.BTreeInternal));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.BTreeLeaf));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.IndexRoot));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.Folder));
    }

    [Fact]
    public void IsValidBlockIdForType_IndexRoot_ValidatesCorrectly()
    {
        var id = _generator.GetNextIndexRootId();

        Assert.True(_generator.IsValidBlockIdForType(id, BlockType.IndexRoot));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.BTreeLeaf));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.BTreeInternal));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.Folder));
    }

    [Fact]
    public void IsValidBlockIdForType_EmailContent_ValidatesCorrectly()
    {
        var id = _generator.GetNextEmailContentId();

        Assert.True(_generator.IsValidBlockIdForType(id, BlockType.EmailContent));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.BTreeLeaf));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.BTreeInternal));
        Assert.False(_generator.IsValidBlockIdForType(id, BlockType.IndexRoot));
    }

    [Fact]
    public void RegisterExistingId_BTreeLeaf_PreventsCollision()
    {
        // Register a high ID as already used
        _generator.RegisterExistingId(BlockType.BTreeLeaf, 50_000_000_000_001L);

        // Next ID should be higher than the registered one
        var nextId = _generator.GetNextBTreeLeafId();

        Assert.True(nextId > 50_000_000_000_001L, "Next BTreeLeaf ID should be above the registered ID");
        Assert.Equal(BlockType.BTreeLeaf, _generator.GetBlockTypeFromId(nextId));
    }

    [Fact]
    public void RegisterExistingId_BTreeInternal_PreventsCollision()
    {
        _generator.RegisterExistingId(BlockType.BTreeInternal, 60_000_000_000_001L);

        var nextId = _generator.GetNextBTreeInternalId();

        Assert.True(nextId > 60_000_000_000_001L, "Next BTreeInternal ID should be above the registered ID");
        Assert.Equal(BlockType.BTreeInternal, _generator.GetBlockTypeFromId(nextId));
    }

    [Fact]
    public void RegisterExistingId_IndexRoot_PreventsCollision()
    {
        _generator.RegisterExistingId(BlockType.IndexRoot, 70_000_000_000_001L);

        var nextId = _generator.GetNextIndexRootId();

        Assert.True(nextId > 70_000_000_000_001L, "Next IndexRoot ID should be above the registered ID");
        Assert.Equal(BlockType.IndexRoot, _generator.GetBlockTypeFromId(nextId));
    }

    [Fact]
    public void RegisterExistingId_EmailContent_PreventsCollision()
    {
        _generator.RegisterExistingId(BlockType.EmailContent, 80_000_000_000_001L);

        var nextId = _generator.GetNextEmailContentId();

        Assert.True(nextId > 80_000_000_000_001L, "Next EmailContent ID should be above the registered ID");
        Assert.Equal(BlockType.EmailContent, _generator.GetBlockTypeFromId(nextId));
    }

    [Fact]
    public async Task ConcurrentAllocation_NoDuplicates_BTreeLeaf()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<long>();
        var tasks = new Task[10];

        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    ids.Add(_generator.GetNextBTreeLeafId());
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1000, ids.Count);
        Assert.Equal(1000, ids.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentAllocation_NoDuplicates_AllBTreeTypes()
    {
        var allIds = new System.Collections.Concurrent.ConcurrentBag<long>();
        var tasks = new Task[4];

        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < 250; i++) allIds.Add(_generator.GetNextBTreeLeafId());
        });
        tasks[1] = Task.Run(() =>
        {
            for (int i = 0; i < 250; i++) allIds.Add(_generator.GetNextBTreeInternalId());
        });
        tasks[2] = Task.Run(() =>
        {
            for (int i = 0; i < 250; i++) allIds.Add(_generator.GetNextIndexRootId());
        });
        tasks[3] = Task.Run(() =>
        {
            for (int i = 0; i < 250; i++) allIds.Add(_generator.GetNextEmailContentId());
        });

        await Task.WhenAll(tasks);

        Assert.Equal(1000, allIds.Count);
        Assert.Equal(1000, allIds.Distinct().Count());
    }

    [Fact]
    public void SequentialIds_AreMonotonicallyIncreasing_BTreeLeaf()
    {
        var prev = _generator.GetNextBTreeLeafId();
        for (int i = 0; i < 100; i++)
        {
            var next = _generator.GetNextBTreeLeafId();
            Assert.True(next > prev, $"BTreeLeaf IDs should be monotonically increasing: {prev} >= {next}");
            prev = next;
        }
    }

    [Fact]
    public void SequentialIds_AreMonotonicallyIncreasing_BTreeInternal()
    {
        var prev = _generator.GetNextBTreeInternalId();
        for (int i = 0; i < 100; i++)
        {
            var next = _generator.GetNextBTreeInternalId();
            Assert.True(next > prev);
            prev = next;
        }
    }

    [Fact]
    public void Reset_ClearsBTreeCounters()
    {
        // Allocate some IDs
        _generator.GetNextBTreeLeafId();
        _generator.GetNextBTreeInternalId();
        _generator.GetNextIndexRootId();
        _generator.GetNextEmailContentId();

        _generator.Reset();

        // After reset, first IDs should be base + 1 again
        var leafId = _generator.GetNextBTreeLeafId();
        var internalId = _generator.GetNextBTreeInternalId();
        var rootId = _generator.GetNextIndexRootId();
        var contentId = _generator.GetNextEmailContentId();

        Assert.Equal(50_000_000_000_001L, leafId);
        Assert.Equal(60_000_000_000_001L, internalId);
        Assert.Equal(70_000_000_000_001L, rootId);
        Assert.Equal(80_000_000_000_001L, contentId);
    }
}
