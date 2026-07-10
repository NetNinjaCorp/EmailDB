namespace EmailDB.Format.V3;

/// <summary>
/// Writes and reads <see cref="FolderPageDirectory"/> blocks (BlockType 11) through the v3 block
/// pipeline, mirroring <see cref="FolderPageStore"/> (Pack → Zstd → Encrypt → Append,
/// EmailDB_FileFormat_Spec.md Section 4.4). The directory carries subjects/senders only indirectly
/// (via the page ranges it indexes) but is content-derived, so it encrypts under the Default policy
/// like the pages it points at.
///
/// <para><b>Stable BlockId.</b> Every rewrite re-appends under the directory's own stable BlockId —
/// the folder's ULID, <see cref="FolderPageDirectory.FolderId"/> — so successive versions share one
/// BlockId as required by docs/Folder_Listing.md Section 2. The FolderId is therefore both the
/// encryption AAD's BlockId component and the value stamped into the block header, and no fresh id
/// is minted on write.</para>
///
/// <para>Reads reverse the pipeline: Read → Decrypt (by the header's KeyEpoch) → Decompress
/// (bomb-guarded) → Unpack. The store owns neither the <see cref="BlockManager"/> nor the
/// <see cref="EpochDekProvider"/>; the caller scopes both.</para>
/// </summary>
public sealed class FolderPageDirectoryStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider? _provider;
    private readonly EncryptionPolicy _policy;

    /// <summary>Wraps a block manager and loaded DEK provider with an encryption policy.</summary>
    /// <param name="manager">Block manager to read/write through (not owned).</param>
    /// <param name="provider">
    /// Loaded DEK provider (active epoch, epoch-addressed decrypt; not owned), or
    /// <see langword="null"/> for a plaintext file. When null, the directory is written and read
    /// as plaintext regardless of <paramref name="policy"/> — a plaintext file has no keys.
    /// </param>
    /// <param name="policy">Encryption policy; the directory encrypts under both (defaults to Default).</param>
    public FolderPageDirectoryStore(
        BlockManager manager, EpochDekProvider? provider, EncryptionPolicy policy = EncryptionPolicy.Default)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider;
        _policy = policy;
    }

    /// <summary>The encryption policy consulted for the FolderPageDirectory block type.</summary>
    public EncryptionPolicy Policy => _policy;

    /// <summary>
    /// Writes a <see cref="FolderPageDirectory"/> (BlockType 11): pack → Zstd → encrypt (Default
    /// policy) → append under the directory's stable BlockId (its <see cref="FolderPageDirectory.FolderId"/>).
    /// A COW rewrite is just this call on the directory's next version. Returns the block location.
    /// </summary>
    public Result<BlockLocation> WriteDirectory(FolderPageDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var payload = directory.PackPayload();

        // Pack → Compress (Zstd), before encryption (spec Section 4.4).
        var compressed = BlockCompressor.Compress(payload, CompressionAlgorithm.Zstd);
        if (compressed.IsFailure)
            return Result<BlockLocation>.Failure(compressed.Error);

        // The stable BlockId is the folder's ULID; every rewrite re-appends under it.
        var blockId = directory.FolderId;

        // Compress → Encrypt under the policy (the directory encrypts under both). A plaintext
        // file (no provider) has no keys, so it writes plaintext regardless of the policy.
        if (_provider is not null && EncryptionPolicySet.RequiresEncryption(_policy, BlockType.FolderPageDirectory))
        {
            var ciphertext = _provider.Encrypt(compressed.Value, blockId, BlockType.FolderPageDirectory);
            return _manager.Append(
                BlockType.FolderPageDirectory, PayloadEncoding.Custom, ciphertext,
                compression: CompressionAlgorithm.Zstd,
                encrypted: true,
                keyEpoch: _provider.ActiveEpoch,
                blockId: blockId);
        }

        return _manager.Append(
            BlockType.FolderPageDirectory, PayloadEncoding.Custom, compressed.Value,
            compression: CompressionAlgorithm.Zstd,
            blockId: blockId);
    }

    /// <summary>Reads and unpacks the <see cref="FolderPageDirectory"/> at <paramref name="offset"/>.</summary>
    public Result<FolderPageDirectory> ReadDirectory(long offset)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<FolderPageDirectory>.Failure(read.VerificationError)
                : Result<FolderPageDirectory>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != BlockType.FolderPageDirectory)
            return Result<FolderPageDirectory>.Failure(
                $"Block at offset {offset} is {block.Header.Type}, expected {BlockType.FolderPageDirectory}.");

        // Read → Decrypt (by the header's KeyEpoch) → Decompress → Unpack (spec Section 4).
        var payload = block.Payload;
        if (block.Header.IsEncrypted)
        {
            if (_provider is null)
                return Result<FolderPageDirectory>.Failure(
                    $"Block at offset {offset} is encrypted but no DEK provider is loaded (a plaintext store cannot read an encrypted FolderPageDirectory).");
            payload = _provider.Decrypt(
                payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
        }

        var decompressed = BlockCompressor.Decompress(
            payload, block.Header.Compression, _manager.MaxPayloadLength);
        if (decompressed.IsFailure)
            return Result<FolderPageDirectory>.Failure(decompressed.Error);

        return Result<FolderPageDirectory>.Success(FolderPageDirectory.UnpackPayload(decompressed.Value));
    }
}
