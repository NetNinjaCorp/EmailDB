using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="LocationIndexRegenerator"/> and the disaster-path
/// <see cref="FullScanBlockIdResolver"/> (US-EMDB-71-8,
/// EmailDB_FileFormat_Spec.md Section 7): the location index is derived data that
/// can be rebuilt from a full file scan, and the rebuilt index must match the
/// live tree entry-by-entry. Also exercises the full resolution precedence chain
/// end-to-end (runtime map → location index → disaster full scan) over real
/// files.
///
/// The data blocks live in ONE block stream (the "data file"); each location
/// index's own B+-tree nodes live in a SEPARATE stream so the scan of the data
/// file sees only data blocks (spec Section 7: the location index is kept out of
/// the block set it maps).
/// </summary>
public class LocationIndexRegeneratorTests : IDisposable
{
    private readonly List<string> _paths = new();
    private readonly List<BlockManager> _managers = new();

    public void Dispose()
    {
        foreach (var manager in _managers)
            manager.Dispose();
        foreach (var path in _paths)
            if (File.Exists(path))
                File.Delete(path);
    }

    // ------------------------------------------------------------- Helpers

    private BlockManager NewManager(RuntimeBlockOffsetMap? offsetMap = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-regen-{Guid.NewGuid():N}.emdb");
        _paths.Add(path);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var manager = new BlockManager(stream, offsetMap: offsetMap, firstBlockOffset: 0, ownsStream: true);
        _managers.Add(manager);
        return manager;
    }

    /// <summary>A node store over its own fresh stream — offset-addressed, no BlockId resolver.</summary>
    private BTreeNodeStore NewNodeStore() => new(NewManager(), blockIdResolver: null);

