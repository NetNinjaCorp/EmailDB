using EmailDB.Format.Models;
using EmailDB.Format.ZoneTree;


namespace EmailDB.Format;

public class StorageManager : IStorageManager
{
    private readonly string filePath;
    private readonly FileStream fileStream;
    private readonly BlockManager blockManager;
    private readonly CacheManager cacheManager;
    private readonly FolderManager folderManager;
    private readonly SegmentManager segmentManager;
    private readonly MaintenanceManager maintenanceManager;

    public StorageManager(string path, bool createNew = false)
    {
        filePath = path;
        var mode = createNew ? FileMode.Create : FileMode.OpenOrCreate;
        fileStream = new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None);

        // Initialize components
        blockManager = new BlockManager(fileStream);
        cacheManager = new CacheManager(blockManager);
        folderManager = new FolderManager(blockManager);
        segmentManager = new SegmentManager(blockManager, cacheManager, folderManager);
        maintenanceManager = new MaintenanceManager(blockManager, cacheManager, folderManager, segmentManager);


        if (createNew)
        {
            InitializeNewFile();
        }
        else
        {
            cacheManager.LoadHeaderContent();
        }
        var zt = new EmailDBZoneTreeFactory<ulong, string>(this, false);
        var ztdb = zt.CreateZoneTree("emaildb");
        ztdb.Dispose();
        // Console.WriteLine(ztdb);    
    }

    public void InitializeNewFile()
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

        // Initialize empty folder tree
        folderManager.WriteFolderTree(new FolderTreeContent());
    }

    public void AddEmailToFolder(string folderName, byte[] emailContent)
    {
        lock (fileStream)
        {
            // Find max segment ID
            ulong newId = segmentManager.GetMaxSegmentId() + 1;

            // Create and write new segment
            var segment = new SegmentContent
            {
                SegmentId = newId,
                SegmentData = emailContent
            };

            long segmentOffset = segmentManager.WriteSegment(segment);

            // Update folder
            var folder = folderManager.GetFolder(folderName);
            if (folder == null)
            {
                throw new InvalidOperationException($"Folder '{folderName}' not found");
            }

            folder.EmailIds.Add(newId);
            long folderOffset = folderManager.WriteFolder(folder);

            // Update metadata
            segmentManager.UpdateMetadata(metadata =>
            {
                metadata.SegmentOffsets.Add(segmentOffset);
                metadata.FolderOffsets[folderName] = folderOffset;
                return metadata;
            });
        }
    }

    public void MoveEmail(ulong emailId, string sourceFolder, string targetFolder)
    {
        lock (fileStream)
        {
            var source = folderManager.GetFolder(sourceFolder);
            var target = folderManager.GetFolder(targetFolder);

            if (source == null || target == null)
            {
                throw new InvalidOperationException("Source or target folder not found");
            }

            if (!source.EmailIds.Contains(emailId))
            {
                throw new InvalidOperationException($"Email {emailId} not found in source folder");
            }

            source.EmailIds.Remove(emailId);
            target.EmailIds.Add(emailId);

            long sourceOffset = folderManager.WriteFolder(source);
            long targetOffset = folderManager.WriteFolder(target);

            segmentManager.UpdateMetadata(metadata =>
            {
                metadata.FolderOffsets[sourceFolder] = sourceOffset;
                metadata.FolderOffsets[targetFolder] = targetOffset;
                return metadata;
            });
        }
    }

    public void DeleteEmail(ulong emailId, string folderName)
    {
        lock (fileStream)
        {
            var folder = folderManager.GetFolder(folderName);
            if (folder == null)
            {
                throw new InvalidOperationException($"Folder '{folderName}' not found");
            }

            if (!folder.EmailIds.Contains(emailId))
            {
                throw new InvalidOperationException($"Email {emailId} not found in folder");
            }

            folder.EmailIds.Remove(emailId);
            long folderOffset = folderManager.WriteFolder(folder);

            var segmentOffsets = segmentManager.GetSegmentOffsets(emailId);
            segmentManager.UpdateMetadata(metadata =>
            {
                metadata.FolderOffsets[folderName] = folderOffset;
                metadata.OutdatedOffsets.AddRange(segmentOffsets);
                return metadata;
            });
        }
    }

    public void UpdateEmailContent(ulong emailId, byte[] newContent)
    {
        lock (fileStream)
        {
            var segment = new SegmentContent
            {
                SegmentId = emailId,
                SegmentData = newContent
            };

            long newOffset = segmentManager.WriteSegment(segment);
            var oldOffsets = segmentManager.GetSegmentOffsets(emailId);

            segmentManager.UpdateMetadata(metadata =>
            {
                metadata.OutdatedOffsets.AddRange(oldOffsets);
                metadata.SegmentOffsets.Add(newOffset);
                return metadata;
            });
        }
    }

    public void CreateFolder(string folderName, string parentFolderId = null)
    {
        lock (fileStream)
        {
            if (folderManager.GetFolder(folderName) != null)
            {
                throw new InvalidOperationException($"Folder '{folderName}' already exists");
            }

            var folderId = Guid.NewGuid().ToString();
            var folder = new FolderContent
            {
                FolderId = folderId,
                Name = folderName,
                EmailIds = new List<ulong>()
            };

            long folderOffset = folderManager.WriteFolder(folder);

            segmentManager.UpdateMetadata(metadata =>
            {
                metadata.FolderOffsets[folderName] = folderOffset;
                return metadata;
            });

            var tree = folderManager.GetLatestFolderTree();
            if (parentFolderId == null)
            {
                parentFolderId = tree.RootFolderId ?? folderId;
                if (tree.RootFolderId == null)
                {
                    tree.RootFolderId = folderId;
                }
            }
            else if (!tree.FolderHierarchy.ContainsKey(parentFolderId))
            {
                throw new InvalidOperationException($"Parent folder ID '{parentFolderId}' not found");
            }

            tree.FolderHierarchy[folderId] = parentFolderId;
            folderManager.WriteFolderTree(tree);
        }
    }

    public void DeleteFolder(string folderName, bool deleteEmails = false)
    {
        lock (fileStream)
        {
            var folder = folderManager.GetFolder(folderName);
            if (folder == null)
            {
                return;
                throw new InvalidOperationException($"Folder '{folderName}' not found");
            }

            if (deleteEmails)
            {
                var segmentOffsets = new List<long>();
                foreach (var emailId in folder.EmailIds)
                {
                    segmentOffsets.AddRange(segmentManager.GetSegmentOffsets(emailId));
                }

                segmentManager.UpdateMetadata(metadata =>
                {
                    metadata.OutdatedOffsets.AddRange(segmentOffsets);
                    metadata.FolderOffsets.Remove(folderName);
                    return metadata;
                });
            }
            else if (folder.EmailIds.Count > 0)
            {
                throw new InvalidOperationException("Cannot delete non-empty folder without deleteEmails flag");
            }

            var tree = folderManager.GetLatestFolderTree();
            tree.FolderHierarchy.Remove(folder.FolderId);
            folderManager.WriteFolderTree(tree);
        }
    }

    public HeaderContent GetHeader()
    {
        return cacheManager.GetHeader();
    }

    public Block ReadBlock(long offset)
    {
        return blockManager.ReadBlock(offset);
    }

    public long WriteBlock(Block block, long? specificOffset = null)
    {
        return blockManager.WriteBlock(block, specificOffset);
    }

    public IEnumerable<(long Offset, Block Block)> WalkBlocks()
    {
        return blockManager.WalkBlocks();
    }

    public void UpdateMetadata(Func<MetadataContent, MetadataContent> updateFunc)
    {
        segmentManager.UpdateMetadata(updateFunc);
    }
    public void Compact(string outputPath)
    {
        lock (fileStream)
        {
            maintenanceManager.Compact(outputPath);
        }
    }

    public void InvalidateCache()
    {
        lock (fileStream)
        {
            cacheManager.InvalidateCache();
        }
    }

    public void Dispose()
    {
        fileStream?.Dispose();
    }
}