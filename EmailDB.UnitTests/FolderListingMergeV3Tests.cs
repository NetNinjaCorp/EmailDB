using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the read-path listing merge (US-EMDB-82-7, story US-EMDB-82,
/// docs/Folder_Listing.md Sections 2-3): <see cref="FolderListingMerger"/> overlays a folder's pending
/// <see cref="FolderDeltaLog"/> chain on its <see cref="FolderPage"/> records in memory to produce the
/// effective listing. Covered:
/// <list type="bullet">
///   <item>A pending Add is inserted in date-descending order among the page rows.</item>
///   <item>A Delete masks a page row; the merged listing drops it.</item>
///   <item>A FlagChange overrides an existing row's flags (and only its flags).</item>
///   <item>Later-in-chain entries win over earlier ones for the same email id
///   (re-Add / Delete-then-Add / Add-then-Delete / repeated FlagChange).</item>
///   <item>A FlagChange for a masked or unknown id is a no-op.</item>
///   <item>A head-to-root chain flattens to chronological apply order so the head block wins.</item>
/// </list>
/// </summary>
public class FolderListingMergeV3Tests
{
    private static V3Id IdOf(byte seed)
    {
        var raw = new byte[V3Id.Size];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new V3Id(raw);
    }

    private static byte[] UlidOf(byte seed)
    {
        var ulid = new byte[UlidGenerator.UlidSize];
        for (var i = 0; i < ulid.Length; i++) ulid[i] = (byte)(seed * 3 + i);
        return ulid;
    }

    private static ListingRecord Record(
        byte seed, long dateTicks, ListingFlags flags = ListingFlags.None) =>
        new()
        {
            EmailHashedId = IdOf(seed),
            ContentBlockId = UlidOf(seed),
            DateTicks = dateTicks,
            Flags = flags,
            MessageSize = 1_000 + seed,
            From = $"sender{seed}@example.com",
            Subject = $"Subject {seed}",
            Preview = $"Preview {seed}",
        };

    /// <summary>Three page rows, dates 300 / 200 / 100 (already newest-first).</summary>
    private static List<ListingRecord> BasePages() =>
        new() { Record(1, 300), Record(2, 200), Record(3, 100) };

    private static long[] Dates(IReadOnlyList<ListingRecord> rows) =>
        rows.Select(r => r.DateTicks).ToArray();

    // ---- Add inserted in date-descending order --------------------------------

    [Fact]
    public void Merge_Add_InsertsRowInDateDescendingOrder()
    {
        // New row dated 250 must land between 300 and 200.
        var add = FolderDeltaEntry.Add(Record(9, 250));
        var merged = FolderListingMerger.Merge(BasePages(), new[] { add });

        Assert.Equal(new long[] { 300, 250, 200, 100 }, Dates(merged));
        Assert.Contains(merged, r => r.EmailHashedId == IdOf(9) && r.DateTicks == 250);
    }

    [Fact]
    public void Merge_EmptyChain_ReturnsBasePagesNewestFirst()
    {
        var merged = FolderListingMerger.Merge(BasePages(), Array.Empty<FolderDeltaEntry>());
        Assert.Equal(new long[] { 300, 200, 100 }, Dates(merged));
    }

    // ---- Delete masks a page row ----------------------------------------------

    [Fact]
    public void Merge_Delete_MasksPageRow()
    {
        var del = FolderDeltaEntry.Delete(IdOf(2)); // the date-200 row
        var merged = FolderListingMerger.Merge(BasePages(), new[] { del });

        Assert.Equal(new long[] { 300, 100 }, Dates(merged));
        Assert.DoesNotContain(merged, r => r.EmailHashedId == IdOf(2));
    }

    // ---- FlagChange overrides an existing row's flags -------------------------

    [Fact]
    public void Merge_FlagChange_OverridesFlagsOnPageRow_LeavingOtherFieldsIntact()
    {
        var baseRow = Record(2, 200); // Flags.None
        var flag = FolderDeltaEntry.FlagChange(IdOf(2), ListingFlags.Read | ListingFlags.Flagged);
        var merged = FolderListingMerger.Merge(BasePages(), new[] { flag });

        var row = merged.Single(r => r.EmailHashedId == IdOf(2));
        Assert.Equal(ListingFlags.Read | ListingFlags.Flagged, row.Flags);
        // Only the flags changed — the rest of the row is untouched.
        Assert.Equal(baseRow.Subject, row.Subject);
        Assert.Equal(baseRow.DateTicks, row.DateTicks);
        Assert.Equal(baseRow.ContentBlockId, row.ContentBlockId);
    }

    [Fact]
    public void Merge_FlagChange_OnUnknownOrDeletedId_IsNoOp()
    {
        // Unknown id: nothing to flag, row count unchanged.
        var unknown = FolderDeltaEntry.FlagChange(IdOf(50), ListingFlags.Read);
        var mergedUnknown = FolderListingMerger.Merge(BasePages(), new[] { unknown });
        Assert.Equal(3, mergedUnknown.Count);
        Assert.DoesNotContain(mergedUnknown, r => r.EmailHashedId == IdOf(50));

        // Deleted then flagged: the delete stands, the flag change has no row to touch.
        var entries = new[]
        {
            FolderDeltaEntry.Delete(IdOf(2)),
            FolderDeltaEntry.FlagChange(IdOf(2), ListingFlags.Read),
        };
        var mergedDeleted = FolderListingMerger.Merge(BasePages(), entries);
        Assert.DoesNotContain(mergedDeleted, r => r.EmailHashedId == IdOf(2));
    }

    // ---- Later-in-chain entries win over earlier ones -------------------------

    [Fact]
    public void Merge_LaterEntryWins_ForSameId()
    {
        // Repeated FlagChange: the last one wins.
        var repeatedFlags = new[]
        {
            FolderDeltaEntry.FlagChange(IdOf(1), ListingFlags.Read),
            FolderDeltaEntry.FlagChange(IdOf(1), ListingFlags.Answered),
        };
        var m1 = FolderListingMerger.Merge(BasePages(), repeatedFlags);
        Assert.Equal(ListingFlags.Answered, m1.Single(r => r.EmailHashedId == IdOf(1)).Flags);

        // Delete then Add re-adds the row (un-masks).
        var deleteThenAdd = new[]
        {
            FolderDeltaEntry.Delete(IdOf(2)),
            FolderDeltaEntry.Add(Record(2, 205, ListingFlags.Flagged)),
        };
        var m2 = FolderListingMerger.Merge(BasePages(), deleteThenAdd);
        var readded = m2.Single(r => r.EmailHashedId == IdOf(2));
        Assert.Equal(205, readded.DateTicks);
        Assert.Equal(ListingFlags.Flagged, readded.Flags);

        // Add then Delete removes the row.
        var addThenDelete = new[]
        {
            FolderDeltaEntry.Add(Record(9, 250)),
            FolderDeltaEntry.Delete(IdOf(9)),
        };
        var m3 = FolderListingMerger.Merge(BasePages(), addThenDelete);
        Assert.DoesNotContain(m3, r => r.EmailHashedId == IdOf(9));
    }

    // ---- Head-to-root chain flattens to chronological order (head wins) -------

    [Fact]
    public void Merge_HeadToRootChain_AppliesRootFirst_HeadWins()
    {
        // Root block (oldest): flag id 1 Read. Head block (newest): flag id 1 Answered.
        var rootBlock = FolderDeltaLog.Create(
            new[] { FolderDeltaEntry.FlagChange(IdOf(1), ListingFlags.Read) });
        var headBlock = FolderDeltaLog.Create(
            new[] { FolderDeltaEntry.FlagChange(IdOf(1), ListingFlags.Answered) },
            previousDeltaBlockId: UlidOf(1));

        // Chain is passed head-first, as walked from the directory head back to the root.
        var chainHeadToRoot = new[] { headBlock, rootBlock };
        var merged = FolderListingMerger.Merge(BasePages(), chainHeadToRoot);

        Assert.Equal(ListingFlags.Answered, merged.Single(r => r.EmailHashedId == IdOf(1)).Flags);

        // The flattener yields root's entry before head's (oldest-first apply order).
        var flat = FolderListingMerger.FlattenChronological(chainHeadToRoot);
        Assert.Equal(ListingFlags.Read, flat[0].Flags);
        Assert.Equal(ListingFlags.Answered, flat[1].Flags);
    }

    [Fact]
    public void Merge_CombinedOps_ProduceEffectiveListing()
    {
        // Add a new newest row, delete the oldest, flag the middle — across a two-block chain.
        var rootBlock = FolderDeltaLog.Create(new[]
        {
            FolderDeltaEntry.Add(Record(9, 400)),          // newest
            FolderDeltaEntry.FlagChange(IdOf(2), ListingFlags.Read),
        });
        var headBlock = FolderDeltaLog.Create(
            new[] { FolderDeltaEntry.Delete(IdOf(3)) },     // drop the date-100 row
            previousDeltaBlockId: UlidOf(1));

        var merged = FolderListingMerger.Merge(BasePages(), new[] { headBlock, rootBlock });

        Assert.Equal(new long[] { 400, 300, 200 }, Dates(merged));
        Assert.Equal(ListingFlags.Read, merged.Single(r => r.EmailHashedId == IdOf(2)).Flags);
        Assert.DoesNotContain(merged, r => r.EmailHashedId == IdOf(3));
    }
}
