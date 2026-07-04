namespace EmailDB.Format.V3;

/// <summary>
/// Runtime BlockId-to-offset map hook (EmailDB_FileFormat_Spec.md Section 7).
/// The append path notifies this map for every block it writes so that blocks
/// appended after the latest Checkpoint remain resolvable before they are
/// batch-inserted into the durable BlockLocationIndex.
///
/// Location resolution precedence (spec Section 7): runtime map first, then
/// BlockLocationIndex, then (disaster only) full scan. The concrete map
/// implementation lives in a separate component; <see cref="BlockManager"/>
/// only depends on this interface.
/// </summary>
public interface IBlockOffsetMap
{
    /// <summary>
    /// Called after a block has been successfully appended. The header is a
    /// private copy (safe to retain). Duplicate BlockIds are legitimate (new
    /// versions of Metadata/KeyStore/directory blocks); later file position
    /// supersedes earlier.
    /// </summary>
    /// <param name="header">Copy of the appended block's header.</param>
    /// <param name="offset">File offset of the block's first header byte.</param>
    /// <param name="totalBlockLength">Entire on-disk block size including the footer.</param>
    void OnBlockAppended(BlockHeader header, long offset, long totalBlockLength);
}
