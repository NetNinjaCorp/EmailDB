using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Direct unit tests for <see cref="FolderBloomIndex"/> and <see cref="BloomFilterStore"/> (US-EMDB-97-5):
/// a folder filter is (re)built from compiled records, the covered-version guard governs when it may skip,
/// and <see cref="FolderBloomIndex.Flush"/> writes a catalog block that <see cref="FolderBloomIndex.Reconstruct"/>
/// reads back — the reopen contract, exercised over a bare (plaintext) block manager whose runtime offset
/// map is the resolver.
/// </summary>
public class FolderBloomIndexV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"emaildb-bloomu-{Guid.NewGuid():N}.emdb");
    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;

    public FolderBloomIndexV3Tests()
    {
        var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        _manager = new BlockManager(stream, offsetMap: _offsetMap, firstBlockOffset: 0, ownsStream: true);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private BloomFilterStore Store() => new(_manager, provider: null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] FolderId(byte seed) => Enumerable.Range(0, 16).Select(i => (byte)(seed + i)).ToArray();

    private static ListingRecord Rec(string subject, string from, string preview) => new()
    {
        EmailHashedId = default,
        ContentBlockId = new byte[16],
        DateTicks = 1,
        Flags = ListingFlags.None,
        MessageSize = 1,
        From = from,
        Subject = subject,
        Preview = preview,
    };

    private static ResolvedRoot AsResolved(BlockLocation loc) => new()
    {
        BlockId = loc.BlockId,
        Offset = loc.Offset,
        TotalBlockLength = loc.TotalBlockLength,
        HintWasStale = false,
    };

    // --------------------------------------------------------- Build + query

    [Fact]
    public void A_rebuilt_folder_eliminates_an_absent_query_but_not_a_present_one()
    {
        var index = new FolderBloomIndex();
        var f = FolderId(1);
        index.RebuildFolder(f, coveredFolderVersion: 5, new[] { Rec("Widget Launch", "bob@corp.com", "quarterly numbers") });
        Assert.True(index.IsDirty);
        Assert.Equal(1, index.FolderCount);

        // Same version, no pending delta: the filter governs. Present token ⇒ scan; absent ⇒ skip.
        Assert.True(index.MightMatch(f, "widget", currentFolderVersion: 5, hasPendingDelta: false));
        Assert.False(index.MightMatch(f, "xylophone-zzz", currentFolderVersion: 5, hasPendingDelta: false));
    }

    [Fact]
    public void A_folder_with_no_filter_is_always_a_might_match()
    {
        var index = new FolderBloomIndex();
        Assert.True(index.MightMatch(FolderId(9), "anything", currentFolderVersion: 1, hasPendingDelta: false));
    }

    [Fact]
    public void The_covered_version_guard_forces_a_scan_on_version_drift_or_a_pending_delta()
    {
        var index = new FolderBloomIndex();
        var f = FolderId(1);
        index.RebuildFolder(f, coveredFolderVersion: 5, new[] { Rec("Widget", "bob@corp.com", "body") });

        // The query is absent, so the filter WOULD eliminate it — but only when it still covers the folder.
        Assert.False(index.MightMatch(f, "absent-token", 5, hasPendingDelta: false)); // covers ⇒ eliminated
        Assert.True(index.MightMatch(f, "absent-token", 6, hasPendingDelta: false));  // version drift ⇒ scan
        Assert.True(index.MightMatch(f, "absent-token", 5, hasPendingDelta: true));   // pending delta ⇒ scan
    }

    // --------------------------------------------------------- Flush + reconstruct

    [Fact]
    public void Flush_writes_a_catalog_that_Reconstruct_reads_back()
    {
        var store = Store();
        var index = new FolderBloomIndex();
        var f1 = FolderId(1);
        var f2 = FolderId(0x40);
        index.RebuildFolder(f1, 5, new[] { Rec("Apple Pie", "a@corp.com", "recipe") });
        index.RebuildFolder(f2, 9, new[] { Rec("Banana Bread", "b@corp.com", "recipe") });

        var flushed = index.Flush(store);
        Ok(flushed);
        Assert.NotNull(flushed.Value);
        Assert.False(index.IsDirty);

        var reopened = FolderBloomIndex.Reconstruct(store, AsResolved(flushed.Value!));
        Ok(reopened);
        var recovered = reopened.Value;
        Assert.Equal(2, recovered.FolderCount);

        // The per-folder filters and their covered versions survive the round-trip.
        Assert.True(recovered.MightMatch(f1, "apple", 5, false));
        Assert.False(recovered.MightMatch(f1, "banana", 5, false)); // apple folder does not hold "banana"
        Assert.True(recovered.MightMatch(f2, "banana", 9, false));
        Assert.True(recovered.MightMatch(f1, "apple", 6, false));   // wrong version ⇒ scan (guard survives)
    }

    [Fact]
    public void Reflushing_a_clean_index_returns_the_same_catalog_without_writing()
    {
        var store = Store();
        var index = new FolderBloomIndex();
        index.RebuildFolder(FolderId(1), 1, new[] { Rec("Hello", "a@corp.com", "world") });

        var first = index.Flush(store);
        Ok(first);
        var second = index.Flush(store); // clean: returns the existing catalog location, writes nothing
        Ok(second);
        Assert.False(index.IsDirty);
        Assert.Equal(first.Value!.Offset, second.Value!.Offset);
        Assert.Equal(first.Value!.BlockId, second.Value!.BlockId);
    }

    [Fact]
    public void An_empty_index_flush_has_no_catalog_to_register()
    {
        var flushed = new FolderBloomIndex().Flush(Store());
        Ok(flushed);
        Assert.Null(flushed.Value);
    }

    [Fact]
    public void RemoveFolder_drops_the_filter_and_marks_dirty()
    {
        var index = new FolderBloomIndex();
        var f = FolderId(1);
        index.RebuildFolder(f, 1, new[] { Rec("Hello", "a@corp.com", "world") });
        Ok(index.Flush(Store()));
        Assert.False(index.IsDirty);

        Assert.True(index.RemoveFolder(f));
        Assert.True(index.IsDirty);
        Assert.Equal(0, index.FolderCount);
        Assert.False(index.RemoveFolder(f)); // second remove is a no-op
    }
}
