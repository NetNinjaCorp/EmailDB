# Integration Patterns and Workflows

## Overview

This document describes the recommended integration patterns and workflows for working with the EmailDB system components. Following these patterns will ensure proper system operation, data integrity, and performance.

## Component Integration Map

```
┌───────────────────┐
│   Application     │
└─────────┬─────────┘
          │
┌─────────┴─────────┐
│   EmailManager    │
└───────┬─────┬─────┘
        │     │
┌───────┴───┐ │ ┌───────────────┐
│ FolderMgr │ │ │ SegmentMgr    │
└───────┬───┘ │ └───────┬───────┘
        │     │         │
        │ ┌───┴─────┐   │
        │ │ MetaMgr │   │
        │ └───┬─────┘   │
        │     │         │
┌───────┴─────┴─────────┴───────┐
│        CacheManager           │
└───────────────┬───────────────┘
                │
┌───────────────┴───────────────┐
│       RawBlockManager         │
└───────────────────────────────┘
```

## Core Workflows

### 1. Email Addition Workflow

```
EmailManager.AddEmailAsync()
  │
  ├─▶ Generate EmailHashedID
  │
  ├─▶ Create EnhancedEmailContent
  │
  ├─▶ Store in ZoneTree
  │    │
  │    └─▶ ZoneTree uses custom storage via SegmentManager
  │
  └─▶ FolderManager.AddEmailToFolder()
       │
       ├─▶ Get folder via CacheManager
       │
       ├─▶ Update FolderContent with new email ID
       │
       └─▶ Write folder via BlockManager
            │
            └─▶ RawBlockManager persists the block
```

### 2. Folder Creation Workflow

```
FolderManager.CreateFolder()
  │
  ├─▶ Validate path
  │
  ├─▶ Get FolderTreeContent via CacheManager
  │
  ├─▶ Create new FolderContent
  │
  ├─▶ Write folder via BlockManager
  │
  ├─▶ Update FolderTreeContent with new folder
  │
  └─▶ Write FolderTree via BlockManager
       │
       ├─▶ RawBlockManager persists the block
       │
       └─▶ MetadataManager.UpdateFolderTreeOffset()
            │
            └─▶ Update and write metadata
```

### 3. Email Retrieval Workflow

```
EmailManager.GetEmailContentAsync()
  │
  └─▶ ZoneTree TryGet operation
       │
       └─▶ ZoneTree uses custom storage via SegmentManager
            │
            ├─▶ Check cache via CacheManager
            │
            └─▶ If not in cache, read via RawBlockManager
```

### 4. Email Search Workflow

```
EmailManager.SearchEmailsAsync()
  │
  └─▶ Search engine query
       │
       └─▶ ZoneTree-based index lookup
            │
            └─▶ Return matching EmailHashedIDs
```

## Integration Patterns

### Manager Initialization Pattern

```csharp
// Adding an email to a folder
public async Task AddEmailToFolderAsync(byte[] emlBytes, string folderPath)
{
    // 1. Ensure folder exists
    if (!await folderManager.FolderExistsAsync(folderPath))
        await folderManager.CreateFolderAsync(folderPath);
    
    // 2. Parse email and create ID
    var message = new MimeMessage(new MemoryStream(emlBytes));
    var emailId = new EmailHashedID(message);
    
    // 3. Check if email already exists
    if (emailIndex.TryGet(emailId, out var _))
        throw new InvalidOperationException("Email already exists");
    
    // 4. Create enhanced content
    var enhancedContent = new EnhancedEmailContent
    {
        StrSubject = message.Subject ?? string.Empty,
        StrFrom = message.From.ToString() ?? string.Empty,
        // Set other properties...
        RawEmailContent = emlBytes
    };
    
    // 5. Store in index
    emailIndex.TryAdd(emailId, enhancedContent, out long opIndex);
    
    // 6. Add to folder
    await folderManager.AddEmailToFolderAsync(folderPath, emailId);
}
```

### 2. Moving an Email Between Folders