    private static byte[] Payload(int length, int seed) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 13 + seed)).ToArray();

    private static void Ok(Result result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
    private static void Ok<T>(Result<T> result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    /// <summary>Appends <paramref name="count"/> data blocks to a fresh data file and returns the manager + its runtime map.</summary>
    private (BlockManager DataFile, RuntimeBlockOffsetMap Runtime) BuildDataFile(int count)
    {
        var runtime = new RuntimeBlockOffsetMap();
        var dataFile = NewManager(runtime);
        for (int i = 0; i < count; i++)
        {
            var appended = dataFile.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, Payload(20 + i, seed: i));
            Ok(appended);
        }
        return (dataFile, runtime);
    }

    /// <summary>Builds a "live" location index over its own node stream from the given data locations.</summary>
    private BlockLocationIndex BuildLiveIndex(
        IReadOnlyList<BlockLocation> locations, int maxLeafEntries = 4, int maxInternalKeys = 3)
    {
        var index = new BlockLocationIndex(NewNodeStore(), maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);
        Ok(index.PutBatch(locations));
        return index;
    }

    /// <summary>A 16-byte ULID whose every byte is <paramref name="tag"/> — a stable, ordered, forgeable BlockId.</summary>
    private static byte[] Ulid(byte tag) => Enumerable.Repeat(tag, 16).ToArray();

    /// <summary>
    /// Builds a data file by writing pre-serialized blocks with caller-chosen
    /// BlockIds directly to the file (the public <see cref="BlockManager.Append"/>
    /// mints a fresh ULID each call, so it cannot produce the duplicate-BlockId
    /// "superseded block" or the specific mixed-type layouts these tests need).
    /// Returns the manager plus a runtime map fed the SAME OnBlockAppended
    /// notifications in ascending file order — i.e. exactly the live set the
    /// durable index would hold (last-position-wins, spec Section 7). So
    /// <c>Runtime.SnapshotOrderedByOffset()</c> is the ground-truth "live" set to
    /// build the live index from and to compare regeneration against.
    /// </summary>
    private (BlockManager DataFile, RuntimeBlockOffsetMap Runtime) BuildRawDataFile(
        IReadOnlyList<(byte[] BlockId, BlockType Type)> specs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-regen-raw-{Guid.NewGuid():N}.emdb");
        _paths.Add(path);
        var runtime = new RuntimeBlockOffsetMap();
        using (var write = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            long offset = 0;
            int seed = 0;
            foreach (var (blockId, type) in specs)
            {
                var payload = Payload(20 + seed, seed);
                seed++;
                var header = new BlockHeader
                {
                    Type = type,
                    Encoding = PayloadEncoding.RawBytes,
                    BlockId = (byte[])blockId.Clone(),
                    PayloadLength = payload.Length,
                };
                var bytes = BlockSerializer.Serialize(header, payload);
                write.Position = offset;
                write.Write(bytes, 0, bytes.Length);
                runtime.OnBlockAppended(header, offset, bytes.Length);
                offset += bytes.Length;
            }
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var manager = new BlockManager(stream, offsetMap: null, firstBlockOffset: 0, ownsStream: true);
        _managers.Add(manager);
        return (manager, runtime);
    }

    /// <summary>Reads the location a BlockLocationIndex currently resolves a BlockId to (asserting present).</summary>
    private static BlockLocationIndex.LocationLookup ResolvedLocation(BlockLocationIndex index, byte[] blockId)
    {
        var lookup = index.Lookup(blockId);
        Ok(lookup);
        Assert.True(lookup.Value.Found, $"BlockId {Convert.ToHexString(blockId)} was expected to be present.");
        return lookup.Value;
    }

    // --------------------------------------------------------------- Tests

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public void Regenerated_index_matches_live_tree_entry_by_entry(int count)
    {
        var (dataFile, runtime) = BuildDataFile(count);
        var live = BuildLiveIndex(runtime.SnapshotOrderedByOffset());

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);
        Assert.Equal(count, regen.Value.BlockCount);
        Assert.Empty(regen.Value.DamagedRanges);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.True(verify.Value.Matches, verify.Value.Mismatch);
        Assert.Equal(count, verify.Value.ComparedEntries);
    }

    [Fact]
    public void Empty_file_regenerates_to_empty_and_matches_empty_live()
    {
        var dataFile = NewManager(); // nothing appended
        var live = BuildLiveIndex(Array.Empty<BlockLocation>());

        var regen = LocationIndexRegenerator.RegenerateFromScan(dataFile, NewNodeStore());
        Ok(regen);
        Assert.Equal(0, regen.Value.BlockCount);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.True(verify.Value.Matches);
        Assert.Equal(0, verify.Value.ComparedEntries);
    }

    [Fact]
    public void Verification_reports_mismatch_when_live_tree_is_missing_an_entry()
    {
        var (dataFile, runtime) = BuildDataFile(6);
        var snapshot = runtime.SnapshotOrderedByOffset();

        // Live index is missing the last block — regeneration (full file) will have more.
        var live = BuildLiveIndex(snapshot.Take(snapshot.Count - 1).ToArray());

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.False(verify.Value.Matches);
        Assert.NotNull(verify.Value.Mismatch);
        Assert.Contains("count differs", verify.Value.Mismatch!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verification_reports_mismatch_when_a_location_differs()
    {
        var (dataFile, runtime) = BuildDataFile(4);
        var snapshot = runtime.SnapshotOrderedByOffset();

        // Live index has the same BlockIds but one corrupted offset.
        var tampered = snapshot.Select(l => l).ToArray();
        var bad = snapshot[1];
        tampered[1] = new BlockLocation
        {
            BlockId = bad.BlockId,
            Offset = bad.Offset + 777,
            TotalBlockLength = bad.TotalBlockLength,
        };
        var live = BuildLiveIndex(tampered);

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.False(verify.Value.Matches);
        Assert.Contains("Location differs", verify.Value.Mismatch!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullScanResolver_resolves_a_data_block_by_ulid()
    {
        var (dataFile, runtime) = BuildDataFile(5);
        var known = runtime.SnapshotOrderedByOffset()[2];

        var logs = new List<string>();
        var resolver = new FullScanBlockIdResolver(dataFile, logs.Add);

        Assert.True(resolver.TryGetLocation(known.BlockId, out var location));
        Assert.Equal(known.Offset, location!.Offset);
        Assert.Equal(known.TotalBlockLength, location.TotalBlockLength);
        Assert.Single(logs); // the one disaster scan is announced

        // A second lookup reuses the cached scan (no additional scan message).
        Assert.True(resolver.TryGetLocation(known.BlockId, out _));
        Assert.Single(logs);

        // An unknown BlockId resolves to nothing.
        Assert.False(resolver.TryGetLocation(new byte[16], out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Precedence_chain_falls_through_to_disaster_scan_over_real_files()
    {
        var (dataFile, runtime) = BuildDataFile(5);
        var target = runtime.SnapshotOrderedByOffset()[3];

        // Empty runtime map and empty location index: only the disaster scan knows.
        var emptyRuntime = new RuntimeBlockOffsetMap();
        var emptyIndex = new BlockLocationIndex(NewNodeStore());
        var logs = new List<string>();
        var disaster = new FullScanBlockIdResolver(dataFile);
        var resolver = CompositeBlockIdResolver.Create(emptyRuntime, emptyIndex, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(target.BlockId, out var location));
        Assert.Equal(target.Offset, location!.Offset);
        Assert.NotEmpty(logs); // the composite flagged the disaster fall-through
    }

    [Fact]
    public void Precedence_prefers_location_index_over_disaster_scan()
    {
        var (dataFile, runtime) = BuildDataFile(5);
        var snapshot = runtime.SnapshotOrderedByOffset();
        var target = snapshot[1];

        var live = BuildLiveIndex(snapshot);
        var logs = new List<string>();
        // Disaster scan would also resolve it, but the index must win — no fall-through.
        var disaster = new FullScanBlockIdResolver(dataFile, logs.Add);
        var resolver = CompositeBlockIdResolver.Create(new RuntimeBlockOffsetMap(), live, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(target.BlockId, out var location));
        Assert.Equal(target.Offset, location!.Offset);
        Assert.Empty(logs); // never touched the disaster path
    }

    // ------------------------------- Realistic-condition regeneration gaps

    [Fact]
    public void Regeneration_dedups_superseded_blocks_last_position_wins_and_matches_live()
    {
        // Block id=0x02 is appended twice — a rewritten/superseded logical block.
        // The live index (fed every physical block in file order) keeps the LATER
        // copy; regeneration must fold the file the same way and agree.
        var duplicated = Ulid(0x02);
        var (dataFile, runtime) = BuildRawDataFile(new[]
        {
            (Ulid(0x01), BlockType.EmailContent),
            (duplicated, BlockType.EmailContent),   // first version
            (Ulid(0x03), BlockType.EmailContent),
            (duplicated, BlockType.EmailContent),   // superseding version, later offset
            (Ulid(0x04), BlockType.EmailContent),
        });

        var live = BuildLiveIndex(runtime.SnapshotOrderedByOffset());

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);
        Assert.Equal(4, regen.Value.BlockCount); // 5 physical blocks, 4 distinct BlockIds

        // The regenerated index resolves the duplicated id to the LATER copy, which
        // is exactly what the live index holds — proving last-position-wins matched.
        var liveDup = ResolvedLocation(live, duplicated);
        var regenDup = ResolvedLocation(regen.Value.Index, duplicated);
        Assert.Equal(liveDup.Offset, regenDup.Offset);
        Assert.True(regenDup.Offset > ResolvedLocation(regen.Value.Index, Ulid(0x01)).Offset);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.True(verify.Value.Matches, verify.Value.Mismatch);
        Assert.Equal(4, verify.Value.ComparedEntries);
    }

    [Fact]
    public void Regeneration_includes_every_block_type_the_live_tree_includes()
    {
        // The location index maps EVERY block in the data stream regardless of type
        // (its own B+-tree nodes live in a separate stream). Interleave several
        // distinct types; regeneration must include exactly the set the live tree
        // does — no type filtering on either side.
        var (dataFile, runtime) = BuildRawDataFile(new[]
        {
            (Ulid(0x10), BlockType.Metadata),
            (Ulid(0x11), BlockType.EmailContent),
            (Ulid(0x12), BlockType.KeyStore),
            (Ulid(0x13), BlockType.FolderPage),
            (Ulid(0x14), BlockType.EmailMetadata),
            (Ulid(0x15), BlockType.Checkpoint),
        });

        var live = BuildLiveIndex(runtime.SnapshotOrderedByOffset());

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);
        Assert.Equal(6, regen.Value.BlockCount);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.True(verify.Value.Matches, verify.Value.Mismatch);
        Assert.Equal(6, verify.Value.ComparedEntries);
    }

    [Fact]
    public void Regeneration_of_a_large_population_builds_a_multi_level_tree_that_matches()
    {
        // Enough blocks (with tiny node capacities) to force a regenerated tree of
        // height >= 2 — internal levels, node splits and shared-leaf batch inserts
        // — not just a single-leaf root, so the multi-level rebuild is exercised.
        const int count = 400;
        var (dataFile, runtime) = BuildDataFile(count);
        var live = BuildLiveIndex(runtime.SnapshotOrderedByOffset());

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);
        Assert.Equal(count, regen.Value.BlockCount);
        Assert.NotNull(regen.Value.Index.Root);
        Assert.True(regen.Value.Index.Root!.Height >= 2,
            $"Expected a multi-level regenerated tree, got height {regen.Value.Index.Root.Height}.");

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.True(verify.Value.Matches, verify.Value.Mismatch);
        Assert.Equal(count, verify.Value.ComparedEntries);
    }

    [Fact]
    public void Regeneration_after_a_compacting_delete_matches_the_post_delete_live_index()
    {
        // A delete is realized only by compaction, which rewrites the file WITHOUT
        // the dead block AND rebuilds the index over the new offsets (there is no
        // tombstone block type) — the index and file are rewritten together and
        // therefore agree. Model the post-compaction state: the scanned file is the
        // compacted one (deleted block's bytes gone) and the live index is the one
        // rebuilt over it. Regeneration must reproduce it exactly, deleted id absent.
        var specs = new[]
        {
            (Ulid(0x21), BlockType.EmailContent),
            (Ulid(0x22), BlockType.EmailContent),
            (Ulid(0x23), BlockType.EmailContent), // dead: dropped by compaction
            (Ulid(0x24), BlockType.EmailContent),
            (Ulid(0x25), BlockType.EmailContent),
        };
        var deleted = Ulid(0x23);

        // Sanity: BlockLocationIndex.Delete actually removes the entry pre-compaction.
        var (_, preRuntime) = BuildRawDataFile(specs);
        var preIndex = BuildLiveIndex(preRuntime.SnapshotOrderedByOffset());
        var wasDeleted = preIndex.Delete(deleted);
        Ok(wasDeleted);
        Assert.True(wasDeleted.Value);
        Assert.False(preIndex.Lookup(deleted).Value.Found);

        // The compacted file and the index rebuilt over it (both omit the dead block).
        var (compacted, compactedRuntime) = BuildRawDataFile(
            specs.Where(s => !s.Item1.AsSpan().SequenceEqual(deleted)).ToArray());
        var live = BuildLiveIndex(compactedRuntime.SnapshotOrderedByOffset());

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            compacted, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);
        Assert.Equal(4, regen.Value.BlockCount);
        Assert.False(regen.Value.Index.Lookup(deleted).Value.Found); // not resurrected

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.True(verify.Value.Matches, verify.Value.Mismatch);
        Assert.Equal(4, verify.Value.ComparedEntries);
    }

    [Fact]
    public void Regeneration_surfaces_a_delete_not_yet_realized_by_compaction_as_a_mismatch()
    {
        // Guard against silent resurrection: if the live index deleted a BlockId
        // but the block's bytes are STILL in the file (a delete not yet compacted —
        // an inconsistent, uncommitted state), regeneration reflects the PHYSICAL
        // file and therefore still holds the entry. VerifyMatchesLiveTree must
        // report this divergence rather than hide it.
        var specs = new[]
        {
            (Ulid(0x31), BlockType.EmailContent),
            (Ulid(0x32), BlockType.EmailContent),
            (Ulid(0x33), BlockType.EmailContent),
            (Ulid(0x34), BlockType.EmailContent),
        };
        var deleted = Ulid(0x33);

        var (dataFile, runtime) = BuildRawDataFile(specs);
        var live = BuildLiveIndex(runtime.SnapshotOrderedByOffset());
        Ok(live.Delete(deleted)); // bytes remain in dataFile — not compacted

        var regen = LocationIndexRegenerator.RegenerateFromScan(
            dataFile, NewNodeStore(), maxLeafEntries: 4, maxInternalKeys: 3);
        Ok(regen);
        Assert.Equal(4, regen.Value.BlockCount); // still sees all four physical blocks
        Assert.True(regen.Value.Index.Lookup(deleted).Value.Found);

        var verify = LocationIndexRegenerator.VerifyMatchesLiveTree(regen.Value.Index, live);
        Ok(verify);
        Assert.False(verify.Value.Matches);
        Assert.NotNull(verify.Value.Mismatch);
    }
}
