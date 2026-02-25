using EmailDB.Format;
using EmailDB.Format.Protobuf;
using EmailDB.Format.Protobuf.Models;
using Xunit;

namespace EmailDB.UnitTests;

public class ProtobufBlockContentSerializerTests
{
    private readonly ProtobufBlockContentSerializer _serializer = new();

    [Fact]
    public void ProtobufBlockContentSerializer_Implements_iBlockContentSerializer()
    {
        // Arrange & Act
        var serializer = new ProtobufBlockContentSerializer();

        // Assert
        Assert.IsAssignableFrom<iBlockContentSerializer>(serializer);
    }

    [Fact]
    public void ProtobufBlockContentSerializer_CanBeUsedAsInterface()
    {
        // Verify it can be assigned to the interface type
        iBlockContentSerializer serializer = new ProtobufBlockContentSerializer();

        Assert.NotNull(serializer);
    }

    // ── MetadataContent ────────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_MetadataContent_RoundTripsCorrectly()
    {
        var original = new MetadataContent
        {
            WALOffset = 4096,
            FolderTreeOffset = 8192,
            SegmentOffsets = new Dictionary<string, long>
            {
                ["segment-a"] = 100,
                ["segment-b"] = 200
            },
            OutdatedOffsets = new List<long> { 50, 75 }
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<MetadataContent>(bytes);

        Assert.Equal(original.WALOffset, result.WALOffset);
        Assert.Equal(original.FolderTreeOffset, result.FolderTreeOffset);
        Assert.Equal(original.SegmentOffsets, result.SegmentOffsets);
        Assert.Equal(original.OutdatedOffsets, result.OutdatedOffsets);
    }

    [Fact]
    public void SerializeDeserialize_MetadataContent_DefaultValues()
    {
        var original = new MetadataContent();

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<MetadataContent>(bytes);

        Assert.Equal(-1L, result.WALOffset);
        Assert.Equal(-1L, result.FolderTreeOffset);
        Assert.Empty(result.SegmentOffsets);
        Assert.Empty(result.OutdatedOffsets);
    }

    // ── FolderContent ──────────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_FolderContent_RoundTripsCorrectly()
    {
        var original = new FolderContent
        {
            FolderId = 42,
            ParentFolderId = 10,
            Name = "Inbox",
            EmailIds = new List<long> { 1, 2, 3, 100 }
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<FolderContent>(bytes);

        Assert.Equal(original.FolderId, result.FolderId);
        Assert.Equal(original.ParentFolderId, result.ParentFolderId);
        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.EmailIds, result.EmailIds);
    }

    [Fact]
    public void SerializeDeserialize_FolderContent_EmptyEmailIds()
    {
        var original = new FolderContent
        {
            FolderId = 1,
            ParentFolderId = 0,
            Name = "Empty Folder",
            EmailIds = new List<long>()
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<FolderContent>(bytes);

        Assert.Equal("Empty Folder", result.Name);
        Assert.Empty(result.EmailIds);
    }

    // ── FolderTreeContent ──────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_FolderTreeContent_RoundTripsCorrectly()
    {
        var original = new FolderTreeContent
        {
            RootFolderId = 1,
            FolderHierarchy = new Dictionary<string, string>
            {
                ["Inbox"] = "",
                ["Inbox/Work"] = "Inbox",
                ["Sent"] = ""
            },
            FolderIDs = new Dictionary<string, long>
            {
                ["Inbox"] = 1,
                ["Inbox/Work"] = 2,
                ["Sent"] = 3
            },
            FolderOffsets = new Dictionary<long, long>
            {
                [1] = 1000,
                [2] = 2000,
                [3] = 3000
            }
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<FolderTreeContent>(bytes);

        Assert.Equal(original.RootFolderId, result.RootFolderId);
        Assert.Equal(original.FolderHierarchy, result.FolderHierarchy);
        Assert.Equal(original.FolderIDs, result.FolderIDs);
        Assert.Equal(original.FolderOffsets, result.FolderOffsets);
    }

    [Fact]
    public void SerializeDeserialize_FolderTreeContent_EmptyDictionaries()
    {
        var original = new FolderTreeContent { RootFolderId = 0 };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<FolderTreeContent>(bytes);

        Assert.Equal(0, result.RootFolderId);
        Assert.Empty(result.FolderHierarchy);
        Assert.Empty(result.FolderIDs);
        Assert.Empty(result.FolderOffsets);
    }

    // ── SegmentContent ─────────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_SegmentContent_RoundTripsCorrectly()
    {
        var original = new SegmentContent
        {
            SegmentId = 999,
            SegmentData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FileName = "segment_000.dat",
            FileOffset = 512,
            ContentLength = 4,
            SegmentTimestamp = 1700000000,
            IsDeleted = false,
            Version = 1,
            Metadata = new Dictionary<string, string>
            {
                ["author"] = "test@example.com",
                ["subject"] = "Hello"
            }
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<SegmentContent>(bytes);

        Assert.Equal(original.SegmentId, result.SegmentId);
        Assert.Equal(original.SegmentData, result.SegmentData);
        Assert.Equal(original.FileName, result.FileName);
        Assert.Equal(original.FileOffset, result.FileOffset);
        Assert.Equal(original.ContentLength, result.ContentLength);
        Assert.Equal(original.SegmentTimestamp, result.SegmentTimestamp);
        Assert.Equal(original.IsDeleted, result.IsDeleted);
        Assert.Equal(original.Version, result.Version);
        Assert.Equal(original.Metadata, result.Metadata);
    }

    [Fact]
    public void SerializeDeserialize_SegmentContent_NullSegmentData()
    {
        var original = new SegmentContent
        {
            SegmentId = 1,
            SegmentData = null,
            FileName = null
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<SegmentContent>(bytes);

        Assert.Equal(1, result.SegmentId);
        Assert.Null(result.SegmentData);
        Assert.Null(result.FileName);
    }

    // ── WALContent ─────────────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_WALContent_RoundTripsCorrectly()
    {
        var original = new WALContent
        {
            Entries = new Dictionary<string, List<WALEntry>>
            {
                ["emails"] = new List<WALEntry>
                {
                    new WALEntry
                    {
                        SerializedKey = new byte[] { 1, 2, 3 },
                        SerializedValue = new byte[] { 4, 5, 6 },
                        OpIndex = 1,
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

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<WALContent>(bytes);

        Assert.Equal(original.NextWALOffset, result.NextWALOffset);
        Assert.Equal(original.CategoryOffsets, result.CategoryOffsets);
        Assert.Single(result.Entries);
        Assert.True(result.Entries.ContainsKey("emails"));

        var entry = result.Entries["emails"][0];
        Assert.Equal(new byte[] { 1, 2, 3 }, entry.SerializedKey);
        Assert.Equal(new byte[] { 4, 5, 6 }, entry.SerializedValue);
        Assert.Equal(1, entry.OpIndex);
        Assert.Equal("emails", entry.Category);
        Assert.Equal(100, entry.SegmentId);
    }

    [Fact]
    public void SerializeDeserialize_WALContent_DefaultValues()
    {
        var original = new WALContent();

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<WALContent>(bytes);

        Assert.Equal(-1L, result.NextWALOffset);
        Assert.Empty(result.Entries);
        Assert.Empty(result.CategoryOffsets);
    }

    // ── HeaderContent ──────────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_HeaderContent_RoundTripsCorrectly()
    {
        var original = new HeaderContent
        {
            FileVersion = 3,
            FirstMetadataOffset = 1024,
            FirstFolderTreeOffset = 2048,
            FirstCleanupOffset = 4096
        };

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<HeaderContent>(bytes);

        Assert.Equal(original.FileVersion, result.FileVersion);
        Assert.Equal(original.FirstMetadataOffset, result.FirstMetadataOffset);
        Assert.Equal(original.FirstFolderTreeOffset, result.FirstFolderTreeOffset);
        Assert.Equal(original.FirstCleanupOffset, result.FirstCleanupOffset);
    }

    [Fact]
    public void SerializeDeserialize_HeaderContent_ZeroValues()
    {
        var original = new HeaderContent();

        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize<HeaderContent>(bytes);

        Assert.Equal(0, result.FileVersion);
        Assert.Equal(0, result.FirstMetadataOffset);
        Assert.Equal(0, result.FirstFolderTreeOffset);
        Assert.Equal(0, result.FirstCleanupOffset);
    }

    // ── Serialized bytes are non-empty ─────────────────────────────

    [Theory]
    [InlineData(typeof(MetadataContent))]
    [InlineData(typeof(FolderContent))]
    [InlineData(typeof(FolderTreeContent))]
    [InlineData(typeof(SegmentContent))]
    [InlineData(typeof(WALContent))]
    [InlineData(typeof(HeaderContent))]
    public void Serialize_AllContentTypes_ProduceNonEmptyBytes(Type contentType)
    {
        var instance = Activator.CreateInstance(contentType)!;
        var serializeMethod = typeof(ProtobufBlockContentSerializer)
            .GetMethod(nameof(ProtobufBlockContentSerializer.Serialize))!
            .MakeGenericMethod(contentType);

        var bytes = (byte[])serializeMethod.Invoke(_serializer, new[] { instance })!;

        Assert.NotNull(bytes);
    }
}
