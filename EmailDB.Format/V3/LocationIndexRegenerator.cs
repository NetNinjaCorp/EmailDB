namespace EmailDB.Format.V3;

/// <summary>
/// Full regeneration of the BlockLocationIndex from a file scan, with
/// verification against a live index (EmailDB_FileFormat_Spec.md Section 7:
/// "derived data rebuilt by compaction or full scan"). The location index is
/// derived data — it can always be rebuilt from the blocks physically present in
/// the file — so this rescans the file, deduplicates duplicate BlockIds
/// last-position-wins, and batch-inserts the resulting live locations into a
/// fresh <see cref="BlockLocationIndex"/> over a caller-supplied node store.
///
/// <para><see cref="VerifyMatchesLiveTree"/> then confirms the rebuilt index
/// equals a live index entry-by-entry: it walks both trees in one lock-step
/// ascending scan and compares every (BlockId → Offset, Length) pair, surfacing
/// the first divergence. This is the acceptance check "Index regenerates from a
/// full scan and matches" — the disaster full scan and the durable index agree.</para>
///
/// <para><b>Node placement.</b> The rebuilt index's own B+-tree nodes are
/// appended through <paramref name="nodeStore"/>'s block manager. Pass a node
/// store over a SEPARATE block stream from the data file being scanned so the
/// regeneration does not append index nodes into the pure data stream it is
/// reconstructing from (spec Section 7: the location index addresses its nodes by
/// offset and is kept out of the logical block set it maps).</para>
/// </summary>
public static class LocationIndexRegenerator
{
    /// <summary>Outcome of <see cref="RegenerateFromScan"/>: the rebuilt index and what the scan saw.</summary>
    /// <param name="Index">The freshly rebuilt BlockLocationIndex (its <see cref="BlockLocationIndex.Root"/> is the regenerated tree).</param>
    /// <param name="BlockCount">Distinct live BlockIds folded into the index (post last-position-wins dedup).</param>
    /// <param name="DamagedRanges">Byte ranges the scan could not attribute to a valid block (spec Section 13).</param>
    public readonly record struct Regeneration(
        BlockLocationIndex Index,
        long BlockCount,
        IReadOnlyList<DamagedRange> DamagedRanges);

