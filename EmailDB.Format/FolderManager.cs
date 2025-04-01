using DragonHoard.InMemory;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models.Blocks;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace EmailDB.Format;

public class FolderManager
{
    private readonly BlockManager blockManager;
    private readonly MetadataManager metadataManager;
    private readonly InMemoryCache inMemoryCache;
    private readonly object folderTreeLock = new object();
    private const char PathSeparator = '\\';

    public FolderManager(BlockManager blockManager, MetadataManager metadataManager)
    {
        this.blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        this.metadataManager = metadataManager ?? throw new ArgumentNullException(nameof(metadataManager));

        IEnumerable<IOptions<InMemoryCacheOptions>> options = new List<IOptions<InMemoryCacheOptions>>()
        {
            new OptionsWrapper<InMemoryCacheOptions>(new InMemoryCacheOptions
            {
                MaxCacheSize = 10000,
                ScanFrequency = TimeSpan.FromMinutes(5)
            })
        };
        inMemoryCache = new InMemoryCache(options);
    }

    // High-level folder operations
    public async Task CreateFolder(string path)
    {
        ValidatePath(path);

        var (parentPath, folderName) = SplitPath(path);
        var folderTree = await GetLatestFolderTree();

        // Check if path already exists
        if (folderTree.FolderIDs.ContainsKey(path))
            throw new InvalidOperationException($"Folder '{path}' already exists");

        // Get parent folder ID
        long parentFolderId = 0;
        if (!string.IsNullOrEmpty(parentPath))
        {
            if (!folderTree.FolderIDs.TryGetValue(parentPath, out parentFolderId))
                throw new InvalidOperationException($"Parent folder '{parentPath}' not found");
        }
        else if (folderTree.RootFolderId == -1)
        {
            // This will be the root folder
            parentFolderId = 0;
        }

        // Create new folder
        var folderId = GetNextFolderId(folderTree);
        var folder = new FolderContent
        {
            FolderId = folderId,
            ParentFolderId = parentFolderId,
            Name = path, // Store the full path as the name
            EmailIds = new List<EmailHashedID>()
        };

        // Write folder and update tree
        var offset = WriteFolder(folder);
        folderTree.FolderIDs[path] = folderId;
        folderTree.FolderOffsets[folderId] = offset;

        if (parentFolderId != 0)
            folderTree.FolderHierarchy[folderId.ToString()] = parentFolderId.ToString();
        else if (folderTree.RootFolderId == -1)
            folderTree.RootFolderId = folderId;

        WriteFolderTree(folderTree);
    }

    public async Task DeleteFolder(string path, bool deleteContents = false)
    {
        ValidatePath(path);
        var folder = await GetFolder(path);
        if (folder == null)
            return;

        var folderTree = await GetLatestFolderTree();

        // Get all subfolders that start with this path
        var subfolders = folderTree.FolderIDs
            .Where(x => x.Key.StartsWith(path + PathSeparator))
            .ToList();

        if (subfolders.Any())
            throw new InvalidOperationException("Cannot delete folder with subfolders");

        if (!deleteContents && folder.EmailIds.Any())
            throw new InvalidOperationException("Cannot delete non-empty folder without deleteContents flag");

        // Remove from folder tree
        folderTree.FolderIDs.Remove(path);
        folderTree.FolderOffsets.Remove(folder.FolderId);
        folderTree.FolderHierarchy.Remove(folder.FolderId.ToString());

        if (folderTree.RootFolderId == folder.FolderId)
            folderTree.RootFolderId = -1;

        WriteFolderTree(folderTree);
        InvalidateCache();
    }

