using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-46-4: Verify that all data remains readable after compaction
/// regardless of whether re-encrypt was enabled. Test both paths.
/// </summary>
public class BTreeCompactionDataReadableTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionDataReadableTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private static EmailHashedID CreateTestKey(int seed)
    {
        var hash = new byte[32];
        BitConverter.TryWriteBytes(hash.AsSpan(0, 4), seed);
        return new EmailHashedID(hash);
    }

    private static KeyWrappingEncryptionProvider CreateProvider(
        byte activeEpoch, params (byte epoch, byte[] dek)[] deks)
    {
        var entries = deks.Select(d => new KeyStoreEntry
        {
            Epoch = d.epoch,
            DEK = d.dek,
            Timestamp = DateTime.UtcNow,
            Retired = false
        }).ToList();

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = entries
        };

        return new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);
    }

    // --- Compaction WITHOUT re-encryption: encrypted data remains readable ---

    [Fact]
    public async Task CompactWithoutReEncrypt_EncryptedData_AllLookupsSucceed()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"no_reencrypt_encrypted_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"no_reencrypt_encrypted_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert entries at epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 15; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert more
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 15; i < 30; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }

        // Compact WITHOUT re-encryption (normal space reclamation preserving encryption)
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify all 30 entries are readable with both DEKs
        using var verifyProvider = CreateProvider(1, (0, dek0), (1, dek1));
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        var latestRoot = await GetLatestIndexRoot(verifyManager, verifyProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, verifyProvider),
            encryptionProvider: verifyProvider);

        for (int i = 0; i < 30; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable after compaction without re-encrypt: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    // --- Compaction WITH re-encryption: all data readable ---

    [Fact]
    public async Task CompactWithReEncrypt_AllLookupsSucceed()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reencrypt_readable_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"reencrypt_readable_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert entries at epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 15; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1, insert more
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 15; i < 30; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }

        // Compact WITH re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify all 30 entries are readable with only the active DEK
        using var activeOnlyProvider = CreateProvider(1, (1, dek1));
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        var latestRoot = await GetLatestIndexRoot(verifyManager, activeOnlyProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, activeOnlyProvider),
            encryptionProvider: activeOnlyProvider);

        for (int i = 0; i < 30; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable after compaction with re-encrypt: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    // --- Compaction WITHOUT re-encryption: unencrypted data remains readable ---

    [Fact]
    public async Task CompactWithoutReEncrypt_UnencryptedData_AllLookupsSucceed()
    {
        var filePath = Path.Combine(_tempDir, $"no_reencrypt_plain_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"no_reencrypt_plain_compacted_{Guid.NewGuid():N}.emdb");

        var insertedKeys = new List<(EmailHashedID Key, long BlockOffset, long BlockId)>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
                insertedKeys.Add((key, i * 100, i));
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        foreach (var (key, blockOffset, blockId) in insertedKeys)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess,
                $"Lookup failed for key ({key}) after unencrypted compaction: {lookupResult.Error}");
            Assert.Equal(blockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);
        }
    }

    // --- Both paths produce identical logical data ---

    [Fact]
    public async Task BothPaths_SameEntriesReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"both_paths_{Guid.NewGuid():N}.emdb");
        var compactedNoReEncrypt = Path.Combine(_tempDir, $"both_no_reencrypt_{Guid.NewGuid():N}.emdb");
        var compactedWithReEncrypt = Path.Combine(_tempDir, $"both_with_reencrypt_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Build a tree with mixed epochs
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 10; i < 25; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact both ways from the same source
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedNoReEncrypt, provider1);

        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedWithReEncrypt, provider1);
        provider1.Dispose();

        // Verify path 1: without re-encrypt (needs both DEKs)
        using var bothDeksProvider = CreateProvider(1, (0, dek0), (1, dek1));
        using var verifyNoReEncrypt = new RawBlockManager(compactedNoReEncrypt, createIfNotExists: false);
        var rootNoReEncrypt = await GetLatestIndexRoot(verifyNoReEncrypt, bothDeksProvider);
        Assert.NotNull(rootNoReEncrypt);

        var indexNoReEncrypt = new BTreeIndex(
            verifyNoReEncrypt, rootNoReEncrypt,
            await GetLatestIndexRootOffset(verifyNoReEncrypt, bothDeksProvider),
            encryptionProvider: bothDeksProvider);

        // Verify path 2: with re-encrypt (only needs active DEK)
        using var activeOnlyProvider = CreateProvider(1, (1, dek1));
        using var verifyWithReEncrypt = new RawBlockManager(compactedWithReEncrypt, createIfNotExists: false);
        var rootWithReEncrypt = await GetLatestIndexRoot(verifyWithReEncrypt, activeOnlyProvider);
        Assert.NotNull(rootWithReEncrypt);

        var indexWithReEncrypt = new BTreeIndex(
            verifyWithReEncrypt, rootWithReEncrypt,
            await GetLatestIndexRootOffset(verifyWithReEncrypt, activeOnlyProvider),
            encryptionProvider: activeOnlyProvider);

        // Both paths must return the same data for all 25 entries
        for (int i = 0; i < 25; i++)
        {
            var key = CreateTestKey(i);

            var lookupA = await indexNoReEncrypt.LookupAsync(key);
            Assert.True(lookupA.IsSuccess,
                $"Key {i} not readable after compaction without re-encrypt: {lookupA.Error}");

            var lookupB = await indexWithReEncrypt.LookupAsync(key);
            Assert.True(lookupB.IsSuccess,
                $"Key {i} not readable after compaction with re-encrypt: {lookupB.Error}");

            Assert.Equal(lookupA.Value.BlockOffset, lookupB.Value.BlockOffset);
            Assert.Equal(lookupA.Value.BlockId, lookupB.Value.BlockId);
        }
    }

    // --- Large tree with internal nodes: both paths ---

    [Fact]
    public async Task LargeTree_BothPaths_AllLookupsSucceed()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"large_both_{Guid.NewGuid():N}.emdb");
        var compactedNoReEncrypt = Path.Combine(_tempDir, $"large_no_reencrypt_{Guid.NewGuid():N}.emdb");
        var compactedWithReEncrypt = Path.Combine(_tempDir, $"large_with_reencrypt_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert enough to create internal nodes
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        int count = BTreeLeafNode.MaxEntries + 20;
        for (int i = 0; i < count; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        Assert.True(btree0.CurrentRoot!.TreeHeight >= 2, "Tree should have internal nodes");
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = count; i < count + 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        int totalEntries = count + 20;

        // Compact without re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFilePreservingEncryption(rawBlockManager, compactedNoReEncrypt, provider1);

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedWithReEncrypt, provider1);
        provider1.Dispose();

        // Verify without re-encrypt path (needs both DEKs)
        using var bothDeksProvider = CreateProvider(1, (0, dek0), (1, dek1));
        using var verifyNoReEncrypt = new RawBlockManager(compactedNoReEncrypt, createIfNotExists: false);
        var rootNoReEncrypt = await GetLatestIndexRoot(verifyNoReEncrypt, bothDeksProvider);
        Assert.NotNull(rootNoReEncrypt);

        var indexNoReEncrypt = new BTreeIndex(
            verifyNoReEncrypt, rootNoReEncrypt,
            await GetLatestIndexRootOffset(verifyNoReEncrypt, bothDeksProvider),
            encryptionProvider: bothDeksProvider);

        for (int i = 0; i < totalEntries; i++)
        {
            var lookup = await indexNoReEncrypt.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} not readable after large tree compaction without re-encrypt: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
        }

        // Verify with re-encrypt path (only active DEK)
        using var activeOnlyProvider = CreateProvider(1, (1, dek1));
        using var verifyWithReEncrypt = new RawBlockManager(compactedWithReEncrypt, createIfNotExists: false);
        var rootWithReEncrypt = await GetLatestIndexRoot(verifyWithReEncrypt, activeOnlyProvider);
        Assert.NotNull(rootWithReEncrypt);

        var indexWithReEncrypt = new BTreeIndex(
            verifyWithReEncrypt, rootWithReEncrypt,
            await GetLatestIndexRootOffset(verifyWithReEncrypt, activeOnlyProvider),
            encryptionProvider: activeOnlyProvider);

        for (int i = 0; i < totalEntries; i++)
        {
            var lookup = await indexWithReEncrypt.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} not readable after large tree compaction with re-encrypt: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
        }
    }

    // --- Three-epoch data readable after re-encryption ---

    [Fact]
    public async Task ThreeEpochs_CompactWithReEncrypt_AllDataReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"three_epoch_readable_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"three_epoch_readable_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 8; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 8; i < 16; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root1 = btree1.CurrentRoot;
        provider1.Dispose();

        // Epoch 2
        var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);
        for (int i = 16; i < 24; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact with re-encryption to epoch 2
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider2);
        provider2.Dispose();

        // Verify all 24 entries readable with only active DEK (epoch 2)
        using var activeOnlyProvider = CreateProvider(2, (2, dek2));
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        var latestRoot = await GetLatestIndexRoot(verifyManager, activeOnlyProvider);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager, activeOnlyProvider),
            encryptionProvider: activeOnlyProvider);

        for (int i = 0; i < 24; i++)
        {
            var lookup = await verifyIndex.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess,
                $"Key {i} should be readable with only epoch-2 DEK after 3-epoch re-encryption: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    #region Compaction Helpers

    /// <summary>
    /// Compacts the live tree to a new file WITHOUT re-encryption.
    /// Preserves original encryption state on each block (normal space reclamation).
    /// </summary>
    private static async Task CompactLiveTreeToFilePreservingEncryption(
        RawBlockManager source, string destPath, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source, encryptionProvider);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUpPreservingEncryption(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight,
            encryptionProvider);

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = newRootOffset,
            EntryCount = latestRoot.Value.Root.EntryCount,
            TreeHeight = latestRoot.Value.Root.TreeHeight,
            RootNodeHash = newRootHash,
            PreviousRootHash = new byte[32],
            PreviousRootOffset = -1
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };

        // Preserve encryption on IndexRoot if originally encrypted
        if (encryptionProvider != null && encryptionProvider.IsEnabled &&
            encryptionProvider.ShouldEncrypt(BlockType.IndexRoot))
        {
            rootBlock.Payload = encryptionProvider.Encrypt(rootPayload, BlockType.IndexRoot, rootBlock.BlockId);
            rootBlock.Flags |= Block.FlagEncrypted;
            rootBlock.SetKeyEpoch((byte)encryptionProvider.ActiveEpoch);
        }

        await dest.WriteBlockAsync(rootBlock);
    }

    /// <summary>
    /// Copies subtree bottom-up preserving original encryption.
    /// Internal node ChildOffsets are remapped and Merkle hashes recomputed,
    /// but payloads are re-encrypted with the SAME epoch's DEK (preserving key epoch).
    /// </summary>
    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUpPreservingEncryption(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight,
        IBlockEncryptionProvider? encryptionProvider = null)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        var sourceBlock = readResult.Value;

        if (remainingHeight == 1)
        {
            // Leaf: copy as-is (payload stays encrypted with original epoch)
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId();
            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = sourceBlock.Flags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = sourceBlock.Payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            // Decrypt to get content hash for Merkle verification
            byte[] decryptedPayload = sourceBlock.IsEncrypted && encryptionProvider != null
                ? encryptionProvider.Decrypt(sourceBlock.Payload, sourceBlock.Type,
                    sourceBlock.BlockId, sourceBlock.KeyEpoch)
                : sourceBlock.Payload;

            var leaf = BTreeNodeSerializer.DeserializeLeaf(decryptedPayload);
            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            // Decrypt to read child offsets
            byte[] decryptedPayload = sourceBlock.IsEncrypted && encryptionProvider != null
                ? encryptionProvider.Decrypt(sourceBlock.Payload, sourceBlock.Type,
                    sourceBlock.BlockId, sourceBlock.KeyEpoch)
                : sourceBlock.Payload;

            var internalNode = BTreeNodeSerializer.DeserializeInternal(decryptedPayload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUpPreservingEncryption(
                    source, dest, positionToBlockId,
                    internalNode.ChildOffsets[i], remainingHeight - 1,
                    encryptionProvider);
                newChildOffsets[i] = childOffset;
                newChildHashes[i] = childHash;
            }

            // Remap offsets and recompute hash
            var remappedNode = new BTreeInternalNode
            {
                NodeType = internalNode.NodeType,
                Version = internalNode.Version,
                KeyCount = internalNode.KeyCount,
                PrevChainHash = new byte[32],
                Keys = internalNode.Keys,
                ChildOffsets = newChildOffsets,
                ChildHashes = newChildHashes
            };
            remappedNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(remappedNode);

            var payload = BTreeNodeSerializer.SerializeInternal(remappedNode);
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId();
            byte newFlags = 0;
            byte[] newPayload;

            if (sourceBlock.IsEncrypted && encryptionProvider != null &&
                encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(sourceBlock.Type))
            {
                // Re-encrypt with the SAME active epoch (preserving encryption, not re-keying)
                newPayload = encryptionProvider.Encrypt(payload, sourceBlock.Type, newBlockId);
                newFlags = (byte)(Block.FlagEncrypted | ((byte)encryptionProvider.ActiveEpoch << 1));
            }
            else
            {
                newPayload = payload;
            }

            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = newFlags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = newPayload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write internal node: {writeResult.Error}");

            return (writeResult.Value.Position, remappedNode.NodeContentHash);
        }
    }

    /// <summary>
    /// Compacts the live tree to a new file WITH re-encryption.
    /// All blocks are decrypted with their original DEK and re-encrypted with the active DEK.
    /// </summary>
    private static async Task CompactLiveTreeToFileWithReEncrypt(
        RawBlockManager source, string destPath, IBlockEncryptionProvider encryptionProvider)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source, encryptionProvider);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUpWithReEncrypt(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight,
            encryptionProvider);

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = newRootOffset,
            EntryCount = latestRoot.Value.Root.EntryCount,
            TreeHeight = latestRoot.Value.Root.TreeHeight,
            RootNodeHash = newRootHash,
            PreviousRootHash = new byte[32],
            PreviousRootOffset = -1
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };

        if (encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(BlockType.IndexRoot))
        {
            rootBlock.Payload = encryptionProvider.Encrypt(rootPayload, BlockType.IndexRoot, rootBlock.BlockId);
            rootBlock.Flags |= Block.FlagEncrypted;
            rootBlock.SetKeyEpoch((byte)encryptionProvider.ActiveEpoch);
        }

        await dest.WriteBlockAsync(rootBlock);
    }

    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUpWithReEncrypt(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight,
        IBlockEncryptionProvider encryptionProvider)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        var sourceBlock = readResult.Value;

        byte[] decryptedPayload;
        if (sourceBlock.IsEncrypted)
        {
            decryptedPayload = encryptionProvider.Decrypt(
                sourceBlock.Payload, sourceBlock.Type, sourceBlock.BlockId, sourceBlock.KeyEpoch);
        }
        else
        {
            decryptedPayload = sourceBlock.Payload;
        }

        if (remainingHeight == 1)
        {
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId();
            byte[] newPayload;
            byte newFlags = 0;

            if (encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(sourceBlock.Type))
            {
                newPayload = encryptionProvider.Encrypt(decryptedPayload, sourceBlock.Type, newBlockId);
                newFlags = (byte)(Block.FlagEncrypted | ((byte)encryptionProvider.ActiveEpoch << 1));
            }
            else
            {
                newPayload = decryptedPayload;
            }

            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = newFlags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = newPayload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            var leaf = BTreeNodeSerializer.DeserializeLeaf(decryptedPayload);
            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(decryptedPayload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUpWithReEncrypt(
                    source, dest, positionToBlockId,
                    internalNode.ChildOffsets[i], remainingHeight - 1,
                    encryptionProvider);
                newChildOffsets[i] = childOffset;
                newChildHashes[i] = childHash;
            }

            var remappedNode = new BTreeInternalNode
            {
                NodeType = internalNode.NodeType,
                Version = internalNode.Version,
                KeyCount = internalNode.KeyCount,
                PrevChainHash = new byte[32],
                Keys = internalNode.Keys,
                ChildOffsets = newChildOffsets,
                ChildHashes = newChildHashes
            };
            remappedNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(remappedNode);

            var payload = BTreeNodeSerializer.SerializeInternal(remappedNode);
            var newBlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId();
            byte[] newPayload;
            byte newFlags = 0;

            if (encryptionProvider.IsEnabled && encryptionProvider.ShouldEncrypt(sourceBlock.Type))
            {
                newPayload = encryptionProvider.Encrypt(payload, sourceBlock.Type, newBlockId);
                newFlags = (byte)(Block.FlagEncrypted | ((byte)encryptionProvider.ActiveEpoch << 1));
            }
            else
            {
                newPayload = payload;
            }

            var newBlock = new Block
            {
                Version = sourceBlock.Version,
                Type = sourceBlock.Type,
                Flags = newFlags,
                Timestamp = sourceBlock.Timestamp,
                BlockId = newBlockId,
                Payload = newPayload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write internal node: {writeResult.Error}");

            return (writeResult.Value.Position, remappedNode.NodeContentHash);
        }
    }

    /// <summary>
    /// Compacts an unencrypted tree (no encryption provider).
    /// </summary>
    private static async Task CompactLiveTreeToFile(RawBlockManager source, string destPath)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUp(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight);

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = newRootOffset,
            EntryCount = latestRoot.Value.Root.EntryCount,
            TreeHeight = latestRoot.Value.Root.TreeHeight,
            RootNodeHash = newRootHash,
            PreviousRootHash = new byte[32],
            PreviousRootOffset = -1
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };
        await dest.WriteBlockAsync(rootBlock);
    }

    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUp(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        if (remainingHeight == 1)
        {
            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
                Payload = readResult.Value.Payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUp(
                    source, dest, positionToBlockId,
                    internalNode.ChildOffsets[i], remainingHeight - 1);
                newChildOffsets[i] = childOffset;
                newChildHashes[i] = childHash;
            }

            var remappedNode = new BTreeInternalNode
            {
                NodeType = internalNode.NodeType,
                Version = internalNode.Version,
                KeyCount = internalNode.KeyCount,
                PrevChainHash = new byte[32],
                Keys = internalNode.Keys,
                ChildOffsets = newChildOffsets,
                ChildHashes = newChildHashes
            };
            remappedNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(remappedNode);

            var payload = BTreeNodeSerializer.SerializeInternal(remappedNode);
            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId(),
                Payload = payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write internal node: {writeResult.Error}");

            return (writeResult.Value.Position, remappedNode.NodeContentHash);
        }
    }

    #endregion

    #region Index Root Helpers

    private static async Task<(IndexRoot Root, long Position)?> FindLatestIndexRoot(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var locations = rawBlockManager.GetBlockLocations();
        IndexRoot? latest = null;
        long maxPosition = -1;

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > maxPosition)
                {
                    maxPosition = kvp.Value.Position;
                    var payload = readResult.Value.Payload;
                    if (readResult.Value.IsEncrypted && encryptionProvider != null)
                    {
                        payload = encryptionProvider.Decrypt(
                            payload, readResult.Value.Type,
                            readResult.Value.BlockId, readResult.Value.KeyEpoch);
                    }
                    latest = BTreeNodeSerializer.DeserializeIndexRoot(payload);
                }
            }
        }

        return latest != null ? (latest, maxPosition) : null;
    }

    private static async Task<IndexRoot?> GetLatestIndexRoot(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var result = await FindLatestIndexRoot(rawBlockManager, encryptionProvider);
        return result?.Root;
    }

    private static async Task<long> GetLatestIndexRootOffset(
        RawBlockManager rawBlockManager, IBlockEncryptionProvider? encryptionProvider = null)
    {
        var result = await FindLatestIndexRoot(rawBlockManager, encryptionProvider);
        return result?.Position ?? -1;
    }

    #endregion
}
