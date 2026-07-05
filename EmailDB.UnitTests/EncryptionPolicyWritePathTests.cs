using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the policy sets and the policy-driven write path (US-EMDB-78-5, spec Section 9.5,
/// docs/Encryption.md Section 4): the Default/Full policy tables, and that
/// <see cref="EncryptedBlockStore.Append"/> encrypts + stamps (Encrypted flag, KeyEpoch) exactly
/// the block types the policy requires while leaving the rest plaintext (flag clear, epoch 0).
/// A mixed-policy file is read back correctly block-by-block off the header flag.
/// </summary>
public class EncryptionPolicyWritePathTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-encpolicy-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xB0 + i)).ToArray();

    private const ushort ActiveEpoch = 3;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static EpochDekProvider MakeProvider()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 1);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

    // ---- Policy tables (spec Section 9.5) -----------------------------------

    // Content and content-derived blocks: encrypted under BOTH policies.
    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.EmailMetadata)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.FolderPage)]
    [InlineData(BlockType.FolderPageDirectory)]
    [InlineData(BlockType.FolderDeltaLog)]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.FTSSegmentMeta)]
    [InlineData(BlockType.FTSTermDictionary)]
    [InlineData(BlockType.FTSPostingList)]
    [InlineData(BlockType.FTSSearchRoot)]
    [InlineData(BlockType.BloomFilter)]
    public void ContentAndDerived_Encrypted_UnderBothPolicies(BlockType type)
    {
        Assert.True(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Default, type));
        Assert.True(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Full, type));
    }

    // Recovery blocks: plaintext under BOTH policies (read before keys exist).
    // KeyStore is KEK-encrypted on its own path, so from the DEK write path it is plaintext.
    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.Cleanup)]
    [InlineData(BlockType.Checkpoint)]
    [InlineData(BlockType.KeyStore)]
    public void RecoveryAndKeyStore_Plaintext_UnderBothPolicies(BlockType type)
    {
        Assert.False(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Default, type));
        Assert.False(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Full, type));
    }

    // B+-tree structure: the ONLY types whose treatment depends on the policy.
    [Theory]
    [InlineData(BlockType.BTreeLeaf)]
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    public void BTreeStructure_PlaintextDefault_EncryptedFull(BlockType type)
    {
        Assert.False(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Default, type));
        Assert.True(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Full, type));
    }

    // ---- Write-path stamping -------------------------------------------------

    [Fact]
    public void Append_EncryptsAndStamps_WhenPolicyRequires()
    {
        var plaintext = "encrypt me"u8.ToArray();
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        // EmailContent is encrypted under Default.
        var appended = store.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintext);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.True(read.Value.Header.IsEncrypted);            // Flags bit 0 set
        Assert.Equal(ActiveEpoch, read.Value.Header.KeyEpoch); // epoch stamped
        Assert.NotEqual(plaintext, read.Value.Payload);        // on-disk bytes are ciphertext

        // Reads back the plaintext through the store.
        var decrypted = store.ReadDecrypted(appended.Value.Offset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);
    }

    [Fact]
    public void Append_LeavesPlaintextUnflagged_WhenPolicyDoesNot()
    {
        var payload = "plaintext btree node"u8.ToArray();
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        // BTreeLeaf is plaintext under Default.
        var appended = store.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, payload);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.False(read.Value.Header.IsEncrypted);   // flag clear
        Assert.Equal(0, read.Value.Header.KeyEpoch);   // no epoch stamped
        Assert.Equal(payload, read.Value.Payload);     // stored verbatim

        var readBack = store.ReadDecrypted(appended.Value.Offset);
        Ok(readBack);
        Assert.Equal(payload, readBack.Value);
    }

    [Fact]
    public void FullPolicy_EncryptsBTree_WhereDefaultWouldNot()
    {
        var payload = "btree node under full policy"u8.ToArray();
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Full);

        var appended = store.Append(BlockType.BTreeInternal, PayloadEncoding.RawBytes, payload);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.True(read.Value.Header.IsEncrypted);
        Assert.Equal(ActiveEpoch, read.Value.Header.KeyEpoch);

        var decrypted = store.ReadDecrypted(appended.Value.Offset);
        Ok(decrypted);
        Assert.Equal(payload, decrypted.Value);
    }

    [Fact]
    public void MixedPolicyFile_ReadsCorrectly_BlockByBlock()
    {
        var content = "secret email body"u8.ToArray();
        var btree = "opaque hash keys"u8.ToArray();
        var metadata = "recovery metadata"u8.ToArray();
        long contentOffset, btreeOffset, metaOffset;

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        var a = store.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, content);
        var b = store.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, btree);
        var c = store.Append(BlockType.Metadata, PayloadEncoding.RawBytes, metadata);
        Ok(a); Ok(b); Ok(c);
        contentOffset = a.Value.Offset; btreeOffset = b.Value.Offset; metaOffset = c.Value.Offset;

        // One encrypted block, two plaintext blocks in the same file.
        Assert.True(manager.Read(contentOffset).Value.Header.IsEncrypted);
        Assert.False(manager.Read(btreeOffset).Value.Header.IsEncrypted);
        Assert.False(manager.Read(metaOffset).Value.Header.IsEncrypted);

        // The read path routes off the per-block flag, so all three read back correctly.
        Assert.Equal(content, store.ReadDecrypted(contentOffset).Value);
        Assert.Equal(btree, store.ReadDecrypted(btreeOffset).Value);
        Assert.Equal(metadata, store.ReadDecrypted(metaOffset).Value);
    }

    // A file whose policy CHANGED mid-life: BTreeLeaf blocks were first written under
    // Default (plaintext) and later under Full (encrypted), so the SAME BlockType exists
    // both encrypted and plaintext in one file. Every block must still read back correctly
    // by its per-block Encrypted FLAG — never by consulting a single policy table for the
    // type. The reader is deliberately configured with Default policy (which says BTreeLeaf
    // is plaintext) yet still decrypts the Full-written encrypted BTreeLeaf, proving the read
    // path routes off the header flag, not the policy.
    [Fact]
    public void PolicyChangedMidLife_SameBlockTypeEncryptedAndPlaintext_ReadsByFlag()
    {
        var plainLeaf = "btree leaf written under Default (plaintext)"u8.ToArray();
        var encLeaf = "btree leaf written under Full (encrypted)"u8.ToArray();
        long plainLeafOffset, encLeafOffset;

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);

        // Phase 1 — Default policy: BTreeLeaf is plaintext.
        var defaultStore = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
        var a = defaultStore.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, plainLeaf);
        Ok(a);
        plainLeafOffset = a.Value.Offset;

        // Phase 2 — policy changed to Full: the SAME BlockType is now encrypted.
        var fullStore = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Full);
        var b = fullStore.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, encLeaf);
        Ok(b);
        encLeafOffset = b.Value.Offset;

        // Same BlockType, opposite on-disk treatment — one plaintext, one ciphertext.
        var plainHeader = manager.Read(plainLeafOffset).Value.Header;
        var encHeader = manager.Read(encLeafOffset).Value.Header;
        Assert.Equal(BlockType.BTreeLeaf, plainHeader.Type);
        Assert.Equal(BlockType.BTreeLeaf, encHeader.Type);
        Assert.False(plainHeader.IsEncrypted);          // flag clear
        Assert.True(encHeader.IsEncrypted);             // flag set
        Assert.Equal(0, plainHeader.KeyEpoch);
        Assert.Equal(ActiveEpoch, encHeader.KeyEpoch);
        Assert.Equal(plainLeaf, manager.Read(plainLeafOffset).Value.Payload);   // stored verbatim
        Assert.NotEqual(encLeaf, manager.Read(encLeafOffset).Value.Payload);    // ciphertext on disk

        // Read every block through a store whose policy (Default) DISAGREES with how the
        // encrypted leaf was written. Correct reads prove routing is by the flag, not policy.
        var readerByDefault = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
        Assert.Equal(plainLeaf, readerByDefault.ReadDecrypted(plainLeafOffset).Value);
        Assert.Equal(encLeaf, readerByDefault.ReadDecrypted(encLeafOffset).Value);

        // And with the WRONG DEK: the plaintext leaf still reads (flag clear bypasses the DEK)
        // while the encrypted leaf of the SAME type needs the key — the flag, not the type,
        // decides per block.
        using var wrongKey = new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, WrongDek()) });
        var wrongReader = new EncryptedBlockStore(manager, wrongKey, EncryptionPolicy.Full);
        Assert.Equal(plainLeaf, wrongReader.ReadDecrypted(plainLeafOffset).Value);
        Assert.Throws<WrongKeyOrTamperError>(() => wrongReader.ReadDecrypted(encLeafOffset));
    }

    private static byte[] WrongDek()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 200);
        return dek;
    }

    // ---- Default policy: on-disk proof per named block type (US-EMDB-78-1) ----
    //
    // The story acceptance criterion names each block type explicitly. These two
    // theories exercise the real write path once per named type under the DEFAULT
    // policy and assert the actual on-disk bytes (not just the header flag): the
    // encrypted set stores ciphertext that differs from the plaintext, the plaintext
    // set stores the payload verbatim.

    // Default ENCRYPTS content, every folder block, WAL, every FTS block type, and
    // the bloom filter: flag set, KeyEpoch stamped, on-disk payload is ciphertext
    // (differs from the plaintext) yet still decrypts back to the plaintext.
    [Theory]
    [InlineData(BlockType.EmailContent)]         // content
    [InlineData(BlockType.FolderTree)]           // folders
    [InlineData(BlockType.FolderPage)]
    [InlineData(BlockType.FolderPageDirectory)]
    [InlineData(BlockType.FolderDeltaLog)]
    [InlineData(BlockType.WAL)]                   // WAL
    [InlineData(BlockType.FTSSegmentMeta)]        // FTS (all four)
    [InlineData(BlockType.FTSTermDictionary)]
    [InlineData(BlockType.FTSPostingList)]
    [InlineData(BlockType.FTSSearchRoot)]
    [InlineData(BlockType.BloomFilter)]           // bloom
    public void Default_WritesEncryptedOnDisk_ForContentFoldersWalFtsBloom(BlockType type)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes($"default-encrypts {type}");
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        var appended = store.Append(type, PayloadEncoding.RawBytes, plaintext);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.True(read.Value.Header.IsEncrypted);             // Flags bit 0 set
        Assert.Equal(ActiveEpoch, read.Value.Header.KeyEpoch);  // epoch stamped
        Assert.NotEqual(plaintext, read.Value.Payload);         // on-disk bytes are ciphertext

        var decrypted = store.ReadDecrypted(appended.Value.Offset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);               // round-trips to plaintext
    }

    // Default leaves the B+-tree structure and the recovery blocks PLAINTEXT: flag
    // clear, no epoch, and the on-disk payload is the exact plaintext (readable as-is).
    [Theory]
    [InlineData(BlockType.BTreeLeaf)]      // BTree structure
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    [InlineData(BlockType.Metadata)]       // Metadata
    [InlineData(BlockType.Checkpoint)]     // Checkpoint
    public void Default_WritesPlaintextOnDisk_ForBTreeMetadataCheckpoint(BlockType type)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes($"default-plaintext {type}");
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        var appended = store.Append(type, PayloadEncoding.RawBytes, payload);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.False(read.Value.Header.IsEncrypted);   // flag clear
        Assert.Equal(0, read.Value.Header.KeyEpoch);   // no epoch stamped
        Assert.Equal(payload, read.Value.Payload);     // stored verbatim on disk

        var readBack = store.ReadDecrypted(appended.Value.Offset);
        Ok(readBack);
        Assert.Equal(payload, readBack.Value);         // readable as-is through the store
    }

    // ---- Full policy: on-disk proof per named block type (US-EMDB-78-2) --------
    //
    // Story acceptance criterion: "Full policy encrypts everything except
    // Metadata/Cleanup/Checkpoint/KeyStore-rules per spec" (spec Section 9.5,
    // docs/Encryption.md Section 4). These two theories exercise the real write
    // path once per named type under the FULL policy and assert the actual on-disk
    // bytes: Full encrypts everything content/content-derived PLUS the structural
    // B+-tree blocks (4-6) that Default leaves plaintext; only the recovery blocks
    // and the KEK-sealed KeyStore stay plaintext on the DEK write path.

    // Full ENCRYPTS the entire content/content-derived set AND the B+-tree
    // structure (BTreeLeaf/BTreeInternal/IndexRoot) that Default leaves plaintext,
    // plus the vector sidecar blocks: flag set, KeyEpoch stamped, on-disk payload
    // is ciphertext (differs from the plaintext) yet still decrypts back.
    [Theory]
    [InlineData(BlockType.EmailContent)]         // content
    [InlineData(BlockType.EmailMetadata)]
    [InlineData(BlockType.FolderTree)]           // folders
    [InlineData(BlockType.FolderPage)]
    [InlineData(BlockType.FolderPageDirectory)]
    [InlineData(BlockType.FolderDeltaLog)]
    [InlineData(BlockType.WAL)]                   // WAL
    [InlineData(BlockType.FTSSegmentMeta)]        // FTS (all four)
    [InlineData(BlockType.FTSTermDictionary)]
    [InlineData(BlockType.FTSPostingList)]
    [InlineData(BlockType.FTSSearchRoot)]
    [InlineData(BlockType.BloomFilter)]           // bloom
    [InlineData(BlockType.BTreeLeaf)]             // B+-tree structure — plaintext under Default
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    [InlineData(BlockType.EmbeddingContent)]      // vector sidecar
    [InlineData(BlockType.VectorIndexNode)]
    [InlineData(BlockType.VectorIndexRoot)]
    public void Full_WritesEncryptedOnDisk_ForEverythingExceptExceptions(BlockType type)
    {
        // Guard: every type asserted here really is a type Full must encrypt.
        Assert.True(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Full, type));

        var plaintext = System.Text.Encoding.UTF8.GetBytes($"full-encrypts {type}");
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Full);

        var appended = store.Append(type, PayloadEncoding.RawBytes, plaintext);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.True(read.Value.Header.IsEncrypted);             // Flags bit 0 set
        Assert.Equal(ActiveEpoch, read.Value.Header.KeyEpoch);  // epoch stamped
        Assert.NotEqual(plaintext, read.Value.Payload);         // on-disk bytes are ciphertext

        var decrypted = store.ReadDecrypted(appended.Value.Offset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);               // round-trips to plaintext
    }

    // Full leaves the recovery blocks (Metadata/Cleanup/Checkpoint) and the
    // KeyStore PLAINTEXT on the DEK write path: flag clear, no epoch, on-disk
    // payload is the exact plaintext. KeyStore is KEK-sealed on its own dedicated
    // path (KeyStoreSerializer) — a DEK policy never encrypts it here.
    [Theory]
    [InlineData(BlockType.Metadata)]       // Metadata — read before keys exist
    [InlineData(BlockType.Cleanup)]        // Cleanup
    [InlineData(BlockType.Checkpoint)]     // Checkpoint
    [InlineData(BlockType.KeyStore)]       // KeyStore — KEK-sealed on its own path
    public void Full_WritesPlaintextOnDisk_ForRecoveryAndKeyStore(BlockType type)
    {
        // Guard: every type asserted here really is a Full exception (not DEK-encrypted).
        Assert.False(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Full, type));

        var payload = System.Text.Encoding.UTF8.GetBytes($"full-plaintext {type}");
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Full);

        var appended = store.Append(type, PayloadEncoding.RawBytes, payload);
        Ok(appended);

        var read = manager.Read(appended.Value.Offset);
        Ok(read);
        Assert.False(read.Value.Header.IsEncrypted);   // flag clear
        Assert.Equal(0, read.Value.Header.KeyEpoch);   // no epoch stamped
        Assert.Equal(payload, read.Value.Payload);     // stored verbatim on disk

        var readBack = store.ReadDecrypted(appended.Value.Offset);
        Ok(readBack);
        Assert.Equal(payload, readBack.Value);         // readable as-is through the store
    }
}
