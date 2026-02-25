using EmailDB.Format;
using EmailDB.Format.Helpers;
using EmailDB.Format.Protobuf;
using EmailDB.Format.Protobuf.Models;
using Xunit;

namespace EmailDB.UnitTests;

/// <summary>
/// Comprehensive round-trip tests for ALL content types through both
/// Protobuf and JSON serializers. Verifies acceptance criterion:
/// "Round-trip tests pass for all content types" (US-EMDB-5).
/// </summary>
public class RoundTripTests
{
    private readonly ProtobufBlockContentSerializer _protobuf = new();
    private readonly DefaultBlockContentSerializer _json = new();

    // ── MetadataContent ─────────────────────────────────────────────

    [Fact]
    public void MetadataContent_Protobuf_RoundTrip()
    {
        var original = CreateMetadataContent();
        var result = RoundTrip<MetadataContent>(_protobuf, original);
        AssertMetadataEqual(original, result);
    }

    [Fact]
    public void MetadataContent_Json_RoundTrip()
    {
        var original = CreateMetadataContent();
        var result = RoundTrip<MetadataContent>(_json, original);
        AssertMetadataEqual(original, result);
    }

    [Fact]
    public void MetadataContent_Protobuf_DoubleRoundTrip()
    {
        var original = CreateMetadataContent();
        var first = RoundTrip<MetadataContent>(_protobuf, original);
        var second = RoundTrip<MetadataContent>(_protobuf, first);
        AssertMetadataEqual(original, second);
    }

    [Fact]
    public void MetadataContent_Protobuf_DefaultValues_RoundTrip()
    {
        var original = new MetadataContent();
        var result = RoundTrip<MetadataContent>(_protobuf, original);

        Assert.Equal(-1L, result.WALOffset);
        Assert.Equal(-1L, result.FolderTreeOffset);
        Assert.Empty(result.SegmentOffsets);
        Assert.Empty(result.OutdatedOffsets);
    }

    // ── FolderContent ───────────────────────────────────────────────

    [Fact]
    public void FolderContent_Protobuf_RoundTrip()
    {
        var original = CreateFolderContent();
        var result = RoundTrip<FolderContent>(_protobuf, original);
        AssertFolderEqual(original, result);
    }

    [Fact]
    public void FolderContent_Json_RoundTrip()
    {
        var original = CreateFolderContent();
        var result = RoundTrip<FolderContent>(_json, original);
        AssertFolderEqual(original, result);
    }

    [Fact]
    public void FolderContent_Protobuf_DoubleRoundTrip()
    {
        var original = CreateFolderContent();
        var first = RoundTrip<FolderContent>(_protobuf, original);
        var second = RoundTrip<FolderContent>(_protobuf, first);
        AssertFolderEqual(original, second);
    }

    [Fact]
    public void FolderContent_EmptyCollections_RoundTrip()
    {
        var original = new FolderContent
        {
            FolderId = 1,
            ParentFolderId = 0,
            Name = "Empty",
            EmailIds = new List<long>()
        };

        var protoResult = RoundTrip<FolderContent>(_protobuf, original);
        Assert.Equal(original.FolderId, protoResult.FolderId);
        Assert.Equal(original.Name, protoResult.Name);
        Assert.Empty(protoResult.EmailIds);

        var jsonResult = RoundTrip<FolderContent>(_json, original);
        Assert.Equal(original.FolderId, jsonResult.FolderId);
        Assert.Equal(original.Name, jsonResult.Name);
        Assert.Empty(jsonResult.EmailIds);
    }

    // ── FolderTreeContent ───────────────────────────────────────────

    [Fact]
    public void FolderTreeContent_Protobuf_RoundTrip()
    {
        var original = CreateFolderTreeContent();
        var result = RoundTrip<FolderTreeContent>(_protobuf, original);
        AssertFolderTreeEqual(original, result);
    }

    [Fact]
    public void FolderTreeContent_Json_RoundTrip()
    {
        var original = CreateFolderTreeContent();
        var result = RoundTrip<FolderTreeContent>(_json, original);
        AssertFolderTreeEqual(original, result);
    }

