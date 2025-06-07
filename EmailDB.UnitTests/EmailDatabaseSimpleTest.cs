using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Simple test to verify EmailDatabase can be created and used
/// </summary>
public class EmailDatabaseSimpleTest : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public EmailDatabaseSimpleTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"EmailDB_Simple_{Guid.NewGuid():N}.emdb");
    }

    [Fact]
    public async Task Should_Create_EmailDatabase_Successfully()
    {
        _output.WriteLine("🧪 SIMPLE EMAILDATABASE CREATION TEST");
        _output.WriteLine("===================================");
        _output.WriteLine($"📁 Test file: {_testFile}");

        try
        {
            _output.WriteLine("\n🏗️ Creating EmailDatabase...");
            using var emailDB = new EmailDatabase(_testFile);
            _output.WriteLine("✅ EmailDatabase created successfully");

            // Test that the file was created
            var fileInfo = new FileInfo(_testFile);
            _output.WriteLine($"📊 File size: {fileInfo.Length} bytes");
            Assert.True(fileInfo.Exists, "Database file should exist");
            Assert.True(fileInfo.Length > 0, "Database file should not be empty");

            _output.WriteLine("\n✅ SIMPLE TEST COMPLETED SUCCESSFULLY");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"\n❌ TEST FAILED: {ex.Message}");
            _output.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    public void Dispose()
    {
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}