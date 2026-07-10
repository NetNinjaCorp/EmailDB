using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// v3 port of the legacy PayloadEncoding test (was v1 content DTOs + the v1
/// serializer registry). Covers the v3 payload-encoding registry
/// (EmailDB_FileFormat_Spec.md Section 4.3) and that the encoding is a first-class
/// header field the block serializer stamps at offset 12 and reads back unchanged:
/// the reader learns how to decode a payload from the block itself, so the encoding
/// MUST survive a full serialize/deserialize round-trip.
/// </summary>
public class PayloadEncodingTests
{
    private const long MaxPayloadLength = 268_435_456; // superblock default, 256 MB

    // ---- Registry (spec Section 4.3): Custom=0, Protobuf=1, 2 reserved, Json=3, RawBytes=4 ----

    [Fact]
    public void PayloadEncoding_HasExpectedRegistryValues()
    {
        Assert.Equal((byte)0, (byte)PayloadEncoding.Custom);
        Assert.Equal((byte)1, (byte)PayloadEncoding.Protobuf);
        Assert.Equal((byte)3, (byte)PayloadEncoding.Json);
        Assert.Equal((byte)4, (byte)PayloadEncoding.RawBytes);
    }

    [Fact]
    public void PayloadEncoding_BackedByByte()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(PayloadEncoding)));
    }

    [Fact]
    public void PayloadEncoding_Value2_IsReserved_AndUndefined()
    {
        // Value 2 is reserved by the spec and must not be a defined member.
        Assert.False(Enum.IsDefined(typeof(PayloadEncoding), (byte)2));
    }

    [Fact]
    public void PayloadEncoding_DefinesExactlyFourEncodings()
    {
        Assert.Equal(4, Enum.GetValues<PayloadEncoding>().Length);
    }

    // ---- The block serializer stamps the encoding and reads it back unchanged ----

    private static BlockHeader Header(PayloadEncoding encoding, long payloadLength) => new()
    {
        Type = BlockType.EmailContent,
        Flags = 0,
        Encoding = encoding,
        Compression = CompressionAlgorithm.None,
        KeyEpoch = 0,
        BlockId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        PayloadLength = payloadLength,
    };

    [Theory]
    [InlineData(PayloadEncoding.Custom)]
    [InlineData(PayloadEncoding.Protobuf)]
    [InlineData(PayloadEncoding.Json)]
    [InlineData(PayloadEncoding.RawBytes)]
    public void SerializeHeader_StampsEncodingByte_AtOffset12(PayloadEncoding encoding)
    {
        var bytes = BlockSerializer.SerializeHeader(Header(encoding, 0));

        // PayloadEncoding lives at header offset 12 (spec Section 4).
        Assert.Equal((byte)encoding, bytes[12]);
    }

    [Theory]
    [InlineData(PayloadEncoding.Custom)]
    [InlineData(PayloadEncoding.Protobuf)]
    [InlineData(PayloadEncoding.Json)]
    [InlineData(PayloadEncoding.RawBytes)]
    public void HeaderRoundTrip_PreservesEncoding(PayloadEncoding encoding)
    {
        var result = BlockSerializer.DeserializeHeader(
            BlockSerializer.SerializeHeader(Header(encoding, 32)), MaxPayloadLength);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(encoding, result.Value.Encoding);
    }

    [Theory]
    [InlineData(PayloadEncoding.Custom)]
    [InlineData(PayloadEncoding.Protobuf)]
    [InlineData(PayloadEncoding.Json)]
    [InlineData(PayloadEncoding.RawBytes)]
    public void FullBlockRoundTrip_PreservesEncoding_AlongsidePayload(PayloadEncoding encoding)
    {
        byte[] payload = Enumerable.Range(0, 48).Select(i => (byte)(i * 7 + 1)).ToArray();

        var block = BlockSerializer.Serialize(Header(encoding, payload.Length), payload);
        var restored = BlockSerializer.Deserialize(block, MaxPayloadLength);

        Assert.True(restored.IsSuccess, restored.IsFailure ? restored.Error : null);
        // The reader recovers both the payload and the encoding needed to decode it.
        Assert.Equal(encoding, restored.Value.Header.Encoding);
        Assert.Equal(payload, restored.Value.Payload.ToArray());
    }

    [Fact]
    public void DistinctEncodings_ProduceDistinctHeaderBytes_AtOffset12()
    {
        var custom = BlockSerializer.SerializeHeader(Header(PayloadEncoding.Custom, 0));
        var protobuf = BlockSerializer.SerializeHeader(Header(PayloadEncoding.Protobuf, 0));
        var json = BlockSerializer.SerializeHeader(Header(PayloadEncoding.Json, 0));

        Assert.NotEqual(custom[12], protobuf[12]);
        Assert.NotEqual(protobuf[12], json[12]);
        Assert.NotEqual(custom[12], json[12]);
    }
}
