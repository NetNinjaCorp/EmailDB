using System.Buffers.Binary;
using System.Diagnostics;
using EmailDB.Format.V3;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Randomized insert/delete stress harness for the v3 copy-on-write B+-tree
/// (US-EMDB-68-5): drives <see cref="CowBTree"/> with a random mix of
/// inserts (fresh keys and upserts), deletes (live and missing keys), and
/// lookups, validating every step against an in-memory reference model
/// (<see cref="SortedDictionary{TKey,TValue}"/>).
///
/// Verification layers:
///  - every op: Insert/Delete outcome and <see cref="BTreeRoot.EntryCount"/>
///    must match the model exactly;
///  - periodic spot audits: sampled live-key lookups (exact value match) and
///    absent-key lookups;
///  - at peak and at the end: full-scan equality (every key and value, in
///    order, against the sorted model), a bounded range-scan comparison, and
///    a full structural walk asserting the B+-tree invariants (occupancy
///    minima/maxima, strict key ordering, child counts, entry-count totals)
///    over Merkle-verified node reads;
///  - snapshot isolation: the peak root is re-scanned AFTER the shrink phase
///    and must still equal the model as it was at the peak.
///
/// The tree uses 8-byte keys encoded big-endian so unsigned-lexicographic
/// tree order equals numeric model order, and offset child addressing
/// (BlockLocation kind) so the store needs no BlockId resolver map — at
/// millions of appended node blocks a runtime BlockId map would cost
/// hundreds of MB of RAM for nothing.
///
/// COW keeps every rewritten root-to-leaf path forever, so the store grows
/// by a few KB per operation; the million-entry run reaches several GB. The
/// backing file therefore goes to tmpfs (/dev/shm) when available instead of
/// the real disk, and is deleted afterwards.
/// </summary>
internal static class CowBTreeModelStressHarness
{
    internal sealed record StressOptions
    {
        /// <summary>Deterministic RNG seed — reruns reproduce the exact op sequence.</summary>
        public required int Seed { get; init; }

        /// <summary>Grow (with interleaved deletes) until this many entries are live.</summary>
        public required long PeakLiveEntries { get; init; }

        /// <summary>Delete-biased mixed ops to run after the peak audit.</summary>
        public required int MixedPhaseOps { get; init; }

        public required int MaxLeafEntries { get; init; }
        public required int MaxInternalKeys { get; init; }

        /// <summary>Probability an op in the growth phase is a delete.</summary>
        public double GrowthDeleteProbability { get; init; } = 0.05;

        /// <summary>Probability a growth-phase insert overwrites an existing key.</summary>
        public double GrowthUpsertProbability { get; init; } = 0.04;

        /// <summary>Probability an op in the mixed (shrink) phase is a delete.</summary>
        public double MixedDeleteProbability { get; init; } = 0.60;

        /// <summary>Ops between spot audits (sampled lookups vs the model).</summary>
        public int AuditInterval { get; init; } = 50_000;

        /// <summary>Minimum tree height required at peak (proves multi-level rebalancing ran).</summary>
        public int MinPeakHeight { get; init; } = 3;
    }

    private const byte KeyWidth = 8;
    private const ushort ValueWidth = 8;

    /// <summary>
    /// State for one run: the tree + store, the reference model, and the live
    /// key list (index map gives O(1) random victim selection for deletes).
    /// </summary>
    private sealed class StressRun(CowBTree tree, BTreeNodeStore store, Random random)
    {
        public CowBTree Tree { get; } = tree;
        public BTreeNodeStore Store { get; } = store;
        public Random Random { get; } = random;
        public SortedDictionary<ulong, ulong> Model { get; } = [];
        public List<ulong> LiveKeys { get; } = [];
        public Dictionary<ulong, int> LivePositions { get; } = [];
        public BTreeRoot? Root;
        public long OpCount;
    }

    /// <summary>Runs the full stress; throws (xunit assert) on any divergence.</summary>
    public static void Run(StressOptions options, ITestOutputHelper output, string storeTag)
    {
        string path = CreateStorePath(storeTag);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1 << 16);
            using var manager = new BlockManager(stream, firstBlockOffset: 0, ownsStream: true);
            // Offset addressing (BlockLocation kind): no BlockId resolver needed.
            var store = new BTreeNodeStore(manager);
            var tree = new CowBTree(
                store, BTreeIndexKind.BlockLocation, KeyWidth, ValueWidth,
                options.MaxLeafEntries, options.MaxInternalKeys);
            var run = new StressRun(tree, store, new Random(options.Seed));

