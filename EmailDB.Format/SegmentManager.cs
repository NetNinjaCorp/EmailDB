using DragonHoard.InMemory;
using EmailDB.Format.Models;

namespace EmailDB.Format;

public class SegmentManager
{
    private readonly BlockManager blockManager;
    private readonly CacheManager cacheManager;
    private readonly FolderManager folderManager;

    public SegmentManager(BlockManager blockManager, CacheManager cacheManager, FolderManager folderManager)
    {
        this.blockManager = blockManager;
        this.cacheManager = cacheManager;
        this.folderManager = folderManager;
    }

    public ulong GetMaxSegmentId()
    {
        ulong maxId = 0;
        foreach (var (_, block) in blockManager.WalkBlocks())
        {
            if (block.Content is SegmentContent segment)
            {
                maxId = Math.Max(maxId, segment.SegmentId);
            }
        }
        return maxId;
    }

    public List<long> GetSegmentOffsets(ulong segmentId)
    {
        var offsets = new List<long>();
        foreach (var (offset, block) in blockManager.WalkBlocks())
        {
            if (block.Content is SegmentContent segment && segment.SegmentId == segmentId)
            {
                offsets.Add(offset);
            }
        }
        return offsets;
    }

    public SegmentContent GetLatestSegment(ulong segmentId)
    {
        SegmentContent latest = null;
        foreach (var (_, block) in blockManager.WalkBlocks())
        {
            if (block.Content is SegmentContent segment && segment.SegmentId == segmentId)
            {
                latest = segment;
            }
        }
        return latest;
    }

    public long WriteSegment(SegmentContent segment)
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Segment,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = segment
        };

        return blockManager.WriteBlock(block);
    }

    public void UpdateMetadata(Func<MetadataContent, MetadataContent> updateFunc)
    {
        var metadata = cacheManager.GetCachedMetadata() ?? new MetadataContent();
        metadata = updateFunc(metadata);

        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Metadata,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = metadata
        };

        long offset = blockManager.WriteBlock(block);

        // Update header
        var header = cacheManager.GetHeader();
        header.FirstMetadataOffset = offset;
        cacheManager.UpdateHeader(header);
    }
}