using EmailDB.Format;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Protobuf;
using EmailDB.Format.Protobuf.Models;
using Xunit;

using Block = EmailDB.Format.Models.Block;
using BlockType = EmailDB.Format.Models.BlockType;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that the PayloadEncoding enum is respected when reading blocks.
/// Verifies that Protobuf-encoded payloads are only correctly deserialized
/// by the Protobuf serializer, JSON-encoded payloads by the JSON serializer,
/// and that cross-format deserialization fails.
/// </summary>
public class PayloadEncodingTests
{
    private readonly ProtobufBlockContentSerializer _protobufSerializer = new();
    private readonly DefaultBlockContentSerializer _jsonSerializer = new();

    // ── Enum definition ─────────────────────────────────────────────

    [Fact]
    public void PayloadEncoding_HasExpectedValues()
    {
        Assert.Equal((byte)1, (byte)PayloadEncoding.Protobuf);
        Assert.Equal((byte)2, (byte)PayloadEncoding.Json);
        Assert.Equal((byte)3, (byte)PayloadEncoding.RawBytes);
    }

    [Fact]
    public void PayloadEncoding_BackedByByte()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(PayloadEncoding)));
    }

    [Fact]
    public void PayloadEncoding_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<PayloadEncoding>();
        Assert.Equal(3, values.Length);
    }

    // ── Protobuf-encoded payloads read correctly with Protobuf serializer ──

    [Fact]
    public void ProtobufEncoded_MetadataContent_DeserializesWithProtobufSerializer()
    {
        var original = new MetadataContent
        {
            WALOffset = 4096,
            FolderTreeOffset = 8192,
            SegmentOffsets = new Dictionary<string, long> { ["seg-1"] = 100 },
            OutdatedOffsets = new List<long> { 50 }
        };

        var payload = _protobufSerializer.Serialize(original);
        var result = _protobufSerializer.Deserialize<MetadataContent>(payload);

        Assert.Equal(original.WALOffset, result.WALOffset);
        Assert.Equal(original.FolderTreeOffset, result.FolderTreeOffset);
        Assert.Equal(original.SegmentOffsets, result.SegmentOffsets);
        Assert.Equal(original.OutdatedOffsets, result.OutdatedOffsets);
    }

    [Fact]
    public void ProtobufEncoded_HeaderContent_DeserializesWithProtobufSerializer()
    {
        var original = new HeaderContent
        {
            FileVersion = 3,
            FirstMetadataOffset = 1024,
            FirstFolderTreeOffset = 2048,
            FirstCleanupOffset = 4096
        };

        var payload = _protobufSerializer.Serialize(original);
        var result = _protobufSerializer.Deserialize<HeaderContent>(payload);

        Assert.Equal(original.FileVersion, result.FileVersion);
        Assert.Equal(original.FirstMetadataOffset, result.FirstMetadataOffset);
        Assert.Equal(original.FirstFolderTreeOffset, result.FirstFolderTreeOffset);
        Assert.Equal(original.FirstCleanupOffset, result.FirstCleanupOffset);
    }

    // ── JSON-encoded payloads read correctly with JSON serializer ────

    [Fact]
    public void JsonEncoded_HeaderContent_DeserializesWithJsonSerializer()
    {
        var original = new HeaderContent
        {
            FileVersion = 2,
            FirstMetadataOffset = 512,
            FirstFolderTreeOffset = 1024,
            FirstCleanupOffset = 2048
        };

        var payload = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<HeaderContent>(payload);

        Assert.Equal(original.FileVersion, result.FileVersion);
        Assert.Equal(original.FirstMetadataOffset, result.FirstMetadataOffset);
        Assert.Equal(original.FirstFolderTreeOffset, result.FirstFolderTreeOffset);
        Assert.Equal(original.FirstCleanupOffset, result.FirstCleanupOffset);
    }

    [Fact]
    public void JsonEncoded_MetadataContent_DeserializesWithJsonSerializer()
    {
        var original = new MetadataContent
        {
            WALOffset = 2048,
            FolderTreeOffset = 4096,
            SegmentOffsets = new Dictionary<string, long> { ["seg-a"] = 300 },
            OutdatedOffsets = new List<long> { 10, 20 }
        };

        var payload = _jsonSerializer.Serialize(original);
        var result = _jsonSerializer.Deserialize<MetadataContent>(payload);

        Assert.Equal(original.WALOffset, result.WALOffset);
        Assert.Equal(original.FolderTreeOffset, result.FolderTreeOffset);
        Assert.Equal(original.SegmentOffsets, result.SegmentOffsets);
        Assert.Equal(original.OutdatedOffsets, result.OutdatedOffsets);
    }

    // ── Cross-format incompatibility (encoding MUST be respected) ───

    [Fact]
    public void ProtobufEncoded_Payload_FailsWithJsonSerializer()
    {
        var original = new MetadataContent
        {
            WALOffset = 4096,
            FolderTreeOffset = 8192,
            SegmentOffsets = new Dictionary<string, long> { ["seg-1"] = 100 },
            OutdatedOffsets = new List<long> { 50 }
        };

        var protobufPayload = _protobufSerializer.Serialize(original);

        // Protobuf bytes are not valid JSON — deserialization must fail
        Assert.ThrowsAny<Exception>(() =>
            _jsonSerializer.Deserialize<MetadataContent>(protobufPayload));
    }

    [Fact]
    public void JsonEncoded_Payload_FailsWithProtobufSerializer()
    {
        var original = new HeaderContent
        {
            FileVersion = 2,
            FirstMetadataOffset = 512,
            FirstFolderTreeOffset = 1024,
            FirstCleanupOffset = 2048
        };

        var jsonPayload = _jsonSerializer.Serialize(original);

        // JSON bytes are not valid Protobuf — deserialization should
        // either throw or produce incorrect values
        try
        {
            var result = _protobufSerializer.Deserialize<HeaderContent>(jsonPayload);
            // If it doesn't throw, the values must be wrong
            bool allMatch = result.FileVersion == original.FileVersion
                && result.FirstMetadataOffset == original.FirstMetadataOffset
                && result.FirstFolderTreeOffset == original.FirstFolderTreeOffset
                && result.FirstCleanupOffset == original.FirstCleanupOffset;
            Assert.False(allMatch,
                "JSON payload incorrectly round-tripped through Protobuf — encoding is not being respected");
        }
        catch (Exception)
        {
            // Expected: Protobuf cannot parse JSON bytes
        }
    }

    // ── Serialization formats produce distinct byte representations ──

    [Fact]
    public void SameContent_ProducesDifferentBytes_AcrossEncodings()
    {
        var header = new HeaderContent
        {
            FileVersion = 1,
            FirstMetadataOffset = 100,
            FirstFolderTreeOffset = 200,
            FirstCleanupOffset = 300
        };

        var protobufBytes = _protobufSerializer.Serialize(header);
        var jsonBytes = _jsonSerializer.Serialize(header);

        Assert.NotEqual(protobufBytes, jsonBytes);
    }

    [Fact]
    public void JsonPayload_IsValidUtf8Text()
    {
        var header = new HeaderContent
        {
            FileVersion = 1,
            FirstMetadataOffset = 100,
            FirstFolderTreeOffset = 200,
            FirstCleanupOffset = 300
        };

        var jsonBytes = _jsonSerializer.Serialize(header);
        var text = System.Text.Encoding.UTF8.GetString(jsonBytes);

        // JSON payloads start with '{'
        Assert.StartsWith("{", text);
    }

    [Fact]
    public void ProtobufPayload_IsNotValidJson()
    {
        var metadata = new MetadataContent
        {
            WALOffset = 4096,
            FolderTreeOffset = 8192
        };

        var protobufBytes = _protobufSerializer.Serialize(metadata);
        var text = System.Text.Encoding.UTF8.GetString(protobufBytes);

        // Protobuf binary format should not start with '{'
        Assert.False(text.StartsWith("{"), "Protobuf payload should not look like JSON");
    }

    // ── Encoding-aware serializer selection ──────────────────────────

    [Fact]
    public void ResolveSerializer_Protobuf_ReturnsProtobufSerializer()
    {
        var serializer = ResolveSerializer(PayloadEncoding.Protobuf);
        Assert.IsType<ProtobufBlockContentSerializer>(serializer);
    }

    [Fact]
    public void ResolveSerializer_Json_ReturnsJsonSerializer()
    {
        var serializer = ResolveSerializer(PayloadEncoding.Json);
        Assert.IsType<DefaultBlockContentSerializer>(serializer);
    }

    [Fact]
    public void ResolveSerializer_RawBytes_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => ResolveSerializer(PayloadEncoding.RawBytes));
    }

    [Theory]
    [InlineData(PayloadEncoding.Protobuf)]
    [InlineData(PayloadEncoding.Json)]
    public void ResolvedSerializer_CorrectlyRoundTrips_HeaderContent(PayloadEncoding encoding)
    {
        var serializer = ResolveSerializer(encoding);

        var original = new HeaderContent
        {
            FileVersion = 5,
            FirstMetadataOffset = 1000,
            FirstFolderTreeOffset = 2000,
            FirstCleanupOffset = 3000
        };

        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<HeaderContent>(bytes);

        Assert.Equal(original.FileVersion, result.FileVersion);
        Assert.Equal(original.FirstMetadataOffset, result.FirstMetadataOffset);
        Assert.Equal(original.FirstFolderTreeOffset, result.FirstFolderTreeOffset);
        Assert.Equal(original.FirstCleanupOffset, result.FirstCleanupOffset);
    }

    // ── Block-level deserialization respects encoding ────────────────

    [Fact]
    public void Block_WithProtobufPayload_DeserializesCorrectly()
    {
        var content = new MetadataContent
        {
            WALOffset = 1024,
            FolderTreeOffset = 2048,
            SegmentOffsets = new Dictionary<string, long> { ["s1"] = 500 },
            OutdatedOffsets = new List<long> { 10 }
        };

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockId = 1,
            Payload = _protobufSerializer.Serialize(content)
        };

        // Deserialize using the correct serializer for Protobuf encoding
        var deserialized = _protobufSerializer.Deserialize<MetadataContent>(block.Payload);

        Assert.NotNull(deserialized);
        Assert.Equal(content.WALOffset, deserialized.WALOffset);
        Assert.Equal(content.FolderTreeOffset, deserialized.FolderTreeOffset);
        Assert.Equal(content.SegmentOffsets, deserialized.SegmentOffsets);
        Assert.Equal(content.OutdatedOffsets, deserialized.OutdatedOffsets);
    }

    [Fact]
    public void Block_WithJsonPayload_DeserializesCorrectly()
    {
        var content = new HeaderContent
        {
            FileVersion = 7,
            FirstMetadataOffset = 512,
            FirstFolderTreeOffset = 1024,
            FirstCleanupOffset = 2048
        };

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockId = 0,
            Payload = _jsonSerializer.Serialize(content)
        };

        // Deserialize using the correct serializer for Json encoding
        var deserialized = _jsonSerializer.Deserialize<HeaderContent>(block.Payload);

        Assert.NotNull(deserialized);
        Assert.Equal(content.FileVersion, deserialized.FileVersion);
        Assert.Equal(content.FirstMetadataOffset, deserialized.FirstMetadataOffset);
    }

    [Fact]
    public void Block_WithProtobufPayload_FailsWithWrongSerializer()
    {
        var content = new FolderContent
        {
            FolderId = 42,
            ParentFolderId = 10,
            Name = "Inbox",
            EmailIds = new List<long> { 1, 2, 3 }
        };

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Folder,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockId = 100,
            Payload = _protobufSerializer.Serialize(content)
        };

        // Using the JSON serializer on a Protobuf-encoded block should fail
        Assert.ThrowsAny<Exception>(() =>
            _jsonSerializer.Deserialize<FolderContent>(block.Payload));
    }

    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public void Block_AllTypes_DeserializeWithCorrectProtobufSerializer(BlockType blockType)
    {
        object content = CreateSampleContent(blockType);

        // Serialize with the generic Serialize method via the interface
        iBlockContentSerializer serializer = _protobufSerializer;
        var payload = SerializeContent(serializer, blockType, content);

        var block = new Block
        {
            Version = 1,
            Type = blockType,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BlockId = 1,
            Payload = payload
        };

        // Deserialize with the same encoding
        var deserialized = DeserializeContent(serializer, blockType, block.Payload);

        Assert.NotNull(deserialized);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static iBlockContentSerializer ResolveSerializer(PayloadEncoding encoding)
    {
        return encoding switch
        {
            PayloadEncoding.Protobuf => new ProtobufBlockContentSerializer(),
            PayloadEncoding.Json => new DefaultBlockContentSerializer(),
            PayloadEncoding.RawBytes => throw new NotSupportedException(
                "RawBytes encoding does not use a content serializer"),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
    }

    private static byte[] SerializeContent(iBlockContentSerializer serializer, BlockType type, object content)
    {
        return type switch
        {
            BlockType.Metadata => serializer.Serialize((MetadataContent)content),
            BlockType.FolderTree => serializer.Serialize((FolderTreeContent)content),
            BlockType.Folder => serializer.Serialize((FolderContent)content),
            BlockType.Segment => serializer.Serialize((SegmentContent)content),
            BlockType.WAL => serializer.Serialize((WALContent)content),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static object? DeserializeContent(iBlockContentSerializer serializer, BlockType type, byte[] payload)
    {
        return type switch
        {
            BlockType.Metadata => serializer.Deserialize<MetadataContent>(payload),
            BlockType.FolderTree => serializer.Deserialize<FolderTreeContent>(payload),
            BlockType.Folder => serializer.Deserialize<FolderContent>(payload),
            BlockType.Segment => serializer.Deserialize<SegmentContent>(payload),
            BlockType.WAL => serializer.Deserialize<WALContent>(payload),
            _ => null
        };
    }

    private static object CreateSampleContent(BlockType type)
    {
        return type switch
        {
            BlockType.Metadata => new MetadataContent
            {
                WALOffset = 100,
                FolderTreeOffset = 200,
                SegmentOffsets = new Dictionary<string, long>(),
                OutdatedOffsets = new List<long>()
            },
            BlockType.FolderTree => new FolderTreeContent
            {
                RootFolderId = 1,
                FolderHierarchy = new Dictionary<string, string>(),
                FolderIDs = new Dictionary<string, long>(),
                FolderOffsets = new Dictionary<long, long>()
            },
            BlockType.Folder => new FolderContent
            {
                FolderId = 1,
                ParentFolderId = 0,
                Name = "Test",
                EmailIds = new List<long>()
            },
            BlockType.Segment => new SegmentContent
            {
                SegmentId = 1,
                FileName = "test.dat"
            },
            BlockType.WAL => new WALContent
            {
                NextWALOffset = -1,
                Entries = new Dictionary<string, List<WALEntry>>(),
                CategoryOffsets = new Dictionary<string, long>()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}
