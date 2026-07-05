namespace EmailDB.Format.V3;

/// <summary>
/// The encryption injection layer over <see cref="BlockManager"/>: it wires a loaded
/// <see cref="EpochDekProvider"/> into the block read/write path so higher layers write and
/// read plaintext while the on-disk bytes are AES-256-GCM ciphertext (spec Section 9.3,
/// docs/Encryption.md Section 3). This is the "provider → BlockStore" step of the open-time
/// bootstrap (docs/Encryption.md Section 2 step 5): once <see cref="EncryptionBootstrap"/> has
/// built the provider from the file's DEK table, an <see cref="EncryptedBlockStore"/> makes the
/// block layer actually encrypt on write and decrypt on read.
///
/// <para><b>Policy-driven write</b> (<see cref="Append"/>): consults the store's
/// <see cref="EncryptionPolicy"/> for the block type (spec Section 9.5). When the policy requires
/// encryption it encrypts and stamps the header (flag + epoch); otherwise it writes plaintext with
/// no flag and no epoch. This is the first-class write path: one call handles both the encrypted
/// and plaintext block types of a policy, so a single file can carry a mix of both and still be
/// read back block-by-block off the header flag.</para>
///
/// <para><b>Unconditional write</b> (<see cref="AppendEncrypted"/>): mints the BlockId (needed up
/// front — it is an AAD component), encrypts the plaintext under the provider's active epoch, and
/// appends the ciphertext with the encrypted flag and epoch stamped into the header — regardless of
/// policy. Used when a block type is known to always encrypt.</para>
///
/// <para><b>Read</b> (<see cref="ReadDecrypted"/>): reads and fully verifies the block via the
/// manager (checksum first — corruption is caught before the tag), then, for an encrypted block,
/// decrypts by selecting the DEK from the header's <c>KeyEpoch</c> — the epoch-addressed lookup
/// that lets a file decrypt blocks written under old epochs (spec Section 9.7). Plaintext blocks
/// pass through unchanged.</para>
///
/// <para>The store does not own the provider or the manager; the caller scopes both (the
/// provider in a <c>using</c> so its DEKs are zeroized).</para>
/// </summary>
public sealed class EncryptedBlockStore
{
    private readonly BlockManager _manager;
    private readonly EpochDekProvider _provider;
    private readonly EncryptionPolicy _policy;

    /// <summary>
    /// Wraps a block manager with a loaded DEK provider and an encryption policy. Neither the
    /// manager nor the provider is owned by the store.
    /// </summary>
    /// <param name="manager">The block manager to read and write through.</param>
    /// <param name="provider">The loaded DEK provider (active epoch, epoch-addressed decrypt).</param>
    /// <param name="policy">
    /// The policy that decides per block type whether <see cref="Append"/> encrypts (spec
    /// Section 9.5). Defaults to <see cref="EncryptionPolicy.Default"/>.
    /// </param>
    public EncryptedBlockStore(
        BlockManager manager, EpochDekProvider provider, EncryptionPolicy policy = EncryptionPolicy.Default)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _policy = policy;
    }

    /// <summary>The block manager this store reads and writes through.</summary>
    public BlockManager Manager => _manager;

    /// <summary>The loaded DEK provider (active epoch, epoch-addressed decrypt).</summary>
    public EpochDekProvider Provider => _provider;

    /// <summary>The encryption policy that <see cref="Append"/> consults per block type.</summary>
    public EncryptionPolicy Policy => _policy;

    /// <summary>
    /// The first-class, policy-driven write path (spec Section 9.5): appends <paramref name="payload"/>
    /// as an encrypted block when the store's <see cref="Policy"/> requires it for
    /// <paramref name="type"/>, otherwise as a plaintext block. Encrypted blocks carry the header
    /// <c>Encrypted</c> flag and the active <c>KeyEpoch</c>; plaintext blocks carry neither (epoch 0,
    /// flag clear), so a mixed-policy file reads back correctly block-by-block off the flag.
    /// </summary>
    /// <param name="type">Block type — the policy input and an AAD component when encrypting.</param>
    /// <param name="encoding">Payload serialization format of <paramref name="payload"/>.</param>
    /// <param name="payload">The plaintext bytes to store (may be empty).</param>
    public Result<BlockLocation> Append(
        BlockType type, PayloadEncoding encoding, ReadOnlySpan<byte> payload)
    {
        return EncryptionPolicySet.RequiresEncryption(_policy, type)
            ? AppendEncrypted(type, encoding, payload)
            : _manager.Append(type, encoding, payload, compression: CompressionAlgorithm.None);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under the provider's active epoch and appends it as
    /// an encrypted block. The header records the encrypted flag and the active <c>KeyEpoch</c>,
    /// so a later reopen decrypts it by that epoch — even after key rotation.
    /// </summary>
    /// <param name="type">Block type (an AAD component and a policy input).</param>
    /// <param name="encoding">Payload serialization format of the plaintext.</param>
    /// <param name="plaintext">The bytes to encrypt and store (may be empty).</param>
    public Result<BlockLocation> AppendEncrypted(
        BlockType type, PayloadEncoding encoding, ReadOnlySpan<byte> plaintext)
    {
        // The BlockId is bound into the AAD, so it must be known before encrypting: mint it
        // from the manager, encrypt against it, then append the ciphertext under that same id.
        var blockId = _manager.MintBlockId();
        var ciphertext = _provider.Encrypt(plaintext, blockId, type);
        return _manager.Append(
            type, encoding, ciphertext,
            compression: CompressionAlgorithm.None,
            encrypted: true,
            keyEpoch: _provider.ActiveEpoch,
            blockId: blockId);
    }

    /// <summary>
    /// Reads and fully verifies the block at <paramref name="offset"/>, then returns its
    /// plaintext. Encrypted blocks are decrypted by the header's <c>KeyEpoch</c> (spec Section
    /// 9.7); plaintext blocks are returned as-is. Corruption surfaces as a failed result from the
    /// manager; a wrong-key / tampered block or a missing/retired epoch surfaces as the thrown
    /// distinct crypto error from the provider (spec Section 13).
    /// </summary>
    /// <param name="offset">File offset of the block's first header byte.</param>
    public Result<byte[]> ReadDecrypted(long offset)
    {
        var read = _manager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<byte[]>.Failure(read.VerificationError)
                : Result<byte[]>.Failure(read.Error);

        var block = read.Value;
        if (!block.Header.IsEncrypted)
            return Result<byte[]>.Success(block.Payload);

        var plaintext = _provider.Decrypt(
            block.Payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
        return Result<byte[]>.Success(plaintext);
    }
}
