using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the FolderDeltaLog chain (US-EMDB-82-6, story US-EMDB-82,
/// docs/Folder_Listing.md Sections 2-3):
/// <list type="bullet">
///   <item>A <see cref="FolderDeltaEntry"/> packs Add (full listing record), Delete (id) and
///   FlagChange (id + flags) ops and round-trips each.</item>
///   <item>A <see cref="FolderDeltaLog"/> (BlockType 13) packs its PreviousDeltaBlockId + entries and
///   round-trips its payload.</item>
///   <item>Blocks chain via PreviousDeltaBlockId and persist encrypted under Default
///   (<see cref="FolderDeltaLogStore"/>); appending advances the directory's HeadDeltaBlockId.</item>
///   <item>A move is a Delete in one folder's log and an Add in another's, leaving the ContentBlockId
///   untouched.</item>
/// </list>
/// </summary>
public class FolderDeltaLogV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-folderdelta-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x60 + i)).ToArray();

    private const ushort ActiveEpoch = 2;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static EpochDekProvider MakeProvider()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 19);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

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

    private static ListingRecord SampleRecord(byte seed = 1, long dateTicks = 638_000_000_000_000_000L) =>
        new()
        {
            EmailHashedId = IdOf(seed),
            ContentBlockId = UlidOf(seed),
            DateTicks = dateTicks,
            Flags = ListingFlags.Read,
            MessageSize = 42_000,
            From = "Alice Example <alice@example.com>",
            Subject = "Quarterly report — Q3 numbers",
            Preview = "Body preview text for the listing row.",
        };

    private static FolderPageDirectory EmptyDirectory(byte[]? head = null, ulong version = 0) =>
        FolderPageDirectory.Create(UlidOf(1), Array.Empty<PageEntry>(), headDeltaBlockId: head, folderVersion: version);

    // ---- Entry packing: Add / Delete / FlagChange (task DoD) -----------------

    [Fact]
    public void DeltaEntry_Add_CarriesRecord_And_RoundTrips()
    {
        var record = SampleRecord(seed: 5);
        var entry = FolderDeltaEntry.Add(record);

        Assert.Equal(FolderDeltaOp.Add, entry.Op);
        Assert.Equal(record.EmailHashedId, entry.EmailHashedId);
        Assert.NotNull(entry.Record);

        var packed = new byte[entry.PackedLength];
        int written = entry.Pack(packed);
        Assert.Equal(packed.Length, written);
        Assert.Equal((byte)FolderDeltaOp.Add, packed[0]);

        var got = FolderDeltaEntry.Unpack(packed, out int consumed);
        Assert.Equal(packed.Length, consumed);
        Assert.Equal(FolderDeltaOp.Add, got.Op);
        Assert.NotNull(got.Record);
        Assert.Equal(record.EmailHashedId, got.Record!.EmailHashedId);
        Assert.Equal(record.ContentBlockId, got.Record.ContentBlockId);
        Assert.Equal(record.Subject, got.Record.Subject);
        Assert.Equal(record.DateTicks, got.Record.DateTicks);
    }

    [Fact]
    public void DeltaEntry_Delete_CarriesIdOnly_And_RoundTrips()
    {
        var entry = FolderDeltaEntry.Delete(IdOf(9));
        Assert.Equal(FolderDeltaOp.Delete, entry.Op);
        Assert.Null(entry.Record);
        Assert.Equal(1 + V3Id.Size, entry.PackedLength);

        var got = FolderDeltaEntry.Unpack(PackTo(entry), out int consumed);
        Assert.Equal(entry.PackedLength, consumed);
        Assert.Equal(FolderDeltaOp.Delete, got.Op);
        Assert.Equal(IdOf(9), got.EmailHashedId);
        Assert.Null(got.Record);
    }

    [Fact]
    public void DeltaEntry_FlagChange_CarriesIdAndFlags_And_RoundTrips()
    {
        var entry = FolderDeltaEntry.FlagChange(IdOf(3), ListingFlags.Read | ListingFlags.Answered);
        Assert.Equal(FolderDeltaOp.FlagChange, entry.Op);
        Assert.Equal(1 + V3Id.Size + sizeof(uint), entry.PackedLength);

        var got = FolderDeltaEntry.Unpack(PackTo(entry), out int consumed);
        Assert.Equal(entry.PackedLength, consumed);
        Assert.Equal(FolderDeltaOp.FlagChange, got.Op);
        Assert.Equal(IdOf(3), got.EmailHashedId);
        Assert.Equal(ListingFlags.Read | ListingFlags.Answered, got.Flags);
    }

    [Fact]
    public void DeltaEntry_Unpack_RejectsUnknownOp_AndTruncation()
    {
        var bad = new byte[] { 0 }; // op 0 is the "unset/corrupt" sentinel
        Assert.Throws<ArgumentException>(() => FolderDeltaEntry.Unpack(bad, out _));

        var flag = PackTo(FolderDeltaEntry.FlagChange(IdOf(1), ListingFlags.Read));
        Assert.Throws<ArgumentException>(() => FolderDeltaEntry.Unpack(flag.AsSpan(0, 10), out _));
    }

    private static byte[] PackTo(FolderDeltaEntry entry)
    {
        var buf = new byte[entry.PackedLength];
        entry.Pack(buf);
        return buf;
    }

    // ---- Block payload round-trip (task DoD) --------------------------------

    [Fact]
    public void DeltaLog_PayloadRoundTrips_MixedEntries_And_PreviousLink()
    {
        var prev = UlidOf(42);
        var log = FolderDeltaLog.Create(
            new[]
            {
                FolderDeltaEntry.Add(SampleRecord(seed: 1, dateTicks: 100)),
                FolderDeltaEntry.Delete(IdOf(2)),
                FolderDeltaEntry.FlagChange(IdOf(3), ListingFlags.Flagged),
                FolderDeltaEntry.Add(SampleRecord(seed: 4, dateTicks: 200)),
            },
            previousDeltaBlockId: prev);

        Assert.True(log.HasPrevious);
        Assert.Equal(21, FolderDeltaLog.HeaderLength);

        var got = FolderDeltaLog.UnpackPayload(log.PackPayload());
        Assert.Equal(prev, got.PreviousDeltaBlockId);
        Assert.True(got.HasPrevious);
        Assert.Equal(4, got.Count);
        Assert.Equal(FolderDeltaOp.Add, got.Entries[0].Op);
        Assert.Equal(FolderDeltaOp.Delete, got.Entries[1].Op);
        Assert.Equal(FolderDeltaOp.FlagChange, got.Entries[2].Op);
        Assert.Equal(ListingFlags.Flagged, got.Entries[2].Flags);
        Assert.Equal(SampleRecord(seed: 4).Subject, got.Entries[3].Record!.Subject);
    }

    [Fact]
    public void DeltaLog_ChainRoot_HasZeroPrevious_And_EmptyRoundTrips()
    {
        var root = FolderDeltaLog.Create(new[] { FolderDeltaEntry.Delete(IdOf(1)) });
        Assert.False(root.HasPrevious);
        Assert.Equal(new byte[UlidGenerator.UlidSize], root.PreviousDeltaBlockId);

        var empty = FolderDeltaLog.Create(Array.Empty<FolderDeltaEntry>());
        var got = FolderDeltaLog.UnpackPayload(empty.PackPayload());
        Assert.Equal(0, got.Count);
        Assert.False(got.HasPrevious);
    }

    [Fact]
    public void DeltaLog_UnpackPayload_RejectsWrongVersion()
    {
        var payload = FolderDeltaLog.Create(new[] { FolderDeltaEntry.Delete(IdOf(1)) }).PackPayload();
        payload[0] = 99;
        Assert.Throws<ArgumentException>(() => FolderDeltaLog.UnpackPayload(payload));
    }

    // ---- Persists encrypted under Default; chains via PreviousDeltaBlockId ---

    [Fact]
    public void DeltaLog_RoundTrips_ThroughStore_EncryptedZstd()
    {
        const string leakSubject = "CONFIDENTIAL delta subject - do not disclose";
        var record = new ListingRecord
        {
            EmailHashedId = IdOf(7),
            ContentBlockId = UlidOf(7),
            DateTicks = 638_000_000_000_000_000L,
            Flags = ListingFlags.None,
            MessageSize = 1000,
            From = "sender@leak-probe.example",
            Subject = leakSubject,
            Preview = "secret preview",
        };
        var log = FolderDeltaLog.Create(new[] { FolderDeltaEntry.Add(record) });

        using var provider = MakeProvider();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var store = new FolderDeltaLogStore(manager, provider, EncryptionPolicy.Default);

            var written = store.WriteDeltaBlock(log);
            Ok(written);

            var block = manager.Read(written.Value.Offset);
            Ok(block);
            var header = block.Value.Header;
            Assert.Equal(BlockType.FolderDeltaLog, header.Type);
            Assert.Equal(CompressionAlgorithm.Zstd, header.Compression);
            Assert.Equal(PayloadEncoding.Custom, header.Encoding);
            Assert.True(header.IsEncrypted);
            Assert.Equal(ActiveEpoch, header.KeyEpoch);

            var read = store.ReadDeltaBlock(written.Value.Offset);
            Ok(read);
            Assert.Equal(1, read.Value.Count);
            Assert.Equal(leakSubject, read.Value.Entries[0].Record!.Subject);
        }

        // Plaintext-leakage guard: the Add record's subject never reaches disk in the clear.
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(_path));
        Assert.DoesNotContain(leakSubject, raw);
    }

    [Fact]
    public void DeltaChain_AppendsLinkedViaPreviousDeltaBlockId_ThroughStore()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new FolderDeltaLogStore(manager, provider, EncryptionPolicy.Default);

        // Root block, then a second block chained to it, then a third chained to the second.
        var root = store.WriteDeltaBlock(FolderDeltaLog.Create(new[] { FolderDeltaEntry.Add(SampleRecord(1)) }));
        Ok(root);
        var mid = store.WriteDeltaBlock(
            FolderDeltaLog.Create(new[] { FolderDeltaEntry.Delete(IdOf(2)) }, previousDeltaBlockId: root.Value.BlockId));
        Ok(mid);
        var head = store.WriteDeltaBlock(
            FolderDeltaLog.Create(new[] { FolderDeltaEntry.FlagChange(IdOf(3), ListingFlags.Read) },
                previousDeltaBlockId: mid.Value.BlockId));
        Ok(head);

        // Walk the chain head → root purely by PreviousDeltaBlockId, resolving each id to its offset.
        var offsetById = new Dictionary<string, long>
        {
            [Hex(root.Value.BlockId)] = root.Value.Offset,
            [Hex(mid.Value.BlockId)] = mid.Value.Offset,
            [Hex(head.Value.BlockId)] = head.Value.Offset,
        };

        var walked = new List<FolderDeltaOp>();
        var current = store.ReadDeltaBlock(head.Value.Offset);
        Ok(current);
        while (true)
        {
            walked.Add(current.Value.Entries[0].Op);
            if (!current.Value.HasPrevious)
                break;
            current = store.ReadDeltaBlock(offsetById[Hex(current.Value.PreviousDeltaBlockId)]);
            Ok(current);
        }

        // Head first, root last — the full chain is reachable via PreviousDeltaBlockId.
        Assert.Equal(new[] { FolderDeltaOp.FlagChange, FolderDeltaOp.Delete, FolderDeltaOp.Add }, walked);
    }

    // ---- AppendChained advances the directory HeadDeltaBlockId (task DoD) ----

    [Fact]
    public void AppendChained_AdvancesDirectoryHead_AndChainsToPreviousHead()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var deltaStore = new FolderDeltaLogStore(manager, provider, EncryptionPolicy.Default);
        var dirStore = new FolderPageDirectoryStore(manager, provider, EncryptionPolicy.Default);

        // Directory starts with no pending delta.
        var dir0 = EmptyDirectory();
        Assert.False(dir0.HasPendingDelta);

        // First append: chain root (zero previous), head advances to the new block, version bumps.
        var a1 = deltaStore.AppendChained(dir0, new[] { FolderDeltaEntry.Add(SampleRecord(1)) });
        Ok(a1);
        var block1 = deltaStore.ReadDeltaBlock(a1.Value.DeltaLocation.Offset);
        Ok(block1);
        Assert.False(block1.Value.HasPrevious);                              // root of the chain
        Assert.True(a1.Value.Directory.HasPendingDelta);
        Assert.Equal(a1.Value.DeltaLocation.BlockId, a1.Value.Directory.HeadDeltaBlockId); // head advanced
        Assert.Equal(1UL, a1.Value.Directory.FolderVersion);                 // COW rewrite bumped version
        Ok(dirStore.WriteDirectory(a1.Value.Directory));                     // caller persists the head advance

        // Second append: previous = the current head, head advances again, version bumps again.
        var a2 = deltaStore.AppendChained(a1.Value.Directory, new[] { FolderDeltaEntry.Delete(IdOf(2)) });
        Ok(a2);
        var block2 = deltaStore.ReadDeltaBlock(a2.Value.DeltaLocation.Offset);
        Ok(block2);
        Assert.True(block2.Value.HasPrevious);
        Assert.Equal(a1.Value.DeltaLocation.BlockId, block2.Value.PreviousDeltaBlockId); // links to prior head
        Assert.Equal(a2.Value.DeltaLocation.BlockId, a2.Value.Directory.HeadDeltaBlockId);
        Assert.Equal(2UL, a2.Value.Directory.FolderVersion);

        // The persisted directory reads back with the advanced head (stable BlockId = folder ULID).
        var writtenDir = dirStore.WriteDirectory(a2.Value.Directory);
        Ok(writtenDir);
        Assert.Equal(dir0.FolderId, writtenDir.Value.BlockId);
        var reread = dirStore.ReadDirectory(writtenDir.Value.Offset);
        Ok(reread);
        Assert.Equal(a2.Value.DeltaLocation.BlockId, reread.Value.HeadDeltaBlockId);
        Assert.Equal(2UL, reread.Value.FolderVersion);
    }

    // ---- Move is two delta entries, content untouched (story AC 5) ----------

    [Fact]
    public void Move_IsDeleteInSource_And_AddInDestination_ContentBlockUntouched()
    {
        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var deltaStore = new FolderDeltaLogStore(manager, provider, EncryptionPolicy.Default);

        // The moved email's row — the ContentBlockId is what must survive a move untouched.
        var moved = SampleRecord(seed: 8, dateTicks: 637_000_000_000_000_000L);
        var contentBlockId = moved.ContentBlockId;

        // Source and destination folders each have their own directory / delta chain.
        var sourceDir = FolderPageDirectory.Create(UlidOf(2), Array.Empty<PageEntry>());
        var destDir = FolderPageDirectory.Create(UlidOf(3), Array.Empty<PageEntry>());

        // A move is exactly two delta entries: a Delete in the source log and an Add in the dest log.
        var srcAppend = deltaStore.AppendChained(sourceDir, new[] { FolderDeltaEntry.Delete(moved.EmailHashedId) });
        Ok(srcAppend);
        var dstAppend = deltaStore.AppendChained(destDir, new[] { FolderDeltaEntry.Add(moved) });
        Ok(dstAppend);

        var srcBlock = deltaStore.ReadDeltaBlock(srcAppend.Value.DeltaLocation.Offset);
        var dstBlock = deltaStore.ReadDeltaBlock(dstAppend.Value.DeltaLocation.Offset);
        Ok(srcBlock);
        Ok(dstBlock);

        Assert.Equal(FolderDeltaOp.Delete, srcBlock.Value.Entries[0].Op);
        Assert.Equal(moved.EmailHashedId, srcBlock.Value.Entries[0].EmailHashedId);

        Assert.Equal(FolderDeltaOp.Add, dstBlock.Value.Entries[0].Op);
        // The Tier 3 content pointer is carried verbatim — no content block is rewritten by a move.
        Assert.Equal(contentBlockId, dstBlock.Value.Entries[0].Record!.ContentBlockId);
        Assert.Equal(moved.EmailHashedId, dstBlock.Value.Entries[0].EmailHashedId);

        // Both folders' heads advanced independently; the source folder ULID is unchanged.
        Assert.True(srcAppend.Value.Directory.HasPendingDelta);
        Assert.True(dstAppend.Value.Directory.HasPendingDelta);
        Assert.Equal(sourceDir.FolderId, srcAppend.Value.Directory.FolderId);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
