# EmailDB Migration Guide: Traditional to Hybrid Architecture

## Overview

This guide helps you migrate from the traditional EmailDB format (one email per segment block) to the new hybrid architecture with append-only storage and hash chains.

## Architecture Comparison

### Traditional EmailDB
```
┌─────────────────┐
│ Metadata Block  │ ← Tracks all blocks
├─────────────────┤
│ Folder Block 1  │ ← Inbox emails
├─────────────────┤
│ Folder Block 2  │ ← Sent emails
├─────────────────┤
│ Segment Block 1 │ ← Email 1 (10KB)
├─────────────────┤
│ Segment Block 2 │ ← Email 2 (5KB)
├─────────────────┤
│ Segment Block 3 │ ← Email 3 (8KB)
└─────────────────┘
```

### New Hybrid Architecture
```
Append-Only Storage:          ZoneTree Indexes:
┌─────────────────┐          ┌──────────────────┐
│ Data Block 1    │          │ MessageId Index  │
│ - Email 1       │          │ Folder Index     │
│ - Email 2       │          │ FullText Index   │
│ - Email 3       │          │ Metadata Index   │
├─────────────────┤          └──────────────────┘
│ Data Block 2    │
│ - Email 4-10    │          Hash Chain:
├─────────────────┤          ┌──────────────────┐
│ Data Block 3    │ ←────────│ Block 3 Hash     │
│ - Email 11-25   │          └──────────────────┘
└─────────────────┘
```

## Migration Steps

### Step 1: Assess Current Database

```csharp
public class MigrationAssessment
{
    public async Task<AssessmentReport> AssessDatabase(string emailDbPath)
    {
        var report = new AssessmentReport();
        var blockManager = new RawBlockManager(emailDbPath, isReadOnly: true);
        
        // Count blocks by type
        foreach (var (location, block) in blockManager.WalkBlocks())
        {
            report.TotalBlocks++;
            report.BlocksByType[block.Type]++;
            
            if (block.Type == BlockType.Segment)
            {
                report.TotalEmails++;
                report.TotalDataSize += block.Payload.Length;
            }
        }
        
        // Estimate new size
        report.EstimatedNewSize = report.TotalDataSize * 1.03; // 3% for indexes
        report.EstimatedMigrationTime = TimeSpan.FromSeconds(report.TotalEmails * 0.01);
        
        return report;
    }
}
```

### Step 2: Create Migration Plan

```csharp
public class MigrationPlan
{
    public string SourcePath { get; set; }
    public string DestinationPath { get; set; }
    public string IndexPath { get; set; }
    public bool EnableHashChain { get; set; } = true;
    public int BlockSizeKB { get; set; } = 512;
    public bool VerifyAfterMigration { get; set; } = true;
    public bool KeepOriginal { get; set; } = true;
}
```

### Step 3: Execute Migration

