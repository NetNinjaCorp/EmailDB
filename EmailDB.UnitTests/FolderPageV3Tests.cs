using System.Text;
using EmailDB.Format;
using EmailDB.Format.Protobuf.V3;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for Tier 1 folder-listing packing (US-EMDB-81-6, story US-EMDB-81,
/// docs/Folder_Listing.md Section 2):
/// <list type="bullet">
///   <item>A <see cref="ListingRecord"/> packs EmailHashedID, ContentBlockId, date, flags, size,
///   From, Subject and Preview into a compact ~400-byte, self-delimiting record.</item>
///   <item>A <see cref="FolderPage"/> (BlockType 12) packs ~80 records in strict date-descending
///   order and round-trips its payload.</item>
///   <item>A page persists through the Zstd + Default-encryption pipeline
///   (<see cref="FolderPageStore"/>) as a genuinely encrypted BlockType 12 block.</item>
///   <item>A record is regenerable from Tier 2 <see cref="EmailMetadata"/> alone (recovery path).</item>
/// </list>
/// </summary>
public class FolderPageV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-folderpage-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();

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
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 7);
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

    private static ListingRecord SampleRecord(
        byte seed = 1, long dateTicks = 638_000_000_000_000_000L, string? preview = null) =>
        new()
        {
            EmailHashedId = IdOf(seed),
            ContentBlockId = UlidOf(seed),
            DateTicks = dateTicks,
            Flags = ListingFlags.Read | ListingFlags.Flagged,
            MessageSize = 42_000,
            From = "Alice Example <alice@example.com>",
            Subject = "Quarterly report — Q3 numbers and next steps",
            Preview = preview ?? new string('x', PreviewExtractor.MaxLength),
        };

    // ---- Listing record packing (story AC 5 / task DoD) ----------------------

    [Fact]
    public void ListingRecord_PacksAllFields_InAround400Bytes()
    {
        var record = SampleRecord();
        var packed = record.Pack();

        // Fixed prefix is exactly 32 + 16 + 8 + 4 + 8 = 68 bytes.
        Assert.Equal(68, ListingRecord.FixedPrefixLength);

        // A realistic record (From ~33, Subject ~45, Preview 200) packs near the ~400-byte target.
        Assert.Equal(record.PackedLength, packed.Length);
        Assert.InRange(packed.Length, 300, 512);
    }

    [Fact]
    public void ListingRecord_ByteLayout_IsStableAtDocumentedOffsets()
    {
        // Golden byte-layout check: each named field must land at its documented offset so the
        // fixed 68-byte prefix stays wire-stable (docs/Folder_Listing.md Section 2). This is the
        // "packs EmailHashedID BlockId date flags size ..." guarantee at the byte level.
        var record = new ListingRecord
        {
            EmailHashedId = IdOf(5),
            ContentBlockId = UlidOf(5),
            DateTicks = 0x1122334455667788L,
            Flags = ListingFlags.Read | ListingFlags.Answered,
            MessageSize = 0x00000000DEADBEEFL,
            From = "A",           // 1 UTF-8 byte
            Subject = "BB",       // 2 UTF-8 bytes
            Preview = "CCC",      // 3 UTF-8 bytes
        };

        var packed = record.Pack();

        // [0..32) EmailHashedID raw digest, [32..48) ContentBlockId ULID.
        Assert.Equal(IdOf(5).GetBytes(), packed[0..32]);
        Assert.Equal(UlidOf(5), packed[32..48]);
        // [48..56) DateTicks Int64 LE, [56..60) Flags UInt32 LE, [60..68) MessageSize Int64 LE.
        Assert.Equal(record.DateTicks, BitConverter.ToInt64(packed, 48));
        Assert.Equal((uint)record.Flags, BitConverter.ToUInt32(packed, 56));
        Assert.Equal(record.MessageSize, BitConverter.ToInt64(packed, 60));
        Assert.True(BitConverter.IsLittleEndian, "layout offsets assume little-endian BitConverter");

        // Fixed prefix (68) then u16-length-prefixed From/Subject/Preview: 1+2, 1+2, 1+3 payloads.
        Assert.Equal(1, BitConverter.ToUInt16(packed, 68));      // From length
        Assert.Equal((byte)'A', packed[70]);
        Assert.Equal(2, BitConverter.ToUInt16(packed, 71));      // Subject length
        Assert.Equal("BB", Encoding.UTF8.GetString(packed, 73, 2));
        Assert.Equal(3, BitConverter.ToUInt16(packed, 75));      // Preview length
        Assert.Equal("CCC", Encoding.UTF8.GetString(packed, 77, 3));
        Assert.Equal(80, packed.Length);                         // 68 + 6 + 1 + 2 + 3
    }

    [Fact]
    public void ListingRecord_Pack_RejectsOversizedString()
    {
        // A single packed string is capped at UInt16.MaxValue UTF-8 bytes (the length prefix).
        var record = new ListingRecord
        {
            EmailHashedId = IdOf(1),
            ContentBlockId = UlidOf(1),
            Subject = new string('x', ListingRecord.MaxStringByteLength + 1),
        };
        Assert.Throws<ArgumentException>(() => record.Pack());
    }

    [Fact]
    public void ListingRecord_RoundTrips_EveryField()
    {
        var record = SampleRecord(seed: 9, dateTicks: 637_123_456_789_000_000L, preview: "Short preview.");
        var packed = record.Pack();

        var got = ListingRecord.Unpack(packed, out var consumed);

        Assert.Equal(packed.Length, consumed);
        Assert.Equal(record.EmailHashedId, got.EmailHashedId);
        Assert.Equal(record.ContentBlockId, got.ContentBlockId);
        Assert.Equal(record.DateTicks, got.DateTicks);
        Assert.Equal(record.Flags, got.Flags);
        Assert.Equal(record.MessageSize, got.MessageSize);
        Assert.Equal(record.From, got.From);
        Assert.Equal(record.Subject, got.Subject);
        Assert.Equal(record.Preview, got.Preview);
    }

    [Fact]
    public void ListingRecord_RoundTrips_EmptyStrings_And_NonAscii()
    {
        var record = new ListingRecord
        {
            EmailHashedId = IdOf(3),
            ContentBlockId = UlidOf(3),
            DateTicks = 0,
            Flags = ListingFlags.None,
            MessageSize = 0,
            From = string.Empty,
            Subject = "Grüße café — 你好",   // multi-byte UTF-8
            Preview = string.Empty,
        };

        var got = ListingRecord.Unpack(record.Pack(), out _);

        Assert.Equal(string.Empty, got.From);
        Assert.Equal("Grüße café — 你好", got.Subject);
        Assert.Equal(string.Empty, got.Preview);
        Assert.Equal(ListingFlags.None, got.Flags);
    }

    [Fact]
    public void ListingRecord_Pack_RejectsBadContentBlockId()
    {
        var record = new ListingRecord
        {
            EmailHashedId = IdOf(1),
            ContentBlockId = new byte[8], // not 16
        };
        Assert.Throws<ArgumentException>(() => record.Pack());
    }

    [Fact]
    public void ListingRecord_Unpack_RejectsTruncatedBuffer()
    {
        var packed = SampleRecord().Pack();
        Assert.Throws<ArgumentException>(() => ListingRecord.Unpack(packed.AsSpan(0, 40), out _));
    }

    // ---- FolderPage: ~80 records, date-descending (task DoD) -----------------

    [Fact]
    public void FolderPage_FromRecords_IsSortedDateDescending()
    {
        var rnd = new Random(1234);
        var records = Enumerable.Range(0, FolderPage.TargetRecordsPerPage)
            .Select(i => SampleRecord(
                seed: (byte)i,
                dateTicks: 600_000_000_000_000_000L + rnd.Next(0, 100_000_000),
                preview: $"Preview {i}"))
            .ToList();

        var page = FolderPage.FromRecords(records);

        Assert.Equal(FolderPage.TargetRecordsPerPage, page.Count);
        Assert.True(page.IsDateDescending());
        for (int i = 1; i < page.Count; i++)
            Assert.True(page.Records[i - 1].DateTicks >= page.Records[i].DateTicks);
    }

    [Fact]
    public void FolderPage_PayloadRoundTrips_PreservingOrderAndFields()
    {
        var records = Enumerable.Range(0, FolderPage.TargetRecordsPerPage)
            .Select(i => SampleRecord(
                seed: (byte)i,
                dateTicks: 600_000_000_000_000_000L + i * 1_000_000L,
                // A distinctive ~200-char preview per record, as a real listing would carry.
                preview: $"Preview line {i}: ".PadRight(PreviewExtractor.MaxLength, 'x')))
            .ToList();
        var page = FolderPage.FromRecords(records);
        var payload = page.PackPayload();

        // ~80 records at ~400 B each land in the spec's ~35 KB ballpark (pre-compression).
        Assert.Equal(page.PayloadLength, payload.Length);
        Assert.InRange(payload.Length, 25_000, 45_000);

        var got = FolderPage.UnpackPayload(payload);
        Assert.Equal(page.Count, got.Count);
        Assert.True(got.IsDateDescending());
        for (int i = 0; i < page.Count; i++)
        {
            Assert.Equal(page.Records[i].EmailHashedId, got.Records[i].EmailHashedId);
            Assert.Equal(page.Records[i].DateTicks, got.Records[i].DateTicks);
            Assert.Equal(page.Records[i].Subject, got.Records[i].Subject);
            Assert.Equal(page.Records[i].Preview, got.Records[i].Preview);
        }
    }

    [Fact]
    public void FolderPage_UnpackPayload_RejectsWrongVersion()
    {
        var payload = FolderPage.FromRecords(new[] { SampleRecord() }).PackPayload();
        payload[0] = 99; // corrupt the format-version byte
        Assert.Throws<ArgumentException>(() => FolderPage.UnpackPayload(payload));
    }

    [Fact]
    public void FolderPage_EmptyPage_RoundTrips()
    {
        var page = FolderPage.FromRecords(Array.Empty<ListingRecord>());
        var got = FolderPage.UnpackPayload(page.PackPayload());
        Assert.Equal(0, got.Count);
    }

    // ---- Page persists encrypted under Default (story AC 4, page half) -------

    [Fact]
    public void FolderPage_RoundTrips_ThroughStore_EncryptedZstd()
    {
        // Distinctive ASCII from/subject/preview strings so the plaintext-leakage guard below is
        // meaningful (Latin1 decoding preserves every byte, so any verbatim leak would be caught).
        const string leakFrom = "Sender Zaphod <zaphod@leak-probe.example>";
        const string leakSubject = "CONFIDENTIAL merger terms - do not disclose";
        const string leakPreview = "Secret preview body text that must never reach disk in the clear.";
        var records = Enumerable.Range(0, FolderPage.TargetRecordsPerPage)
            .Select(i => SampleRecord(seed: (byte)i, dateTicks: 600_000_000_000_000_000L + i * 5_000L))
            .ToList();
        records[0] = new ListingRecord
        {
            EmailHashedId = records[0].EmailHashedId,
            ContentBlockId = records[0].ContentBlockId,
            DateTicks = records[0].DateTicks,
            Flags = records[0].Flags,
            MessageSize = records[0].MessageSize,
            From = leakFrom,
            Subject = leakSubject,
            Preview = leakPreview,
        };
        var page = FolderPage.FromRecords(records);

        using var provider = MakeProvider();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var store = new FolderPageStore(manager, provider, EncryptionPolicy.Default);

            var written = store.WritePage(page);
            Ok(written);

            // Header reflects a Zstd-compressed, encrypted BlockType 12 at the active epoch.
            var block = manager.Read(written.Value.Offset);
            Ok(block);
            var header = block.Value.Header;
            Assert.Equal(BlockType.FolderPage, header.Type);
            Assert.Equal(CompressionAlgorithm.Zstd, header.Compression);
            Assert.Equal(PayloadEncoding.Custom, header.Encoding);
            Assert.True(header.IsEncrypted);
            Assert.Equal(ActiveEpoch, header.KeyEpoch);

            var read = store.ReadPage(written.Value.Offset);
            Ok(read);
            Assert.Equal(page.Count, read.Value.Count);
            Assert.True(read.Value.IsDateDescending());
            // The sensitive record survives the encrypted round-trip intact (found by its id, since
            // date-descending ordering may place it anywhere in the page).
            var restored = read.Value.Records.Single(r => r.EmailHashedId == records[0].EmailHashedId);
            Assert.Equal(leakSubject, restored.Subject);
            Assert.Equal(leakFrom, restored.From);
            Assert.Equal(leakPreview, restored.Preview);
        }

        // Plaintext-leakage guard: nothing sensitive (from/subject/preview) survives in the raw file
        // bytes on disk — the whole page payload is ciphertext.
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(_path));
        Assert.DoesNotContain(leakFrom, raw);
        Assert.DoesNotContain(leakSubject, raw);
        Assert.DoesNotContain(leakPreview, raw);
    }

    // ---- Regeneration from Tier 2 (recovery path) ---------------------------

    [Fact]
    public void ListingRecord_RegeneratesFromMetadata()
    {
        var contentId = UlidOf(7);
        var meta = new EmailMetadata
        {
            EmailHashedId = IdOf(7).GetBytes(),
            ContentBlockId = contentId,
            DateTicks = 637_900_000_000_000_000L,
            MessageSize = 12_345,
            From = "Bob <bob@example.com>",
            Subject = "Re: lunch",
            Preview = "Sounds good, see you at noon.",
        };

        var record = ListingRecordBuilder.FromMetadata(meta, ListingFlags.Answered);

        Assert.Equal(IdOf(7), record.EmailHashedId);
        Assert.Equal(contentId, record.ContentBlockId);
        Assert.Equal(meta.DateTicks, record.DateTicks);
        Assert.Equal(meta.MessageSize, record.MessageSize);
        Assert.Equal(meta.From, record.From);
        Assert.Equal(meta.Subject, record.Subject);
        Assert.Equal(meta.Preview, record.Preview);
        Assert.Equal(ListingFlags.Answered, record.Flags);

        // Regenerated record packs and round-trips like any other.
        var got = ListingRecord.Unpack(record.Pack(), out _);
        Assert.Equal("Re: lunch", got.Subject);
    }

    [Fact]
    public void ListingRecordBuilder_DefaultsFlagsToNone()
    {
        var meta = new EmailMetadata { EmailHashedId = IdOf(1).GetBytes(), ContentBlockId = UlidOf(1) };
        var record = ListingRecordBuilder.FromMetadata(meta);
        Assert.Equal(ListingFlags.None, record.Flags);
    }
}
