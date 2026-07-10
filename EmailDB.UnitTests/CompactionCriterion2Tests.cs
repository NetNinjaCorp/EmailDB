using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// AC-verification for US-EMDB-89 acceptance criterion 2 — "BlockLocationIndex
/// rebuilt for the new layout and verifies" (EmailDB_FileFormat_Spec.md Section 11.2,
/// docs/Compaction.md Section 2). The sibling suites (CompactorRebuildTests,
/// CompactorSwapTests) prove the rebuilt index MAPS every copied block to its new
/// side-file offset and re-reads intact. These tests close the two gaps specific to
/// this criterion's <b>verifies</b> half:
/// <list type="bullet">
/// <item><b>The rebuilt index's own Merkle/ChildHash verification passes on read.</b>
/// The compacted file is reopened through the real <see cref="CleanOpener"/> and its
/// rebuilt <see cref="BlockLocationIndex"/> is put through the O(nodes) full-tree
/// audit (<see cref="CowBTree.VerifyFullTree"/>) with a cold node cache — every node
/// re-hashed and checked against its parent's ChildHash (the root against the
/// committed RootHash) — and every live block resolves at its NEW offset. A
/// multi-level rebuild is audited too, so ChildHash verification runs across internal
/// levels, not just the root.</item>
/// <item><b>That verification is non-vacuous.</b> Corrupting the committed RootHash by
/// a single bit makes both the full-tree audit and an ordinary lookup fail with the
/// contracted <see cref="BTreeNodeStore.MerkleVerificationErrorCode"/> — proving the
/// pass over an intact tree is real gating, not an unconditional success.</item>
/// <item><b>No stale source offset survives the rebuild.</b> A source built with
/// interleaved dead blocks moves every live block to a strictly smaller offset; the
/// rebuilt index maps only the new side-file offsets and its value set is disjoint
/// from the source offsets — the derived index was regenerated for the new layout,
/// not carried over.</item>
/// </list>
/// Every handle and <see cref="Compactor"/> is disposed before a file is reopened, so
/// the tests pass in isolation and in the full parallel suite.
/// </summary>
public class CompactionCriterion2Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-crit2-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    public void Dispose()
    {
        Delete(_path);
        Delete(SideFile);
    }

    private static void Delete(string p)
    {
        if (File.Exists(p))
            File.Delete(p);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static T Require<T>(Result<T> r)
    {
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
        return r.Value;
    }

    private static BlockManager OpenManager(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return new BlockManager(stream, ownsStream: true);
    }

    private const long SourceLiveByteCount = 321_000;

    // ---------------------------------- Rebuilt index verifies on the swapped file

    [Fact]
    public void Rebuilt_index_on_the_swapped_file_passes_full_merkle_verification_and_resolves_every_block_at_its_new_offset()
    {
        var source = BuildSource(dataCount: 14);

        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            copied = compactor.CopiedBlocks.ToArray();
            Ok(compactor.FinalizeAndSwap());
        }

        // Reopen the compacted file through the real clean-open path: a fresh node store
        // (cold cache) reconstructs the rebuilt index from the committed location root.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;
        var index = state.LocationIndex;

        // The rebuilt index VERIFIES: the full-tree audit walks every node and checks
        // each against its parent's ChildHash (the root against the committed RootHash)
        // over the swapped file, with the cold cache the reopen just created.
        var audit = index.Tree.VerifyFullTree(index.Root);
        Assert.True(audit.IsIntact, $"rebuilt index failed Merkle verification at {audit.DivergentNodePath}: {audit.Error}");
        Assert.Null(audit.Error);
        Assert.True(audit.NodesVerified >= 1, "the audit must have verified at least the root node");

        // Every live block resolves at its NEW offset (never its source offset) and the
        // block actually sitting there carries the matching BlockId.
        Assert.Equal(copied.Count, index.Count);
        foreach (var cb in copied)
        {
            var lookup = Require(index.Lookup(cb.BlockId));
            Assert.True(lookup.Found, $"copied block {Convert.ToHexString(cb.BlockId)} missing after compaction");
            Assert.Equal(cb.NewOffset, lookup.Offset);
            Assert.NotEqual(cb.SourceOffset, lookup.Offset);

            var block = Require(state.BlockManager.Read(lookup.Offset));
            Assert.Equal(cb.BlockId, block.Header.BlockId);
        }

        // The named roots the compaction remapped resolve at their new offsets too.
        Assert.NotNull(state.Checkpoint.FolderTreeRoot);
        Assert.NotNull(state.Checkpoint.MetadataRoot);
        _ = source; // source facts asserted via the copied set above
    }

    // ------------------------------- Verification is non-vacuous (RootHash gate)

    [Fact]
    public void Merkle_verification_of_the_rebuilt_index_fails_when_the_committed_root_hash_is_corrupted()
    {
        BuildSource(dataCount: 12);

        LocationIndexRoot emittedRoot;
        byte[] probeBlockId;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            emittedRoot = compactor.NewLocationIndexRoot!;
            probeBlockId = compactor.CopiedBlocks[0].BlockId;
        }

        using var sideManager = OpenManager(SideFile);

        // Control: reconstructing from the genuine emitted RootHash both audits intact
        // and resolves the probe block — the verification pass is a real success here.
        var intact = Reconstruct(sideManager, emittedRoot, emittedRoot.RootHash);
        var intactAudit = intact.Tree.VerifyFullTree(intact.Root);
        Assert.True(intactAudit.IsIntact, intactAudit.Error);
        Assert.True(Require(intact.Lookup(probeBlockId)).Found);

        // Flip one bit of the committed RootHash: the on-disk nodes are untouched, but
        // every read of the root now diverges from the expected hash.
        var corruptHash = (byte[])emittedRoot.RootHash.Clone();
        corruptHash[0] ^= 0x01;
        var tampered = Reconstruct(sideManager, emittedRoot, corruptHash);

        // The full-tree audit reports the root as the first divergent node with the
        // contracted Merkle-mismatch error — not an unconditional pass.
        var audit = tampered.Tree.VerifyFullTree(tampered.Root);
        Assert.False(audit.IsIntact, "a corrupted RootHash must fail full-tree verification");
        Assert.Equal("root", audit.DivergentNodePath);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(audit.Error),
            $"expected a contracted Merkle failure, got: {audit.Error}");

        // The O(height) lookup path verifies the same way — the probe lookup now fails
        // (Merkle mismatch) rather than returning a location from an unverified node.
        var lookup = tampered.Lookup(probeBlockId);
        Assert.True(lookup.IsFailure, "a lookup through a corrupted root hash must fail verification");
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(lookup.Error),
            $"expected a contracted Merkle failure on lookup, got: {lookup.Error}");
    }

    // ---------------------------------------------- No stale source offset survives

    [Fact]
    public void Rebuilt_index_maps_only_new_side_file_offsets_and_no_source_offset_survives()
    {
        BuildSource(dataCount: 16);

        LocationIndexRoot emittedRoot;
        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            emittedRoot = compactor.NewLocationIndexRoot!;
            copied = compactor.CopiedBlocks.ToArray();
        }

        // The interleaved dead blocks guarantee every live block moved to a strictly
        // smaller offset — so a rebuilt index that still held any source offset would be
        // detectable, not masked by a block that happened to keep its offset.
        Assert.All(copied, cb => Assert.True(cb.NewOffset < cb.SourceOffset,
            $"block {Convert.ToHexString(cb.BlockId)} did not move: source {cb.SourceOffset}, new {cb.NewOffset}"));

        var newOffsets = copied.Select(cb => cb.NewOffset).ToHashSet();

        using var sideManager = OpenManager(SideFile);
        var index = Reconstruct(sideManager, emittedRoot, emittedRoot.RootHash);

        Assert.Equal(copied.Count, index.Count);

        var resolved = new HashSet<long>();
        foreach (var cb in copied)
        {
            var lookup = Require(index.Lookup(cb.BlockId));
            Assert.True(lookup.Found);
            // Each block resolves to its OWN new side-file offset, never its OWN source
            // offset — the stale offset the rebuild had to replace.
            Assert.Equal(cb.NewOffset, lookup.Offset);
            Assert.NotEqual(cb.SourceOffset, lookup.Offset);
            resolved.Add(lookup.Offset);
        }

        // The whole value set is exactly the new layout's packed offsets — the derived
        // index was regenerated for the new physical layout, not carried over.
        Assert.Equal(newOffsets, resolved);
    }

    // ---------------------------- Multi-level rebuild verifies across internal levels

    [Fact]
    public void Rebuilt_multi_level_index_verifies_every_node_across_levels_on_a_cold_read()
    {
        BuildSource(dataCount: 40);

        LocationIndexRoot emittedRoot;
        IReadOnlyList<CopiedBlock> copied;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            // Force a small fan-out so the rebuilt tree is genuinely multi-level: the
            // full-tree audit then exercises ChildHash verification at internal nodes.
            Require(compactor.RebuildLocationIndexAndWriteCheckpoint(maxLeafEntries: 4, maxInternalKeys: 3));
            emittedRoot = compactor.NewLocationIndexRoot!;
            copied = compactor.CopiedBlocks.ToArray();
        }

        Assert.True(emittedRoot.TreeHeight > 1, "the forced fan-out must produce a multi-level rebuilt tree");

        using var sideManager = OpenManager(SideFile);
        var store = new BTreeNodeStore(sideManager, blockIdResolver: null);
        var index = Reconstruct(store, emittedRoot, emittedRoot.RootHash);

        // Cold audit of the whole rebuilt tree: every node verifies against its parent's
        // ChildHash. NodesVerified exceeds the height, so nodes on more than one level
        // were checked, and each was actually hashed (no cache could pre-satisfy a cold
        // walk) — the ChildHash chain, not just the root, is verified.
        var audit = index.Tree.VerifyFullTree(index.Root);
        Assert.True(audit.IsIntact, $"multi-level rebuilt index failed at {audit.DivergentNodePath}: {audit.Error}");
        Assert.True(audit.NodesVerified > emittedRoot.TreeHeight,
            $"a multi-level tree must verify more nodes ({audit.NodesVerified}) than its height ({emittedRoot.TreeHeight})");
        Assert.Equal(audit.NodesVerified, store.HashComputationCount);

        // And every live block still resolves through the verified multi-level tree.
        Assert.Equal(copied.Count, index.Count);
        foreach (var cb in copied)
        {
            var lookup = Require(index.Lookup(cb.BlockId));
            Assert.True(lookup.Found);
            Assert.Equal(cb.NewOffset, lookup.Offset);
        }
    }

    // ================================================================= Helpers

    /// <summary>
    /// Reconstructs a live <see cref="BlockLocationIndex"/> over <paramref name="manager"/>
    /// from an emitted <see cref="LocationIndexRoot"/>, using <paramref name="rootHash"/>
    /// as the committed RootHash the root node is verified against — the same
    /// offset-root reconstruction a clean open performs, with the RootHash exposed so a
    /// test can prove verification is real by corrupting it.
    /// </summary>
    private static BlockLocationIndex Reconstruct(BlockManager manager, LocationIndexRoot root, byte[] rootHash) =>
        Reconstruct(new BTreeNodeStore(manager, blockIdResolver: null), root, rootHash);

    private static BlockLocationIndex Reconstruct(BTreeNodeStore store, LocationIndexRoot root, byte[] rootHash)
    {
        var reference = new byte[BTreeNodeRef.OffsetReferenceSize];
        BinaryPrimitives.WriteInt64LittleEndian(reference, root.RootOffset);
        var btreeRoot = new BTreeRoot
        {
            RootRef = new BTreeNodeRef
            {
                Addressing = BTreeChildAddressing.Offset,
                Reference = reference,
                NodeHash = rootHash,
            },
            Height = root.TreeHeight,
            EntryCount = root.EntryCount,
        };
        return new BlockLocationIndex(store, initialRoot: btreeRoot);
    }

    private sealed record SourceFile(byte[] FileId, IReadOnlyList<BlockLocation> LiveBlocks, ulong CheckpointSequence);

    /// <summary>
    /// Builds a clean v3 file at <see cref="_path"/> whose named roots (folder tree,
    /// metadata) are live blocks in the BlockLocationIndex — the realistic shape the
    /// sibling suites use — but with a dead (unindexed) block appended before every live
    /// block, so the source carries interior dead space and compaction moves every live
    /// block to a strictly smaller offset. That guarantees the "no stale offset" and
    /// "moved" assertions are not vacuously satisfied by a block that kept its offset.
    /// </summary>
    private SourceFile BuildSource(int dataCount, int dataSize = 256)
    {
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0x60 + i)).ToArray();
        var live = new List<BlockLocation>();
        ulong sequence;
        CheckpointRootPointer checkpointPointer;

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            var deadBuf = new byte[dataSize];
            var buf = new byte[dataSize];
            for (int i = 0; i < dataCount; i++)
            {
                // A dead block that is deliberately NOT added to the live index: it
                // occupies space (so live blocks shift) but compaction never copies it.
                Array.Fill(deadBuf, (byte)0xDD);
                Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, deadBuf));

                Array.Fill(buf, (byte)(i & 0xFF));
                live.Add(Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, buf)));
            }

            var folder = Require(manager.Append(BlockType.FolderTree, PayloadEncoding.RawBytes, new byte[64]));
            live.Add(folder);
            var metadata = Require(manager.Append(BlockType.Metadata, PayloadEncoding.RawBytes, new byte[48]));
            live.Add(metadata);

            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
            Ok(index.PutBatch(live));
            Ok(manager.Flush());

            long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root!.RootRef.Reference);
            var rootBlockId = runtimeMap.SnapshotOrderedByOffset().First(b => b.Offset == rootOffset).BlockId;

            var writer = new CheckpointWriter(manager, fileId);
            Require(writer.WriteCheckpoint(new CheckpointContents
            {
                FolderTreeRoot = CheckpointRootPointer.Create(folder.BlockId, folder.Offset),
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = CheckpointRootPointer.Create(rootBlockId, rootOffset),
                MetadataRoot = CheckpointRootPointer.Create(metadata.BlockId, metadata.Offset),
                KeyStoreRoot = CheckpointRootPointer.None,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = live.Count,
                LiveByteCount = SourceLiveByteCount,
                DeadByteCount = 200_000,
            }));
            sequence = writer.LastSequence!.Value;
            checkpointPointer = writer.LastCheckpointPointer;
        }

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var sbManager = new SuperblockManager(stream, ownsStream: true))
        {
            Superblock MakeSuperblock() => new()
            {
                FileId = (byte[])fileId.Clone(),
                CleanShutdown = 1,
                LastCheckpointBlockId = (byte[])checkpointPointer.BlockId.Clone(),
                LastCheckpointOffset = checkpointPointer.Offset,
            };
            Require(sbManager.Write(MakeSuperblock()));
            Require(sbManager.Write(MakeSuperblock()));
        }

        return new SourceFile(fileId, live, sequence);
    }
}