```csharp
public class EmailDbMigrator
{
    private readonly MigrationPlan _plan;
    private readonly IProgress<MigrationProgress> _progress;
    
    public async Task<MigrationResult> MigrateAsync()
    {
        // Initialize new hybrid store
        var hybridStore = new HybridEmailStore(
            _plan.DestinationPath,
            _plan.IndexPath,
            blockSizeThreshold: _plan.BlockSizeKB * 1024,
            enableHashChain: _plan.EnableHashChain
        );
        
        // Open traditional database
        var oldDb = new RawBlockManager(_plan.SourcePath, isReadOnly: true);
        var folders = new Dictionary<string, List<EmailHashedID>>();
        
        // Phase 1: Extract folder structure
        foreach (var (_, block) in oldDb.WalkBlocks())
        {
            if (block.Type == BlockType.Folder)
            {
                var folderContent = DeserializeFolderContent(block.Payload);
                folders[folderContent.Name] = folderContent.EmailIds;
            }
        }
        
        // Phase 2: Migrate emails
        var emailMap = new Dictionary<EmailHashedID, EmailId>();
        var migratedCount = 0;
        
        foreach (var (_, block) in oldDb.WalkBlocks())
        {
            if (block.Type == BlockType.Segment)
            {
                var segmentContent = DeserializeSegmentContent(block.Payload);
                var oldEmailId = new EmailHashedID { Value = (ulong)segmentContent.SegmentId };
                
                // Find folder for this email
                var folder = folders.FirstOrDefault(f => f.Value.Contains(oldEmailId)).Key 
                           ?? "migrated";
                
                // Extract email metadata if available
                var metadata = ExtractMetadata(segmentContent);
                
                // Store in new format
                var newEmailId = await hybridStore.StoreEmailAsync(
                    messageId: metadata?.MessageId ?? $"migrated_{oldEmailId}",
                    folder: folder,
                    content: segmentContent.SegmentData,
                    subject: metadata?.Subject,
                    from: metadata?.From,
                    to: metadata?.To,
                    date: metadata?.Date ?? DateTime.UtcNow
                );
                
                emailMap[oldEmailId] = newEmailId;
                migratedCount++;
                
                _progress?.Report(new MigrationProgress
                {
                    EmailsMigrated = migratedCount,
                    PercentComplete = (migratedCount * 100) / totalEmails
                });
            }
        }
        
        // Phase 3: Verify migration if requested
        if (_plan.VerifyAfterMigration)
        {
            await VerifyMigrationAsync(oldDb, hybridStore, emailMap);
        }
        
        return new MigrationResult
        {
            Success = true,
            EmailsMigrated = migratedCount,
            NewDatabaseSize = new FileInfo(_plan.DestinationPath).Length,
            MigrationDuration = stopwatch.Elapsed
        };
    }
}
```

### Step 4: Verification

```csharp
public async Task VerifyMigrationAsync(
    RawBlockManager oldDb, 
    HybridEmailStore newDb,
    Dictionary<EmailHashedID, EmailId> emailMap)
{
    var errors = new List<string>();
    
    foreach (var (oldId, newId) in emailMap)
    {
        // Read from old database
        var oldBlock = await ReadSegmentBlock(oldDb, oldId);
        var oldData = oldBlock.Payload;
        
        // Read from new database
        var (newData, metadata) = await newDb.GetEmailAsync(newId);
        
        // Compare
        if (!oldData.SequenceEqual(newData))
        {
            errors.Add($"Email {oldId} data mismatch");
        }
    }
    
    if (errors.Any())
    {
        throw new MigrationException($"Verification failed: {string.Join(", ", errors)}");
    }
}
```

## Migration Scenarios

### Scenario 1: Simple Migration

```csharp
// Minimal migration with defaults
var migrator = new EmailDbMigrator(new MigrationPlan
{
    SourcePath = "emails.db",
    DestinationPath = "emails_new.db",
    IndexPath = "indexes/"
});

var result = await migrator.MigrateAsync();
Console.WriteLine($"Migrated {result.EmailsMigrated} emails successfully");
```

### Scenario 2: Large Database with Progress

```csharp
var plan = new MigrationPlan
{
    SourcePath = "large_email_archive.db",
    DestinationPath = "archive_hybrid.db",
    IndexPath = "archive_indexes/",
    BlockSizeKB = 2048,  // Larger blocks for archive
    EnableHashChain = true
};

var progress = new Progress<MigrationProgress>(p =>
{
    Console.WriteLine($"Progress: {p.PercentComplete}% ({p.EmailsMigrated} emails)");
});

var migrator = new EmailDbMigrator(plan, progress);
await migrator.MigrateAsync();
```

### Scenario 3: Incremental Migration

```csharp
public async Task IncrementalMigrationAsync(DateTime since)
{
    var oldDb = new RawBlockManager("emails.db", isReadOnly: true);
    var hybridStore = new HybridEmailStore("emails_hybrid.db", "indexes/");
    
    foreach (var (_, block) in oldDb.WalkBlocks())
    {
        if (block.Type == BlockType.Segment && block.Timestamp > since.Ticks)
        {
            // Migrate only new emails
            await MigrateSegmentAsync(block, hybridStore);
        }
    }
}
```

