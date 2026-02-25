using EmailDB.Format;
using EmailDB.Format.Helpers;
using EmailDB.Format.Protobuf;
using EmailDB.Format.Protobuf.Models;
using Xunit;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that the JSON serializer (DefaultBlockContentSerializer) is retained
/// as a fallback for debugging, per acceptance criterion on US-EMDB-5.
/// </summary>
public class JsonSerializerFallbackTests
{
    private readonly DefaultBlockContentSerializer _jsonSerializer = new();

    // ── Interface compliance ────────────────────────────────────────

    [Fact]
    public void DefaultBlockContentSerializer_Implements_iBlockContentSerializer()
    {
        Assert.IsAssignableFrom<iBlockContentSerializer>(_jsonSerializer);
    }

    [Fact]
    public void DefaultBlockContentSerializer_IsInterchangeableWithProtobuf()
    {
        // Both serializers can be used through the same interface
        iBlockContentSerializer json = new DefaultBlockContentSerializer();
        iBlockContentSerializer proto = new ProtobufBlockContentSerializer();

        var content = new HeaderContent
        {
            FileVersion = 1,
            FirstMetadataOffset = 100,
            FirstFolderTreeOffset = 200,
            FirstCleanupOffset = 300
        };

        // Both produce bytes and round-trip correctly via the interface
        var jsonResult = json.Deserialize<HeaderContent>(json.Serialize(content));
        var protoResult = proto.Deserialize<HeaderContent>(proto.Serialize(content));

        Assert.Equal(jsonResult.FileVersion, protoResult.FileVersion);
        Assert.Equal(jsonResult.FirstMetadataOffset, protoResult.FirstMetadataOffset);
        Assert.Equal(jsonResult.FirstFolderTreeOffset, protoResult.FirstFolderTreeOffset);
        Assert.Equal(jsonResult.FirstCleanupOffset, protoResult.FirstCleanupOffset);
    }

    // ── All 6 content types round-trip via JSON ─────────────────────

    [Fact]
    public void JsonFallback_MetadataContent_RoundTrips()
    {
        var original = new MetadataContent
        {
            WALOffset = 4096,
            FolderTreeOffset = 8192,
            SegmentOffsets = new Dictionary<string, long> { ["seg-1"] = 100 },
            OutdatedOffsets = new List<long> { 50, 75 }
        };

        var bytes = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<MetadataContent>(bytes);

        Assert.Equal(original.WALOffset, result.WALOffset);
        Assert.Equal(original.FolderTreeOffset, result.FolderTreeOffset);
        Assert.Equal(original.SegmentOffsets, result.SegmentOffsets);
        Assert.Equal(original.OutdatedOffsets, result.OutdatedOffsets);
    }