```csharp
// Moving an email between folders
public async Task MoveEmailAsync(EmailHashedID emailId, string sourceFolder, string targetFolder)
{
    // 1. Ensure both folders exist
    if (!await folderManager.FolderExistsAsync(sourceFolder))
        throw new ArgumentException("Source folder does not exist");
    
    if (!await folderManager.FolderExistsAsync(targetFolder))
        throw new ArgumentException("Target folder does not exist");
    
    // 2. Perform the move operation
    await folderManager.MoveEmailAsync(emailId, sourceFolder, targetFolder);
    
    // 3. Update storage manager records if needed
    storageManager.MoveEmail(emailId, sourceFolder, targetFolder);
}
```

### 3. Searching for Emails

```csharp
// Searching for emails with specific criteria
public async Task<List<EmailHashedID>> SearchEmailsAsync(string searchTerm, SearchField field)
{
    var results = new List<EmailHashedID>();
    
    // Different search strategies based on field
    switch (field)
    {
        case SearchField.Subject:
            // Use search engine to find by subject
            results = await searchEngine.SearchByFieldAsync("subject", searchTerm);
            break;
            
        case SearchField.From:
            // Use search engine to find by sender
            results = await searchEngine.SearchByFieldAsync("from", searchTerm);
            break;
            
        case SearchField.Content:
            // Full text search in content
            results = await searchEngine.FullTextSearchAsync(searchTerm);
            break;
            
        // Other cases...
    }
    
    return results;
}
```

### 4. Compacting Storage

```csharp
// Compacting storage to remove outdated blocks
public async Task CompactStorageAsync(string outputPath)
{
    // 1. Prepare for compaction
    await storageManager.PrepareForCompactionAsync();
    
    // 2. Get current state
    var metadata = await metadataManager.GetCachedMetadataAsync();
    var folderTree = await folderManager.GetLatestFolderTreeAsync();
    
    // 3. Create new file
    using var compactedStorage = new StorageManager(outputPath, createNew: true);
    
    // 4. Copy active folder structure
    await folderManager.CopyFoldersToAsync(compactedStorage.folderManager);
    
    // 5. Copy active segments
    foreach (var segmentId in metadata.SegmentOffsets.Keys)
    {
        if (!metadata.OutdatedOffsets.Contains(metadata.SegmentOffsets[segmentId]))
        {
            await segmentManager.CopySegmentToAsync(
                segmentId, 
                compactedStorage.segmentManager);
        }
    }
    
    // 6. Finalize compaction
    await compactedStorage.FinalizeAsync();
    
    // 7. Replace original with compacted version
    storageManager.ReplaceWithCompacted(outputPath);
}
```

## Integration with ZoneTree

The EmailDB system uses ZoneTree for efficient indexing and searching:

```csharp
// Creating and configuring ZoneTree
public void InitializeZoneTree()
{
    // Create factory with EmailDB storage integration
    var factory = new EmailDBZoneTreeFactory<EmailHashedID, EnhancedEmailContent>(
        storageManager);
    
    // Configure ZoneTree
    factory.SetComparer(new EmailHashedID())
           .SetKeySerializer(new EmailHashedID())
           .SetMarkValueDeletedDelegate(value => 
           {
               // Mark value as deleted logic
               return value; // Return modified value
           });
    
    // Create or open ZoneTree
    if (!factory.CreateZoneTree("email_index"))
        throw new Exception("Failed to create email index");
        
    emailIndex = factory.OpenOrCreate();
    
    // Initialize search engine with ZoneTree
    emailSearchEngine = new HashedSearchEngine<EmailHashedID>(
        new IndexOfTokenRecordPreviousToken<EmailHashedID, ulong>());
}
```

## Error Handling Patterns

### Result Pattern

```csharp
// Using Result pattern for operations
public async Task<Result<EmailHashedID>> TryAddEmailAsync(byte[] emlBytes, string folderPath)
{
    try
    {
        // Perform validation
        if (emlBytes == null || emlBytes.Length == 0)
            return Result<EmailHashedID>.Failure("Email bytes cannot be empty");
            
        if (string.IsNullOrWhiteSpace(folderPath))
            return Result<EmailHashedID>.Failure("Folder path cannot be empty");
        
        // Actual processing
        var message = new MimeMessage(new MemoryStream(emlBytes));
        var emailId = new EmailHashedID(message);
        
        // Add email logic...
        
        return Result<EmailHashedID>.Success(emailId);
    }
    catch (Exception ex)
    {
        return Result<EmailHashedID>.Failure($"Failed to add email: {ex.Message}");
    }
}
```

### Transaction Safety