## Performance Optimization

### 1. Batch Processing

```csharp
public async Task BatchMigrateAsync()
{
    const int batchSize = 1000;
    var batch = new List<(Block block, string folder)>();
    
    foreach (var (_, block) in oldDb.WalkBlocks())
    {
        if (block.Type == BlockType.Segment)
        {
            batch.Add((block, DetermineFolder(block)));
            
            if (batch.Count >= batchSize)
            {
                await ProcessBatchAsync(batch);
                batch.Clear();
            }
        }
    }
    
    if (batch.Any())
    {
        await ProcessBatchAsync(batch);
    }
}
```

### 2. Parallel Migration

```csharp
public async Task ParallelMigrateAsync()
{
    var segments = oldDb.WalkBlocks()
        .Where(b => b.Item2.Type == BlockType.Segment)
        .ToList();
    
    await Parallel.ForEachAsync(segments, 
        new ParallelOptions { MaxDegreeOfParallelism = 4 },
        async (segment, ct) =>
        {
            await MigrateSegmentAsync(segment.Item2, hybridStore);
        });
}
```

## Post-Migration Steps

### 1. Update Application Configuration

```json
{
  "EmailDB": {
    "Type": "Hybrid",
    "DataPath": "emails_hybrid.db",
    "IndexPath": "indexes/",
    "BlockSizeKB": 512,
    "EnableHashChain": true
  }
}
```

### 2. Update Access Code

```csharp
// Old code
var storage = new StorageManager("emails.db");
var email = storage.GetEmail(emailId);

// New code
var storage = new HybridEmailStore("emails_hybrid.db", "indexes/");
var (content, metadata) = await storage.GetEmailAsync(emailId);
```

### 3. Set Up Monitoring

```csharp
// Monitor storage efficiency
var stats = hybridStore.GetStats();
Console.WriteLine($"Storage efficiency: {stats.Efficiency:P}");
Console.WriteLine($"Average block utilization: {stats.AvgBlockUtilization:P}");

// Verify integrity (if using hash chain)
if (enableHashChain)
{
    var integrity = await hybridStore.VerifyIntegrityAsync();
    if (!integrity.IsValid)
    {
        await AlertAdministrator(integrity);
    }
}
```

## Rollback Plan

If issues arise:

```csharp
public class MigrationRollback
{
    public async Task RollbackAsync(MigrationPlan plan)
    {
        // 1. Stop using new database
        // 2. Delete new files
        if (File.Exists(plan.DestinationPath))
            File.Delete(plan.DestinationPath);
        
        if (Directory.Exists(plan.IndexPath))
            Directory.Delete(plan.IndexPath, recursive: true);
        
        // 3. Restore original configuration
        await RestoreConfiguration("emails.db");
        
        // 4. Verify original database
        var oldDb = new RawBlockManager(plan.SourcePath);
        var blockCount = await oldDb.ScanFile();
        Console.WriteLine($"Original database restored: {blockCount.Count} blocks");
    }
}
```

## Common Issues and Solutions

### Issue 1: Migration Too Slow
**Solution**: Increase batch size and parallelism
```csharp
plan.BatchSize = 5000;
plan.MaxParallelism = 8;
```

### Issue 2: Disk Space Constraints
**Solution**: Use in-place migration
```csharp
// Migrate to temporary, then swap
await migrator.MigrateAsync(tempPath);
File.Move(originalPath, backupPath);
File.Move(tempPath, originalPath);
```

### Issue 3: Memory Usage
**Solution**: Use streaming migration
```csharp
// Process one email at a time instead of loading batches
await StreamingMigrateAsync();
```

## Summary

The migration process:
1. **Preserves all email data** exactly as stored
2. **Improves storage efficiency** from ~95% to ~99.6%
3. **Adds cryptographic integrity** via hash chains
4. **Enables faster searches** through dedicated indexes
5. **Maintains backward compatibility** through migration tools

The new hybrid architecture provides significant benefits while maintaining data integrity throughout the migration process.