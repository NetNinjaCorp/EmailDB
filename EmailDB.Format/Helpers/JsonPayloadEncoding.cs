using System;
using System.Text;
using System.Text.Json;
using EmailDB.Format.Models;

namespace EmailDB.Format.Helpers;

/// <summary>
/// JSON payload encoding using System.Text.Json.
/// </summary>
public class JsonPayloadEncoding : IPayloadEncoding
{
    private readonly JsonSerializerOptions _options;

    public PayloadEncoding EncodingType => PayloadEncoding.Json;

    public JsonPayloadEncoding(JsonSerializerOptions options = null)
    {
        _options = options ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public Result<byte[]> Serialize<T>(T payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, _options);
            var bytes = Encoding.UTF8.GetBytes(json);
            return Result<byte[]>.Success(bytes);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"JSON serialization failed: {ex.Message}");
        }
    }

    public Result<T> Deserialize<T>(byte[] data)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);
            var obj = JsonSerializer.Deserialize<T>(json, _options);
            return Result<T>.Success(obj);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure($"JSON deserialization failed: {ex.Message}");
        }
    }
}