            // ---- Phase 1: randomized growth (insert-biased, deletes interleaved) to the peak.
            long growthDeletes = 0, growthUpserts = 0;
            long opCap = options.PeakLiveEntries * 4 + 1000; // safety net against a livelock bug
            while (run.Model.Count < options.PeakLiveEntries)
            {
                Assert.True(run.OpCount < opCap,
                    $"Growth phase ran {run.OpCount} ops without reaching {options.PeakLiveEntries} live entries.");
                if (run.Random.NextDouble() < options.GrowthDeleteProbability && run.LiveKeys.Count > 0)
                {
                    DeleteOp(run);
                    growthDeletes++;
                }
                else if (run.Random.NextDouble() < options.GrowthUpsertProbability && run.LiveKeys.Count > 0)
                {
                    InsertOp(run, PickLiveKey(run)); // upsert: value replaced, count unchanged
                    growthUpserts++;
                }
                else
                {
                    InsertOp(run, RandomKey(run.Random));
                }
                if (run.OpCount % options.AuditInterval == 0)
                    SpotAudit(run);
            }
            output.WriteLine(
                $"Growth: {run.OpCount:N0} ops ({growthDeletes:N0} deletes, {growthUpserts:N0} upserts) " +
                $"-> {run.Model.Count:N0} live entries in {stopwatch.Elapsed.TotalSeconds:F1}s, " +
                $"height {run.Root!.Height}, store {new FileInfo(path).Length / (1024.0 * 1024):F0} MB");

            // ---- Peak audit: the acceptance criterion's "at 1M+ entries" point.
            Assert.True(run.Model.Count >= options.PeakLiveEntries,
                $"Expected at least {options.PeakLiveEntries:N0} live entries at peak, got {run.Model.Count:N0}.");
            Assert.True(run.Root!.Height >= options.MinPeakHeight,
                $"Peak tree height {run.Root.Height} is below {options.MinPeakHeight} — stress shape is degenerate.");
            SpotAudit(run);
            AssertFullScanMatchesModel(run.Tree, run.Root, run.Model.GetEnumerator(), run.Model.Count);
            AssertBoundedRangeScanMatchesModel(run);
            AssertTreeInvariants(run.Tree, run.Store, run.Root, run.Model.Count);
            output.WriteLine(
                $"Peak audit passed at {run.Model.Count:N0} entries in {stopwatch.Elapsed.TotalSeconds:F1}s.");

            // Snapshot the peak version: COW must keep it intact through phase 2.
            var peakRoot = run.Root;
            var peakSnapshot = run.Model.ToArray(); // already in key order

            // ---- Phase 2: delete-biased mixed ops (underflow, borrow/merge, height shrink).
            long mixedStartOps = run.OpCount;
            long mixedDeletes = 0;
            for (int i = 0; i < options.MixedPhaseOps; i++)
            {
                if (run.Random.NextDouble() < options.MixedDeleteProbability && run.LiveKeys.Count > 0)
                {
                    DeleteOp(run);
                    mixedDeletes++;
                }
                else if (run.Random.NextDouble() < 0.15 && run.LiveKeys.Count > 0)
                {
                    InsertOp(run, PickLiveKey(run)); // upsert
                }
                else
                {
                    InsertOp(run, RandomKey(run.Random));
                }
                if (run.OpCount % options.AuditInterval == 0)
                    SpotAudit(run);
            }
            output.WriteLine(
                $"Mixed: {run.OpCount - mixedStartOps:N0} ops ({mixedDeletes:N0} deletes) " +
                $"-> {run.Model.Count:N0} live entries in {stopwatch.Elapsed.TotalSeconds:F1}s.");

            // ---- Final audit: full equality + invariants on the final version.
            SpotAudit(run);
            AssertFullScanMatchesModel(run.Tree, run.Root, run.Model.GetEnumerator(), run.Model.Count);
            AssertBoundedRangeScanMatchesModel(run);
            AssertTreeInvariants(run.Tree, run.Store, run.Root, run.Model.Count);

