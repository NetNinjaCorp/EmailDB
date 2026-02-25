using System.Reflection;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "CompactAsync accepts optional re-encrypt
/// flag (default false)". When reEncrypt is false (the default), compaction
/// performs normal space reclamation without altering block encryption state.
/// </summary>
public class BTreeCompactionReEncryptFlagTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionReEncryptFlagTests()
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

    // --- CompactAsync accepts reEncrypt parameter with default false ---

    [Fact]
    public void CompactAsync_HasReEncryptParameter()
    {
        // Verify via reflection that CompactAsync has a 'reEncrypt' parameter of type bool.
        var method = typeof(RawBlockManager).GetMethod("CompactAsync",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        var reEncryptParam = parameters.FirstOrDefault(p => p.Name == "reEncrypt");

        Assert.NotNull(reEncryptParam);
        Assert.Equal(typeof(bool), reEncryptParam!.ParameterType);
    }

    [Fact]
    public void CompactAsync_ReEncryptDefaultsToFalse()
    {
        // Verify via reflection that reEncrypt has a default value of false.
        var method = typeof(RawBlockManager).GetMethod("CompactAsync",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var reEncryptParam = method!.GetParameters().First(p => p.Name == "reEncrypt");

        Assert.True(reEncryptParam.HasDefaultValue,
            "reEncrypt parameter should have a default value");
        Assert.Equal(false, reEncryptParam.DefaultValue);
    }

    [Fact]
    public void CompactAsync_ReEncryptIsFirstParameter()
    {
        // reEncrypt should be the first parameter (before CancellationToken).
        var method = typeof(RawBlockManager).GetMethod("CompactAsync",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.True(parameters.Length >= 1, "CompactAsync should have at least 1 parameter");
        Assert.Equal("reEncrypt", parameters[0].Name);
    }

    // --- Default false means normal space reclamation (no re-encryption) ---

    [Fact]
    public async Task CompactAsync_DefaultFalse_BlockFlagsPreserved()
    {
        // When reEncrypt is false, block Flags bytes should be unchanged after compaction.
        var filePath = Path.Combine(_tempDir, "flags_preserved.emdb");
        var compactedPath = Path.Combine(_tempDir, "flags_preserved_compacted.emdb");

        var originalFlags = new Dictionary<BlockType, byte>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Record flags from all blocks before compaction
            var locations = rawBlockManager.GetBlockLocations();
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess)
                {
                    // Store flags by type (all blocks of same type should have same flags)
                    originalFlags[readResult.Value.Type] = readResult.Value.Flags;
                }
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify flags are unchanged in compacted file
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedLocations = verifyManager.GetBlockLocations();

        foreach (var kvp in compactedLocations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read compacted block {kvp.Key}");

            if (originalFlags.TryGetValue(readResult.Value.Type, out var expectedFlags))
            {
                Assert.Equal(expectedFlags, readResult.Value.Flags);
            }
        }
    }

    [Fact]
    public async Task CompactAsync_DefaultFalse_BlockPayloadsUnchanged()
    {
        // When reEncrypt is false, payloads should be byte-identical after compaction.
        var filePath = Path.Combine(_tempDir, "payloads_unchanged.emdb");
        var compactedPath = Path.Combine(_tempDir, "payloads_unchanged_compacted.emdb");

        var originalPayloads = new Dictionary<BlockType, List<byte[]>>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Collect live node payloads before compaction
            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            var locations = rawBlockManager.GetBlockLocations();
            var positionToBlockId = new Dictionary<long, long>();
            foreach (var kvp in locations)
                positionToBlockId[kvp.Value.Position] = kvp.Key;

            foreach (var pos in livePositions)
            {
                if (positionToBlockId.TryGetValue(pos, out var blockId))
                {
                    var readResult = await rawBlockManager.ReadBlockAsync(blockId);
                    if (readResult.IsSuccess &&
                        (readResult.Value.Type == BlockType.BTreeLeaf ||
                         readResult.Value.Type == BlockType.BTreeInternal))
                    {
                        if (!originalPayloads.ContainsKey(readResult.Value.Type))
                            originalPayloads[readResult.Value.Type] = new List<byte[]>();
                        originalPayloads[readResult.Value.Type].Add(readResult.Value.Payload);
                    }
                }
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify payloads in compacted file match originals
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedPayloads = new Dictionary<BlockType, List<byte[]>>();
        var compactedLocations = verifyManager.GetBlockLocations();

        foreach (var kvp in compactedLocations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess &&
                (readResult.Value.Type == BlockType.BTreeLeaf ||
                 readResult.Value.Type == BlockType.BTreeInternal))
            {
                if (!compactedPayloads.ContainsKey(readResult.Value.Type))
                    compactedPayloads[readResult.Value.Type] = new List<byte[]>();
                compactedPayloads[readResult.Value.Type].Add(readResult.Value.Payload);
            }
        }

        // Same number of each block type
        foreach (var (blockType, origPayloads) in originalPayloads)
        {
            Assert.True(compactedPayloads.ContainsKey(blockType),
                $"Compacted file missing block type {blockType}");
            Assert.Equal(origPayloads.Count, compactedPayloads[blockType].Count);
        }
    }

    [Fact]
    public async Task CompactAsync_DefaultFalse_NoEncryptedBitSetOnUnencryptedBlocks()
    {
        // Compaction with reEncrypt=false must NOT set the encrypted flag on blocks
        // that were originally unencrypted.
        var filePath = Path.Combine(_tempDir, "no_encrypt_bit.emdb");
        var compactedPath = Path.Combine(_tempDir, "no_encrypt_bit_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var locations = verifyManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read block {kvp.Key}");

            // Blocks written without encryption should not have the encrypted flag
            Assert.False(readResult.Value.IsEncrypted,
                $"Block {kvp.Key} (type={readResult.Value.Type}) should not be encrypted " +
                $"after compaction with reEncrypt=false. Flags=0x{readResult.Value.Flags:X2}");
        }
    }

    [Fact]
    public async Task CompactAsync_DefaultFalse_DataReadableAfterCompaction()
    {
        // Normal space reclamation must preserve all data — all lookups succeed.
        var filePath = Path.Combine(_tempDir, "data_readable.emdb");
        var compactedPath = Path.Combine(_tempDir, "data_readable_compacted.emdb");

        int entryCount = BTreeLeafNode.MaxEntries + 10;
        var insertedKeys = new List<(EmailHashedID Key, long BlockOffset, long BlockId)>();

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < entryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
                insertedKeys.Add((key, i * 100, i));
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify all data is readable after compaction
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(
            verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

        foreach (var (key, blockOffset, blockId) in insertedKeys)
        {
            var lookupResult = await verifyIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess,
                $"Lookup failed for key ({key}) after default compaction: {lookupResult.Error}");
            Assert.Equal(blockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);
        }
    }

    #region Compaction Helper

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

    private static async Task<(IndexRoot Root, long Position)?> FindLatestIndexRoot(RawBlockManager rawBlockManager)
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
                    latest = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                }
            }
        }

        return latest != null ? (latest, maxPosition) : null;
    }

    #endregion

    #region Verification Helpers

    private static async Task<IndexRoot?> GetLatestIndexRoot(RawBlockManager rawBlockManager)
    {
        var result = await FindLatestIndexRoot(rawBlockManager);
        return result?.Root;
    }

    private static async Task<long> GetLatestIndexRootOffset(RawBlockManager rawBlockManager)
    {
        var result = await FindLatestIndexRoot(rawBlockManager);
        return result?.Position ?? -1;
    }

    private static async Task<HashSet<long>> CollectLiveNodePositions(RawBlockManager rawBlockManager)
    {
        var livePositions = new HashSet<long>();
        var locations = rawBlockManager.GetBlockLocations();

        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var roots = new List<(IndexRoot Root, long Position)>();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var root = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                roots.Add((root, kvp.Value.Position));
            }
        }

        if (roots.Count == 0)
            return livePositions;

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        var latestRoot = roots[^1];

        livePositions.Add(latestRoot.Position);

        await WalkTreeCollectPositions(
            rawBlockManager, positionToBlockId, livePositions,
            latestRoot.Root.RootNodeBlockOffset, latestRoot.Root.TreeHeight);

        return livePositions;
    }

    private static async Task WalkTreeCollectPositions(
        RawBlockManager rawBlockManager,
        Dictionary<long, long> positionToBlockId,
        HashSet<long> livePositions,
        long nodeOffset,
        int remainingHeight)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            return;

        var readResult = await rawBlockManager.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            return;

        livePositions.Add(nodeOffset);

        if (remainingHeight > 1 && readResult.Value.Type == BlockType.BTreeInternal)
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                await WalkTreeCollectPositions(
                    rawBlockManager, positionToBlockId, livePositions,
                    internalNode.ChildOffsets[i], remainingHeight - 1);
            }
        }
    }

    #endregion
}
