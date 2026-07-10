namespace EmailDB.Format.V3;

/// <summary>
/// Writes and reads <see cref="FolderDeltaLog"/> blocks (BlockType 13) through the v3 block pipeline,
/// mirroring <see cref="FolderPageStore"/> (Pack → Zstd → Encrypt → Append,
/// EmailDB_FileFormat_Spec.md Section 4.4). Delta blocks buffer Add/Delete/FlagChange mutations and
/// carry subjects/senders (in Add records), so they encrypt under the Default policy like the pages
/// they will compile into.
///
/// <para>Each write appends a <b>new</b> block under a freshly minted BlockId — the chain is
/// append-only, never rewritten in place. <see cref="AppendChained"/> ties the two moving parts of a
/// mutation together: it links the new block to the folder's current head via
/// <see cref="FolderDeltaLog.PreviousDeltaBlockId"/> and returns the directory rewritten with its
/// <see cref="FolderPageDirectory.HeadDeltaBlockId"/> advanced to the new block (docs/Folder_Listing.md
/// Section 2). The caller persists that directory through a <see cref="FolderPageDirectoryStore"/>.</para>
///
/// <para>Reads reverse the pipeline: Read → Decrypt (by the header's KeyEpoch) → Decompress
/// (bomb-guarded) → Unpack. The store owns neither the <see cref="BlockManager"/> nor the
/// <see cref="EpochDekProvider"/>; the caller scopes both.</para>
/// </summary>
public sealed class FolderDeltaLogStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider? _provider;
    private readonly EncryptionPolicy _policy;

    /// <summary>Wraps a block manager and loaded DEK provider with an encryption policy.</summary>
    /// <param name="manager">Block manager to read/write through (not owned).</param>
    /// <param name="provider">
    /// Loaded DEK provider (active epoch, epoch-addressed decrypt; not owned), or
    /// <see langword="null"/> for a plaintext file. When null, delta blocks are written and read
    /// as plaintext regardless of <paramref name="policy"/> — a plaintext file has no keys.
    /// </param>
    /// <param name="policy">Encryption policy; delta blocks encrypt under both (defaults to Default).</param>
    public FolderDeltaLogStore(
        BlockManager manager, EpochDekProvider? provider, EncryptionPolicy policy = EncryptionPolicy.Default)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider;
        _policy = policy;
    }

    /// <summary>The encryption policy consulted for the FolderDeltaLog block type.</summary>
    public EncryptionPolicy Policy => _policy;

    /// <summary>
    /// Writes a <see cref="FolderDeltaLog"/> (BlockType 13): pack → Zstd → encrypt (Default policy) →
    /// append under a freshly minted BlockId. The returned <see cref="BlockLocation.BlockId"/> is the
    /// new head of the chain. Returns the block location (BlockId, offset, length).
    /// </summary>
    public Result<BlockLocation> WriteDeltaBlock(FolderDeltaLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var payload = log.PackPayload();

        // Pack → Compress (Zstd), before encryption (spec Section 4.4).
        var compressed = BlockCompressor.Compress(payload, CompressionAlgorithm.Zstd);
        if (compressed.IsFailure)
            return Result<BlockLocation>.Failure(compressed.Error);

        // Compress → Encrypt under the policy (delta blocks encrypt under both). A plaintext
        // file (no provider) has no keys, so it writes plaintext regardless of the policy.
        if (_provider is not null && EncryptionPolicySet.RequiresEncryption(_policy, BlockType.FolderDeltaLog))
        {
            // The BlockId is an AAD component, so it must be minted before encrypting.
            var blockId = _manager.MintBlockId();
            var ciphertext = _provider.Encrypt(compressed.Value, blockId, BlockType.FolderDeltaLog);
            return _manager.Append(
                BlockType.FolderDeltaLog, PayloadEncoding.Custom, ciphertext,
                compression: CompressionAlgorithm.Zstd,
                encrypted: true,
                keyEpoch: _provider.ActiveEpoch,
                blockId: blockId);
        }

        return _manager.Append(
            BlockType.FolderDeltaLog, PayloadEncoding.Custom, compressed.Value,
            compression: CompressionAlgorithm.Zstd);
    }

    /// <summary>Reads and unpacks the <see cref="FolderDeltaLog"/> at <paramref name="offset"/>.</summary>
    public Result<FolderDeltaLog> ReadDeltaBlock(long offset)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<FolderDeltaLog>.Failure(read.VerificationError)
                : Result<FolderDeltaLog>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != BlockType.FolderDeltaLog)
            return Result<FolderDeltaLog>.Failure(
                $"Block at offset {offset} is {block.Header.Type}, expected {BlockType.FolderDeltaLog}.");

        // Read → Decrypt (by the header's KeyEpoch) → Decompress → Unpack (spec Section 4).
        var payload = block.Payload;
        if (block.Header.IsEncrypted)
        {
            if (_provider is null)
                return Result<FolderDeltaLog>.Failure(
                    $"Block at offset {offset} is encrypted but no DEK provider is loaded (a plaintext store cannot read an encrypted FolderDeltaLog).");
            payload = _provider.Decrypt(
                payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
        }

        var decompressed = BlockCompressor.Decompress(
            payload, block.Header.Compression, _manager.MaxPayloadLength);
        if (decompressed.IsFailure)
            return Result<FolderDeltaLog>.Failure(decompressed.Error);

        return Result<FolderDeltaLog>.Success(FolderDeltaLog.UnpackPayload(decompressed.Value));
    }

    /// <summary>
    /// Appends <paramref name="entries"/> as a new delta block chained to <paramref name="directory"/>'s
    /// current head (docs/Folder_Listing.md Section 2): the new block's
    /// <see cref="FolderDeltaLog.PreviousDeltaBlockId"/> is the directory's
    /// <see cref="FolderPageDirectory.HeadDeltaBlockId"/> (zero for the first block), and the returned
    /// directory is a COW rewrite (<see cref="FolderPageDirectory.Rewrite"/>) with its head advanced to
    /// the new block and <see cref="FolderPageDirectory.FolderVersion"/> incremented. The delta block is
    /// persisted here; the caller persists the returned directory via a
    /// <see cref="FolderPageDirectoryStore"/>. No page is rewritten.
    /// </summary>
    public Result<FolderDeltaAppendResult> AppendChained(
        FolderPageDirectory directory, IEnumerable<FolderDeltaEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(entries);

        var previous = directory.HasPendingDelta ? directory.HeadDeltaBlockId : null;
        var log = FolderDeltaLog.Create(entries, previous);

        var written = WriteDeltaBlock(log);
        if (written.IsFailure)
            return Result<FolderDeltaAppendResult>.Failure(written.Error);

        var advanced = directory.Rewrite(directory.PageEntries, headDeltaBlockId: written.Value.BlockId);
        return Result<FolderDeltaAppendResult>.Success(
            new FolderDeltaAppendResult(written.Value, advanced));
    }
}

/// <summary>
/// Outcome of <see cref="FolderDeltaLogStore.AppendChained"/>: the appended delta block's
/// <see cref="BlockLocation"/> (its <see cref="BlockLocation.BlockId"/> is the new chain head) and the
/// COW-rewritten <see cref="FolderPageDirectory"/> whose <see cref="FolderPageDirectory.HeadDeltaBlockId"/>
/// now points at that block. The directory is <b>not</b> yet persisted — the caller writes it through a
/// <see cref="FolderPageDirectoryStore"/> to commit the head advance.
/// </summary>
public sealed record FolderDeltaAppendResult(BlockLocation DeltaLocation, FolderPageDirectory Directory);