    [Fact]
    public void FolderTreeContent_Protobuf_DoubleRoundTrip()
    {
        var original = CreateFolderTreeContent();
        var first = RoundTrip<FolderTreeContent>(_protobuf, original);
        var second = RoundTrip<FolderTreeContent>(_protobuf, first);
        AssertFolderTreeEqual(original, second);
    }

    // ── SegmentContent ──────────────────────────────────────────────

    [Fact]
    public void SegmentContent_Protobuf_RoundTrip()
    {
        var original = CreateSegmentContent();
        var result = RoundTrip<SegmentContent>(_protobuf, original);
        AssertSegmentEqual(original, result);
    }

    [Fact]
    public void SegmentContent_Json_RoundTrip()
    {
        var original = CreateSegmentContent();
        var result = RoundTrip<SegmentContent>(_json, original);
        AssertSegmentEqual(original, result);
    }

    [Fact]
    public void SegmentContent_Protobuf_DoubleRoundTrip()
    {
        var original = CreateSegmentContent();
        var first = RoundTrip<SegmentContent>(_protobuf, original);
        var second = RoundTrip<SegmentContent>(_protobuf, first);
        AssertSegmentEqual(original, second);
    }

    [Fact]
    public void SegmentContent_NullFields_RoundTrip()
    {
        var original = new SegmentContent
        {
            SegmentId = 42,
            SegmentData = null,
            FileName = null,
            Metadata = new Dictionary<string, string>()
        };

        var protoResult = RoundTrip<SegmentContent>(_protobuf, original);
        Assert.Equal(42, protoResult.SegmentId);
        Assert.Null(protoResult.SegmentData);
        Assert.Null(protoResult.FileName);
    }

    [Fact]
    public void SegmentContent_LargeBinaryData_RoundTrip()
    {
        var largeData = new byte[8192];
        new Random(42).NextBytes(largeData);

        var original = new SegmentContent
        {
            SegmentId = 1,
            SegmentData = largeData,
            FileName = "large_segment.dat",
            ContentLength = largeData.Length,
            Metadata = new Dictionary<string, string>()
        };

        var result = RoundTrip<SegmentContent>(_protobuf, original);
        Assert.Equal(original.SegmentData, result.SegmentData);
        Assert.Equal(original.ContentLength, result.ContentLength);
    }

    // ── WALContent ──────────────────────────────────────────────────

    [Fact]
    public void WALContent_Protobuf_RoundTrip()
    {
        var original = CreateWALContent();
        var result = RoundTrip<WALContent>(_protobuf, original);
        AssertWALEqual(original, result);
    }

    [Fact]
    public void WALContent_Json_RoundTrip()
    {
        var original = CreateWALContent();
        var result = RoundTrip<WALContent>(_json, original);
        AssertWALEqual(original, result);
    }

    [Fact]
    public void WALContent_Protobuf_DoubleRoundTrip()
    {
        var original = CreateWALContent();
        var first = RoundTrip<WALContent>(_protobuf, original);
        var second = RoundTrip<WALContent>(_protobuf, first);
        AssertWALEqual(original, second);
    }

    [Fact]
    public void WALContent_MultipleCategories_RoundTrip()
    {
        var original = new WALContent
        {
            Entries = new Dictionary<string, List<WALEntry>>
            {
                ["emails"] = new List<WALEntry>
                {
                    new WALEntry { SerializedKey = new byte[] { 1 }, SerializedValue = new byte[] { 2 }, OpIndex = 1, Category = "emails", SegmentId = 100 },
                    new WALEntry { SerializedKey = new byte[] { 3 }, SerializedValue = new byte[] { 4 }, OpIndex = 2, Category = "emails", SegmentId = 100 }
                },
                ["folders"] = new List<WALEntry>
                {
                    new WALEntry { SerializedKey = new byte[] { 5 }, SerializedValue = new byte[] { 6 }, OpIndex = 3, Category = "folders", SegmentId = 200 }
                }
            },
            NextWALOffset = 8192,
            CategoryOffsets = new Dictionary<string, long>
            {
                ["emails"] = 0,
                ["folders"] = 512
            }
        };

        var result = RoundTrip<WALContent>(_protobuf, original);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(2, result.Entries["emails"].Count);
        Assert.Single(result.Entries["folders"]);
        Assert.Equal(original.NextWALOffset, result.NextWALOffset);
        Assert.Equal(original.CategoryOffsets, result.CategoryOffsets);
    }

