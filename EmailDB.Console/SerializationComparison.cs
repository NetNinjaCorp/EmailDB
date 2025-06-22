using System;
using System.IO;
using System.Text;
using System.Text.Json;
using EmailDB.Format.Models;
using ProtoBuf;

namespace EmailDB.Console;

/// <summary>
/// Demonstrates the storage efficiency difference between JSON and Protobuf serialization
/// </summary>
public static class SerializationComparison
{
    public static void ShowComparison()
    {
        System.Console.WriteLine("\nSerialization Format Comparison");
        System.Console.WriteLine("===============================\n");

        // Create sample email content
        var email = new ProtoEmailContent
        {
            MessageId = "msg123@example.com",
            Subject = "Important Project Update - Q4 2024",
            From = "john.doe@company.com",
            To = "team@company.com; manager@company.com",
            Date = DateTime.UtcNow,
            TextBody = "This is an important update about our Q4 project. We have made significant progress on the EmailDB implementation. The new storage system using Protobuf serialization is showing excellent results.",
            HtmlBody = "<html><body><h1>Project Update</h1><p>This is an important update about our Q4 project.</p></body></html>",
            Size = 4096,
            FileName = "project-update.eml"
        };

        // JSON Serialization
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false // Compact JSON
        };
        var jsonString = JsonSerializer.Serialize(email, jsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(jsonString);

        // Protobuf Serialization
        byte[] protobufBytes;
        using (var stream = new MemoryStream())
        {
            Serializer.Serialize(stream, email);
            protobufBytes = stream.ToArray();
        }

        // Display results
        System.Console.WriteLine("Sample Email Content:");
        System.Console.WriteLine($"  Subject: {email.Subject}");
        System.Console.WriteLine($"  From: {email.From}");
        System.Console.WriteLine($"  Body Length: {email.TextBody.Length} chars");
        System.Console.WriteLine();

        System.Console.WriteLine("Serialization Results:");
        System.Console.WriteLine($"  JSON size: {jsonBytes.Length:N0} bytes");
        System.Console.WriteLine($"  Protobuf size: {protobufBytes.Length:N0} bytes");
        System.Console.WriteLine($"  Reduction: {jsonBytes.Length - protobufBytes.Length:N0} bytes ({((1 - (double)protobufBytes.Length / jsonBytes.Length) * 100):F1}%)");
        System.Console.WriteLine();

        // Show actual data samples
        System.Console.WriteLine("JSON Sample (first 200 chars):");
        System.Console.WriteLine($"  {jsonString.Substring(0, Math.Min(200, jsonString.Length))}...");
        System.Console.WriteLine();

        System.Console.WriteLine("Protobuf Binary (first 50 bytes as hex):");
        System.Console.Write("  ");
        for (int i = 0; i < Math.Min(50, protobufBytes.Length); i++)
        {
            System.Console.Write($"{protobufBytes[i]:X2} ");
            if ((i + 1) % 16 == 0) System.Console.Write("\n  ");
        }
        System.Console.WriteLine("...");
        System.Console.WriteLine();

        // Test with multiple emails
        System.Console.WriteLine("Batch Storage Comparison (1000 emails):");
        var totalJsonSize = 0L;
        var totalProtobufSize = 0L;

        for (int i = 0; i < 1000; i++)
        {
            email.MessageId = $"msg{i:D6}@example.com";
            email.Subject = $"Email {i} - Subject Line";
            
            // JSON
            var json = JsonSerializer.Serialize(email, jsonOptions);
            totalJsonSize += Encoding.UTF8.GetByteCount(json);
            
            // Protobuf
            using var stream = new MemoryStream();
            Serializer.Serialize(stream, email);
            totalProtobufSize += stream.Length;
        }

        System.Console.WriteLine($"  Total JSON size: {totalJsonSize:N0} bytes ({totalJsonSize / 1024.0:F2} KB)");
        System.Console.WriteLine($"  Total Protobuf size: {totalProtobufSize:N0} bytes ({totalProtobufSize / 1024.0:F2} KB)");
        System.Console.WriteLine($"  Space saved: {totalJsonSize - totalProtobufSize:N0} bytes ({((1 - (double)totalProtobufSize / totalJsonSize) * 100):F1}%)");
        System.Console.WriteLine();

        // Performance note
        System.Console.WriteLine("Additional Benefits of Protobuf:");
        System.Console.WriteLine("  ✓ Faster serialization/deserialization");
        System.Console.WriteLine("  ✓ Strong typing with schema evolution");
        System.Console.WriteLine("  ✓ Better network transmission efficiency");
        System.Console.WriteLine("  ✓ Language-neutral format");
        System.Console.WriteLine();

        System.Console.WriteLine("When stored in EmailDB:");
        System.Console.WriteLine("  - JSON in ZoneTree: Stored as UTF-8 strings");
        System.Console.WriteLine("  - Protobuf in ZoneTree: Stored as base64 strings (adds ~33% overhead)");
        System.Console.WriteLine("  - Still more efficient than JSON even with base64 encoding");
    }
}