            // ---- Snapshot isolation: the peak root is untouched by phase 2.
            AssertFullScanMatchesModel(
                run.Tree, peakRoot,
                ((IEnumerable<KeyValuePair<ulong, ulong>>)peakSnapshot).GetEnumerator(),
                peakSnapshot.Length);
            AssertTreeInvariants(run.Tree, run.Store, peakRoot, peakSnapshot.Length);

            output.WriteLine(
                $"Done: {run.OpCount:N0} total ops, final {run.Model.Count:N0} live entries, " +
                $"store {new FileInfo(path).Length / (1024.0 * 1024):F0} MB, " +
                $"total {stopwatch.Elapsed.TotalSeconds:F1}s (seed {options.Seed}).");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// The store grows a few KB per COW op and is deleted at the end; prefer
    /// RAM-backed tmpfs over the real disk when it exists (Linux).
    /// </summary>
    private static string CreateStorePath(string tag)
    {
        string dir = OperatingSystem.IsLinux() && Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        return Path.Combine(dir, $"emaildb-cow-stress-{tag}-{Guid.NewGuid():N}.emdb");
    }

    // ---------------------------------------------------------------- Ops

    private static ulong RandomKey(Random random) => (ulong)random.NextInt64();

    private static ulong PickLiveKey(StressRun run) =>
        run.LiveKeys[run.Random.Next(run.LiveKeys.Count)];

    private static void WriteKey(Span<byte> buffer, ulong key) =>
        BinaryPrimitives.WriteUInt64BigEndian(buffer, key);

    /// <summary>Inserts/upserts <paramref name="key"/> in the tree AND the model, then cross-checks.</summary>
    private static void InsertOp(StressRun run, ulong key)
    {
        run.OpCount++;
        ulong value = (ulong)run.OpCount; // op counter: proves latest-write-wins on upserts
        Span<byte> keyBytes = stackalloc byte[KeyWidth];
        Span<byte> valueBytes = stackalloc byte[ValueWidth];
        WriteKey(keyBytes, key);
        BinaryPrimitives.WriteUInt64BigEndian(valueBytes, value);

        var result = run.Tree.Insert(run.Root, keyBytes, valueBytes);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        run.Root = result.Value;

        if (!run.Model.ContainsKey(key))
        {
            run.LivePositions[key] = run.LiveKeys.Count;
            run.LiveKeys.Add(key);
        }
        run.Model[key] = value;
        Assert.Equal(run.Model.Count, run.Root.EntryCount);
    }

    /// <summary>
    /// Deletes a random live key (or, 10% of the time, a random almost
    /// certainly missing key) from the tree AND the model; the Removed flag
    /// and entry count must match the model's outcome exactly.
    /// </summary>
    private static void DeleteOp(StressRun run)
    {
        run.OpCount++;
        ulong key = run.Random.NextDouble() < 0.10 ? RandomKey(run.Random) : PickLiveKey(run);
        Span<byte> keyBytes = stackalloc byte[KeyWidth];
        WriteKey(keyBytes, key);

        var result = run.Tree.Delete(run.Root!, keyBytes);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

        bool expectedRemoved = run.Model.Remove(key);
        Assert.Equal(expectedRemoved, result.Value.Removed);
        if (expectedRemoved)
        {
            int position = run.LivePositions[key];
            ulong lastKey = run.LiveKeys[^1];
            run.LiveKeys[position] = lastKey;
            run.LivePositions[lastKey] = position;
            run.LiveKeys.RemoveAt(run.LiveKeys.Count - 1);
            run.LivePositions.Remove(key);
            run.Root = result.Value.Root;
        }
        else
        {
            // A miss is a no-op: the exact same version handle comes back.
            Assert.Same(run.Root, result.Value.Root);
        }
        Assert.Equal(run.Model.Count, run.Root?.EntryCount ?? 0);
    }

    // ---------------------------------------------------------------- Audits

