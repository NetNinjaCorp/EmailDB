namespace EmailDB.Format.V3;

/// <summary>
/// Writes and reads Tier 1 <see cref="FolderPage"/> blocks (BlockType 12) through the v3 block
/// pipeline, mirroring the email-block write order (Serialize → Compress → Encrypt → Checksum →
/// Append, EmailDB_FileFormat_Spec.md Section 4.4):
///
/// <list type="number">
///   <item><b>Pack</b> the page to its custom binary payload (<see cref="PayloadEncoding.Custom"/>).</item>
///   <item><b>Compress</b> it with Zstd via <see cref="BlockCompressor"/>.</item>
///   <item><b>Encrypt</b> under the <see cref="EncryptionPolicy"/> — folder pages carry subjects and
///   senders, so they encrypt under both policies — stamping the header Encrypted flag and active
///   KeyEpoch.</item>
///   <item><b>Append</b> with the Zstd byte recorded so the read path decompresses.</item>
/// </list>
///
/// Reads reverse the pipeline: Read → Decrypt (by the header's KeyEpoch) → Decompress
/// (bomb-guarded) → Unpack. The store owns neither the <see cref="BlockManager"/> nor the
/// <see cref="EpochDekProvider"/>; the caller scopes both. The FolderPageDirectory (BlockType 11)
/// that indexes these pages is a separate concern.
/// </summary>
public sealed class FolderPageStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider? _provider;
    private readonly EncryptionPolicy _policy;

    /// <summary>Wraps a block manager and loaded DEK provider with an encryption policy.</summary>
    /// <param name="manager">Block manager to read/write through (not owned).</param>
    /// <param name="provider">
    /// Loaded DEK provider (active epoch, epoch-addressed decrypt; not owned), or
    /// <see langword="null"/> for a plaintext file. When null, pages are written and read as
    /// plaintext regardless of <paramref name="policy"/> — a plaintext file has no keys, so
    /// nothing can be encrypted.
    /// </param>
    /// <param name="policy">Encryption policy; folder pages encrypt under both (defaults to Default).</param>
    public FolderPageStore(
        BlockManager manager, EpochDekProvider? provider, EncryptionPolicy policy = EncryptionPolicy.Default)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider;
        _policy = policy;
    }

    /// <summary>The encryption policy consulted for the FolderPage block type.</summary>
    public EncryptionPolicy Policy => _policy;

    /// <summary>
    /// Writes a <see cref="FolderPage"/> (BlockType 12): pack → Zstd → encrypt (Default policy) →
    /// append. Returns the block location (BlockId, offset, length).
    /// </summary>
    public Result<BlockLocation> WritePage(FolderPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var payload = page.PackPayload();

        // Pack → Compress (Zstd), before encryption (spec Section 4.4).
        var compressed = BlockCompressor.Compress(payload, CompressionAlgorithm.Zstd);
        if (compressed.IsFailure)
            return Result<BlockLocation>.Failure(compressed.Error);

        // Compress → Encrypt under the policy (folder pages encrypt under both). A plaintext
        // file (no provider) has no keys, so it writes plaintext regardless of the policy.
        if (_provider is not null && EncryptionPolicySet.RequiresEncryption(_policy, BlockType.FolderPage))
        {
            // The BlockId is an AAD component, so it must be minted before encrypting.
            var blockId = _manager.MintBlockId();
            var ciphertext = _provider.Encrypt(compressed.Value, blockId, BlockType.FolderPage);
            return _manager.Append(
                BlockType.FolderPage, PayloadEncoding.Custom, ciphertext,
                compression: CompressionAlgorithm.Zstd,
                encrypted: true,
                keyEpoch: _provider.ActiveEpoch,
                blockId: blockId);
        }

        return _manager.Append(
            BlockType.FolderPage, PayloadEncoding.Custom, compressed.Value,
            compression: CompressionAlgorithm.Zstd);
    }

    /// <summary>Reads and unpacks the <see cref="FolderPage"/> at <paramref name="offset"/>.</summary>
    public Result<FolderPage> ReadPage(long offset)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<FolderPage>.Failure(read.VerificationError)
                : Result<FolderPage>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != BlockType.FolderPage)
            return Result<FolderPage>.Failure(
                $"Block at offset {offset} is {block.Header.Type}, expected {BlockType.FolderPage}.");

        // Read → Decrypt (by the header's KeyEpoch) → Decompress → Unpack (spec Section 4).
        var payload = block.Payload;
        if (block.Header.IsEncrypted)
        {
            if (_provider is null)
                return Result<FolderPage>.Failure(
                    $"Block at offset {offset} is encrypted but no DEK provider is loaded (a plaintext store cannot read an encrypted FolderPage).");
            payload = _provider.Decrypt(
                payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
        }

        var decompressed = BlockCompressor.Decompress(
            payload, block.Header.Compression, _manager.MaxPayloadLength);
        if (decompressed.IsFailure)
            return Result<FolderPage>.Failure(decompressed.Error);

        return Result<FolderPage>.Success(FolderPage.UnpackPayload(decompressed.Value));
    }
}
