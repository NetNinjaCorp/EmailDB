namespace EmailDB.Format.V3;

/// <summary>
/// Reads and writes the Phase 1 FTS blocks (BlockTypes 14-17: <see cref="FtsSegmentMeta"/>,
/// <see cref="FtsTermDictionary"/>, <see cref="FtsPostingList"/>, <see cref="FtsSearchRoot"/>)
/// through the v3 block pipeline, mirroring <see cref="FolderPageStore"/>: Serialize → Encrypt →
/// Append on the way out, Read → verify type → Decrypt → Deserialize on the way back
/// (docs/Search.md Phase 1, EmailDB_FileFormat_Spec.md Section 4.4).
///
/// <para><b>Always encrypted.</b> Trigrams and their posting lists reverse to the indexed
/// addresses, so every FTS block is encrypted under both policies (spec Section 9.5,
/// <see cref="EncryptionPolicySet.RequiresEncryption"/> returns true for types 14-18). When a DEK
/// provider is loaded this store therefore always encrypts; a plaintext file (no provider) has no
/// keys and writes plaintext — exactly the FolderPageStore contract. FTS payloads are compact
/// binary already, so — unlike folder pages — they are stored uncompressed.</para>
///
/// <para><b>BlockId resolution.</b> The <see cref="FtsSearchRoot"/> names its segments by ULID, a
/// segment names its term dictionary by ULID, and the dictionary names each posting list by ULID;
/// <see cref="ResolveOffset"/> turns any of those ULIDs into a file offset through the read-side
/// resolution chain (runtime map → location index, spec Section 7) so the ingest reconstruction
/// (task 91-7) and the query path (task 91-8) can walk root → segments → dictionaries → posting
/// lists. The store owns neither the manager, the provider, nor the resolver.</para>
/// </summary>
public sealed class FtsBlockStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider? _provider;
    private readonly IBlockIdResolver? _resolver;

    /// <summary>Wraps a block manager, an optional loaded DEK provider, and an optional resolver.</summary>
    /// <param name="manager">Block manager to read/write through (not owned).</param>
    /// <param name="provider">Loaded DEK provider (not owned), or null for a plaintext file — then FTS blocks are written and read as plaintext.</param>
    /// <param name="resolver">Read-side BlockId→location chain used by <see cref="ResolveOffset"/>; null when only offset reads are needed.</param>
    public FtsBlockStore(BlockManager manager, EpochDekProvider? provider, IBlockIdResolver? resolver)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider;
        _resolver = resolver;
    }

    /// <summary>True when this store encrypts FTS blocks (a DEK provider is loaded).</summary>
    public bool IsEncrypted => _provider is not null;

    // ------------------------------------------------------------- Write

    /// <summary>Appends an <see cref="FtsSegmentMeta"/> block (type 14).</summary>
    public Result<BlockLocation> AppendSegmentMeta(FtsSegmentMeta meta) =>
        Append(BlockType.FTSSegmentMeta, FtsSegmentMetaSerializer.Serialize(meta));

    /// <summary>Appends an <see cref="FtsTermDictionary"/> block (type 15).</summary>
    public Result<BlockLocation> AppendTermDictionary(FtsTermDictionary dictionary) =>
        Append(BlockType.FTSTermDictionary, FtsTermDictionarySerializer.Serialize(dictionary));

    /// <summary>Appends an <see cref="FtsPostingList"/> block (type 16).</summary>
    public Result<BlockLocation> AppendPostingList(FtsPostingList list) =>
        Append(BlockType.FTSPostingList, FtsPostingListSerializer.Serialize(list));

    /// <summary>Appends an <see cref="FtsSearchRoot"/> block (type 17).</summary>
    public Result<BlockLocation> AppendSearchRoot(FtsSearchRoot root) =>
        Append(BlockType.FTSSearchRoot, FtsSearchRootSerializer.Serialize(root));

    private Result<BlockLocation> Append(BlockType type, byte[] payload)
    {
        // FTS blocks always encrypt when a provider is loaded (spec Section 9.5). The BlockId is
        // an AAD component, so it is minted before encrypting and passed through as the block id.
        if (_provider is not null)
        {
            var blockId = _manager.MintBlockId();
            var ciphertext = _provider.Encrypt(payload, blockId, type);
            return _manager.Append(
                type, PayloadEncoding.RawBytes, ciphertext,
                compression: CompressionAlgorithm.None,
                encrypted: true,
                keyEpoch: _provider.ActiveEpoch,
                blockId: blockId);
        }
        return _manager.Append(type, PayloadEncoding.RawBytes, payload, compression: CompressionAlgorithm.None);
    }

    // ------------------------------------------------------------- Read

    /// <summary>Reads and deserializes the <see cref="FtsSearchRoot"/> at <paramref name="offset"/>.</summary>
    public Result<FtsSearchRoot> ReadSearchRoot(long offset)
    {
        var payload = ReadPayload(offset, BlockType.FTSSearchRoot);
        return payload.IsFailure
            ? Result<FtsSearchRoot>.Failure(payload.Error)
            : FtsSearchRootSerializer.Deserialize(payload.Value);
    }

    /// <summary>Reads and deserializes the <see cref="FtsSegmentMeta"/> at <paramref name="offset"/>.</summary>
    public Result<FtsSegmentMeta> ReadSegmentMeta(long offset)
    {
        var payload = ReadPayload(offset, BlockType.FTSSegmentMeta);
        return payload.IsFailure
            ? Result<FtsSegmentMeta>.Failure(payload.Error)
            : FtsSegmentMetaSerializer.Deserialize(payload.Value);
    }

    /// <summary>Reads and deserializes the <see cref="FtsTermDictionary"/> at <paramref name="offset"/>.</summary>
    public Result<FtsTermDictionary> ReadTermDictionary(long offset)
    {
        var payload = ReadPayload(offset, BlockType.FTSTermDictionary);
        return payload.IsFailure
            ? Result<FtsTermDictionary>.Failure(payload.Error)
            : FtsTermDictionarySerializer.Deserialize(payload.Value);
    }

    /// <summary>Reads and deserializes the <see cref="FtsPostingList"/> at <paramref name="offset"/>.</summary>
    public Result<FtsPostingList> ReadPostingList(long offset)
    {
        var payload = ReadPayload(offset, BlockType.FTSPostingList);
        return payload.IsFailure
            ? Result<FtsPostingList>.Failure(payload.Error)
            : FtsPostingListSerializer.Deserialize(payload.Value);
    }

    /// <summary>
    /// Resolves an FTS block's ULID to its file offset through the read-side chain (spec Section 7).
    /// A block appended this session resolves through the runtime map; a committed one through the
    /// durable location index.
    /// </summary>
    /// <exception cref="InvalidOperationException">No resolver was supplied at construction.</exception>
    public Result<long> ResolveOffset(ReadOnlySpan<byte> blockId)
    {
        if (_resolver is null)
            throw new InvalidOperationException("FtsBlockStore was constructed without a resolver; offset reads only.");
        if (!_resolver.TryGetLocation(blockId, out var location) || location is null)
            return Result<long>.Failure(
                "FTS block id could not be resolved to a physical offset (runtime map + location index miss, spec Section 7).");
        return Result<long>.Success(location.Offset);
    }

    private Result<byte[]> ReadPayload(long offset, BlockType expected)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<byte[]>.Failure(read.VerificationError)
                : Result<byte[]>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != expected)
            return Result<byte[]>.Failure(
                $"Block at offset {offset} is {block.Header.Type}, expected {expected} " +
                "(stale/misdirected hint or corrupt layout, spec Section 13).");

        if (!block.Header.IsEncrypted)
            return Result<byte[]>.Success(block.Payload);

        if (_provider is null)
            return Result<byte[]>.Failure(
                $"Block at offset {offset} is encrypted but no DEK provider is loaded (a plaintext store cannot read an encrypted FTS block).");
        try
        {
            var plaintext = _provider.Decrypt(
                block.Payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
            return Result<byte[]>.Success(plaintext);
        }
        catch (WrongKeyOrTamperError ex)
        {
            return ex.ToResult<byte[]>();
        }
        catch (EpochDekUnavailableError ex)
        {
            return Result<byte[]>.Failure($"Read at offset {offset}: {ex.Message}");
        }
    }
}
