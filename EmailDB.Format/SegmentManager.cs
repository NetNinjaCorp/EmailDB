using EmailDB.Format.FileManagement;
using EmailDB.Format.Models.Blocks;

namespace EmailDB.Format;

public class SegmentManager
{
    private readonly BlockManager blockManager;
    private readonly CacheManager cacheManager;
    private readonly MetadataManager metadataManager;
    private readonly object metadataLock = new();

    public SegmentManager(BlockManager blockManager, CacheManager cacheManager, MetadataManager metadataManager )
    {
        this.blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        this.cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        this.metadataManager = metadataManager ?? throw new ArgumentNullException(nameof(metadataManager));
    }

    public MetadataContent GetMetadata()
    {
        return cacheManager.GetCachedMetadata() ?? new MetadataContent();
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
        // Update metadata with new segment offset
        lock (metadataLock)
        {      
            var offset = blockManager.WriteBlock(block);           
            metadataManager.AddOrUpdateSegmentOffset(segment.SegmentId, offset);
            return offset;
        }
       
    }

    public Block ReadBlock(long offset)
    {
        return blockManager.ReadBlock(offset);
    }

   

    public void DeleteSegment(string path)
    {
        lock (metadataLock)
        {
            var metadata = GetMetadata();
            if (metadata.SegmentOffsets.TryGetValue(path, out var offset))
            {
                metadata.OutdatedOffsets.Add(offset);
                metadata.SegmentOffsets.Remove(path);
                UpdateMetadata(metadata);
            }
        }
    }

    public IEnumerable<(long Offset, Block Block)> WalkBlocks()
    {
        return blockManager.WalkBlocks();
    }

    public void Compact()
    {
        lock (metadataLock)
        {
            var metadata = GetMetadata();
            var validOffsets = new HashSet<long>(metadata.SegmentOffsets.Values);
            var outdatedOffsets = metadata.OutdatedOffsets.ToList();

            // Remove any outdated offsets that are still referenced
            outdatedOffsets.RemoveAll(offset => validOffsets.Contains(offset));

            metadata.OutdatedOffsets = outdatedOffsets;
            UpdateMetadata(metadata);
        }
    }

    public bool TryGetSegment(string path, out SegmentContent segment)
    {
        segment = null;
        var metadata = GetMetadata();

        if (metadata.SegmentOffsets.TryGetValue(path, out var offset))
        {
            var block = ReadBlock(offset);
            if (block?.Content is SegmentContent segmentContent)
            {
                segment = segmentContent;
                return true;
            }
        }

        return false;
    }

    public Dictionary<string, SegmentContent> GetAllSegments()
    {
        var result = new Dictionary<string, SegmentContent>();
        var metadata = GetMetadata();

        foreach (var kvp in metadata.SegmentOffsets)
        {
            var block = ReadBlock(kvp.Value);
            if (block?.Content is SegmentContent segment)
            {
                result[kvp.Key] = segment;
            }
        }

        return result;
    }

    public long GetSegmentOffset(string path)
    {
        var metadata = GetMetadata();
        return metadata.SegmentOffsets.TryGetValue(path, out var offset) ? offset : -1;
    }

    public bool IsSegmentOutdated(long offset)
    {
        var metadata = GetMetadata();
        return metadata.OutdatedOffsets.Contains(offset);
    }

    public void UpdateSegmentPath(string oldPath, string newPath)
    {
        lock (metadataLock)
        {
            var metadata = GetMetadata();
            if (metadata.SegmentOffsets.TryGetValue(oldPath, out var offset))
            {
                metadata.SegmentOffsets.Remove(oldPath);
                metadata.SegmentOffsets[newPath] = offset;
                UpdateMetadata(metadata);
            }
        }
    }

    public void CleanupOutdatedSegments()
    {
        lock (metadataLock)
        {
            var metadata = GetMetadata();
            var validOffsets = new HashSet<long>(metadata.SegmentOffsets.Values);
            metadata.OutdatedOffsets.RemoveAll(offset => validOffsets.Contains(offset));
            UpdateMetadata(metadata);
        }
    }
}