    public async Task MoveFolder(string sourcePath, string targetParentPath)
    {
        ValidatePath(sourcePath);
        ValidatePath(targetParentPath);

        var sourceFolder = await GetFolder(sourcePath);
        if (sourceFolder == null)
            throw new InvalidOperationException($"Folder '{sourcePath}' not found");

        var folderTree = await GetLatestFolderTree();

        // Get target parent folder ID
        if (!folderTree.FolderIDs.TryGetValue(targetParentPath, out var targetParentId))
            throw new InvalidOperationException($"Target parent folder '{targetParentPath}' not found");

        // Check for circular reference
        var currentParent = targetParentId;
        while (currentParent != 0)
        {
            if (currentParent == sourceFolder.FolderId)
                throw new InvalidOperationException("Moving folder would create circular reference");

            var parentStr = currentParent.ToString();
            if (!folderTree.FolderHierarchy.TryGetValue(parentStr, out var nextParentStr))
                break;

            if (!long.TryParse(nextParentStr, out currentParent))
                break;
        }

        // Calculate new path
        var folderName = GetFolderName(sourcePath);
        var newPath = Combine(targetParentPath, folderName);

        // Check if new path already exists
        if (folderTree.FolderIDs.ContainsKey(newPath))
            throw new InvalidOperationException($"Target folder '{newPath}' already exists");

        // Update folder and its subfolders
        var oldPrefix = sourcePath + PathSeparator;
        var newPrefix = newPath + PathSeparator;
        var foldersToUpdate = folderTree.FolderIDs
            .Where(x => x.Key.StartsWith(oldPrefix) || x.Key == sourcePath)
            .ToList();

        foreach (var kvp in foldersToUpdate)
        {
            var oldPath = kvp.Key;
            var newFolderPath = oldPath == sourcePath ?
                newPath :
                newPrefix + oldPath.Substring(oldPrefix.Length);

            var folderToUpdate = await GetFolder(oldPath);
            folderToUpdate.Name = newFolderPath;

            if (oldPath == sourcePath)
                folderToUpdate.ParentFolderId = targetParentId;

            var newOffset = WriteFolder(folderToUpdate);

            folderTree.FolderIDs.Remove(oldPath);
            folderTree.FolderIDs[newFolderPath] = kvp.Value;
            folderTree.FolderOffsets[kvp.Value] = newOffset;
        }

        folderTree.FolderHierarchy[sourceFolder.FolderId.ToString()] = targetParentId.ToString();
        WriteFolderTree(folderTree);
    }

    public async Task<List<EmailHashedID>> GetEmails(string path)
    {
        var folder = await GetFolder(path);
        return folder?.EmailIds ?? new List<EmailHashedID>();
    }


    public async Task AddEmailToFolder(string path, EmailHashedID emailId)
    {
        var folder = await GetFolder(path);
        if (folder == null)
            throw new InvalidOperationException($"Folder '{path}' not found");

        if (!folder.EmailIds.Contains(emailId))
        {
            folder.EmailIds.Add(emailId);
            WriteFolder(folder);
        }
    }

    public async Task RemoveEmailFromFolder(string path, EmailHashedID emailId)
    {
        var folder = await GetFolder(path);
        if (folder == null)
            throw new InvalidOperationException($"Folder '{path}' not found");

        if (folder.EmailIds.Remove(emailId))
        {
            WriteFolder(folder);
        }
    }

    public async Task MoveEmail(EmailHashedID emailId, string sourcePath, string targetPath)
    {
        var source = await GetFolder(sourcePath);
        var target = await GetFolder(targetPath);

        if (source == null)
            throw new InvalidOperationException($"Source folder '{sourcePath}' not found");
        if (target == null)
            throw new InvalidOperationException($"Target folder '{targetPath}' not found");

        if (!source.EmailIds.Contains(emailId))
            throw new InvalidOperationException($"Email {emailId} not found in source folder");

        source.EmailIds.Remove(emailId);
        target.EmailIds.Add(emailId);

        WriteFolder(source);
        WriteFolder(target);
    }

