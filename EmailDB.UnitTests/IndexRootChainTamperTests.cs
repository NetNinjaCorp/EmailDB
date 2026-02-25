using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that "quick verification" (walking the IndexRoot chain) detects tampered
/// IndexRoot blocks. Quick verification walks the chain of IndexRoot blocks backward
/// via PreviousRootOffset/PreviousRootHash and detects any link where
/// PreviousRootHash != actual previous IndexRoot's RootNodeHash.
/// This is O(flush_count) — it only reads IndexRoot blocks, not tree nodes.
/// </summary>
public class IndexRootChainTamperTests : IDisposable
{
    private readonly string _tempDir;

    public IndexRootChainTamperTests()
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

    [Fact]
    public async Task QuickVerification_ValidChain_AllLinksMatch()
    {
        // Build a tree with multiple inserts, producing a chain of IndexRoot blocks.
        // Walk the chain and verify every PreviousRootHash matches the previous
        // IndexRoot's RootNodeHash.
        var filePath = Path.Combine(_tempDir, "valid_chain.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 20; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 3, $"Expected at least 3 IndexRoot blocks, got {indexRoots.Count}");

        var chainResult = VerifyIndexRootChain(indexRoots);
        Assert.True(chainResult.IsValid, $"Chain should be valid: {chainResult.Error}");
    }

    [Fact]
    public async Task QuickVerification_TamperedRootNodeHash_DetectsMismatch()
    {
        // Tamper with an intermediate IndexRoot's RootNodeHash.
        // The successor's PreviousRootHash will no longer match.
        var filePath = Path.Combine(_tempDir, "tampered_root_hash.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 20; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 3);

        // Verify chain is valid before tampering
        var preCheck = VerifyIndexRootChain(indexRoots);
        Assert.True(preCheck.IsValid, $"Pre-tamper chain should be valid: {preCheck.Error}");

        // Tamper with an intermediate IndexRoot's RootNodeHash
        int tamperIdx = indexRoots.Count / 2;
        indexRoots[tamperIdx].Root.RootNodeHash[0] ^= 0xFF;
        indexRoots[tamperIdx].Root.RootNodeHash[15] ^= 0xFF;

        var postCheck = VerifyIndexRootChain(indexRoots);
        Assert.False(postCheck.IsValid, "Chain should be invalid after tampering with RootNodeHash");
        Assert.Contains("PreviousRootHash mismatch", postCheck.Error);
    }

    [Fact]
    public async Task QuickVerification_TamperedPreviousRootHash_DetectsMismatch()
    {
        // Tamper with an IndexRoot's PreviousRootHash field directly.
        var filePath = Path.Combine(_tempDir, "tampered_prev_hash.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 20; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 3);

        // Tamper with the latest IndexRoot's PreviousRootHash
        int tamperIdx = indexRoots.Count - 1;
        indexRoots[tamperIdx].Root.PreviousRootHash[0] ^= 0xFF;

        var check = VerifyIndexRootChain(indexRoots);
        Assert.False(check.IsValid, "Chain should be invalid after tampering with PreviousRootHash");
        Assert.Contains("PreviousRootHash mismatch", check.Error);
    }

    [Fact]
    public async Task QuickVerification_SingleBitFlip_InPreviousRootHash_Detected()
    {
        // Even a single-bit change in PreviousRootHash breaks the chain.
        var filePath = Path.Combine(_tempDir, "single_bit_flip.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 2);

        // Flip a single bit in the last byte of PreviousRootHash
        int tamperIdx = indexRoots.Count - 1;
        indexRoots[tamperIdx].Root.PreviousRootHash[31] ^= 0x01;

        var check = VerifyIndexRootChain(indexRoots);
        Assert.False(check.IsValid, "Single-bit flip should be detected");
    }

    [Fact]
    public async Task QuickVerification_TamperedGenesisRootNodeHash_DetectedBySuccessor()
    {
        // Tamper with the genesis IndexRoot's RootNodeHash.
        // The second IndexRoot's PreviousRootHash no longer matches.
        var filePath = Path.Combine(_tempDir, "tamper_genesis.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var r1 = await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        Assert.True(r1.IsSuccess);
        var r2 = await btreeIndex.InsertAsync(new EmailHashedID(2, 0, 0, 0), 200, 2);
        Assert.True(r2.IsSuccess);

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 2, $"Expected at least 2 IndexRoot blocks, got {indexRoots.Count}");

        // Tamper with the genesis root's RootNodeHash
        indexRoots[0].Root.RootNodeHash[0] ^= 0xFF;

        var check = VerifyIndexRootChain(indexRoots);
        Assert.False(check.IsValid, "Tampering with genesis root's RootNodeHash should break the chain");
    }

    [Fact]
    public async Task QuickVerification_GenesisRoot_HasZeroPreviousRootHash()
    {
        // The genesis IndexRoot has PreviousRootHash = all zeros and PreviousRootOffset = -1.
        var filePath = Path.Combine(_tempDir, "genesis_root.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 0, 0, 0);
        var r = await btreeIndex.InsertAsync(key, 100, 1);
        Assert.True(r.IsSuccess);

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 1);

        var genesis = indexRoots[0];
        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, genesis.Root.PreviousRootOffset);

        // Quick verification should pass for a single-root chain
        var check = VerifyIndexRootChain(indexRoots);
        Assert.True(check.IsValid, $"Single genesis root chain should be valid: {check.Error}");
    }

    [Fact]
    public async Task QuickVerification_AfterSplit_ChainRemainsValid()
    {
        // After a tree split (height 1 -> 2), the IndexRoot chain should still be valid.
        var filePath = Path.Combine(_tempDir, "chain_after_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        var chainResult = VerifyIndexRootChain(indexRoots);
        Assert.True(chainResult.IsValid, $"Chain after split should be valid: {chainResult.Error}");
    }

    [Fact]
    public async Task QuickVerification_TamperedPayload_DetectedViaPayloadHash()
    {
        // Tampering with any field in an IndexRoot's serialized payload (e.g. EntryCount)
        // causes the BLAKE3 hash of the payload to differ from the original.
        var filePath = Path.Combine(_tempDir, "tampered_payload.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 3);

        // Tamper: change EntryCount in an intermediate IndexRoot
        int tamperIdx = indexRoots.Count / 2;
        var originalPayloadHash = BTreeHasher.ComputeHash(indexRoots[tamperIdx].OriginalPayload);

        indexRoots[tamperIdx].Root.EntryCount = 999999;
        var tamperedPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoots[tamperIdx].Root);
        var tamperedPayloadHash = BTreeHasher.ComputeHash(tamperedPayload);

        Assert.NotEqual(originalPayloadHash, tamperedPayloadHash);
    }

    [Fact]
    public async Task QuickVerification_ChainLinksAllPresent_NoMissingRoots()
    {
        // Verify that every PreviousRootOffset in the chain points to an existing
        // IndexRoot block — no broken links.
        var filePath = Path.Combine(_tempDir, "chain_links_present.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 15; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        var offsetSet = new HashSet<long>(indexRoots.Select(r => r.BlockOffset));

        // Walk from latest to genesis, verify every PreviousRootOffset exists
        var current = indexRoots[^1];
        int hops = 0;
        while (current.Root.PreviousRootOffset >= 0)
        {
            Assert.True(offsetSet.Contains(current.Root.PreviousRootOffset),
                $"PreviousRootOffset {current.Root.PreviousRootOffset} not found in IndexRoot blocks (hop {hops})");
            current = indexRoots.First(r => r.BlockOffset == current.Root.PreviousRootOffset);
            hops++;
        }

        // Should have traversed the entire chain
        Assert.Equal(indexRoots.Count - 1, hops);
    }

    #region Helpers

    private class IndexRootEntry
    {
        public IndexRoot Root { get; }
        public long BlockOffset { get; }
        public byte[] OriginalPayload { get; }

        public IndexRootEntry(IndexRoot root, long blockOffset, byte[] originalPayload)
        {
            Root = root;
            BlockOffset = blockOffset;
            OriginalPayload = originalPayload;
        }
    }

    /// <summary>
    /// Collects all IndexRoot blocks from the file, ordered by file position (earliest first).
    /// </summary>
    private static async Task<List<IndexRootEntry>> CollectIndexRoots(RawBlockManager rawBlockManager)
    {
        var roots = new List<(IndexRoot Root, long Position, byte[] Payload)>();
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var root = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                roots.Add((root, kvp.Value.Position, (byte[])readResult.Value.Payload.Clone()));
            }
        }

        // Order by file position (ascending) — earliest written first
        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        return roots.Select(r => new IndexRootEntry(r.Root, r.Position, r.Payload)).ToList();
    }

    /// <summary>
    /// Performs "quick verification" of the IndexRoot chain.
    /// Walks from the latest IndexRoot backwards via PreviousRootOffset,
    /// checking that each PreviousRootHash matches the previous IndexRoot's RootNodeHash.
    /// This is O(flush_count) — only IndexRoot blocks are read.
    /// </summary>
    private static (bool IsValid, string? Error) VerifyIndexRootChain(List<IndexRootEntry> orderedRoots)
    {
        if (orderedRoots.Count == 0)
            return (true, null);

        // Build offset -> IndexRootEntry map for chain traversal
        var offsetMap = new Dictionary<long, IndexRootEntry>();
        foreach (var entry in orderedRoots)
            offsetMap[entry.BlockOffset] = entry;

        // Walk chain from latest to genesis
        var current = orderedRoots[^1];
        int chainLink = 1;

        while (current.Root.PreviousRootOffset >= 0)
        {
            if (!offsetMap.TryGetValue(current.Root.PreviousRootOffset, out var previous))
                return (false, $"PreviousRootOffset {current.Root.PreviousRootOffset} not found in IndexRoot blocks at chain link {chainLink}");

            // Quick verification check: PreviousRootHash must match previous root's RootNodeHash
            if (!current.Root.PreviousRootHash.SequenceEqual(previous.Root.RootNodeHash))
            {
                return (false,
                    $"PreviousRootHash mismatch at chain link {chainLink}: " +
                    $"stored={BitConverter.ToString(current.Root.PreviousRootHash.AsSpan(0, 4).ToArray())}..., " +
                    $"expected={BitConverter.ToString(previous.Root.RootNodeHash.AsSpan(0, 4).ToArray())}...");
            }

            current = previous;
            chainLink++;
        }

        // Genesis root: PreviousRootHash must be all zeros
        if (!current.Root.PreviousRootHash.All(b => b == 0))
            return (false, "Genesis IndexRoot's PreviousRootHash is not zeroed");

        return (true, null);
    }

    #endregion
}
