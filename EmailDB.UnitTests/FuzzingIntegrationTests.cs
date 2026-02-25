using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Seeded fuzzing integration tests that exercise the full EmailDB stack under
/// randomised workloads. Each seed produces a deterministic sequence of
/// add / delete / move / compact operations, allowing failures to be reproduced
/// by re-running with the same seed.
/// </summary>
public class FuzzingIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public FuzzingIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_fuzz_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ─── Action types ──────────────────────────────────────────────────

    private enum FuzzAction { AddEmails, DeleteEmails, MoveEmails, CompactAndVerify }

    // ─── Seed generators ───────────────────────────────────────────────

    /// <summary>100 seeds → 100 unique deterministic workloads.</summary>
    public static IEnumerable<object[]> GetFuzzSeeds()
    {
        for (int i = 0; i < 100; i++)
            yield return new object[] { i };
    }

    /// <summary>10 seeds with larger email counts for stress testing.</summary>
    public static IEnumerable<object[]> GetStressSeeds()
    {
        for (int i = 1000; i < 1010; i++)
            yield return new object[] { i };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BTree-level fuzzing (add / delete / compact)
    //  100 seeded workloads, 5-50 sequences each, 10-200 emails per batch
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(GetFuzzSeeds))]
    public async Task Fuzz_BTreeWorkloads_SeededRandom(int seed)
    {
        var rng = new Random(seed);
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"fuzz_btree_{seed}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // State tracking
        var liveEmails = new Dictionary<EmailHashedID, (long BlockId, long Offset)>();
        var deletedEmails = new HashSet<EmailHashedID>();
        int emailCounter = 0;

        int numSequences = rng.Next(5, 51);

        for (int seq = 0; seq < numSequences; seq++)
        {
            var action = PickBTreeAction(rng, liveEmails.Count, seq, numSequences);

            switch (action)
            {
                case FuzzAction.AddEmails:
                {
                    int count = rng.Next(10, 201);
                    for (int i = 0; i < count; i++)
                    {
                        emailCounter++;
                        var emailId = new EmailHashedID(
                            $"msg-{seed}-{emailCounter}@fuzz.test",
                            emailCounter * 1000L,
                            $"Subject {emailCounter}",
                            $"from{emailCounter}@test.com",
                            $"to{emailCounter}@test.com");

                        var payload = System.Text.Encoding.UTF8.GetBytes(
                            $"Fuzz email seed={seed} n={emailCounter}");

                        var emailBlock = new Block
                        {
                            Version = 1,
                            Type = BlockType.EmailContent,
                            Flags = 0,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                            Payload = payload
                        };

                        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
                        Assert.True(writeResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Write failed: {writeResult.Error}");

                        var insertResult = await btreeIndex.InsertAsync(
                            emailId, writeResult.Value.Position, emailBlock.BlockId);
                        Assert.True(insertResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Insert failed: {insertResult.Error}");

                        liveEmails[emailId] = (emailBlock.BlockId, writeResult.Value.Position);
                    }
                    break;
                }

                case FuzzAction.DeleteEmails:
                {
                    if (liveEmails.Count == 0) break;

                    int maxDelete = Math.Max(1, liveEmails.Count / 3);
                    int count = Math.Min(rng.Next(1, maxDelete + 1), liveEmails.Count);
                    var toDelete = liveEmails.Keys.OrderBy(_ => rng.Next()).Take(count).ToList();

                    foreach (var emailId in toDelete)
                    {
                        var deleteResult = await btreeIndex.DeleteAsync(emailId);
                        Assert.True(deleteResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Delete failed: {deleteResult.Error}");

                        liveEmails.Remove(emailId);
                        deletedEmails.Add(emailId);
                    }
                    break;
                }

                case FuzzAction.CompactAndVerify:
                {
                    if (liveEmails.Count == 0) break;
                    await VerifyCompaction(rawBlockManager, liveEmails, deletedEmails, seed, seq, rng);
                    break;
                }
            }

            // Periodic quick-check: spot-verify a random live email
            if (liveEmails.Count > 0 && seq % 5 == 0)
            {
                var sample = liveEmails.ElementAt(rng.Next(liveEmails.Count));
                var spotCheck = await btreeIndex.LookupAsync(sample.Key);
                Assert.True(spotCheck.IsSuccess,
                    $"[seed={seed} seq={seq}] Spot-check lookup failed: {spotCheck.Error}");
            }
        }

        // ─── Final verification ────────────────────────────────────────

        // Entry count
        long expectedCount = liveEmails.Count;
        long actualCount = btreeIndex.CurrentRoot?.EntryCount ?? 0;
        Assert.Equal(expectedCount, actualCount);

        // All live emails are lookupable with correct data
        foreach (var (emailId, (blockId, offset)) in liveEmails)
        {
            var lookup = await btreeIndex.LookupAsync(emailId);
            Assert.True(lookup.IsSuccess,
                $"[seed={seed}] Final: live email lookup failed: {lookup.Error}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
            Assert.Equal(blockId, lookup.Value.BlockId);

            // Content block is still physically readable
            var read = await rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(read.IsSuccess,
                $"[seed={seed}] Final: content block read failed: {read.Error}");
            Assert.Equal(BlockType.EmailContent, read.Value.Type);
        }

        // All deleted emails are NOT lookupable
        foreach (var emailId in deletedEmails)
        {
            var lookup = await btreeIndex.LookupAsync(emailId);
            Assert.True(lookup.IsFailure,
                $"[seed={seed}] Final: deleted email should not be in BTree");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Full-stack fuzzing (add / delete / move / compact)
    //  100 seeded workloads. Move operations exercise the BTree under
    //  interleaved workloads — folder membership is tracked in-memory
    //  so the test validates BTree integrity across mixed action types.
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(GetFuzzSeeds))]
    public async Task Fuzz_FullStackWorkloads_SeededRandom(int seed)
    {
        var rng = new Random(seed);
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"fuzz_full_{seed}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Folder membership tracked in-memory (move doesn't touch BTree)
        string[] folders = { "Inbox", "Archive", "Trash", "Drafts", "Sent" };

        // State tracking
        var liveEmails = new Dictionary<EmailHashedID, (long BlockId, long Offset, string Folder)>();
        var deletedEmails = new HashSet<EmailHashedID>();
        var folderContents = new Dictionary<string, HashSet<EmailHashedID>>();
        foreach (var f in folders)
            folderContents[f] = new HashSet<EmailHashedID>();
        int emailCounter = 0;

        int numSequences = rng.Next(5, 51);

        for (int seq = 0; seq < numSequences; seq++)
        {
            var action = PickFullStackAction(rng, liveEmails.Count, seq, numSequences);

            switch (action)
            {
                case FuzzAction.AddEmails:
                {
                    int count = rng.Next(10, 201);
                    for (int i = 0; i < count; i++)
                    {
                        emailCounter++;
                        var emailId = new EmailHashedID(
                            $"msg-{seed}-{emailCounter}@fuzz.test",
                            emailCounter * 1000L,
                            $"Subject {emailCounter}",
                            $"from{emailCounter}@test.com",
                            $"to{emailCounter}@test.com");

                        var payload = System.Text.Encoding.UTF8.GetBytes(
                            $"Fuzz email seed={seed} n={emailCounter}");

                        var emailBlock = new Block
                        {
                            Version = 1,
                            Type = BlockType.EmailContent,
                            Flags = 0,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                            Payload = payload
                        };

                        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
                        Assert.True(writeResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Write failed: {writeResult.Error}");

                        var insertResult = await btreeIndex.InsertAsync(
                            emailId, writeResult.Value.Position, emailBlock.BlockId);
                        Assert.True(insertResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Insert failed: {insertResult.Error}");

                        var folder = folders[rng.Next(folders.Length)];
                        liveEmails[emailId] = (emailBlock.BlockId, writeResult.Value.Position, folder);
                        folderContents[folder].Add(emailId);
                    }
                    break;
                }

                case FuzzAction.DeleteEmails:
                {
                    if (liveEmails.Count == 0) break;

                    int maxDelete = Math.Max(1, liveEmails.Count / 3);
                    int count = Math.Min(rng.Next(1, maxDelete + 1), liveEmails.Count);
                    var toDelete = liveEmails.Keys.OrderBy(_ => rng.Next()).Take(count).ToList();

                    foreach (var emailId in toDelete)
                    {
                        var (_, _, folder) = liveEmails[emailId];

                        var deleteResult = await btreeIndex.DeleteAsync(emailId);
                        Assert.True(deleteResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Delete failed: {deleteResult.Error}");

                        folderContents[folder].Remove(emailId);
                        liveEmails.Remove(emailId);
                        deletedEmails.Add(emailId);
                    }
                    break;
                }

                case FuzzAction.MoveEmails:
                {
                    if (liveEmails.Count == 0) break;

                    int maxMove = Math.Max(1, liveEmails.Count / 4);
                    int count = Math.Min(rng.Next(1, maxMove + 1), liveEmails.Count);
                    var toMove = liveEmails.Keys.OrderBy(_ => rng.Next()).Take(count).ToList();

                    foreach (var emailId in toMove)
                    {
                        var (blockId, offset, sourceFolder) = liveEmails[emailId];
                        var targetFolder = folders[rng.Next(folders.Length)];

                        if (targetFolder == sourceFolder) continue;

                        // Move doesn't touch BTree — only folder metadata
                        folderContents[sourceFolder].Remove(emailId);
                        folderContents[targetFolder].Add(emailId);
                        liveEmails[emailId] = (blockId, offset, targetFolder);

                        // Verify the email is still in the BTree after the move
                        var lookup = await btreeIndex.LookupAsync(emailId);
                        Assert.True(lookup.IsSuccess,
                            $"[seed={seed} seq={seq}] Email should still be in BTree after move: {lookup.Error}");
                    }
                    break;
                }

                case FuzzAction.CompactAndVerify:
                {
                    if (liveEmails.Count == 0) break;

                    var btreeOnly = liveEmails.ToDictionary(
                        kv => kv.Key,
                        kv => (kv.Value.BlockId, kv.Value.Offset));
                    await VerifyCompaction(rawBlockManager, btreeOnly, deletedEmails, seed, seq, rng);
                    break;
                }
            }
        }

        // ─── Final verification ────────────────────────────────────────

        // BTree entry count
        long expectedCount = liveEmails.Count;
        long actualCount = btreeIndex.CurrentRoot?.EntryCount ?? 0;
        Assert.Equal(expectedCount, actualCount);

        // All live emails lookupable and content readable
        foreach (var (emailId, (blockId, offset, _)) in liveEmails)
        {
            var lookup = await btreeIndex.LookupAsync(emailId);
            Assert.True(lookup.IsSuccess,
                $"[seed={seed}] Final: live email lookup failed: {lookup.Error}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
            Assert.Equal(blockId, lookup.Value.BlockId);

            var read = await rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(read.IsSuccess,
                $"[seed={seed}] Final: content block read failed: {read.Error}");
            Assert.Equal(BlockType.EmailContent, read.Value.Type);
        }

        // All deleted emails NOT lookupable
        foreach (var emailId in deletedEmails)
        {
            var lookup = await btreeIndex.LookupAsync(emailId);
            Assert.True(lookup.IsFailure,
                $"[seed={seed}] Final: deleted email should not be in BTree");
        }

        // Folder membership is internally consistent
        int totalInFolders = folderContents.Values.Sum(s => s.Count);
        Assert.Equal(liveEmails.Count, totalInFolders);
        foreach (var (emailId, (_, _, expectedFolder)) in liveEmails)
        {
            Assert.Contains(emailId, folderContents[expectedFolder]);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Stress fuzzing — fewer seeds, larger email batches (500-2000)
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(GetStressSeeds))]
    [Trait("Category", "Stress")]
    public async Task Fuzz_StressWorkloads_LargeScale(int seed)
    {
        var rng = new Random(seed);
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"fuzz_stress_{seed}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var liveEmails = new Dictionary<EmailHashedID, (long BlockId, long Offset)>();
        var deletedEmails = new HashSet<EmailHashedID>();
        int emailCounter = 0;

        int numSequences = rng.Next(10, 101);

        for (int seq = 0; seq < numSequences; seq++)
        {
            var action = PickBTreeAction(rng, liveEmails.Count, seq, numSequences);

            switch (action)
            {
                case FuzzAction.AddEmails:
                {
                    int count = rng.Next(500, 2001);
                    for (int i = 0; i < count; i++)
                    {
                        emailCounter++;
                        var emailId = new EmailHashedID(
                            $"stress-{seed}-{emailCounter}@fuzz.test",
                            emailCounter * 1000L,
                            $"Subject {emailCounter}",
                            $"from{emailCounter}@test.com",
                            $"to{emailCounter}@test.com");

                        var payload = System.Text.Encoding.UTF8.GetBytes(
                            $"Stress email seed={seed} n={emailCounter}");

                        var emailBlock = new Block
                        {
                            Version = 1,
                            Type = BlockType.EmailContent,
                            Flags = 0,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                            Payload = payload
                        };

                        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
                        Assert.True(writeResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Write failed: {writeResult.Error}");

                        var insertResult = await btreeIndex.InsertAsync(
                            emailId, writeResult.Value.Position, emailBlock.BlockId);
                        Assert.True(insertResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Insert failed: {insertResult.Error}");

                        liveEmails[emailId] = (emailBlock.BlockId, writeResult.Value.Position);
                    }
                    break;
                }

                case FuzzAction.DeleteEmails:
                {
                    if (liveEmails.Count == 0) break;

                    int maxDelete = Math.Max(1, liveEmails.Count / 3);
                    int count = Math.Min(rng.Next(1, maxDelete + 1), liveEmails.Count);
                    var toDelete = liveEmails.Keys.OrderBy(_ => rng.Next()).Take(count).ToList();

                    foreach (var emailId in toDelete)
                    {
                        var deleteResult = await btreeIndex.DeleteAsync(emailId);
                        Assert.True(deleteResult.IsSuccess,
                            $"[seed={seed} seq={seq}] Delete failed: {deleteResult.Error}");

                        liveEmails.Remove(emailId);
                        deletedEmails.Add(emailId);
                    }
                    break;
                }

                case FuzzAction.CompactAndVerify:
                {
                    if (liveEmails.Count == 0) break;
                    await VerifyCompaction(rawBlockManager, liveEmails, deletedEmails, seed, seq, rng);
                    break;
                }
            }
        }

        // ─── Final verification ────────────────────────────────────────

        long expectedCount = liveEmails.Count;
        long actualCount = btreeIndex.CurrentRoot?.EntryCount ?? 0;
        Assert.Equal(expectedCount, actualCount);

        foreach (var (emailId, (blockId, offset)) in liveEmails)
        {
            var lookup = await btreeIndex.LookupAsync(emailId);
            Assert.True(lookup.IsSuccess,
                $"[seed={seed}] Final: live email lookup failed: {lookup.Error}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
            Assert.Equal(blockId, lookup.Value.BlockId);
        }

        // Sample-verify deleted emails (full set may be huge)
        var deletedSample = deletedEmails.OrderBy(_ => rng.Next()).Take(500);
        foreach (var emailId in deletedSample)
        {
            var lookup = await btreeIndex.LookupAsync(emailId);
            Assert.True(lookup.IsFailure,
                $"[seed={seed}] Final: deleted email should not be in BTree");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Action pickers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Picks an action for BTree-only fuzzing. Weights shift from adds
    /// early on toward deletes and compactions later.
    /// </summary>
    private static FuzzAction PickBTreeAction(Random rng, int liveCount, int seq, int totalSeqs)
    {
        if (liveCount == 0) return FuzzAction.AddEmails;

        double progress = (double)seq / totalSeqs;
        int roll = rng.Next(100);

        if (progress < 0.3)
        {
            // Early: 65% add, 25% delete, 10% compact
            if (roll < 65) return FuzzAction.AddEmails;
            if (roll < 90) return FuzzAction.DeleteEmails;
            return FuzzAction.CompactAndVerify;
        }
        else if (progress < 0.7)
        {
            // Middle: 35% add, 40% delete, 25% compact
            if (roll < 35) return FuzzAction.AddEmails;
            if (roll < 75) return FuzzAction.DeleteEmails;
            return FuzzAction.CompactAndVerify;
        }
        else
        {
            // Late: 20% add, 50% delete, 30% compact
            if (roll < 20) return FuzzAction.AddEmails;
            if (roll < 70) return FuzzAction.DeleteEmails;
            return FuzzAction.CompactAndVerify;
        }
    }

    /// <summary>
    /// Picks an action for full-stack fuzzing, including move operations.
    /// </summary>
    private static FuzzAction PickFullStackAction(Random rng, int liveCount, int seq, int totalSeqs)
    {
        if (liveCount == 0) return FuzzAction.AddEmails;

        double progress = (double)seq / totalSeqs;
        int roll = rng.Next(100);

        if (progress < 0.3)
        {
            // Early: 55% add, 20% delete, 15% move, 10% compact
            if (roll < 55) return FuzzAction.AddEmails;
            if (roll < 75) return FuzzAction.DeleteEmails;
            if (roll < 90) return FuzzAction.MoveEmails;
            return FuzzAction.CompactAndVerify;
        }
        else if (progress < 0.7)
        {
            // Middle: 30% add, 30% delete, 25% move, 15% compact
            if (roll < 30) return FuzzAction.AddEmails;
            if (roll < 60) return FuzzAction.DeleteEmails;
            if (roll < 85) return FuzzAction.MoveEmails;
            return FuzzAction.CompactAndVerify;
        }
        else
        {
            // Late: 15% add, 40% delete, 25% move, 20% compact
            if (roll < 15) return FuzzAction.AddEmails;
            if (roll < 55) return FuzzAction.DeleteEmails;
            if (roll < 80) return FuzzAction.MoveEmails;
            return FuzzAction.CompactAndVerify;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Compaction verification
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compacts the live BTree to a new file and verifies that all live
    /// entries are lookupable and all deleted entries are absent.
    /// </summary>
    private async Task VerifyCompaction(
        RawBlockManager sourceManager,
        Dictionary<EmailHashedID, (long BlockId, long Offset)> liveEmails,
        HashSet<EmailHashedID> deletedEmails,
        int seed, int seq, Random rng)
    {
        var compactedPath = Path.Combine(_tempDir, $"fuzz_{seed}_compact_{seq}.emdb");
        try
        {
            await CompactLiveTreeToFile(sourceManager, compactedPath);

            using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
            var latestRoot = await GetLatestIndexRoot(verifyManager);
            Assert.NotNull(latestRoot);

            var verifyIndex = new BTreeIndex(
                verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));

            Assert.Equal(liveEmails.Count, latestRoot.EntryCount);

            // Sample-verify live emails (up to 200 per compaction check)
            int sampleSize = Math.Min(200, liveEmails.Count);
            var sample = liveEmails.OrderBy(_ => rng.Next()).Take(sampleSize);

            foreach (var (emailId, (blockId, offset)) in sample)
            {
                var lookup = await verifyIndex.LookupAsync(emailId);
                Assert.True(lookup.IsSuccess,
                    $"[seed={seed} seq={seq}] Compacted lookup failed for live email: {lookup.Error}");
                Assert.Equal(offset, lookup.Value.BlockOffset);
                Assert.Equal(blockId, lookup.Value.BlockId);
            }

            // Sample-verify deleted emails are absent
            var deletedSample = deletedEmails.OrderBy(_ => rng.Next()).Take(50);
            foreach (var emailId in deletedSample)
            {
                var lookup = await verifyIndex.LookupAsync(emailId);
                Assert.True(lookup.IsFailure,
                    $"[seed={seed} seq={seq}] Deleted email should not be in compacted BTree");
            }
        }
        finally
        {
            if (File.Exists(compactedPath))
                File.Delete(compactedPath);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Compaction helpers (adapted from BTreeCompactionFullFunctionalityTests)
    // ═══════════════════════════════════════════════════════════════════

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
}
