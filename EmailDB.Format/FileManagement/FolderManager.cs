using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using System.Text.RegularExpressions;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages folder operations including folder hierarchy, email organization, and persistence.
/// </summary>
public partial class FolderManager
{
    protected readonly CacheManager cacheManager;
    protected readonly MetadataManager metadataManager;
    private readonly object folderTreeLock = new object();
    protected const char PathSeparator = '\\';
    
    // Phase 2 fields for block storage support
    protected RawBlockManager _blockManager;
    protected iBlockContentSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the FolderManager class.
    /// </summary>
    /// <param name="cacheManager">The cache manager for folder content caching.</param>
    /// <param name="metadataManager">The metadata manager for system metadata operations.</param>
    public FolderManager(CacheManager cacheManager, MetadataManager metadataManager)
    {
        this.cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        this.metadataManager = metadataManager ?? throw new ArgumentNullException(nameof(metadataManager));
    }
    
    /// <summary>
    /// Initializes a new instance of the FolderManager class with block storage support.
    /// </summary>
    /// <param name="cacheManager">The cache manager for folder content caching.</param>
    /// <param name="metadataManager">The metadata manager for system metadata operations.</param>
    /// <param name="blockManager">The raw block manager for block storage.</param>
    /// <param name="serializer">The block content serializer.</param>
    public FolderManager(
        CacheManager cacheManager, 
        MetadataManager metadataManager,
        RawBlockManager blockManager,
        iBlockContentSerializer serializer) : this(cacheManager, metadataManager)
    {
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <summary>
    /// Creates a new folder at the specified path.
    /// </summary>
    /// <param name="path">The full path of the folder to create.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Result> CreateFolderAsync(string path)
    {
        try
        {
            ValidatePath(path);

            var folderTree = await cacheManager.GetCachedFolderTree();
            if (folderTree == null)
            {
                return Result.Failure("Failed to retrieve folder tree from cache");
            }

            // Check if path already exists
            if (folderTree.FolderIDs.ContainsKey(path))
                return Result.Failure($"Folder '{path}' already exists");

            var (parentPath, folderName) = SplitPath(path);

            // Get parent folder ID
            long parentFolderId = 0;
            if (!string.IsNullOrEmpty(parentPath))
            {
                if (!folderTree.FolderIDs.TryGetValue(parentPath, out parentFolderId))
                    return Result.Failure($"Parent folder '{parentPath}' not found");
            }
            else if (folderTree.RootFolderId != -1)
            {
                // Use existing root
                parentFolderId = folderTree.RootFolderId;
            }
            // else parentFolderId remains 0 (no parent)

            // Create new folder
            var folderId = GetNextFolderId(folderTree);
            var folder = new FolderContent
            {
                FolderId = folderId,
                ParentFolderId = parentFolderId,
                Name = path, // Store the full path as the name
                EmailIds = new List<EmailHashedID>()
            };

            // Write folder
            var offset = await cacheManager.UpdateFolder(path, folder);

            // Update folder tree
            folderTree.FolderIDs[path] = folderId;
            folderTree.FolderOffsets[folderId] = offset;

            // Set as root if this is the first folder
            if (folderTree.RootFolderId == -1 && string.IsNullOrEmpty(parentPath))
            {
                folderTree.RootFolderId = folderId;
            }
            else if (parentFolderId != 0)
            {
                folderTree.FolderHierarchy[folderId.ToString()] = parentFolderId.ToString();
            }

            // Write updated folder tree
            await cacheManager.UpdateFolderTree(folderTree);

            // Update metadata with new folder tree offset
            var metadata = await cacheManager.GetCachedMetadata();
            if (metadata != null)
            {
                var header = await cacheManager.LoadHeaderContent();
                metadata.FolderTreeOffset = header.FirstFolderTreeOffset;
                await cacheManager.UpdateMetadata(metadata);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to create folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a folder at the specified path.
    /// </summary>
    /// <param name="path">The path of the folder to delete.</param>
    /// <param name="deleteContents">If true, deletes the folder even if it contains emails.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task DeleteFolderAsync(string path, bool deleteContents = false)
    {
        ValidatePath(path);
        var folder = await GetFolderAsync(path);
        if (folder == null)
            return;

        var folderTree = await cacheManager.GetCachedFolderTree();

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

        await cacheManager.UpdateFolderTree(folderTree);

        // Invalidate cache
        cacheManager.InvalidateCache();
    }

    /// <summary>
    /// Moves a folder from the source path to a new parent folder.
    /// </summary>
    /// <param name="sourcePath">The current path of the folder.</param>
    /// <param name="targetParentPath">The path of the new parent folder.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task MoveFolderAsync(string sourcePath, string targetParentPath)
    {
        ValidatePath(sourcePath);
        ValidatePath(targetParentPath);

        var sourceFolder = await GetFolderAsync(sourcePath);
        if (sourceFolder == null)
            throw new InvalidOperationException($"Folder '{sourcePath}' not found");

        var folderTree = await cacheManager.GetCachedFolderTree();

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

            var folderToUpdate = await GetFolderAsync(oldPath);
            folderToUpdate.Name = newFolderPath;

            if (oldPath == sourcePath)
                folderToUpdate.ParentFolderId = targetParentId;

            var newOffset = await cacheManager.UpdateFolder(newFolderPath, folderToUpdate);

            folderTree.FolderIDs.Remove(oldPath);
            folderTree.FolderIDs[newFolderPath] = kvp.Value;
            folderTree.FolderOffsets[kvp.Value] = newOffset;
        }

        folderTree.FolderHierarchy[sourceFolder.FolderId.ToString()] = targetParentId.ToString();
        await cacheManager.UpdateFolderTree(folderTree);
    }

    /// <summary>
    /// Gets all emails in a specified folder.
    /// </summary>
    /// <param name="path">The folder path.</param>
    /// <returns>A list of email IDs in the folder.</returns>
    public async Task<List<EmailHashedID>> GetEmailsAsync(string path)
    {
        var folder = await GetFolderAsync(path);
        return folder?.EmailIds ?? new List<EmailHashedID>();
    }

    /// <summary>
    /// Checks if a folder exists at the specified path.
    /// </summary>
    /// <param name="path">The folder path to check.</param>
    /// <returns>True if the folder exists, false otherwise.</returns>
    public async Task<bool> FolderExistsAsync(string path)
    {
        ValidatePath(path);
        var folderTree = await cacheManager.GetCachedFolderTree();
        return folderTree.FolderIDs.ContainsKey(path);
    }

    /// <summary>
    /// Adds an email to a folder.
    /// </summary>
    /// <param name="path">The folder path.</param>
    /// <param name="emailId">The email ID to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddEmailToFolderAsync(string path, EmailHashedID emailId)
    {
        var folder = await GetFolderAsync(path);
        if (folder == null)
            throw new InvalidOperationException($"Folder '{path}' not found");

        if (!folder.EmailIds.Contains(emailId))
        {
            folder.EmailIds.Add(emailId);
            await cacheManager.UpdateFolder(path, folder);
        }
    }

    // RemoveEmailFromFolderAsync is now implemented in FolderManager.BlockStorage.cs

    /// <summary>
    /// Moves an email from one folder to another.
    /// </summary>
    /// <param name="emailId">The email ID to move.</param>
    /// <param name="sourcePath">The source folder path.</param>
    /// <param name="targetPath">The target folder path.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task MoveEmailAsync(EmailHashedID emailId, string sourcePath, string targetPath)
    {
        var source = await GetFolderAsync(sourcePath);
        var target = await GetFolderAsync(targetPath);

        if (source == null)
            throw new InvalidOperationException($"Source folder '{sourcePath}' not found");
        if (target == null)
            throw new InvalidOperationException($"Target folder '{targetPath}' not found");

        if (!source.EmailIds.Contains(emailId))
            throw new InvalidOperationException($"Email {emailId} not found in source folder");

        source.EmailIds.Remove(emailId);
        target.EmailIds.Add(emailId);

        await cacheManager.UpdateFolder(sourcePath, source);
        await cacheManager.UpdateFolder(targetPath, target);

    }

    /// <summary>
    /// Gets all direct subfolders of a specified folder.
    /// </summary>
    /// <param name="path">The parent folder path.</param>
    /// <returns>A list of subfolder paths.</returns>
    public async Task<List<string>> GetSubfoldersAsync(string path)
    {
        ValidatePath(path);
        var folderTree = await cacheManager.GetCachedFolderTree();
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
    /// <summary>
    /// Gets a folder by its path.
    /// </summary>
    /// <param name="folderName">The folder path.</param>
    /// <returns>The folder content, or null if not found.</returns>
    internal async Task<FolderContent> GetFolderAsync(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be empty", nameof(folderName));

        // Try to get from cache via CacheManager
        var cachedFolder = await cacheManager.GetCachedFolder(folderName);
        if (cachedFolder != null)
            return cachedFolder;       

        return null;
    }


    private long GetNextFolderId(FolderTreeContent folderTree)
    {
        return folderTree.FolderOffsets.Keys.DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>
    /// Invalidates the folder cache.
    /// </summary>
    public void InvalidateCache()
    {
        cacheManager.InvalidateCache();
    }
}