```csharp
// Ensuring transaction safety
public async Task<Result> MoveEmailWithTransactionAsync(
    EmailHashedID emailId, 
    string sourceFolder, 
    string targetFolder)
{
    // Acquire transaction lock
    await transactionSemaphore.WaitAsync();
    
    try
    {
        // Source folder operations
        var sourceResult = await folderManager.RemoveEmailFromFolderAsync(
            sourceFolder, emailId);
        if (sourceResult.IsFailure)
            return Result.Failure(sourceResult.Error);
        
        // Target folder operations
        var targetResult = await folderManager.AddEmailToFolderAsync(
            targetFolder, emailId);
        if (targetResult.IsFailure)
        {
            // Rollback on failure
            await folderManager.AddEmailToFolderAsync(sourceFolder, emailId);
            return Result.Failure(targetResult.Error);
        }
        
        return Result.Success();
    }
    catch (Exception ex)
    {
        return Result.Failure($"Transaction failed: {ex.Message}");
    }
    finally
    {
        // Always release transaction lock
        transactionSemaphore.Release();
    }
}
```

## Performance Optimization Patterns

### Batch Processing

```csharp
// Batch processing multiple emails
public async Task<Result> ImportEmailBatchAsync(
    List<byte[]> emlBytesList, 
    string folderPath)
{
    var successCount = 0;
    var errors = new List<string>();
    
    // Process in batches for better performance
    foreach (var batch in emlBytesList.Chunk(100))
    {
        var tasks = batch.Select(async emlBytes => 
        {
            try
            {
                await AddEmailToFolderAsync(emlBytes, folderPath);
                Interlocked.Increment(ref successCount);
                return true;
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex.Message);
                }
                return false;
            }
        });
        
        // Wait for batch to complete
        await Task.WhenAll(tasks);
    }
    
    if (errors.Count > 0)
        return Result.Failure($"Imported {successCount} emails with {errors.Count} errors: {string.Join("; ", errors.Take(5))}");
    
    return Result.Success();
}
```

### Parallel Processing with Synchronization

```csharp
// Parallel processing with proper synchronization
public async Task<Result> ProcessFoldersInParallelAsync(List<string> folderPaths, Func<string, Task> processor)
{
    var options = new ParallelOptions
    {
        MaxDegreeOfParallelism = Environment.ProcessorCount
    };
    
    var exceptions = new ConcurrentBag<Exception>();
    
    await Parallel.ForEachAsync(folderPaths, options, async (folderPath, token) =>
    {
        try
        {
            await processor(folderPath);
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    });
    
    if (exceptions.Count > 0)
        return Result.Failure($"Encountered {exceptions.Count} errors during processing");
    
    return Result.Success();
}
```
// Proper initialization order
var rawBlockManager = new RawBlockManager(filePath);
var cacheManager = new CacheManager(rawBlockManager);
var metadataManager = new MetadataManager(cacheManager);
var folderManager = new FolderManager(cacheManager, metadataManager);
var segmentManager = new SegmentManager(cacheManager, metadataManager);
var emailManager = new EmailManager(folderManager, segmentManager);
```

### Content Update Pattern

```csharp
// Pattern for updating content
var content = await manager.GetContentAsync(...);
// Modify content
content.Property = newValue;
// Write back through the proper manager
await manager.UpdateContentAsync(content);
// Cache is updated automatically
```

### Cache-First Access Pattern

```csharp
// Always go through the appropriate manager
var folder = await folderManager.GetFolder(folderPath);
// Never try to access RawBlockManager directly
// Never bypass CacheManager for performance-critical operations
```

### Transaction Pattern

```csharp
// For operations requiring multiple updates
lock (transactionLock)
{
    // Step 1: Retrieve current state
    var folderTree = await folderManager.GetLatestFolderTree();
    var folder = await folderManager.GetFolder(folderPath);
    
    // Step 2: Make changes
    folder.EmailIds.Add(emailId);
    folderTree.FolderOffsets[folder.FolderId] = newOffset;
    
    // Step 3: Persist changes in correct order
    var folderOffset = folderManager.WriteFolder(folder);
    folderManager.WriteFolderTree(folderTree);
    
    // Step 4: Update metadata if needed
    metadataManager.UpdateFolderTreeOffset(newFolderTreeOffset);
}
```

## Common Scenarios

### 1. Adding an Email to a Folder

```csharp