    // ── HeaderContent ───────────────────────────────────────────────

    [Fact]
    public void HeaderContent_Protobuf_RoundTrip()
    {
        var original = CreateHeaderContent();
        var result = RoundTrip<HeaderContent>(_protobuf, original);
        AssertHeaderEqual(original, result);
    }

    [Fact]
    public void HeaderContent_Json_RoundTrip()
    {
        var original = CreateHeaderContent();
        var result = RoundTrip<HeaderContent>(_json, original);
        AssertHeaderEqual(original, result);
    }

    [Fact]
    public void HeaderContent_Protobuf_DoubleRoundTrip()
    {
        var original = CreateHeaderContent();
        var first = RoundTrip<HeaderContent>(_protobuf, original);
        var second = RoundTrip<HeaderContent>(_protobuf, first);
        AssertHeaderEqual(original, second);
    }

    // ── CleanupContent (bonus — part of BlockContent hierarchy) ─────

    [Fact]
    public void CleanupContent_Protobuf_RoundTrip()
    {
        var original = new CleanupContent
        {
            FolderTreeOffsets = new List<long> { 100, 200, 300 },
            FolderOffsets = new Dictionary<string, List<long>>
            {
                ["Inbox"] = new List<long> { 1000, 2000 },
                ["Sent"] = new List<long> { 3000 }
            },
            MetadataOffsets = new List<long> { 500, 600 },
            CleanupThreshold = 10000
        };

        var result = RoundTrip<CleanupContent>(_protobuf, original);

        Assert.Equal(original.FolderTreeOffsets, result.FolderTreeOffsets);
        Assert.Equal(original.FolderOffsets.Count, result.FolderOffsets.Count);
        Assert.Equal(original.FolderOffsets["Inbox"], result.FolderOffsets["Inbox"]);
        Assert.Equal(original.FolderOffsets["Sent"], result.FolderOffsets["Sent"]);
        Assert.Equal(original.MetadataOffsets, result.MetadataOffsets);
        Assert.Equal(original.CleanupThreshold, result.CleanupThreshold);
    }

    // ── Interface polymorphism ──────────────────────────────────────

    [Theory]
    [InlineData(typeof(MetadataContent))]
    [InlineData(typeof(FolderContent))]
    [InlineData(typeof(FolderTreeContent))]
    [InlineData(typeof(SegmentContent))]
    [InlineData(typeof(WALContent))]
    [InlineData(typeof(HeaderContent))]
    public void AllContentTypes_RoundTrip_ViaInterface_Protobuf(Type contentType)
    {
        iBlockContentSerializer serializer = _protobuf;
        var original = CreateSample(contentType);

        var bytes = InvokeSerialize(serializer, contentType, original);
        var result = InvokeDeserialize(serializer, contentType, bytes);

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(typeof(MetadataContent))]
    [InlineData(typeof(FolderContent))]
    [InlineData(typeof(FolderTreeContent))]
    [InlineData(typeof(SegmentContent))]
    [InlineData(typeof(WALContent))]
    [InlineData(typeof(HeaderContent))]
    public void AllContentTypes_RoundTrip_ViaInterface_Json(Type contentType)
    {
        iBlockContentSerializer serializer = _json;
        var original = CreateSample(contentType);

        var bytes = InvokeSerialize(serializer, contentType, original);
        var result = InvokeDeserialize(serializer, contentType, bytes);

        Assert.NotNull(result);
    }

    // ── Byte-level idempotency ──────────────────────────────────────

