# Metadata Persistence Analysis

## Issue Summary
The EmailDatabase is not persisting the `email_ids_index` metadata between sessions. After adding emails and closing the database, reopening results in "email_ids_index = NOT_FOUND" and no emails being found, despite blocks existing in storage.

## Root Cause Analysis

### 1. What Gets Written to JSON Metadata Files
From the test output, we can see:
- Files are created: `emails_.json_0`, `search_.json_0`, `folders_.json_0`, `metadata_.json_0`
- These files are being written with approximately 1302 bytes each
- The Replace operation shows successful file operations

### 2. Why "email_ids_index = NOT_FOUND" After Reopening
The key issue is in the ZoneTreeFactory configuration:
```cs
// options.FileStreamProvider = new EmailDBFileStreamProvider(_blockManager);
```
This line is commented out, meaning ZoneTree is likely using its default file system provider instead of our custom EmailDBFileStreamProvider that integrates with the block storage.

### 3. What ZoneTree Expects vs What We're Saving
ZoneTree expects:
- Metadata files to be persisted to disk using its file stream provider
- These files contain segment information and key-value mappings
- On reopening, it reads these files to reconstruct the in-memory state

What's actually happening:
- Our EmailDBFileStreamProvider correctly handles file operations
- Data is being written to blocks in the RawBlockManager
- But ZoneTree isn't configured to use our provider, so it's using default file operations
- The metadata is being saved to actual files on disk (in the temp directory) but not in our block storage

## The Fix

The solution is to properly configure ZoneTree to use our EmailDBFileStreamProvider. However, the exact property name for setting the file stream provider needs to be determined from the ZoneTree API.

### Immediate Workaround
Since we can't find the correct property name, we need to investigate alternative approaches:
1. Check if ZoneTree has a different configuration mechanism
2. Look for examples of custom file providers in ZoneTree
3. Consider if the file provider needs to be set at a different level (e.g., in the factory constructor)

### Possible Solutions
1. **Find the correct property**: Research ZoneTree documentation or source to find how to set a custom IFileStreamProvider
2. **Use a different approach**: Instead of trying to intercept file operations, we could:
   - Let ZoneTree use its default file operations
   - Store our own metadata separately in blocks
   - Sync between ZoneTree's files and our blocks
3. **Hybrid approach**: Use ZoneTree's persistence for metadata and our block storage for actual email data

## Next Steps
1. Investigate ZoneTree's source code or documentation to find the correct way to set a custom file stream provider
2. Test with a simple example to verify the file stream provider integration
3. Update the ZoneTreeFactory configuration once the correct property is found