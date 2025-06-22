using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using EmailDB.Format;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests
{
    /// <summary>
    /// Comprehensive tests for Stage 2: Serialization & Encoding layer
    /// Tests all payload encoding implementations and serialization workflows
    /// </summary>
    public class Stage2SerializationTests
    {
        private readonly ITestOutputHelper _output;

        public Stage2SerializationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        #region JSON Payload Encoding Tests

        [Fact]
        public void JsonPayloadEncoding_Should_Serialize_Simple_Objects()
        {
            // Arrange
            var encoder = new JsonPayloadEncoding();
            var testObject = new { Name = "Test", Value = 42, Active = true };

            // Act
            var result = encoder.Serialize(testObject);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotEmpty(result.Value);
            
            var json = Encoding.UTF8.GetString(result.Value);
            _output.WriteLine($"Serialized JSON: {json}");
            Assert.Contains("\"name\":", json); // camelCase naming policy
            Assert.Contains("\"value\":42", json);
            Assert.Contains("\"active\":true", json);
        }

        [Fact]
        public void JsonPayloadEncoding_Should_Deserialize_Simple_Objects()
        {
            // Arrange
            var encoder = new JsonPayloadEncoding();
            var json = """{"name":"Test","value":42,"active":true}""";
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            // Act
            var result = encoder.Deserialize<Dictionary<string, object>>(jsonBytes);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            
            var deserialized = result.Value;
            Assert.Equal("Test", deserialized["name"].ToString());
            Assert.Equal(JsonValueKind.Number, ((JsonElement)deserialized["value"]).ValueKind);
            Assert.Equal(JsonValueKind.True, ((JsonElement)deserialized["active"]).ValueKind);
        }

        [Fact]
        public void JsonPayloadEncoding_Should_Handle_Complex_Objects()
        {
            // Arrange
            var encoder = new JsonPayloadEncoding();
            var folderContent = new FolderContent
            {
                FolderId = 123,
                Name = "Test Folder",
                Version = 2,
                ParentFolderId = 456,
                LastModified = DateTime.UtcNow
            };

            // Act - Serialize
            var serializeResult = encoder.Serialize(folderContent);
            Assert.True(serializeResult.IsSuccess);

            // Act - Deserialize
            var deserializeResult = encoder.Deserialize<FolderContent>(serializeResult.Value);
            Assert.True(deserializeResult.IsSuccess);

            // Assert
            var deserialized = deserializeResult.Value;
            Assert.Equal(folderContent.FolderId, deserialized.FolderId);
            Assert.Equal(folderContent.Name, deserialized.Name);
            Assert.Equal(folderContent.Version, deserialized.Version);
            Assert.Equal(folderContent.ParentFolderId, deserialized.ParentFolderId);
        }

        [Fact]
        public void JsonPayloadEncoding_Should_Handle_Null_And_Empty()
        {
            var encoder = new JsonPayloadEncoding();

            // Test null
            var nullResult = encoder.Serialize<object>(null);
            Assert.True(nullResult.IsSuccess);
            
            // Test empty string
            var emptyResult = encoder.Serialize("");
            Assert.True(emptyResult.IsSuccess);
            
            var deserializedEmpty = encoder.Deserialize<string>(emptyResult.Value);
            Assert.True(deserializedEmpty.IsSuccess);
            Assert.Equal("", deserializedEmpty.Value);
        }

        [Fact]
        public void JsonPayloadEncoding_Should_Return_Correct_EncodingType()
        {
            var encoder = new JsonPayloadEncoding();
            Assert.Equal(PayloadEncoding.Json, encoder.EncodingType);
        }

        #endregion

        #region Protobuf Payload Encoding Tests

        [Fact]
        public void ProtobufPayloadEncoding_Should_Return_Correct_EncodingType()
        {
            var encoder = new ProtobufPayloadEncoding();
            Assert.Equal(PayloadEncoding.Protobuf, encoder.EncodingType);
        }

        [Fact]
        public void ProtobufPayloadEncoding_Should_Handle_Simple_Types()
        {
            var encoder = new ProtobufPayloadEncoding();

            // Test with string
            var stringResult = encoder.Serialize("test string");
            Assert.True(stringResult.IsSuccess);
            Assert.NotEmpty(stringResult.Value);

            var deserializedString = encoder.Deserialize<string>(stringResult.Value);
            Assert.True(deserializedString.IsSuccess);
            Assert.Equal("test string", deserializedString.Value);
        }

        [Fact] 
        public void ProtobufPayloadEncoding_Should_Handle_Byte_Arrays()
        {
            var encoder = new ProtobufPayloadEncoding();
            var testBytes = new byte[] { 1, 2, 3, 4, 5 };

            var result = encoder.Serialize(testBytes);
            Assert.True(result.IsSuccess);

            var deserialized = encoder.Deserialize<byte[]>(result.Value);
            Assert.True(deserialized.IsSuccess);
            Assert.Equal(testBytes, deserialized.Value);
        }

        #endregion

        #region RawBytes Payload Encoding Tests

        [Fact]
        public void RawBytesPayloadEncoding_Should_Return_Correct_EncodingType()
        {
            var encoder = new RawBytesPayloadEncoding();
            Assert.Equal(PayloadEncoding.RawBytes, encoder.EncodingType);
        }

        [Fact]
        public void RawBytesPayloadEncoding_Should_Handle_Byte_Arrays()
        {
            // Arrange
            var encoder = new RawBytesPayloadEncoding();
            var testBytes = new byte[] { 0xFF, 0x00, 0xAB, 0xCD, 0xEF };

            // Act - Serialize
            var serializeResult = encoder.Serialize(testBytes);
            Assert.True(serializeResult.IsSuccess);
            Assert.Equal(testBytes, serializeResult.Value);

            // Act - Deserialize
            var deserializeResult = encoder.Deserialize<byte[]>(serializeResult.Value);
            Assert.True(deserializeResult.IsSuccess);
            Assert.Equal(testBytes, deserializeResult.Value);
        }

        [Fact]
        public void RawBytesPayloadEncoding_Should_Reject_Non_Byte_Arrays()
        {
            var encoder = new RawBytesPayloadEncoding();

            // Test serialize non-byte array
            var serializeResult = encoder.Serialize("not a byte array");
            Assert.False(serializeResult.IsSuccess);
            Assert.Contains("can only serialize byte arrays", serializeResult.Error);

            // Test deserialize to non-byte array
            var testBytes = new byte[] { 1, 2, 3 };
            var deserializeResult = encoder.Deserialize<string>(testBytes);
            Assert.False(deserializeResult.IsSuccess);
            Assert.Contains("can only deserialize to byte arrays", deserializeResult.Error);
        }

        [Fact]
        public void RawBytesPayloadEncoding_Should_Handle_Empty_Arrays()
        {
            var encoder = new RawBytesPayloadEncoding();
            var emptyBytes = Array.Empty<byte>();

            var result = encoder.Serialize(emptyBytes);
            Assert.True(result.IsSuccess);
            Assert.Empty(result.Value);

            var deserialized = encoder.Deserialize<byte[]>(result.Value);
            Assert.True(deserialized.IsSuccess);
            Assert.Empty(deserialized.Value);
        }

        #endregion

        #region DefaultBlockContentSerializer Tests

        [Fact]
        public void DefaultBlockContentSerializer_Should_Support_All_Encodings()
        {
            var serializer = new DefaultBlockContentSerializer();
            var testObject = new { Message = "Test", Count = 5 };

            // Test JSON
            var jsonResult = serializer.Serialize(testObject, PayloadEncoding.Json);
            Assert.True(jsonResult.IsSuccess);

            // Test Protobuf
            var protobufResult = serializer.Serialize(testObject, PayloadEncoding.Protobuf);
            Assert.True(protobufResult.IsSuccess);

            // Test RawBytes with byte array
            var testBytes = new byte[] { 1, 2, 3 };
            var rawResult = serializer.Serialize(testBytes, PayloadEncoding.RawBytes);
            Assert.True(rawResult.IsSuccess);
        }

        [Fact]
        public void DefaultBlockContentSerializer_Should_Roundtrip_With_All_Encodings()
        {
            var serializer = new DefaultBlockContentSerializer();
            var folderContent = new FolderContent
            {
                FolderId = 999,
                Name = "Serialization Test",
                Version = 3,
                LastModified = DateTime.UtcNow
            };

            // Test JSON roundtrip
            var jsonSerialized = serializer.Serialize(folderContent, PayloadEncoding.Json);
            Assert.True(jsonSerialized.IsSuccess);
            
            var jsonDeserialized = serializer.Deserialize<FolderContent>(jsonSerialized.Value, PayloadEncoding.Json);
            Assert.True(jsonDeserialized.IsSuccess);
            Assert.Equal(folderContent.FolderId, jsonDeserialized.Value.FolderId);
            Assert.Equal(folderContent.Name, jsonDeserialized.Value.Name);

            // Test Protobuf roundtrip
            var protobufSerialized = serializer.Serialize(folderContent, PayloadEncoding.Protobuf);
            Assert.True(protobufSerialized.IsSuccess);
            
            var protobufDeserialized = serializer.Deserialize<FolderContent>(protobufSerialized.Value, PayloadEncoding.Protobuf);
            Assert.True(protobufDeserialized.IsSuccess);
            Assert.Equal(folderContent.FolderId, protobufDeserialized.Value.FolderId);
            Assert.Equal(folderContent.Name, protobufDeserialized.Value.Name);
        }

        [Fact]
        public void DefaultBlockContentSerializer_Should_Use_JSON_As_Default()
        {
            var serializer = new DefaultBlockContentSerializer();
            var testObject = new { Data = "test" };

            // Use legacy method (should default to JSON)
            var serialized = serializer.Serialize(testObject);
            var deserialized = serializer.Deserialize<Dictionary<string, object>>(serialized);

            Assert.NotNull(deserialized);
            // Should be successfully deserializable as JSON
            Assert.Contains("data", deserialized); // camelCase from JSON
        }

        [Fact]
        public void DefaultBlockContentSerializer_Should_Handle_Unsupported_Encoding()
        {
            var serializer = new DefaultBlockContentSerializer();
            var testObject = new { Data = "test" };

            // Test unsupported encoding
            var result = serializer.Serialize(testObject, PayloadEncoding.CapnProto);
            Assert.False(result.IsSuccess);
            Assert.Contains("Unsupported encoding: CapnProto", result.Error);

            // Test deserialization with unsupported encoding
            var deserializeResult = serializer.Deserialize<object>(new byte[] { 1, 2, 3 }, PayloadEncoding.CapnProto);
            Assert.False(deserializeResult.IsSuccess);
            Assert.Contains("Unsupported encoding: CapnProto", deserializeResult.Error);
        }

        #endregion

        #region Serialization Performance Tests

        [Fact]
        public void Serialization_Performance_Comparison()
        {
            var serializer = new DefaultBlockContentSerializer();
            var testData = CreateLargeFolderContent();

            // Warm up
            serializer.Serialize(testData, PayloadEncoding.Json);
            serializer.Serialize(testData, PayloadEncoding.Protobuf);

            // Measure JSON
            var jsonStart = DateTime.UtcNow;
            var jsonResult = serializer.Serialize(testData, PayloadEncoding.Json);
            var jsonTime = DateTime.UtcNow - jsonStart;
            Assert.True(jsonResult.IsSuccess);

            // Measure Protobuf
            var protobufStart = DateTime.UtcNow;
            var protobufResult = serializer.Serialize(testData, PayloadEncoding.Protobuf);
            var protobufTime = DateTime.UtcNow - protobufStart;
            Assert.True(protobufResult.IsSuccess);

            _output.WriteLine($"JSON serialization: {jsonTime.TotalMilliseconds:F2}ms, Size: {jsonResult.Value.Length} bytes");
            _output.WriteLine($"Protobuf serialization: {protobufTime.TotalMilliseconds:F2}ms, Size: {protobufResult.Value.Length} bytes");

            // Both should complete in reasonable time
            Assert.True(jsonTime.TotalSeconds < 1);
            Assert.True(protobufTime.TotalSeconds < 1);
        }

        [Fact]
        public void Serialization_Size_Comparison()
        {
            var serializer = new DefaultBlockContentSerializer();
            var testData = CreateLargeFolderContent();

            var jsonResult = serializer.Serialize(testData, PayloadEncoding.Json);
            var protobufResult = serializer.Serialize(testData, PayloadEncoding.Protobuf);

            Assert.True(jsonResult.IsSuccess);
            Assert.True(protobufResult.IsSuccess);

            _output.WriteLine($"JSON size: {jsonResult.Value.Length} bytes");
            _output.WriteLine($"Protobuf size: {protobufResult.Value.Length} bytes");

            // Both should be non-empty
            Assert.True(jsonResult.Value.Length > 0);
            Assert.True(protobufResult.Value.Length > 0);

            // Typically protobuf should be more compact
            var compressionRatio = (double)protobufResult.Value.Length / jsonResult.Value.Length;
            _output.WriteLine($"Protobuf/JSON size ratio: {compressionRatio:F3}");
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public void Serialization_Should_Handle_Circular_References()
        {
            var serializer = new DefaultBlockContentSerializer();
            
            // Create circular reference (this will fail with System.Text.Json)
            var obj1 = new Dictionary<string, object>();
            var obj2 = new Dictionary<string, object>();
            obj1["ref"] = obj2;
            obj2["ref"] = obj1;

            var result = serializer.Serialize(obj1, PayloadEncoding.Json);
            Assert.False(result.IsSuccess);
            Assert.Contains("serialization failed", result.Error);
        }

        [Fact]
        public void Serialization_Should_Handle_Corrupted_Data()
        {
            var serializer = new DefaultBlockContentSerializer();
            var corruptedData = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC };

            // Try to deserialize corrupted JSON
            var jsonResult = serializer.Deserialize<FolderContent>(corruptedData, PayloadEncoding.Json);
            Assert.False(jsonResult.IsSuccess);
            Assert.Contains("deserialization failed", jsonResult.Error);

            // Try to deserialize corrupted Protobuf
            var protobufResult = serializer.Deserialize<FolderContent>(corruptedData, PayloadEncoding.Protobuf);
            Assert.False(protobufResult.IsSuccess);
            Assert.Contains("deserialization failed", protobufResult.Error);
        }

        #endregion

        #region Integration Tests

        [Fact]
        public void Stage2_Integration_Full_Serialization_Workflow()
        {
            // Test the complete Stage 2 serialization workflow
            var serializer = new DefaultBlockContentSerializer();
            
            // Create test data representing different block types
            var folderContent = new FolderContent { FolderId = 1, Name = "Inbox", Version = 1 };
            var metadataContent = new MetadataContent { WALOffset = 100 };
            var rawData = new byte[] { 1, 2, 3, 4, 5 };

            // Test JSON and Protobuf with FolderContent
            var folderScenarios = new (PayloadEncoding encoding, string description)[]
            {
                (PayloadEncoding.Json, "Folder with JSON"),
                (PayloadEncoding.Protobuf, "Folder with Protobuf")
            };

            foreach (var (encoding, description) in folderScenarios)
            {
                _output.WriteLine($"Testing: {description}");
                
                // Serialize
                var serializeResult = serializer.Serialize(folderContent, encoding);
                Assert.True(serializeResult.IsSuccess, $"Serialization failed for {description}: {serializeResult.Error}");
                Assert.NotEmpty(serializeResult.Value);

                // Deserialize to correct type
                var deserializeResult = serializer.Deserialize<FolderContent>(serializeResult.Value, encoding);
                Assert.True(deserializeResult.IsSuccess, $"Deserialization failed for {description}: {deserializeResult.Error}");
                Assert.NotNull(deserializeResult.Value);
                Assert.Equal(folderContent.FolderId, deserializeResult.Value.FolderId);
                Assert.Equal(folderContent.Name, deserializeResult.Value.Name);

                _output.WriteLine($"✓ {description} - {serializeResult.Value.Length} bytes");
            }

            // Test RawBytes separately
            _output.WriteLine("Testing: Raw data with RawBytes");
            var rawSerializeResult = serializer.Serialize(rawData, PayloadEncoding.RawBytes);
            Assert.True(rawSerializeResult.IsSuccess, "Raw data serialization failed");
            
            var rawDeserializeResult = serializer.Deserialize<byte[]>(rawSerializeResult.Value, PayloadEncoding.RawBytes);
            Assert.True(rawDeserializeResult.IsSuccess, "Raw data deserialization failed");
            Assert.Equal(rawData, rawDeserializeResult.Value);
            _output.WriteLine($"✓ Raw data with RawBytes - {rawSerializeResult.Value.Length} bytes");
        }

        #endregion

        #region Helper Methods

        private FolderContent CreateLargeFolderContent()
        {
            return new FolderContent
            {
                FolderId = 999999,
                Name = "Large Test Folder With A Very Long Name That Should Test Serialization Performance",
                Version = 100,
                ParentFolderId = 888888,
                LastModified = DateTime.UtcNow
            };
        }

        #endregion
    }
}