    /// <summary>Sampled point-lookup audit: live keys return the model's value, absent keys miss.</summary>
    private static void SpotAudit(StressRun run, int liveSamples = 32, int absentSamples = 8)
    {
        Span<byte> keyBytes = stackalloc byte[KeyWidth];
        for (int i = 0; i < liveSamples && run.LiveKeys.Count > 0; i++)
        {
            ulong key = PickLiveKey(run);
            WriteKey(keyBytes, key);
            var lookup = run.Tree.TryGet(run.Root!, keyBytes);
            Assert.True(lookup.IsSuccess, lookup.IsFailure ? lookup.Error : null);
            Assert.True(lookup.Value.Found, $"Live key {key} not found at op {run.OpCount}.");
            Assert.Equal(run.Model[key], BinaryPrimitives.ReadUInt64BigEndian(lookup.Value.Value));
        }
        for (int i = 0; i < absentSamples; i++)
        {
            ulong key = RandomKey(run.Random);
            if (run.Model.ContainsKey(key))
                continue; // ~0 probability, but stay exact
            WriteKey(keyBytes, key);
            if (run.Root is null)
                break;
            var lookup = run.Tree.TryGet(run.Root, keyBytes);
            Assert.True(lookup.IsSuccess, lookup.IsFailure ? lookup.Error : null);
            Assert.False(lookup.Value.Found, $"Absent key {key} was found at op {run.OpCount}.");
        }
    }

    /// <summary>
    /// Walks an unbounded scan of <paramref name="root"/> in lockstep with the
    /// (sorted) reference entries: every key and value must match, in order,
    /// with no extra or missing entries on either side.
    /// </summary>
    private static void AssertFullScanMatchesModel(
        CowBTree tree, BTreeRoot? root, IEnumerator<KeyValuePair<ulong, ulong>> expected, long expectedCount)
    {
        var scan = tree.Scan(root);
        long matched = 0;
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            Assert.True(expected.MoveNext(),
                $"Tree scan yielded more than the {expectedCount} entries in the model.");
            Assert.Equal(expected.Current.Key, BinaryPrimitives.ReadUInt64BigEndian(scan.Current.Key));
            Assert.Equal(expected.Current.Value, BinaryPrimitives.ReadUInt64BigEndian(scan.Current.Value));
            matched++;
        }
        Assert.False(expected.MoveNext(), "Tree scan ended before the model was exhausted.");
        Assert.Equal(expectedCount, matched);
    }

    /// <summary>Compares one random bounded [start, end) range scan against the model.</summary>
    private static void AssertBoundedRangeScanMatchesModel(StressRun run)
    {
        if (run.LiveKeys.Count < 2)
            return;
        ulong a = PickLiveKey(run);
        ulong b = PickLiveKey(run);
        if (a > b)
            (a, b) = (b, a);

        Span<byte> start = stackalloc byte[KeyWidth];
        Span<byte> end = stackalloc byte[KeyWidth];
        WriteKey(start, a);
        WriteKey(end, b);
        var scan = run.Tree.Scan(run.Root, start, end);

        using var expected = run.Model
            .Where(pair => pair.Key >= a && pair.Key < b)
            .GetEnumerator();
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            Assert.True(expected.MoveNext(), "Bounded scan yielded an entry the model does not have in range.");
            Assert.Equal(expected.Current.Key, BinaryPrimitives.ReadUInt64BigEndian(scan.Current.Key));
            Assert.Equal(expected.Current.Value, BinaryPrimitives.ReadUInt64BigEndian(scan.Current.Value));
        }
        Assert.False(expected.MoveNext(), "Bounded scan ended before the model's range was exhausted.");
    }

    // ------------------------------------------- Structural invariant walker

    /// <summary>
    /// Full structural walk of one tree version over Merkle-verified reads
    /// (same invariants as CowBTreeDeleteTests): non-root occupancy minima,
    /// occupancy maxima, strict key ordering, Keys+1 == Children, and the
    /// leaf total matching <see cref="BTreeRoot.EntryCount"/>.
    /// </summary>
    private static void AssertTreeInvariants(
        CowBTree tree, BTreeNodeStore store, BTreeRoot? root, long expectedEntryCount)
    {
        if (root is null)
        {
            Assert.Equal(0, expectedEntryCount);
            return;
        }
        Assert.Equal(expectedEntryCount, root.EntryCount);
        long counted = CountAndValidateSubtree(tree, store, root.RootRef, root.Height, isRoot: true);
        Assert.Equal(expectedEntryCount, counted);
    }

    private static long CountAndValidateSubtree(
        CowBTree tree, BTreeNodeStore store, BTreeNodeRef nodeRef, int height, bool isRoot)
    {
        if (height <= 1)
        {
            var payload = store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
            var leafResult = BTreeNodeSerializer.DeserializeLeaf(payload.Value);
            Assert.True(leafResult.IsSuccess, leafResult.IsFailure ? leafResult.Error : null);
            var entries = leafResult.Value.Entries;

            int minimum = isRoot ? 1 : tree.MinLeafEntries;
            Assert.True(entries.Count >= minimum,
                $"Leaf occupancy {entries.Count} below minimum {minimum} (isRoot={isRoot}).");
            Assert.True(entries.Count <= tree.MaxLeafEntries,
                $"Leaf occupancy {entries.Count} above maximum {tree.MaxLeafEntries}.");
            for (int i = 1; i < entries.Count; i++)
                Assert.True(entries[i - 1].Key.AsSpan().SequenceCompareTo(entries[i].Key) < 0,
                    "Leaf keys must be strictly ascending.");
            return entries.Count;
        }

        var nodePayload = store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(nodePayload.IsSuccess, nodePayload.IsFailure ? nodePayload.Error : null);
        var nodeResult = BTreeNodeSerializer.DeserializeInternal(nodePayload.Value);
        Assert.True(nodeResult.IsSuccess, nodeResult.IsFailure ? nodeResult.Error : null);
        var node = nodeResult.Value;

        int minKeys = isRoot ? 1 : tree.MinInternalKeys;
        Assert.True(node.Keys.Count >= minKeys,
            $"Internal occupancy {node.Keys.Count} keys below minimum {minKeys} at height {height} (isRoot={isRoot}).");
        Assert.True(node.Keys.Count <= tree.MaxInternalKeys,
            $"Internal occupancy {node.Keys.Count} keys above maximum {tree.MaxInternalKeys}.");
        Assert.Equal(node.Keys.Count + 1, node.Children.Count);
        for (int i = 1; i < node.Keys.Count; i++)
            Assert.True(node.Keys[i - 1].AsSpan().SequenceCompareTo(node.Keys[i]) < 0,
                "Internal routing keys must be strictly ascending.");

        long total = 0;
        foreach (var record in node.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            total += CountAndValidateSubtree(tree, store, childRef.Value, height - 1, isRoot: false);
        }
        return total;
    }
}

