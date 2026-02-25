using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format;

/// <summary>
/// Storage manager interface updated for B+-tree-backed operations.
/// All email operations are async and route through the B+-tree index
/// (BTreeIndex.InsertAsync / LookupAsync / DeleteAsync).
/// </summary>
public interface IStorageManager : IDisposable
{
    /// <summary>
    /// Stores email content as a block, then indexes it in the B+-tree.
    /// Path: write email content block → get BlockLocation → BTreeIndex.InsertAsync(hashedId, location).
    /// </summary>
    Task<Result<EmailHashedID>> AddEmailAsync(byte[] emailContent, string folderName, CancellationToken ct = default);

    /// <summary>
    /// Retrieves an email via B+-tree lookup.
    /// Path: BTreeIndex.LookupAsync(hashedId) → BlockLocation → read email content block.
    /// </summary>
    Task<Result<byte[]>> GetEmailAsync(EmailHashedID emailId, CancellationToken ct = default);

    /// <summary>
    /// Removes an email from the B+-tree index and marks its content block as outdated.
    /// Path: BTreeIndex.DeleteAsync(hashedId) + mark content block outdated.
    /// </summary>
    Task<Result> DeleteEmailAsync(EmailHashedID emailId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an email exists in the B+-tree index.
    /// </summary>
    Task<Result<bool>> ContainsEmailAsync(EmailHashedID emailId, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of emails currently indexed in the B+-tree.
    /// </summary>
    Task<Result<long>> GetEmailCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs a range query over the B+-tree index.
    /// Returns all leaf entries whose keys fall within [startKey, endKey].
    /// </summary>
    Task<Result<List<LeafEntry>>> RangeQueryAsync(EmailHashedID startKey, EmailHashedID endKey, CancellationToken ct = default);

    /// <summary>
    /// Flushes pending B+-tree writes to the underlying storage.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifies the integrity of the B+-tree index (BLAKE3 hashes, chain hashes, structure).
    /// </summary>
    Task<Result> VerifyIntegrityAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a folder in the storage system.
    /// </summary>
    void CreateFolder(string folderName, string parentFolderId = null);

    /// <summary>
    /// Deletes a folder from the storage system.
    /// </summary>
    void DeleteFolder(string folderName, bool deleteEmails = false);

    /// <summary>
    /// Compacts the storage file, reclaiming space from outdated blocks.
    /// </summary>
    void Compact(string outputPath);

    /// <summary>
    /// Invalidates any cached B+-tree nodes or email content.
    /// </summary>
    void InvalidateCache();
}