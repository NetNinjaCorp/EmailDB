using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Dedicated acceptance test for the US-EMDB-75 criterion
/// "Referenced live data loss surfaces the affected BlockId"
/// (EmailDB_FileFormat_Spec.md Section 13, "referenced live -> data-loss").
///
/// Where <see cref="PerFailureHandlerTests.ReferencedLiveCorruptPayload_SurfacesDataLossNamingBlockId"/>
/// proves the handler is WIRED by appending a lone BlockId-addressed node and reading it
/// straight back, this suite proves the end-to-end GUARANTEE a user actually hits:
/// committed data reachable from a live COW B+-tree (a child leaf genuinely referenced
/// by its parent, the tree root a Checkpoint's IndexRoot would point at) is destroyed on
/// disk, the file is REOPENED with a cold cache, and the paths a user drives — resolving
/// the BlockId through the offset map, then a real tree lookup — surface the loss naming
/// the destroyed block's BlockId BYTE-FOR-BYTE (not merely non-null), with the damaged
/// byte range located.
///
/// It also nails the negative half of the contract: destroying a DEAD (COW-superseded,
/// unreferenced) node must NOT produce a live-data-loss report on the live traversal, and
/// the disaster full-scan recovery path drops the destroyed block rather than resurrecting
/// its corrupt bytes.
/// </summary>
public class ReferencedDataLossTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-refdataloss-{Guid.NewGuid():N}.emdb");

    // PrimaryEmail-shaped tree: 32-byte EmailHashedID key -> 16-byte BlockId value,
    // BlockId-addressed children (every index except BlockLocation is BlockId-addressed,
    // CowBTree line 70) — so its nodes flow through the ReferencedDataLoss path.
    private const byte KeySize = 32;
    private const ushort ValueSize = 16;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ------------------------------------------------------------- Helpers

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private BlockManager CreateManager(IBlockOffsetMap? offsetMap = null) =>
        new(OpenStream(), Superblock.DefaultMaxPayloadLength, offsetMap: offsetMap, firstBlockOffset: 0, ownsStream: true);

    private static CowBTree NewTree(BTreeNodeStore store) =>
        new(store, BTreeIndexKind.PrimaryEmail, KeySize, ValueSize, maxLeafEntries: 2, maxInternalKeys: 2);

    /// <summary>A distinct 32-byte key whose leading byte orders it (so routing is predictable).</summary>
    private static byte[] Key(byte lead)
    {
        var k = new byte[KeySize];
        k[0] = lead;
        for (int i = 1; i < KeySize; i++) k[i] = (byte)(lead * 7 + i);
        return k;
    }

    private static byte[] Value(byte tag) => Enumerable.Repeat(tag, ValueSize).ToArray();

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private void CorruptPayloadByte(long blockOffset, int payloadByteIndex = 4)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long at = blockOffset + BlockSerializer.PayloadOffset + payloadByteIndex;
        fs.Seek(at, SeekOrigin.Begin);
        var b = (byte)fs.ReadByte();
        fs.Seek(at, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
        fs.Flush(flushToDisk: true);
    }

    // --- A live child leaf's payload is destroyed: tree lookup names its exact BlockId ---

    [Fact]
    public void ReferencedLiveLeafChild_DestroyedOnDisk_SurfacesExactBlockIdThroughTreeLookup()
    {
        // Build a real, committed tree whose root is INTERNAL with two live leaf children
        // (maxLeafEntries 2, three keys forces one split). The left child holds Key(1).
        var runtimeMap = new RuntimeBlockOffsetMap();
        BTreeRoot root;
        BTreeNodeRef liveLeafRef;
        long liveLeafOffset;

        using (var writer = CreateManager(runtimeMap))
        {
            var store = new BTreeNodeStore(writer, runtimeMap);
            var tree = NewTree(store);

            var r = tree.Insert(null, Key(1), Value(0x11)); Ok(r);
            r = tree.Insert(r.Value, Key(2), Value(0x22)); Ok(r);
            r = tree.Insert(r.Value, Key(3), Value(0x33)); Ok(r);
            root = r.Value;
            Assert.Equal(2, root.Height); // root split -> internal over two leaves

            // Read the live root and extract child[0]'s reference — a node genuinely
            // REFERENCED by the live tree (parent -> child), not merely appended. The
            // reference (BlockId + expected ChildHash) is exactly what a traversal uses.
            var rootPayload = store.ReadVerified(root.RootRef, BTreeNodeKind.Internal); Ok(rootPayload);
            var internalNode = EmailDB.Format.V3.BTreeNodeSerializer.DeserializeInternal(rootPayload.Value); Ok(internalNode);
            var child0 = BTreeNodeRef.FromChildRecord(
                BTreeChildAddressing.BlockId, internalNode.Value.Children[0]); Ok(child0);
            liveLeafRef = child0.Value;
            Assert.Equal(BTreeChildAddressing.BlockId, liveLeafRef.Addressing);

            Assert.True(writer.Flush().IsSuccess);
            Assert.True(runtimeMap.TryGetLocation(liveLeafRef.Reference, out var loc) && loc is not null);
            liveLeafOffset = loc!.Offset;
        }

        var liveLeafBlockId = liveLeafRef.Reference;

        // Destroy the referenced leaf's committed payload on disk.
        CorruptPayloadByte(liveLeafOffset);

        // Reopen with a fresh manager + cold-cache node store so the read hits the
        // damaged bytes on disk (not the writer's buffer or a warm cache).
        using var reader = CreateManager();
        var readerStore = new BTreeNodeStore(reader, runtimeMap);
        var readerTree = NewTree(readerStore);

        // The BlockId still resolves live through the offset map — it IS referenced.
        Assert.True(runtimeMap.TryGetLocation(liveLeafBlockId, out var live) && live is not null);
        Assert.Equal(liveLeafOffset, live!.Offset);

        // User path #1 — the block read a traversal performs via the resolver: resolve
        // the referenced BlockId and read+verify it. This surfaces the TYPED data-loss
        // error carrying the affected BlockId.
        var read = readerStore.ReadVerified(liveLeafRef, BTreeNodeKind.Leaf);
        Assert.True(read.IsFailure);
        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
        Assert.Equal(CorruptionCause.ReferencedLiveDataLoss, error.Cause);
        Assert.True(error.ReferencedLiveData);

        // The affected BlockId is surfaced BYTE-FOR-BYTE (not merely non-null).
        Assert.NotNull(error.BlockId);
        Assert.True(liveLeafBlockId.AsSpan().SequenceEqual(error.BlockId!),
            $"expected BlockId {Convert.ToHexStringLower(liveLeafBlockId)}, got {Convert.ToHexStringLower(error.BlockId!)}");
        Assert.Contains(Convert.ToHexStringLower(liveLeafBlockId), error.Message);

        // The loss is located: offset of the destroyed block and the damaged payload range.
        Assert.Equal(liveLeafOffset, error.Offset);
        Assert.NotNull(error.DamagedRange);
        Assert.Equal(liveLeafOffset + BlockSerializer.PayloadOffset, error.DamagedRange!.Value.Start);
        Assert.InRange(
            liveLeafOffset + BlockSerializer.PayloadOffset + 4,
            error.DamagedRange.Value.Start, error.DamagedRange.Value.End - 1);

        // User path #2 — a full point lookup routing root -> child[0] (the destroyed
        // leaf). CowBTree.TryGet flattens Result errors to their message, so the typed
        // error does not survive that layer, but the surfaced message still names the
        // affected BlockId exactly (hex), so the criterion holds end-to-end.
        var lookup = readerTree.TryGet(root, Key(1));
        Assert.True(lookup.IsFailure);
        Assert.Contains(Convert.ToHexStringLower(liveLeafBlockId), lookup.Error);
    }

    // --- Distinguish: a DEAD (unreferenced) node's loss is silent; the LIVE one is not ---

    [Fact]
    public void UnreferencedDeadNodeLoss_IsSilent_WhileReferencedLiveNodeLoss_NamesTheBlockId()
    {
        // One key, then COW-overwrite it: root1's leaf (B_dead) is superseded and no
        // longer referenced; root2's leaf (B_live) is the live node.
        var runtimeMap = new RuntimeBlockOffsetMap();
        BTreeRoot root2;
        byte[] deadBlockId, liveBlockId;
        long deadOffset, liveOffset;

        using (var writer = CreateManager(runtimeMap))
        {
            var store = new BTreeNodeStore(writer, runtimeMap);
            var tree = NewTree(store);

            var r1 = tree.Insert(null, Key(9), Value(0xAA)); Ok(r1);
            Assert.Equal(1, r1.Value.Height);
            deadBlockId = r1.Value.RootRef.Reference; // root leaf, height 1

            var r2 = tree.Insert(r1.Value, Key(9), Value(0xBB)); Ok(r2); // COW overwrite
            root2 = r2.Value;
            liveBlockId = root2.RootRef.Reference;
            Assert.False(deadBlockId.AsSpan().SequenceEqual(liveBlockId), "COW must append a new node");

            Assert.True(writer.Flush().IsSuccess);
            Assert.True(runtimeMap.TryGetLocation(deadBlockId, out var d) && d is not null);
            Assert.True(runtimeMap.TryGetLocation(liveBlockId, out var l) && l is not null);
            deadOffset = d!.Offset;
            liveOffset = l!.Offset;
        }

        // Phase 1: destroy ONLY the dead, unreferenced node. A lookup on the live root
        // never touches it, so no live-data-loss is reported — the read simply succeeds.
        CorruptPayloadByte(deadOffset);
        using (var reader = CreateManager())
        {
            var tree = NewTree(new BTreeNodeStore(reader, runtimeMap));
            var lookup = tree.TryGet(root2, Key(9));
            Assert.True(lookup.IsSuccess, lookup.IsFailure ? lookup.Error : null);
            Assert.True(lookup.Value.Found);
            Assert.True(Value(0xBB).AsSpan().SequenceEqual(lookup.Value.Value!)); // latest value intact
        }

        // Phase 2: now also destroy the LIVE node (root2's leaf). Reading the SAME live
        // reference now surfaces the loss naming the live node's exact BlockId — proving
        // the silence in phase 1 was because the block was unreferenced, not because the
        // path is inert. (root2.RootRef is the live leaf's own reference.)
        CorruptPayloadByte(liveOffset);
        using (var reader = CreateManager()) // fresh cold cache
        {
            var store = new BTreeNodeStore(reader, runtimeMap);
            var read = store.ReadVerified(root2.RootRef, BTreeNodeKind.Leaf);
            Assert.True(read.IsFailure);

            var error = Assert.IsType<CorruptionError>(read.VerificationError);
            Assert.Equal(CorruptionCause.ReferencedLiveDataLoss, error.Cause);
            Assert.True(liveBlockId.AsSpan().SequenceEqual(error.BlockId!),
                $"expected live BlockId {Convert.ToHexStringLower(liveBlockId)}, got {Convert.ToHexStringLower(error.BlockId!)}");
            Assert.Equal(liveOffset, error.Offset);

            // And the end-to-end lookup fails naming that same BlockId in its message.
            var tree = NewTree(new BTreeNodeStore(reader, runtimeMap));
            var lookup = tree.TryGet(root2, Key(9));
            Assert.True(lookup.IsFailure);
            Assert.Contains(Convert.ToHexStringLower(liveBlockId), lookup.Error);
        }
    }

    // --- The disaster/regeneration full scan drops the destroyed block, not resurrecting it ---

    [Fact]
    public void DisasterFullScan_SkipsTheDestroyedReferencedBlock_AndLogsItsDamagedRange()
    {
        var runtimeMap = new RuntimeBlockOffsetMap();
        byte[] liveLeafBlockId;
        long liveLeafOffset;

        using (var writer = CreateManager(runtimeMap))
        {
            var store = new BTreeNodeStore(writer, runtimeMap);
            var tree = NewTree(store);

            var r = tree.Insert(null, Key(1), Value(0x11)); Ok(r);
            r = tree.Insert(r.Value, Key(2), Value(0x22)); Ok(r);
            r = tree.Insert(r.Value, Key(3), Value(0x33)); Ok(r);

            var rootPayload = store.ReadVerified(r.Value.RootRef, BTreeNodeKind.Internal); Ok(rootPayload);
            var internalNode = EmailDB.Format.V3.BTreeNodeSerializer.DeserializeInternal(rootPayload.Value); Ok(internalNode);
            var child0 = BTreeNodeRef.FromChildRecord(
                BTreeChildAddressing.BlockId, internalNode.Value.Children[0]); Ok(child0);
            liveLeafBlockId = child0.Value.Reference;

            Assert.True(writer.Flush().IsSuccess);
            Assert.True(runtimeMap.TryGetLocation(liveLeafBlockId, out var loc) && loc is not null);
            liveLeafOffset = loc!.Offset;
        }

        CorruptPayloadByte(liveLeafOffset);

        // A forward scan (the disaster/regeneration recovery primitive) refuses to fold
        // the destroyed block into the rebuilt block set and records the damaged bytes.
        using var reader = CreateManager();
        var scanned = reader.ScanForward();
        Ok(scanned);

        Assert.DoesNotContain(scanned.Value.Blocks,
            b => liveLeafBlockId.AsSpan().SequenceEqual(b.BlockId));
        Assert.Contains(scanned.Value.DamagedRanges,
            d => liveLeafOffset >= d.Start && liveLeafOffset < d.End);
    }
}