    [Fact]
    public void JsonFallback_FolderContent_RoundTrips()
    {
        var original = new FolderContent
        {
            FolderId = 42,
            ParentFolderId = 10,
            Name = "Inbox",
            EmailIds = new List<long> { 1, 2, 3 }
        };

        var bytes = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<FolderContent>(bytes);

        Assert.Equal(original.FolderId, result.FolderId);
        Assert.Equal(original.ParentFolderId, result.ParentFolderId);
        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.EmailIds, result.EmailIds);
    }

    [Fact]
    public void JsonFallback_FolderTreeContent_RoundTrips()
    {
        var original = new FolderTreeContent
        {
            RootFolderId = 1,
            FolderHierarchy = new Dictionary<string, string>
            {
                ["Inbox"] = "",
                ["Sent"] = ""
            },
            FolderIDs = new Dictionary<string, long>
            {
                ["Inbox"] = 1,
                ["Sent"] = 2
            },
            FolderOffsets = new Dictionary<long, long>
            {
                [1] = 1000,
                [2] = 2000
            }
        };

        var bytes = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<FolderTreeContent>(bytes);

        Assert.Equal(original.RootFolderId, result.RootFolderId);
        Assert.Equal(original.FolderHierarchy, result.FolderHierarchy);
        Assert.Equal(original.FolderIDs, result.FolderIDs);
        Assert.Equal(original.FolderOffsets, result.FolderOffsets);
    }

    [Fact]
    public void JsonFallback_SegmentContent_RoundTrips()
    {
        var original = new SegmentContent
        {
            SegmentId = 999,
            SegmentData = new byte[] { 0xDE, 0xAD },
            FileName = "segment.dat",
            FileOffset = 512,
            ContentLength = 2,
            SegmentTimestamp = 1700000000,
            IsDeleted = false,
            Version = 1,
            Metadata = new Dictionary<string, string> { ["key"] = "value" }
        };

        var bytes = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<SegmentContent>(bytes);

        Assert.Equal(original.SegmentId, result.SegmentId);
        Assert.Equal(original.SegmentData, result.SegmentData);
        Assert.Equal(original.FileName, result.FileName);
        Assert.Equal(original.FileOffset, result.FileOffset);
        Assert.Equal(original.ContentLength, result.ContentLength);
        Assert.Equal(original.Metadata, result.Metadata);
    }

    [Fact]
    public void JsonFallback_WALContent_RoundTrips()
    {
        var original = new WALContent
        {
            Entries = new Dictionary<string, List<WALEntry>>
            {
                ["emails"] = new List<WALEntry>
                {
                    new WALEntry
                    {
                        SerializedKey = new byte[] { 1, 2 },
                        SerializedValue = new byte[] { 3, 4 },
                        OpIndex = 1,
                        Category = "emails",
                        SegmentId = 100
                    }
                }
            },
            NextWALOffset = 4096,
            CategoryOffsets = new Dictionary<string, long> { ["emails"] = 0 }
        };

        var bytes = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<WALContent>(bytes);

        Assert.Equal(original.NextWALOffset, result.NextWALOffset);
        Assert.Equal(original.CategoryOffsets, result.CategoryOffsets);
        Assert.Single(result.Entries);
        Assert.True(result.Entries.ContainsKey("emails"));
    }

    [Fact]
    public void JsonFallback_HeaderContent_RoundTrips()
    {
        var original = new HeaderContent
        {
            FileVersion = 3,
            FirstMetadataOffset = 1024,
            FirstFolderTreeOffset = 2048,
            FirstCleanupOffset = 4096
        };

        var bytes = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<HeaderContent>(bytes);

        Assert.Equal(original.FileVersion, result.FileVersion);
        Assert.Equal(original.FirstMetadataOffset, result.FirstMetadataOffset);
        Assert.Equal(original.FirstFolderTreeOffset, result.FirstFolderTreeOffset);
        Assert.Equal(original.FirstCleanupOffset, result.FirstCleanupOffset);
    }

    // ── Human-readable output (key for debugging) ───────────────────

    [Fact]
    public void JsonFallback_Output_IsHumanReadableJson()
    {
        var content = new MetadataContent
        {
            WALOffset = 4096,
            FolderTreeOffset = 8192
        };

        var bytes = _jsonSerializer.Serialize(content);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        // JSON output must be valid UTF-8 text starting with '{'
        Assert.StartsWith("{", text);
        Assert.EndsWith("}", text);

        // Must contain field names as readable strings for debugging
        Assert.Contains("WALOffset", text);
        Assert.Contains("FolderTreeOffset", text);
        Assert.Contains("4096", text);
        Assert.Contains("8192", text);
    }

    [Fact]
    public void JsonFallback_Output_ContainsFieldNames_ForAllTypes()
    {
        // FolderContent — field names readable in output
        var folder = new FolderContent { FolderId = 7, Name = "TestFolder" };
        var folderJson = System.Text.Encoding.UTF8.GetString(_jsonSerializer.Serialize(folder));
        Assert.Contains("FolderId", folderJson);
        Assert.Contains("TestFolder", folderJson);

        // HeaderContent
        var header = new HeaderContent { FileVersion = 5 };
        var headerJson = System.Text.Encoding.UTF8.GetString(_jsonSerializer.Serialize(header));
        Assert.Contains("FileVersion", headerJson);
        Assert.Contains("5", headerJson);
    }

    [Theory]
    [InlineData(typeof(MetadataContent))]
    [InlineData(typeof(FolderContent))]
    [InlineData(typeof(FolderTreeContent))]
    [InlineData(typeof(SegmentContent))]
    [InlineData(typeof(WALContent))]
    [InlineData(typeof(HeaderContent))]
    public void JsonFallback_AllContentTypes_ProduceValidJsonBytes(Type contentType)
    {
        var instance = Activator.CreateInstance(contentType)!;
        var serializeMethod = typeof(DefaultBlockContentSerializer)
            .GetMethod(nameof(DefaultBlockContentSerializer.Serialize))!
            .MakeGenericMethod(contentType);

        var bytes = (byte[])serializeMethod.Invoke(_jsonSerializer, new[] { instance })!;

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        // All JSON output must be valid UTF-8 starting with '{'
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("{", text);
    }
}
