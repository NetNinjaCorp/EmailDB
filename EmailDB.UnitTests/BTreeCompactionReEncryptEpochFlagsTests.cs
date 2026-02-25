using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-46-3: Verify that after compaction with re-encrypt, every encrypted
/// block's Flags bits 1-7 match the current active epoch.
/// </summary>
public class BTreeCompactionReEncryptEpochFlagsTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionReEncryptEpochFlagsTests()
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

    // --- Flags bits 1-7 match active epoch after two-epoch re-encryption ---

    [Fact]
    public async Task CompactWithReEncrypt_TwoEpochs_FlagsBits1Through7MatchActiveEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"epoch_flags_two_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"epoch_flags_two_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Phase 1: Insert entries with epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 10; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree0.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 0 insert {i} failed: {r.Error}");
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Phase 2: Rotate to epoch 1, insert more
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 10; i < 20; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree1.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Epoch 1 insert {i} failed: {r.Error}");
        }

        // Verify mixed epochs exist before compaction
        var epochsBefore = new HashSet<int>();
        foreach (var kvp in rawBlockManager.GetBlockLocations())
        {
            var br = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (br.IsSuccess && br.Value.IsEncrypted)
                epochsBefore.Add(br.Value.KeyEpoch);
        }
        Assert.Contains(0, epochsBefore);
        Assert.Contains(1, epochsBefore);

        // Compact with re-encryption, active epoch = 1
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify: every encrypted block's Flags bits 1-7 == active epoch (1)
        byte activeEpoch = 1;
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        int encryptedCount = 0;

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read compacted block {kvp.Key}");

            if (readResult.Value.IsEncrypted)
            {
                encryptedCount++;
                byte extractedEpoch = (byte)((readResult.Value.Flags >> 1) & 0x7F);
                Assert.Equal(activeEpoch, extractedEpoch);
                Assert.Equal(activeEpoch, readResult.Value.KeyEpoch);
            }
        }

        Assert.True(encryptedCount > 0, "Should have at least one encrypted block after compaction");
    }

    // --- Flags bits 1-7 match active epoch after three-epoch re-encryption ---

    [Fact]
    public async Task CompactWithReEncrypt_ThreeEpochs_FlagsBits1Through7MatchActiveEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"epoch_flags_three_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"epoch_flags_three_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 5; i++)
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
        for (int i = 5; i < 10; i++)
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
        for (int i = 10; i < 15; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Verify at least 2 different epochs before compaction
        var epochsBefore = new HashSet<int>();
        foreach (var kvp in rawBlockManager.GetBlockLocations())
        {
            var br = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (br.IsSuccess && br.Value.IsEncrypted)
                epochsBefore.Add(br.Value.KeyEpoch);
        }
        Assert.True(epochsBefore.Count >= 2, "Should have blocks from at least 2 different epochs");

        // Compact with re-encryption, active epoch = 2
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider2);
        provider2.Dispose();

        // Verify: every encrypted block's Flags bits 1-7 == active epoch (2)
        byte activeEpoch = 2;
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        int encryptedCount = 0;

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read compacted block {kvp.Key}");

            if (readResult.Value.IsEncrypted)
            {
                encryptedCount++;
                byte extractedEpoch = (byte)((readResult.Value.Flags >> 1) & 0x7F);
                Assert.Equal(activeEpoch, extractedEpoch);
                Assert.Equal(activeEpoch, readResult.Value.KeyEpoch);
            }
        }

        Assert.True(encryptedCount > 0, "Should have at least one encrypted block after compaction");
    }

    // --- Flags byte encoding: encrypted bit (0) + epoch bits (1-7) ---

    [Fact]
    public async Task CompactWithReEncrypt_FlagsByteEncodingCorrect()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"flags_encoding_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"flags_encoding_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert with epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 10; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Rotate to epoch 1
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 10; i < 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact with re-encryption, active epoch = 1
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify: each encrypted block's Flags byte == FlagEncrypted | (activeEpoch << 1)
        byte expectedFlags = (byte)(Block.FlagEncrypted | (1 << 1)); // 0x03
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.IsEncrypted)
            {
                Assert.Equal(expectedFlags, readResult.Value.Flags);
                Assert.True(readResult.Value.IsEncrypted, "Encrypted bit (bit 0) should be set");
                Assert.Equal(1, readResult.Value.KeyEpoch);
            }
        }
    }

    // --- Large tree with internal nodes: all blocks get active epoch ---

    [Fact]
    public async Task CompactWithReEncrypt_LargeTreeWithInternalNodes_AllFlagsBitsMatchActiveEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"flags_large_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"flags_large_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: enough entries to create internal nodes
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

        // Epoch 1: more entries
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = count; i < count + 20; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Compact with re-encryption
        BlockIdGenerator.Instance.Reset();
        await CompactLiveTreeToFileWithReEncrypt(rawBlockManager, compactedPath, provider1);
        provider1.Dispose();

        // Verify: all encrypted blocks (both leaf and internal) have epoch 1 in bits 1-7
        byte activeEpoch = 1;
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        int leafCount = 0;
        int internalCount = 0;
        int indexRootCount = 0;

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.IsEncrypted)
            {
                byte extractedEpoch = (byte)((readResult.Value.Flags >> 1) & 0x7F);
                Assert.Equal(activeEpoch, extractedEpoch);

                switch (readResult.Value.Type)
                {
                    case BlockType.BTreeLeaf:
                        leafCount++;
                        break;
                    case BlockType.BTreeInternal:
                        internalCount++;
                        break;
                    case BlockType.IndexRoot:
                        indexRootCount++;
                        break;
                }
            }
        }

        Assert.True(leafCount > 0, "Should have encrypted leaf blocks");
        Assert.True(internalCount > 0, "Should have encrypted internal blocks");
    }

    // --- No stale epoch in any block after re-encryption ---

    [Fact]
    public async Task CompactWithReEncrypt_NoStaleEpochsRemain()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"no_stale_{Guid.NewGuid():N}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"no_stale_compacted_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Insert across 3 epochs
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 8; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

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

        // Verify: no block has epoch 0 or epoch 1 — only epoch 2 (or unencrypted)
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var epochsAfter = new HashSet<int>();

        foreach (var kvp in verifyManager.GetBlockLocations())
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.IsEncrypted)
            {
                epochsAfter.Add(readResult.Value.KeyEpoch);
                Assert.Equal(2, readResult.Value.KeyEpoch);
            }
        }

        Assert.Single(epochsAfter);
        Assert.Contains(2, epochsAfter);
    }

    #region Compaction with Re-encryption Helper

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

    #endregion
}