/// <summary>
/// Default-suite randomized model stress (US-EMDB-68-5): tens of thousands of
/// random inserts/deletes/upserts against the reference model, sized to keep
/// the default ./test.sh run fast. The full 1M+-entry acceptance run lives in
/// <see cref="MillionEntryModelStressTests"/> (Category=Stress, excluded from
/// the default filter).
/// </summary>
public class CowBTreeModelStressTests(ITestOutputHelper output)
{
    [Fact]
    public void RandomizedInsertDeleteStress_TensOfThousands_MatchesReferenceModel()
    {
        CowBTreeModelStressHarness.Run(new CowBTreeModelStressHarness.StressOptions
        {
            Seed = 20260704,
            PeakLiveEntries = 8_000,
            MixedPhaseOps = 4_000,
            MaxLeafEntries = 16,
            MaxInternalKeys = 8,
            GrowthDeleteProbability = 0.08,
            GrowthUpsertProbability = 0.05,
            AuditInterval = 2_000,
            MinPeakHeight = 4,
        }, output, storeTag: "default");
    }
}

/// <summary>
/// US-EMDB-68 acceptance criterion: "Randomized insert/delete stress against
/// a reference model at 1M+ entries". Reaches 1M+ live entries at peak with
/// randomized deletes and upserts interleaved throughout, full-scan equality
/// against the SortedDictionary model at peak and after a delete-biased mixed
/// phase, structural invariants over Merkle-verified reads, and peak-snapshot
/// isolation.
///
/// Excluded from the default ./test.sh filter (no default fragment matches
/// this class name) and marked Category=Stress: the run appends ~1.2M COW
/// path rewrites (a several-GB store in /dev/shm) and takes minutes, not
/// seconds. Run it explicitly with:
///
///     ./test.sh MillionEntryModelStress
/// </summary>
public class MillionEntryModelStressTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Stress")]
    public void RandomizedInsertDeleteStress_OneMillionEntries_MatchesReferenceModel()
    {
        CowBTreeModelStressHarness.Run(new CowBTreeModelStressHarness.StressOptions
        {
            Seed = 20260704,
            PeakLiveEntries = 1_005_000,
            MixedPhaseOps = 40_000,
            MaxLeafEntries = 48,
            MaxInternalKeys = 10,
            GrowthDeleteProbability = 0.04,
            GrowthUpsertProbability = 0.03,
            AuditInterval = 100_000,
            MinPeakHeight = 4,
        }, output, storeTag: "1m");
    }
}
