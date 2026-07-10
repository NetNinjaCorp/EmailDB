using EmailDB.Format;
using EmailDB.Format.V3;
using ProtoBuf;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// Writes and reads the Tier 2 (<see cref="EmailMetadata"/>, BlockType 10) and Tier 3
/// (<see cref="EmailContent"/>, BlockType 7) email blocks through the v3 block pipeline, wiring
/// the models to compression and encryption exactly per the spec write order
/// (Serialize → Compress → Encrypt → Checksum → Append, EmailDB_FileFormat_Spec.md Section 4.4):
///
/// <list type="number">
///   <item><b>Serialize</b> the protobuf payload (<see cref="PayloadEncoding.Protobuf"/>).</item>
///   <item><b>Compress</b> it with Zstd via <see cref="BlockCompressor"/>.</item>
///   <item><b>Encrypt</b> under the <see cref="EncryptionPolicy"/> — both email block types are
///   encrypted under the Default policy — stamping the header Encrypted flag and active KeyEpoch.</item>
///   <item><b>Append</b> with the Zstd compression byte recorded so the read path decompresses.</item>
/// </list>
///
/// Reads reverse the pipeline: Read → Decrypt (by the header's KeyEpoch) → Decompress (bomb-guarded)
/// → Deserialize. The store owns neither the <see cref="BlockManager"/> nor the
/// <see cref="EpochDekProvider"/>; the caller scopes both.
/// </summary>
public sealed class EmailBlockStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider _provider;
    private readonly EncryptionPolicy _policy;

    /// <summary>
    /// Wraps a block manager and loaded DEK provider with an encryption policy.
    /// </summary>
    /// <param name="manager">Block manager to read/write through (not owned).</param>
    /// <param name="provider">Loaded DEK provider (active epoch, epoch-addressed decrypt; not owned).</param>
    /// <param name="policy">
    /// Policy deciding per block type whether the payload is DEK-encrypted (spec Section 9.5).
    /// Defaults to <see cref="EncryptionPolicy.Default"/>, under which both email block types encrypt.
    /// </param>
    public EmailBlockStore(
        BlockManager manager, EpochDekProvider provider, EncryptionPolicy policy = EncryptionPolicy.Default)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _policy = policy;
    }

    /// <summary>The encryption policy consulted per block type.</summary>
    public EncryptionPolicy Policy => _policy;

    /// <summary>
    /// Writes a Tier 3 <see cref="EmailContent"/> (BlockType 7): serialize → Zstd → encrypt (Default
    /// policy) → append. Returns the block location (BlockId, offset, length).
    /// </summary>
    public Result<BlockLocation> WriteContent(EmailContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Write(BlockType.EmailContent, content);
    }

    /// <summary>
    /// Writes a Tier 2 <see cref="EmailMetadata"/> (BlockType 10): serialize → Zstd → encrypt
    /// (Default policy) → append. Returns the block location (BlockId, offset, length).
    /// </summary>
    public Result<BlockLocation> WriteMetadata(EmailMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Write(BlockType.EmailMetadata, metadata);
    }

    /// <summary>Reads and decodes the Tier 3 <see cref="EmailContent"/> at <paramref name="offset"/>.</summary>
    public Result<EmailContent> ReadContent(long offset) =>
        Read<EmailContent>(offset, BlockType.EmailContent);

    /// <summary>Reads and decodes the Tier 2 <see cref="EmailMetadata"/> at <paramref name="offset"/>.</summary>
    public Result<EmailMetadata> ReadMetadata(long offset) =>
        Read<EmailMetadata>(offset, BlockType.EmailMetadata);

    private Result<BlockLocation> Write<T>(BlockType type, T model)
    {
        byte[] serialized;
        using (var ms = new MemoryStream())
        {
            Serializer.Serialize(ms, model);
            serialized = ms.ToArray();
        }

        // Serialize → Compress (Zstd), before encryption (spec Section 4.4).
        var compressed = BlockCompressor.Compress(serialized, CompressionAlgorithm.Zstd);
        if (compressed.IsFailure)
            return Result<BlockLocation>.Failure(compressed.Error);

        // Compress → Encrypt under the policy (both email tiers encrypt under Default).
        if (EncryptionPolicySet.RequiresEncryption(_policy, type))
        {
            // The BlockId is an AAD component, so it must be minted before encrypting.
            var blockId = _manager.MintBlockId();
            var ciphertext = _provider.Encrypt(compressed.Value, blockId, type);
            return _manager.Append(
                type, PayloadEncoding.Protobuf, ciphertext,
                compression: CompressionAlgorithm.Zstd,
                encrypted: true,
                keyEpoch: _provider.ActiveEpoch,
                blockId: blockId);
        }

        return _manager.Append(
            type, PayloadEncoding.Protobuf, compressed.Value,
            compression: CompressionAlgorithm.Zstd);
    }

    private Result<T> Read<T>(long offset, BlockType expected)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<T>.Failure(read.VerificationError)
                : Result<T>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != expected)
            return Result<T>.Failure(
                $"Block at offset {offset} is {block.Header.Type}, expected {expected}.");

        // Read → Decrypt (by the header's KeyEpoch) → Decompress → Deserialize (spec Section 4).
        var payload = block.Payload;
        if (block.Header.IsEncrypted)
            payload = _provider.Decrypt(
                payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);

        var decompressed = BlockCompressor.Decompress(
            payload, block.Header.Compression, _manager.MaxPayloadLength);
        if (decompressed.IsFailure)
            return Result<T>.Failure(decompressed.Error);

        using var ms = new MemoryStream(decompressed.Value, writable: false);
        return Result<T>.Success(Serializer.Deserialize<T>(ms));
    }
}
