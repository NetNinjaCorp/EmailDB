using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-End test demonstrating hash chain functionality and archival features.
/// </summary>
public class HashChainArchiveE2ETest : IDisposable
{
    private readonly string _testFile;
    private readonly string _archiveFile;
    private readonly ITestOutputHelper _output;

    public HashChainArchiveE2ETest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"HashChainTest_{Guid.NewGuid():N}.emdb");
        _archiveFile = Path.Combine(Path.GetTempPath(), $"Archive_{Guid.NewGuid():N}.emdb");
    }

    [Fact]
    public async Task Should_Create_And_Verify_Hash_Chain()
    {
        _output.WriteLine("🔗 HASH CHAIN INTEGRITY TEST");
        _output.WriteLine("===========================");
        _output.WriteLine($"📁 Test file: {_testFile}");

        // Step 1: Create blocks with hash chain
        _output.WriteLine("\n📝 STEP 1: Creating Blocks with Hash Chain");
        _output.WriteLine("========================================");

        using var chainManager = new HashChainBlockManager(_testFile);

        var emails = new[]
        {
            new { From = "ceo@company.com", Subject = "Q1 Results", Content = "Financial report..." },
            new { From = "legal@company.com", Subject = "Contract Review", Content = "Legal document..." },
            new { From = "hr@company.com", Subject = "Policy Update", Content = "New policies..." }
        };

        var blockIds = new List<long>();

        foreach (var (email, index) in emails.Select((e, i) => (e, i)))
        {
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.AddMinutes(index).Ticks,
                BlockId = 1000 + index,
                Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(email)
            };

            var result = await chainManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
            Assert.True(result.Value.HashChainSuccess);
            
            blockIds.Add(block.BlockId);
            
            _output.WriteLine($"   ✅ Block {block.BlockId} written and chained");
            _output.WriteLine($"      Hash: {result.Value.HashChainEntry.BlockHash.Substring(0, 16)}...");
            _output.WriteLine($"      Chain: {result.Value.HashChainEntry.ChainHash.Substring(0, 16)}...");
        }

        // Step 2: Verify chain integrity
        _output.WriteLine("\n🔍 STEP 2: Verifying Chain Integrity");
        _output.WriteLine("==================================");

        using var blockManager = new RawBlockManager(_testFile, createIfNotExists: false);
        var hashChainManager = new HashChainManager(blockManager);

        var chainVerification = await hashChainManager.VerifyEntireChainAsync();
        Assert.True(chainVerification.IsSuccess);
        
        _output.WriteLine($"   ✅ Chain verification complete:");
        _output.WriteLine($"      Total blocks: {chainVerification.Value.TotalBlocks}");
        _output.WriteLine($"      Valid blocks: {chainVerification.Value.ValidBlocks}");
        _output.WriteLine($"      Chain integrity: {chainVerification.Value.ChainIntegrity}");
        
        Assert.True(chainVerification.Value.ChainIntegrity ?? false);

        // Step 3: Test email updates (append-only)
        _output.WriteLine("\n📝 STEP 3: Testing Email Updates (Append-Only)");
        _output.WriteLine("============================================");

        using var updateManager = new HashChainBlockManager(_testFile, enforceChaining: true);

        // Update the first email
        var updatedBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0x10, // Update flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 2000,
            Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                From = "ceo@company.com",
                Subject = "Q1 Results - CORRECTED",
                Content = "Updated financial report...",
                PreviousBlockId = 1000
            })
        };

        var updateResult = await updateManager.UpdateRecordAsync(1000, updatedBlock, UpdateReason.ContentCorrection);
        Assert.True(updateResult.IsSuccess);
        
        _output.WriteLine($"   ✅ Updated block 1000 → {updateResult.Value.NewBlockId}");
        _output.WriteLine($"      Update metadata block: {updateResult.Value.UpdateMetadataBlockId}");

        // Get current version
        var currentVersion = await updateManager.GetCurrentVersionAsync(1000);
        Assert.True(currentVersion.IsSuccess);
        Assert.Equal(2000, currentVersion.Value.BlockId);
        
        _output.WriteLine($"   ✅ Current version of record 1000 is now block {currentVersion.Value.BlockId}");

        // Step 4: Test deletion (logical)
        _output.WriteLine("\n🗑️ STEP 4: Testing Logical Deletion");
        _output.WriteLine("=================================");

        var deleteResult = await updateManager.DeleteRecordAsync(1001, "Legal hold expired");
        Assert.True(deleteResult.IsSuccess);
        
        _output.WriteLine($"   ✅ Block 1001 marked as deleted");

        // Verify deletion - GetCurrentVersionAsync will succeed but show deleted status
        var deletedVersion = await updateManager.ReadVerifiedBlockAsync(1001);
        Assert.True(deletedVersion.IsSuccess);
        Assert.True(deletedVersion.Value.Status.IsDeleted);

        // Step 5: Get record history
        _output.WriteLine("\n📚 STEP 5: Retrieving Record History");
        _output.WriteLine("==================================");

        var history = await updateManager.GetRecordHistoryAsync(1000);
        _output.WriteLine($"   📊 History for record 1000:");
        foreach (var version in history.Versions)
        {
            _output.WriteLine($"      Block {version.BlockId} - {version.Timestamp:yyyy-MM-dd HH:mm:ss}");
            if (version.UpdateReason.HasValue)
            {
                _output.WriteLine($"        Reason: {version.UpdateReason}");
            }
        }
        
        Assert.Equal(2, history.Versions.Count); // Original + update

        // Dispose managers
        chainManager.Dispose();
        updateManager.Dispose();
        blockManager.Dispose();

        _output.WriteLine("\n✅ HASH CHAIN INTEGRITY TEST COMPLETED");
    }

    [Fact]
    public async Task Should_Create_And_Verify_Archive()
    {
        _output.WriteLine("📦 ARCHIVE CREATION AND VERIFICATION TEST");
        _output.WriteLine("=======================================");

        // Step 1: Create an archive with hash chain
        _output.WriteLine("\n📝 STEP 1: Creating Archive with Hash Chain");
        _output.WriteLine("=========================================");

        using (var chainManager = new HashChainBlockManager(_archiveFile))
        {
            // Write archive metadata
            var metadata = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1, // Metadata at block 1 by convention
                Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ArchiveId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    Version = "1.0",
                    Description = "Legal email archive for Q1 2024",
                    Organization = "ACME Corp"
                })
            };

            await chainManager.WriteBlockAsync(metadata);
            _output.WriteLine("   ✅ Archive metadata written");

            // Write some emails
            for (int i = 0; i < 5; i++)
            {
                var email = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTime.UtcNow.AddDays(-i).Ticks,
                    BlockId = 100 + i,
                    Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        From = $"user{i}@company.com",
                        Subject = $"Important Document {i}",
                        Content = $"This is email content {i}",
                        Attachments = new[] { $"document{i}.pdf" }
                    })
                };

                await chainManager.WriteBlockAsync(email);
            }
            
            _output.WriteLine($"   ✅ Wrote 5 emails to archive");
        }

        // Step 2: Open archive in read-only mode
        _output.WriteLine("\n🔒 STEP 2: Opening Archive in Read-Only Mode");
        _output.WriteLine("===========================================");

        using var archiveManager = new ArchiveManager(_archiveFile, strictMode: true);

        // Step 3: Verify archive integrity
        _output.WriteLine("\n🔍 STEP 3: Verifying Archive Integrity");
        _output.WriteLine("====================================");

        var verificationResult = await archiveManager.VerifyArchiveAsync();
        
        _output.WriteLine($"   📊 Verification Results:");
        _output.WriteLine($"      File size: {verificationResult.FileSize:N0} bytes");
        _output.WriteLine($"      Header valid: {verificationResult.HeaderValid}");
        _output.WriteLine($"      Checksums passed: {verificationResult.ChecksumsPassed}");
        _output.WriteLine($"      Checksums failed: {verificationResult.ChecksumsFailed}");
        _output.WriteLine($"      Hash chain valid: {verificationResult.HashChainValid}");
        _output.WriteLine($"      Overall valid: {verificationResult.IsValid}");
        
        Assert.True(verificationResult.IsValid);

        // Step 4: Search archive
        _output.WriteLine("\n🔎 STEP 4: Searching Archive");
        _output.WriteLine("===========================");

        var searchCriteria = new ArchiveSearchCriteria
        {
            StartDate = DateTime.UtcNow.AddDays(-10),
            EndDate = DateTime.UtcNow
        };

        var searchResults = await archiveManager.SearchEmailsAsync(searchCriteria);
        _output.WriteLine($"   📊 Found {searchResults.Count} emails in date range");
        
        foreach (var email in searchResults.Take(3))
        {
            _output.WriteLine($"      📧 {email.Subject} from {email.Sender}");
            _output.WriteLine($"         Block: {email.BlockId}, Size: {email.Size} bytes");
        }

        // Step 5: Generate existence proof
        _output.WriteLine("\n🏛️ STEP 5: Generating Existence Proof");
        _output.WriteLine("====================================");

        var proofBlockId = searchResults.First().BlockId;
        var proof = await archiveManager.GenerateExistenceProofAsync(proofBlockId);
        
        Assert.NotNull(proof);
        _output.WriteLine($"   ✅ Generated proof for block {proof.BlockId}:");
        _output.WriteLine($"      Timestamp: {proof.Timestamp:yyyy-MM-dd HH:mm:ss}");
        _output.WriteLine($"      Block hash: {proof.BlockHash.Substring(0, 16)}...");
        _output.WriteLine($"      Chain hash: {proof.ChainHash.Substring(0, 16)}...");
        _output.WriteLine($"      Sequence: {proof.SequenceNumber}");
        _output.WriteLine($"      Merkle root: {proof.MerkleRoot.Substring(0, 16)}...");

        // Save proof as JSON
        var proofJson = proof.ToJson();
        _output.WriteLine($"\n   📄 Proof JSON (truncated):");
        _output.WriteLine($"      {proofJson.Substring(0, Math.Min(200, proofJson.Length))}...");

        // Step 6: Get archive statistics
        _output.WriteLine("\n📊 STEP 6: Archive Statistics");
        _output.WriteLine("===========================");

        var stats = await archiveManager.GetStatisticsAsync();
        _output.WriteLine($"   📈 Archive Stats:");
        _output.WriteLine($"      Total blocks: {stats.TotalBlocks}");
        _output.WriteLine($"      Email blocks: {stats.EmailBlocks}");
        _output.WriteLine($"      Metadata blocks: {stats.MetadataBlocks}");
        _output.WriteLine($"      Hash chain length: {stats.HashChainLength}");
        _output.WriteLine($"      Created: {stats.CreatedAt:yyyy-MM-dd}");

        _output.WriteLine("\n✅ ARCHIVE VERIFICATION TEST COMPLETED");
    }

    [Fact]
    public async Task Should_Detect_Tampering_In_Archive()
    {
        _output.WriteLine("🚨 TAMPER DETECTION TEST");
        _output.WriteLine("======================");

        // Create a small archive
        using (var chainManager = new HashChainBlockManager(_testFile))
        {
            for (int i = 0; i < 3; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = 100 + i,
                    Payload = Encoding.UTF8.GetBytes($"{{\"data\": \"Email {i}\"}}")
                };
                
                await chainManager.WriteBlockAsync(block);
            }
        }

        _output.WriteLine("   ✅ Created test archive with hash chain");

        // Tamper with the file
        var fileBytes = await File.ReadAllBytesAsync(_testFile);
        var tamperedBytes = new byte[fileBytes.Length];
        Array.Copy(fileBytes, tamperedBytes, fileBytes.Length);
        
        // Corrupt some data in the middle
        for (int i = 0; i < 10; i++)
        {
            tamperedBytes[fileBytes.Length / 2 + i] = 0xFF;
        }
        
        await File.WriteAllBytesAsync(_testFile, tamperedBytes);
        _output.WriteLine("   💥 Tampered with archive data");

        // Try to verify
        using var archiveManager = new ArchiveManager(_testFile, strictMode: false);
        var result = await archiveManager.VerifyArchiveAsync();
        
        _output.WriteLine($"   🔍 Verification detected tampering:");
        _output.WriteLine($"      Archive valid: {result.IsValid}");
        _output.WriteLine($"      Hash chain valid: {result.HashChainValid}");
        
        Assert.False(result.IsValid);
        Assert.False(result.HashChainValid ?? true);
        
        _output.WriteLine("\n✅ TAMPER DETECTION TEST COMPLETED");
    }

    public void Dispose()
    {
        // Clean up test files
        foreach (var file in new[] { _testFile, _archiveFile })
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best effort
                }
            }
        }
    }
}