    [Theory]
    [InlineData(typeof(MetadataContent))]
    [InlineData(typeof(FolderContent))]
    [InlineData(typeof(FolderTreeContent))]
    [InlineData(typeof(SegmentContent))]
    [InlineData(typeof(WALContent))]
    [InlineData(typeof(HeaderContent))]
    public void AllContentTypes_Protobuf_SerializeIsIdempotent(Type contentType)
    {
        var original = CreateSample(contentType);

        var bytes1 = InvokeSerialize(_protobuf, contentType, original);
        var deserialized = InvokeDeserialize(_protobuf, contentType, bytes1);
        var bytes2 = InvokeSerialize(_protobuf, contentType, deserialized);

        Assert.Equal(bytes1, bytes2);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static T RoundTrip<T>(iBlockContentSerializer serializer, T original)
    {
        var bytes = serializer.Serialize(original);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        return serializer.Deserialize<T>(bytes);
    }

    private static byte[] InvokeSerialize(iBlockContentSerializer serializer, Type type, object obj)
    {
        var method = typeof(iBlockContentSerializer)
            .GetMethod(nameof(iBlockContentSerializer.Serialize))!
            .MakeGenericMethod(type);
        return (byte[])method.Invoke(serializer, new[] { obj })!;
    }

    private static object InvokeDeserialize(iBlockContentSerializer serializer, Type type, byte[] bytes)
    {
        var method = typeof(iBlockContentSerializer)
            .GetMethod(nameof(iBlockContentSerializer.Deserialize))!
            .MakeGenericMethod(type);
        return method.Invoke(serializer, new object[] { bytes })!;
    }

    private static object CreateSample(Type type)
    {
        if (type == typeof(MetadataContent)) return CreateMetadataContent();
        if (type == typeof(FolderContent)) return CreateFolderContent();
        if (type == typeof(FolderTreeContent)) return CreateFolderTreeContent();
        if (type == typeof(SegmentContent)) return CreateSegmentContent();
        if (type == typeof(WALContent)) return CreateWALContent();
        if (type == typeof(HeaderContent)) return CreateHeaderContent();
        throw new ArgumentException($"Unknown content type: {type.Name}");
    }

    private static MetadataContent CreateMetadataContent() => new()
    {
        WALOffset = 4096,
        FolderTreeOffset = 8192,
        SegmentOffsets = new Dictionary<string, long>
        {
            ["segment-alpha"] = 100,
            ["segment-beta"] = 200,
            ["segment-gamma"] = 300
        },
        OutdatedOffsets = new List<long> { 50, 75, 125 }
    };

    private static FolderContent CreateFolderContent() => new()
    {
        FolderId = 42,
        ParentFolderId = 10,
        Name = "Inbox/Work Projects",
        EmailIds = new List<long> { 1, 2, 3, 100, 9999 }
    };

    private static FolderTreeContent CreateFolderTreeContent() => new()
    {
        RootFolderId = 1,
        FolderHierarchy = new Dictionary<string, string>
        {
            ["Inbox"] = "",
            ["Inbox/Work"] = "Inbox",
            ["Inbox/Personal"] = "Inbox",
            ["Sent"] = "",
            ["Drafts"] = ""
        },
        FolderIDs = new Dictionary<string, long>
        {
            ["Inbox"] = 1,
            ["Inbox/Work"] = 2,
            ["Inbox/Personal"] = 3,
            ["Sent"] = 4,
            ["Drafts"] = 5
        },
        FolderOffsets = new Dictionary<long, long>
        {
            [1] = 1000,
            [2] = 2000,
            [3] = 3000,
            [4] = 4000,
            [5] = 5000
        }
    };

    private static SegmentContent CreateSegmentContent() => new()
    {
        SegmentId = 999,
        SegmentData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE },
        FileName = "segment_000.dat",
        FileOffset = 512,
        ContentLength = 6,
        SegmentTimestamp = 1700000000,
        IsDeleted = false,
        Version = 3,
        Metadata = new Dictionary<string, string>
        {
            ["author"] = "test@example.com",
            ["subject"] = "Hello World",
            ["importance"] = "high"
        }
    };

