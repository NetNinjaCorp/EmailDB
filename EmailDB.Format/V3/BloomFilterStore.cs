namespace EmailDB.Format.V3;

/// <summary>
/// Reads and writes the <see cref="BloomFilterCatalog"/> block (BlockType 18) through the v3 block
/// pipeline, mirroring <see cref="FtsBlockStore"/>: Serialize → Encrypt → Append on the way out,
/// Read → verify type → Decrypt → Deserialize on the way back (docs/Search.md Phase 5).
///
/// <para><b>Always encrypted.</b> Bloom filter bits leak token presence, so the block is encrypted under
/// both policies (spec Section 9.5, <see cref="EncryptionPolicySet.RequiresEncryption"/> returns true for
/// type 18). When a DEK provider is loaded this store therefore always encrypts; a plaintext file (no
/// provider) has no keys and writes plaintext — exactly the <see cref="FtsBlockStore"/> contract. The
/// catalog resolves to a single block, so — unlike the FTS store — no BlockId resolver is needed; the
/// Checkpoint's IndexKind-4 pointer gives the offset to read directly.</para>
/// </summary>
public sealed class BloomFilterStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider? _provider;

    /// <summary>Wraps a block manager and an optional loaded DEK provider (null ⇒ plaintext file).</summary>
    public BloomFilterStore(BlockManager manager, EpochDekProvider? provider)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider;
    }

    /// <summary>True when this store encrypts the catalog block (a DEK provider is loaded).</summary>
    public bool IsEncrypted => _provider is not null;

    /// <summary>Appends a <see cref="BloomFilterCatalog"/> block (type 18); always encrypted under a provider.</summary>
    public Result<BlockLocation> AppendCatalog(BloomFilterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var payload = BloomFilterCatalogSerializer.Serialize(catalog);

        if (_provider is not null)
        {
            var blockId = _manager.MintBlockId();
            var ciphertext = _provider.Encrypt(payload, blockId, BlockType.BloomFilter);
            return _manager.Append(
                BlockType.BloomFilter, PayloadEncoding.RawBytes, ciphertext,
                compression: CompressionAlgorithm.None,
                encrypted: true,
                keyEpoch: _provider.ActiveEpoch,
                blockId: blockId);
        }
        return _manager.Append(
            BlockType.BloomFilter, PayloadEncoding.RawBytes, payload, compression: CompressionAlgorithm.None);
    }

    /// <summary>Reads and deserializes the <see cref="BloomFilterCatalog"/> at <paramref name="offset"/>.</summary>
    public Result<BloomFilterCatalog> ReadCatalog(long offset)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<BloomFilterCatalog>.Failure(read.VerificationError)
                : Result<BloomFilterCatalog>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != BlockType.BloomFilter)
            return Result<BloomFilterCatalog>.Failure(
                $"Block at offset {offset} is {block.Header.Type}, expected {BlockType.BloomFilter} " +
                "(stale/misdirected hint or corrupt layout, spec Section 13).");

        if (!block.Header.IsEncrypted)
            return BloomFilterCatalogSerializer.Deserialize(block.Payload);

        if (_provider is null)
            return Result<BloomFilterCatalog>.Failure(
                $"Block at offset {offset} is encrypted but no DEK provider is loaded (a plaintext store cannot read an encrypted Bloom catalog).");
        try
        {
            var plaintext = _provider.Decrypt(
                block.Payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
            return BloomFilterCatalogSerializer.Deserialize(plaintext);
        }
        catch (WrongKeyOrTamperError ex)
        {
            return ex.ToResult<BloomFilterCatalog>();
        }
        catch (EpochDekUnavailableError ex)
        {
            return Result<BloomFilterCatalog>.Failure($"Read at offset {offset}: {ex.Message}");
        }
    }
}
