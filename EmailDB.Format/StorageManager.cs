using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models.Blocks;

public class StorageManager : IStorageManager
{
    private readonly string filePath;
    private readonly FileStream fileStream;
    public readonly BlockManager blockManager;
    public readonly CacheManager cacheManager;
    public readonly MetadataManager metadataManager;
    public readonly FolderManager folderManager;
    public readonly SegmentManager segmentManager;
    public readonly EmailManager emailManager;
    public readonly MaintenanceManager maintenanceManager;

    public StorageManager(string path, bool createNew = false)
    {
        filePath = path;
        var mode = createNew ? FileMode.Create : FileMode.OpenOrCreate;
        fileStream = new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None);

        // Initialize components
        blockManager = new BlockManager(fileStream);
        cacheManager = new CacheManager(blockManager);
        metadataManager = new MetadataManager(blockManager);
        folderManager = new FolderManager(blockManager, metadataManager);
        if (createNew)
        {
            InitializeNewFile();
        }
        else
        {
            cacheManager.LoadHeaderContent();
        }
      
        segmentManager = new SegmentManager(blockManager, cacheManager);
        emailManager = new EmailManager(this, folderManager, segmentManager);
        maintenanceManager = new MaintenanceManager(blockManager, cacheManager, folderManager, segmentManager);

        if (createNew)
        {
            InitializeNewFile();
        }
        else
        {
            cacheManager.LoadHeaderContent();
        }
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

    public async void AddEmailToFolder(string folderName, byte[] emailContent)
    {
        // The StorageManager now just coordinates between EmailManager and FolderManager
        var emailId = await emailManager.AddEmailAsync(emailContent,folderName);
        folderManager.AddEmailToFolder(folderName, emailId);
    }

    public void UpdateEmailContent(EmailHashedID emailId, byte[] newContent)
    {
        throw new NotImplementedException();
    }

    public async void MoveEmail(EmailHashedID emailId, string sourceFolder, string targetFolder)
    {
        // Coordinate the move operation between managers       
        await emailManager.MoveEmailAsync(emailId, sourceFolder, targetFolder);
    }

    public async void DeleteEmail(EmailHashedID emailId, string folderName)
    {
        // Coordinate deletion between managers
        await emailManager.MoveEmailAsync(emailId, folderName, "Deleted Items");
    }

    public async void CreateFolder(string folderName, string parentFolderId = null)
    {
        if (parentFolderId == null)
        {
            folderManager.CreateFolder(folderName);

        }
        else
        {
            var parentFolder = await folderManager.GetFolder(parentFolderId);
            if (parentFolder == null)
            {
                throw new InvalidOperationException("Parent folder does not exist");
            }
            folderManager.CreateFolder($"{parentFolderId}\\{folderName}");
        }
    }

    public async void DeleteFolder(string folderName, bool deleteEmails = false)
    {
        if (deleteEmails)
        {
            var folder = folderManager.GetFolder(folderName).Result;
            if (folder != null)
            {
                foreach (var emailId in folder.EmailIds)
                {
                    await emailManager.MoveEmailAsync(emailId, folderName, "Deleted Items");
                }
            }
        }
        folderManager.DeleteFolder(folderName, deleteEmails);
    }

    public HeaderContent GetHeader()
    {
        return cacheManager.GetHeader();
    }

    // These methods should be internal and only used by other managers
    internal Block ReadBlock(long offset)
    {
        return blockManager.ReadBlock(offset);
    }

    internal long WriteBlock(Block block, long? specificOffset = null)
    {
        return blockManager.WriteBlock(block, specificOffset);
    }

    internal IEnumerable<(long Offset, Block Block)> WalkBlocks()
    {
        return blockManager.WalkBlocks();
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
        cacheManager.InvalidateCache();
    }

    public void Dispose()
    {
        emailManager?.Dispose();
        fileStream?.Dispose();
    }


}