    private static WALContent CreateWALContent() => new()
    {
        Entries = new Dictionary<string, List<WALEntry>>
        {
            ["emails"] = new List<WALEntry>
            {
                new WALEntry
                {
                    SerializedKey = new byte[] { 1, 2, 3 },
                    SerializedValue = new byte[] { 4, 5, 6, 7 },
                    OpIndex = 1,
                    Category = "emails",
                    SegmentId = 100
                },
                new WALEntry
                {
                    SerializedKey = new byte[] { 10, 11 },
                    SerializedValue = new byte[] { 12, 13, 14 },
                    OpIndex = 2,
                    Category = "emails",
                    SegmentId = 100
                }
            }
        },
        NextWALOffset = 4096,
        CategoryOffsets = new Dictionary<string, long>
        {
            ["emails"] = 0,
            ["folders"] = 512
        }
    };

    private static HeaderContent CreateHeaderContent() => new()
    {
        FileVersion = 3,
        FirstMetadataOffset = 1024,
        FirstFolderTreeOffset = 2048,
        FirstCleanupOffset = 4096
    };

    private static void AssertMetadataEqual(MetadataContent expected, MetadataContent actual)
    {
        Assert.Equal(expected.WALOffset, actual.WALOffset);
        Assert.Equal(expected.FolderTreeOffset, actual.FolderTreeOffset);
        Assert.Equal(expected.SegmentOffsets, actual.SegmentOffsets);
        Assert.Equal(expected.OutdatedOffsets, actual.OutdatedOffsets);
    }

    private static void AssertFolderEqual(FolderContent expected, FolderContent actual)
    {
        Assert.Equal(expected.FolderId, actual.FolderId);
        Assert.Equal(expected.ParentFolderId, actual.ParentFolderId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.EmailIds, actual.EmailIds);
    }

    private static void AssertFolderTreeEqual(FolderTreeContent expected, FolderTreeContent actual)
    {
        Assert.Equal(expected.RootFolderId, actual.RootFolderId);
        Assert.Equal(expected.FolderHierarchy, actual.FolderHierarchy);
        Assert.Equal(expected.FolderIDs, actual.FolderIDs);
        Assert.Equal(expected.FolderOffsets, actual.FolderOffsets);
    }

    private static void AssertSegmentEqual(SegmentContent expected, SegmentContent actual)
    {
        Assert.Equal(expected.SegmentId, actual.SegmentId);
        Assert.Equal(expected.SegmentData, actual.SegmentData);
        Assert.Equal(expected.FileName, actual.FileName);
        Assert.Equal(expected.FileOffset, actual.FileOffset);
        Assert.Equal(expected.ContentLength, actual.ContentLength);
        Assert.Equal(expected.SegmentTimestamp, actual.SegmentTimestamp);
        Assert.Equal(expected.IsDeleted, actual.IsDeleted);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Metadata, actual.Metadata);
    }

    private static void AssertWALEqual(WALContent expected, WALContent actual)
    {
        Assert.Equal(expected.NextWALOffset, actual.NextWALOffset);
        Assert.Equal(expected.CategoryOffsets, actual.CategoryOffsets);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);

        foreach (var key in expected.Entries.Keys)
        {
            Assert.True(actual.Entries.ContainsKey(key), $"Missing WAL category: {key}");
            var expectedEntries = expected.Entries[key];
            var actualEntries = actual.Entries[key];
            Assert.Equal(expectedEntries.Count, actualEntries.Count);

            for (int i = 0; i < expectedEntries.Count; i++)
            {
                Assert.Equal(expectedEntries[i].SerializedKey, actualEntries[i].SerializedKey);
                Assert.Equal(expectedEntries[i].SerializedValue, actualEntries[i].SerializedValue);
                Assert.Equal(expectedEntries[i].OpIndex, actualEntries[i].OpIndex);
                Assert.Equal(expectedEntries[i].Category, actualEntries[i].Category);
                Assert.Equal(expectedEntries[i].SegmentId, actualEntries[i].SegmentId);
            }
        }
    }

    private static void AssertHeaderEqual(HeaderContent expected, HeaderContent actual)
    {
        Assert.Equal(expected.FileVersion, actual.FileVersion);
        Assert.Equal(expected.FirstMetadataOffset, actual.FirstMetadataOffset);
        Assert.Equal(expected.FirstFolderTreeOffset, actual.FirstFolderTreeOffset);
        Assert.Equal(expected.FirstCleanupOffset, actual.FirstCleanupOffset);
    }
}