    /// <summary>
    /// Rebuilds the location index from a full forward scan of
    /// <paramref name="dataFile"/>: every fully-verified block is collected,
    /// duplicate BlockIds resolve last-position-wins (the greater file offset is
    /// the live version), and the deduplicated set is batch-inserted into a fresh
    /// <see cref="BlockLocationIndex"/> in one copy-on-write pass. Fails only on an
    /// I/O error during the scan or a node-write failure during the rebuild;
    /// corrupt blocks are skipped (spec Section 13), not fatal.
    /// </summary>
    /// <param name="dataFile">The block manager whose file is scanned for live blocks.</param>
    /// <param name="nodeStore">Node persistence for the rebuilt tree — should be over a stream SEPARATE from <paramref name="dataFile"/> (see type remarks).</param>
    /// <param name="maxLeafEntries">Test hook forwarded to the rebuilt index; null uses the spec capacity.</param>
    /// <param name="maxInternalKeys">Test hook forwarded to the rebuilt index; null uses the spec capacity.</param>
    /// <param name="log">Optional sink receiving one message per damaged range encountered during the scan.</param>
    public static Result<Regeneration> RegenerateFromScan(
        BlockManager dataFile,
        BTreeNodeStore nodeStore,
        int? maxLeafEntries = null,
        int? maxInternalKeys = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(dataFile);
        ArgumentNullException.ThrowIfNull(nodeStore);

        // Scan the file, deduplicating duplicate BlockIds last-position-wins via a
        // throwaway runtime map (the greater offset supersedes, spec Section 7).
        var dedup = new RuntimeBlockOffsetMap();
        var scan = dataFile.ScanForward(dedup, log);
        if (scan.IsFailure)
            return Result<Regeneration>.Failure(scan.Error);

        var index = new BlockLocationIndex(
            nodeStore, maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

        // One ascending-key COW pass over the deduplicated live locations.
        var snapshot = dedup.SnapshotOrderedByOffset();
        var put = index.PutBatch(snapshot);
        if (put.IsFailure)
            return Result<Regeneration>.Failure(put.Error);

        return Result<Regeneration>.Success(
            new Regeneration(index, dedup.Count, scan.Value.DamagedRanges));
    }

    /// <summary>Outcome of <see cref="VerifyMatchesLiveTree"/>.</summary>
    /// <param name="Matches">True when the two trees hold identical entries in identical order.</param>
    /// <param name="ComparedEntries">Entries compared before a mismatch or exhaustion.</param>
    /// <param name="Mismatch">Human-readable description of the first divergence, or null when they match.</param>
    public readonly record struct VerificationReport(bool Matches, long ComparedEntries, string? Mismatch);

    /// <summary>
    /// Verifies that <paramref name="regenerated"/> matches <paramref name="live"/>
    /// entry-by-entry (spec Section 7 acceptance: the regenerated index and the
    /// live tree agree). Both trees yield entries in ascending unsigned-lexicographic
    /// BlockId order, so a single lock-step scan compares them cheaply: every
    /// (BlockId → Offset, Length) pair must be identical and both trees must
    /// exhaust together. Returns a report describing the first divergence; fails
    /// only on an I/O or Merkle verification error while scanning either tree
    /// (spec Section 13).
    /// </summary>
    /// <param name="regenerated">The index rebuilt by <see cref="RegenerateFromScan"/>.</param>
    /// <param name="live">The live/durable index to compare against.</param>
    public static Result<VerificationReport> VerifyMatchesLiveTree(
        BlockLocationIndex regenerated, BlockLocationIndex live)
    {
        ArgumentNullException.ThrowIfNull(regenerated);
        ArgumentNullException.ThrowIfNull(live);

        var regenScan = regenerated.Tree.Scan(regenerated.Root);
        var liveScan = live.Tree.Scan(live.Root);

        long compared = 0;
        while (true)
        {
            var regenNext = regenScan.MoveNext();
            if (regenNext.IsFailure)
                return Result<VerificationReport>.Failure(
                    $"Scanning the regenerated index failed at entry {compared}: {regenNext.Error}");
            var liveNext = liveScan.MoveNext();
            if (liveNext.IsFailure)
                return Result<VerificationReport>.Failure(
                    $"Scanning the live index failed at entry {compared}: {liveNext.Error}");

            // Length mismatch: one tree still has entries when the other is spent.
            if (regenNext.Value != liveNext.Value)
            {
                var which = regenNext.Value ? "regenerated" : "live";
                return Result<VerificationReport>.Success(new VerificationReport(
                    false, compared,
                    $"Entry count differs: the {which} index has more than {compared} entries."));
            }

            // Both exhausted together: a full, matching comparison.
            if (!regenNext.Value)
                return Result<VerificationReport>.Success(new VerificationReport(true, compared, null));

            var regenEntry = regenScan.Current;
            var liveEntry = liveScan.Current;
            if (!regenEntry.Key.AsSpan().SequenceEqual(liveEntry.Key))
                return Result<VerificationReport>.Success(new VerificationReport(
                    false, compared,
                    $"BlockId differs at entry {compared}: regenerated {Hex(regenEntry.Key)} vs live {Hex(liveEntry.Key)}."));
            if (!regenEntry.Value.AsSpan().SequenceEqual(liveEntry.Value))
                return Result<VerificationReport>.Success(new VerificationReport(
                    false, compared,
                    $"Location differs for BlockId {Hex(liveEntry.Key)} at entry {compared}: " +
                    $"regenerated {Hex(regenEntry.Value)} vs live {Hex(liveEntry.Value)}."));

            compared++;
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
