using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-44-4: Verify that BLAKE3 hash chain verification passes after decrypt.
/// Hashes are computed on plaintext before encryption (write path) and must remain
/// valid after decryption (read path). Tests cover NodeContentHash, PrevChainHash,
/// ChildHashes (Merkle), and IndexRoot.RootNodeHash.
/// </summary>
public class BTreeBlake3HashChainAfterDecryptTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeBlake3HashChainAfterDecryptTests()
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

    private (BTreeIndex btree, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider)
        CreateEncryptedBTreeIndex(byte activeEpoch = 0, List<KeyStoreEntry>? entries = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        KeyStoreContent keyStore;
        if (entries != null)
        {
            keyStore = new KeyStoreContent
            {
                ActiveEpoch = activeEpoch,
                Entries = entries
            };
        }
        else
        {
            keyStore = new KeyStoreContent
            {
                ActiveEpoch = activeEpoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = activeEpoch, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
        }

        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);
        var btreeIndex = new BTreeIndex(rawBlockManager, encryptionProvider: provider);

        return (btreeIndex, rawBlockManager, provider);
    }

    private async Task<Block?> ReadRawBlockAtOffset(RawBlockManager rawBlockManager, long offset)
    {
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            if (kvp.Value.Position == offset)
            {
                var result = await rawBlockManager.ReadBlockAsync(kvp.Key);
                return result.IsSuccess ? result.Value : null;
            }
        }
        return null;
    }

    [Fact]
    public async Task LeafNodeContentHash_ValidAfterDecrypt()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        for (int i = 0; i < 5; i++)
        {
            var key = CreateTestKey(i + 1);
            var r = await btree.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Read encrypted block from disk and decrypt
        var block = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(block);
        Assert.True(block!.IsEncrypted, "Block should be encrypted on disk");

        var decryptedPayload = provider.Decrypt(block.Payload, block.Type, block.BlockId, block.KeyEpoch);
        var leaf = BTreeNodeSerializer.DeserializeLeaf(decryptedPayload);

        // Recompute BLAKE3 hash on the decrypted content — must match stored hash
        var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
        Assert.Equal(leaf.NodeContentHash, recomputed);
    }

    [Fact]
    public async Task InternalNodeContentHash_ValidAfterDecrypt()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        // Insert enough to trigger splits and create internal nodes
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = CreateTestKey(i + 1);
            var r = await btree.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btree.CurrentRoot!.TreeHeight >= 2, "Tree must have internal nodes");

        // Read encrypted internal (root) node from disk and decrypt
        var block = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(block);
        Assert.True(block!.IsEncrypted);

        var decryptedPayload = provider.Decrypt(block.Payload, block.Type, block.BlockId, block.KeyEpoch);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(decryptedPayload);

        // Recompute BLAKE3 hash — must match stored hash
        var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
        Assert.Equal(internalNode.NodeContentHash, recomputed);
    }

    [Fact]
    public async Task PrevChainHash_ValidAfterDecrypt()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        // Insert first key — genesis leaf
        var key1 = CreateTestKey(1);
        var r1 = await btree.InsertAsync(key1, 100, 1);
        Assert.True(r1.IsSuccess);

        // Record genesis leaf's offset and read its content hash after decrypt
        var genesisBlock = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(genesisBlock);
        var genesisDecrypted = provider.Decrypt(genesisBlock!.Payload, genesisBlock.Type, genesisBlock.BlockId, genesisBlock.KeyEpoch);
        var genesisLeaf = BTreeNodeSerializer.DeserializeLeaf(genesisDecrypted);
        var genesisHash = BTreeHasher.ComputeLeafContentHash(genesisLeaf);
        Assert.Equal(genesisLeaf.NodeContentHash, genesisHash);

        // Insert second key — successor chains to genesis via PrevChainHash
        var key2 = CreateTestKey(2);
        var r2 = await btree.InsertAsync(key2, 200, 2);
        Assert.True(r2.IsSuccess);

        var successorBlock = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(successorBlock);
        var successorDecrypted = provider.Decrypt(successorBlock!.Payload, successorBlock.Type, successorBlock.BlockId, successorBlock.KeyEpoch);
        var successorLeaf = BTreeNodeSerializer.DeserializeLeaf(successorDecrypted);

        // After decrypt: successor.PrevChainHash must equal genesis.NodeContentHash
        Assert.Equal(genesisLeaf.NodeContentHash, successorLeaf.PrevChainHash);
    }

    [Fact]
    public async Task MerkleChildHashes_ValidAfterDecrypt()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        // Build a 2-level tree
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = CreateTestKey(i + 1);
            var r = await btree.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btree.CurrentRoot!.TreeHeight >= 2);

        // Read and decrypt the root internal node
        var rootBlock = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootDecrypted = provider.Decrypt(rootBlock!.Payload, rootBlock.Type, rootBlock.BlockId, rootBlock.KeyEpoch);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootDecrypted);

        // For each child, read+decrypt and verify parent's ChildHash matches child's computed hash
        int childCount = rootNode.KeyCount + 1;
        for (int i = 0; i < childCount; i++)
        {
            var childBlock = await ReadRawBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childDecrypted = provider.Decrypt(childBlock!.Payload, childBlock.Type, childBlock.BlockId, childBlock.KeyEpoch);
            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childDecrypted);

            var childRecomputed = BTreeHasher.ComputeLeafContentHash(childLeaf);
            Assert.Equal(childLeaf.NodeContentHash, childRecomputed);
            Assert.Equal(rootNode.ChildHashes[i], childRecomputed);
        }
    }

    [Fact]
    public async Task IndexRootRootNodeHash_MatchesDecryptedRootNode()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        for (int i = 0; i < 10; i++)
        {
            var key = CreateTestKey(i + 1);
            var r = await btree.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Read and decrypt root node
        var rootBlock = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootDecrypted = provider.Decrypt(rootBlock!.Payload, rootBlock.Type, rootBlock.BlockId, rootBlock.KeyEpoch);

        byte[] recomputed;
        if (btree.CurrentRoot.TreeHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootDecrypted);
            recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(rootDecrypted);
            recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
        }

        // IndexRoot.RootNodeHash must match the decrypted root's computed hash
        Assert.Equal(btree.CurrentRoot.RootNodeHash, recomputed);
    }

    [Fact]
    public async Task HashChainValid_AfterMultipleInserts_WithEncryption()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        // Track root hashes across multiple inserts to verify PreviousRootHash chain
        var rootHashes = new List<byte[]>();

        for (int i = 0; i < 20; i++)
        {
            var key = CreateTestKey(i + 1);
            var r = await btree.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");

            // Read+decrypt root node and verify its hash matches IndexRoot.RootNodeHash
            var rootBlock = await ReadRawBlockAtOffset(rawBlockManager, btree.CurrentRoot!.RootNodeBlockOffset);
            Assert.NotNull(rootBlock);
            var decrypted = provider.Decrypt(rootBlock!.Payload, rootBlock.Type, rootBlock.BlockId, rootBlock.KeyEpoch);

            byte[] computed;
            if (btree.CurrentRoot.TreeHeight == 1)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(decrypted);
                computed = BTreeHasher.ComputeLeafContentHash(leaf);
                Assert.Equal(leaf.NodeContentHash, computed);
            }
            else
            {
                var internalNode = BTreeNodeSerializer.DeserializeInternal(decrypted);
                computed = BTreeHasher.ComputeInternalContentHash(internalNode);
                Assert.Equal(internalNode.NodeContentHash, computed);
            }

            Assert.Equal(btree.CurrentRoot.RootNodeHash, computed);
            rootHashes.Add(computed);
        }

        // All root hashes should be distinct (each insert changes the tree)
        for (int i = 0; i < rootHashes.Count; i++)
        {
            for (int j = i + 1; j < rootHashes.Count; j++)
            {
                Assert.NotEqual(rootHashes[i], rootHashes[j]);
            }
        }
    }

    [Fact]
    public async Task HashChainValid_AfterDecrypt_WithMultipleKeyEpochs()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"multi_epoch_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Write entries with epoch 0
        var keyStoreEpoch0 = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var providerEpoch0 = new KeyWrappingEncryptionProvider(keyStoreEpoch0, EncryptionPolicy.Full);
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: providerEpoch0);

        for (int i = 0; i < 5; i++)
        {
            var key = CreateTestKey(i + 1);
            await btree0.InsertAsync(key, i * 100, i);
        }

        var rootAfterEpoch0 = btree0.CurrentRoot;
        providerEpoch0.Dispose();

        // Continue writing with epoch 1 (both DEKs available)
        var keyStoreBoth = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerBoth = new KeyWrappingEncryptionProvider(keyStoreBoth, EncryptionPolicy.Full);
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: rootAfterEpoch0,
            existingRootBlockOffset: -1, encryptionProvider: providerBoth);

        for (int i = 5; i < 10; i++)
        {
            var key = CreateTestKey(i + 1);
            await btree1.InsertAsync(key, i * 100, i);
        }

        // Read+decrypt root node (encrypted with epoch 1)
        var rootBlock = await ReadRawBlockAtOffset(rawBlockManager, btree1.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootDecrypted = providerBoth.Decrypt(rootBlock!.Payload, rootBlock.Type, rootBlock.BlockId, rootBlock.KeyEpoch);

        byte[] rootRecomputed;
        if (btree1.CurrentRoot.TreeHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(rootDecrypted);
            rootRecomputed = BTreeHasher.ComputeLeafContentHash(leaf);
            Assert.Equal(leaf.NodeContentHash, rootRecomputed);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(rootDecrypted);
            rootRecomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
            Assert.Equal(internalNode.NodeContentHash, rootRecomputed);
        }

        Assert.Equal(btree1.CurrentRoot.RootNodeHash, rootRecomputed);

        // Also verify all data is still readable through the BTree API
        for (int i = 0; i < 10; i++)
        {
            var key = CreateTestKey(i + 1);
            var result = await btree1.LookupAsync(key);
            Assert.True(result.IsSuccess, $"LookupAsync for key {i + 1} failed: {result.Error}");
            Assert.Equal(i * 100, result.Value.BlockOffset);
        }

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task FullMerkleTree_AllHashes_ValidAfterDecrypt()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        // Build a large enough tree to have multiple levels
        for (int i = 0; i < BTreeLeafNode.MaxEntries * 3; i++)
        {
            var key = CreateTestKey(i + 1);
            var r = await btree.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btree.CurrentRoot!.TreeHeight >= 2);

        // Recursively verify the entire Merkle tree after decrypt
        await VerifyNodeHashesRecursive(rawBlockManager, provider, btree.CurrentRoot.RootNodeBlockOffset,
            btree.CurrentRoot.TreeHeight, btree.CurrentRoot.RootNodeHash);
    }

    private async Task VerifyNodeHashesRecursive(
        RawBlockManager rawBlockManager,
        KeyWrappingEncryptionProvider provider,
        long offset,
        int remainingHeight,
        byte[] expectedHash)
    {
        var block = await ReadRawBlockAtOffset(rawBlockManager, offset);
        Assert.NotNull(block);

        var decrypted = provider.Decrypt(block!.Payload, block.Type, block.BlockId, block.KeyEpoch);

        if (remainingHeight == 1)
        {
            // Leaf node
            var leaf = BTreeNodeSerializer.DeserializeLeaf(decrypted);
            var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
            Assert.Equal(leaf.NodeContentHash, recomputed);
            Assert.Equal(expectedHash, recomputed);
        }
        else
        {
            // Internal node
            var internalNode = BTreeNodeSerializer.DeserializeInternal(decrypted);
            var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
            Assert.Equal(internalNode.NodeContentHash, recomputed);
            Assert.Equal(expectedHash, recomputed);

            // Recurse into children
            int childCount = internalNode.KeyCount + 1;
            for (int i = 0; i < childCount; i++)
            {
                await VerifyNodeHashesRecursive(rawBlockManager, provider,
                    internalNode.ChildOffsets[i], remainingHeight - 1, internalNode.ChildHashes[i]);
            }
        }
    }
}
