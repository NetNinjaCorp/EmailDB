namespace EmailDB.Format.V3;

/// <summary>
/// One live block copied by side-file compaction (EmailDB_FileFormat_Spec.md
/// Section 11.2, docs/Compaction.md Section 2): the durable <see cref="BlockId"/>
/// (unchanged — compaction moves blocks, never rewrites their logical content),
/// the <see cref="SourceOffset"/> it lived at in the original file, and the
/// <see cref="NewOffset"/>/<see cref="NewLength"/> it now occupies in the
/// <c>.emdb.compact</c> side file.
///
/// <para>This is the hand-off record between the copy pass (US-EMDB-89-6) and the
/// two follow-on slices: the location-index rebuild (US-EMDB-89-7) batch-inserts
/// one <c>BlockId → (NewOffset, NewLength)</c> entry per copied block into the fresh
/// index, and the fresh Checkpoint remaps each named root (folder tree, primary
/// index, metadata, KeyStore, secondary indexes) from its source offset to its
/// <see cref="NewOffset"/>. Because <see cref="BlockId"/> is preserved, every
/// persisted ULID pointer stays valid across the move (spec Section 11.2).</para>
/// </summary>
/// <param name="BlockId">The block's 16-byte ULID, identical in the source and side file.</param>
/// <param name="SourceOffset">File offset the block was read from in the original file.</param>
/// <param name="NewOffset">File offset the verbatim copy was written to in the side file.</param>
/// <param name="NewLength">Total on-disk length of the block (identical to the source; a verbatim copy).</param>
public readonly record struct CopiedBlock(byte[] BlockId, long SourceOffset, long NewOffset, long NewLength);
