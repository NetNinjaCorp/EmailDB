using EmailDB.Format.Models;

namespace EmailDB.Format;

public class MaintenanceManager
{
    private readonly BlockManager blockManager;
    private readonly CacheManager cacheManager;
    private readonly FolderManager folderManager;
    private readonly SegmentManager segmentManager;

    public MaintenanceManager(BlockManager blockManager, CacheManager cacheManager,
                            FolderManager folderManager, SegmentManager emailManager)
    {
        this.blockManager = blockManager;
        this.cacheManager = cacheManager;
        this.folderManager = folderManager;
        segmentManager = emailManager;
    }

    public void Compact(string outputPath)
    {
        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var outputBlockManager = new BlockManager(outputStream);
        var outputCacheManager = new CacheManager(outputBlockManager);

        // Initialize components for new file
        var outputFolderManager = new FolderManager(outputBlockManager, outputCacheManager);
        var outputEmailManager = new SegmentManager(outputBlockManager, outputCacheManager, outputFolderManager);

        // Get current state
        var folderTree = folderManager.GetLatestFolderTree();
        var referencedSegments = new HashSet<ulong>();

        // Initialize new file
        InitializeNewFile(outputBlockManager, outputCacheManager);

        // Write folder tree
        outputFolderManager.WriteFolderTree(folderTree);

        // Process each folder and its contents
        foreach (var (_, block) in blockManager.WalkBlocks())
        {
            if (block.Content is FolderContent folder)
            {
                if (folder.EmailIds.Count > 0)
                {
                    // Write folder
                    var folderOffset = outputFolderManager.WriteFolder(folder);

                    // Track and write segments
                    foreach (var emailId in folder.EmailIds)
                    {
                        if (referencedSegments.Add(emailId))
                        {
                            var segment = segmentManager.GetLatestSegment(emailId);
                            if (segment != null)
                            {
                                var segmentOffset = outputEmailManager.WriteSegment(segment);

                                // Update metadata for new segment
                                outputEmailManager.UpdateMetadata(metadata =>
                                {
                                    metadata.SegmentOffsets.Add(segmentOffset);
                                    return metadata;
                                });
                            }
                        }
                    }

                    // Update metadata for new folder
                    outputEmailManager.UpdateMetadata(metadata =>
                    {
                        metadata.FolderOffsets[folder.Name] = folderOffset;
                        return metadata;
                    });
                }
            }
        }
    }

    private void InitializeNewFile(BlockManager blockManager, CacheManager cacheManager)
    {
        var headerBlock = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Header,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = new HeaderContent
            {
                FileVersion = 1,
                FirstMetadataOffset = -1,
                FirstFolderTreeOffset = -1,
                FirstCleanupOffset = -1
            }
        };

        blockManager.WriteBlock(headerBlock, 0);
        cacheManager.LoadHeaderContent();
    }

    public void PerformCleanup(long cleanupThreshold)
    {
        var metadata = cacheManager.GetCachedMetadata() ?? new MetadataContent();
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Create cleanup record
        var cleanup = new CleanupContent
        {
            CleanupThreshold = cleanupThreshold,
            FolderTreeOffsets = new List<long>(),
            FolderOffsets = new Dictionary<string, List<long>>(),
            MetadataOffsets = new List<long>()
        };

        // Collect outdated blocks
        foreach (var (offset, block) in blockManager.WalkBlocks())
        {
            if (block.Header.Timestamp < cleanupThreshold)
            {
                switch (block.Content)
                {
                    case FolderTreeContent:
                        cleanup.FolderTreeOffsets.Add(offset);
                        break;
                    case FolderContent folder:
                        if (!cleanup.FolderOffsets.ContainsKey(folder.Name))
                            cleanup.FolderOffsets[folder.Name] = new List<long>();
                        cleanup.FolderOffsets[folder.Name].Add(offset);
                        break;
                    case MetadataContent:
                        cleanup.MetadataOffsets.Add(offset);
                        break;
                }
            }
        }

        // Write cleanup record
        var cleanupBlock = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Cleanup,
                Timestamp = currentTime,
                Version = 1
            },
            Content = cleanup
        };

        var cleanupOffset = blockManager.WriteBlock(cleanupBlock);

        // Update header with cleanup offset
        var header = cacheManager.GetHeader();
        header.FirstCleanupOffset = cleanupOffset;
        cacheManager.UpdateHeader(header);
    }
}