    public async Task<List<string>> GetSubfolders(string path)
    {
        ValidatePath(path);
        var folderTree = await GetLatestFolderTree();
        var prefix = path + PathSeparator;

        return folderTree.FolderIDs
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => x.Key)
            .Where(x => x.Substring(prefix.Length).IndexOf(PathSeparator) == -1)
            .ToList();
    }

    // Path handling methods
    private void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty");

        if (path.Contains("//"))
            throw new ArgumentException("Path cannot contain consecutive separators");

        if (path.EndsWith(PathSeparator))
            throw new ArgumentException("Path cannot end with separator");

        // Additional validation as needed
        var invalidChars = Path.GetInvalidPathChars()
            .Concat(new[] { '<', '>', ':', '"', '|', '?', '*' })
            .Where(c => c != PathSeparator);

        if (path.Any(c => invalidChars.Contains(c)))
            throw new ArgumentException("Path contains invalid characters");
    }

    private (string ParentPath, string FolderName) SplitPath(string path)
    {
        var lastSeparatorIndex = path.LastIndexOf(PathSeparator);
        if (lastSeparatorIndex == -1)
            return (string.Empty, path);

        var parentPath = path.Substring(0, lastSeparatorIndex);
        var folderName = path.Substring(lastSeparatorIndex + 1);
        return (parentPath, folderName);
    }

    private string GetFolderName(string path)
    {
        var (_, name) = SplitPath(path);
        return name;
    }

    private string Combine(string path1, string path2)
    {
        if (string.IsNullOrEmpty(path1)) return path2;
        return path1 + PathSeparator + path2;
    }

    // Internal helper methods
    internal async Task<FolderContent> GetFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be empty", nameof(folderName));

        if (inMemoryCache.TryGetValue<FolderContent>(GetFolderCacheKey(folderName), out var cachedFolder))
            return cachedFolder;

        var folderTree = await GetLatestFolderTree();
        if (!folderTree.FolderIDs.TryGetValue(folderName, out var folderId))
            return null;

        if (!folderTree.FolderOffsets.TryGetValue(folderId, out var folderOffset))
            return null;

        var result = await blockManager.ReadBlockAsync(folderOffset);
        if (result?.Content is not FolderContent folder)
            return null;

        inMemoryCache.Set(GetFolderCacheKey(folderName), folder);
        return folder;
    }

    internal async Task<FolderTreeContent> GetLatestFolderTree()
    {
        if (inMemoryCache.TryGetValue<FolderTreeContent>(GetFolderTreeCacheKey(), out var cachedTree))
            return cachedTree;

        var folderTree = await metadataManager.GetFolderTree();
        lock (folderTreeLock)
        {          
            if (folderTree == null)
            {
                folderTree = new FolderTreeContent
                {
                    RootFolderId = -1,
                    FolderHierarchy = new Dictionary<string, string>(),
                    FolderIDs = new Dictionary<string, long>(),
                    FolderOffsets = new Dictionary<long, long>()
                };
            }

            inMemoryCache.Set(GetFolderTreeCacheKey(), folderTree);
            return folderTree;
        }
    }

    internal long WriteFolder(FolderContent folder)
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.Folder,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = folder
        };

        var offset = blockManager.WriteBlock(block);
        inMemoryCache.Set(GetFolderCacheKey(folder.Name), folder);
        return offset;
    }

    internal void WriteFolderTree(FolderTreeContent folderTree)
    {
        var block = new Block
        {
            Header = new BlockHeader
            {
                Type = BlockType.FolderTree,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Version = 1
            },
            Content = folderTree
        };

        var offset = blockManager.WriteBlock(block);
        metadataManager.UpdateFolderTreeOffset(offset);
        inMemoryCache.Set(GetFolderTreeCacheKey(), folderTree);
    }

    private long GetNextFolderId(FolderTreeContent folderTree)
    {
        return folderTree.FolderOffsets.Keys.DefaultIfEmpty(0).Max() + 1;
    }

    private string GetFolderCacheKey(string folderName)
    {
        return $"folder_{folderName}";
    }

    private string GetFolderTreeCacheKey()
    {
        return "folder_tree";
    }

    public void InvalidateCache()
    {
        inMemoryCache.Remove(GetFolderTreeCacheKey());
    }
}