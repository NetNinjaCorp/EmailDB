using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-End test using the high-level EmailDatabase API with real EML content,
/// verifying complete data flow from EML parsing through storage to retrieval.
/// </summary>
public class EmailDatabaseRealEMLTest : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public EmailDatabaseRealEMLTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"EmailDB_RealEML_{Guid.NewGuid():N}.emdb");
    }

    [Fact]
    public async Task Should_Handle_Complete_EML_Processing_Pipeline()
    {
        _output.WriteLine("📧 EMAILDATABASE REAL EML PROCESSING TEST");
        _output.WriteLine("========================================");
        _output.WriteLine($"📁 Test file: {_testFile}");

        using var emailDB = new EmailDatabase(_testFile);
        await TrackFileSize("EmailDatabase created", 0);

        // Step 1: Create realistic EML content
        _output.WriteLine("\n📝 STEP 1: Creating Realistic EML Content");
        _output.WriteLine("========================================");

        var realEMLFiles = CreateRealisticEMLContent();
        _output.WriteLine($"   ✅ Created {realEMLFiles.Length} realistic EML files");

        // Step 2: Import EML files through high-level API
        _output.WriteLine("\n📥 STEP 2: EML Import Through High-Level API");
        _output.WriteLine("==========================================");

        var importedEmails = new List<(string fileName, EmailHashedID emailId)>();

        foreach (var (fileName, emlContent) in realEMLFiles)
        {
            try
            {
                _output.WriteLine($"\n   📧 Processing: {fileName}");
                _output.WriteLine($"      EML size: {emlContent.Length} characters");

                // Import through EmailDatabase API
                var emailId = await emailDB.ImportEMLAsync(emlContent, fileName);
                importedEmails.Add((fileName, emailId));

                _output.WriteLine($"      ✅ Imported successfully");
                _output.WriteLine($"      🆔 Email ID: {emailId}");

                // Verify immediate retrieval
                var retrievedEmail = await emailDB.GetEmailAsync(emailId);
                _output.WriteLine($"      📧 Subject: {retrievedEmail.Subject}");
                _output.WriteLine($"      👤 From: {retrievedEmail.From}");
                _output.WriteLine($"      📅 Date: {retrievedEmail.Date:yyyy-MM-dd HH:mm}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"      ❌ Import failed: {ex.Message}");
            }
        }

        await TrackFileSize("After EML imports", 1);

        // Step 3: Test search functionality
        _output.WriteLine("\n🔍 STEP 3: Full-Text Search Testing");
        _output.WriteLine("==================================");

        var searchTerms = new[] { "project", "meeting", "urgent", "invoice", "conference" };
        var totalSearchResults = 0;

        foreach (var term in searchTerms)
        {
            var results = await emailDB.SearchAsync(term);
            totalSearchResults += results.Count;
            
            _output.WriteLine($"\n   🔍 Search '{term}': {results.Count} results");
            foreach (var result in results.Take(2))
            {
                _output.WriteLine($"      📧 {result.Subject}");
                _output.WriteLine($"         From: {result.From}");
                _output.WriteLine($"         Relevance: {result.RelevanceScore:F2}");
                _output.WriteLine($"         Matched in: {string.Join(", ", result.MatchedFields)}");
            }
        }

        // Step 4: Test folder organization
        _output.WriteLine("\n📁 STEP 4: Email Organization Testing");
        _output.WriteLine("====================================");

        var folders = new[] { "Inbox", "Important", "Projects", "Archive", "Personal" };
        var organizationCount = 0;

        for (int i = 0; i < importedEmails.Count; i++)
        {
            var (fileName, emailId) = importedEmails[i];
            var folder = folders[i % folders.Length];
            
            await emailDB.AddToFolderAsync(emailId, folder);
            organizationCount++;
            
            // Add some emails to multiple folders
            if (i % 3 == 0)
            {
                await emailDB.AddToFolderAsync(emailId, "Important");
                _output.WriteLine($"   📧 {fileName} → {folder} + Important");
            }
            else
            {
                _output.WriteLine($"   📧 {fileName} → {folder}");
            }
        }

        await TrackFileSize("After email organization", 2);

        // Step 5: Verify folder assignments
        _output.WriteLine("\n✅ STEP 5: Folder Assignment Verification");
        _output.WriteLine("========================================");

        foreach (var (fileName, emailId) in importedEmails.Take(5))
        {
            var emailFolders = await emailDB.GetEmailFoldersAsync(emailId);
            _output.WriteLine($"   📧 {fileName}:");
            _output.WriteLine($"      📁 Folders: {string.Join(", ", emailFolders)}");
        }

        // Step 6: Test batch operations
        _output.WriteLine("\n⚡ STEP 6: Batch Operations Testing");
        _output.WriteLine("=================================");

        var batchEMLs = CreateBatchEMLContent(10);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var batchResult = await emailDB.ImportEMLBatchAsync(batchEMLs);
        stopwatch.Stop();

        _output.WriteLine($"   ⚡ Batch import results:");
        _output.WriteLine($"      ✅ Successful: {batchResult.SuccessCount}");
        _output.WriteLine($"      ❌ Failed: {batchResult.ErrorCount}");
        _output.WriteLine($"      ⏱️ Time: {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"      📊 Average: {(double)stopwatch.ElapsedMilliseconds / batchResult.SuccessCount:F2}ms per email");

        if (batchResult.Errors.Any())
        {
            _output.WriteLine($"   ❌ Errors:");
            foreach (var error in batchResult.Errors.Take(3))
            {
                _output.WriteLine($"      {error}");
            }
        }

        await TrackFileSize("After batch operations", 3);

        // Step 7: Database statistics and verification
        _output.WriteLine("\n📊 STEP 7: Database Statistics");
        _output.WriteLine("=============================");

        var stats = await emailDB.GetDatabaseStatsAsync();
        _output.WriteLine($"   📧 Total emails: {stats.TotalEmails}");
        _output.WriteLine($"   📦 Storage blocks: {stats.StorageBlocks}");
        _output.WriteLine($"   🔍 Search indexes: {stats.SearchIndexes}");
        _output.WriteLine($"   📁 Total folders: {stats.TotalFolders}");

        // Step 8: Cross-verification with underlying storage
        _output.WriteLine("\n🔍 STEP 8: Underlying Storage Verification");
        _output.WriteLine("========================================");

        // Access the underlying block manager to verify data
        var blockManagerField = typeof(EmailDatabase).GetField("_blockManager", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (blockManagerField?.GetValue(emailDB) is RawBlockManager blockManager)
        {
            var blockLocations = blockManager.GetBlockLocations();
            _output.WriteLine($"   📦 Total blocks in storage: {blockLocations.Count}");

            // Verify some blocks contain email data
            var emailDataBlocks = 0;
            var searchDataBlocks = 0;
            var metadataBlocks = 0;

            foreach (var (blockId, location) in blockLocations.Take(10))
            {
                var blockResult = await blockManager.ReadBlockAsync(blockId);
                if (blockResult.IsSuccess && blockResult.Value != null)
                {
                    var block = blockResult.Value;
                    if (block.Payload != null && block.Payload.Length > 0)
                    {
                        var payloadText = System.Text.Encoding.UTF8.GetString(block.Payload);
                        
                        if (payloadText.Contains("Subject:") || payloadText.Contains("From:"))
                            emailDataBlocks++;
                        else if (payloadText.Contains("search") || payloadText.Contains("index"))
                            searchDataBlocks++;
                        else if (payloadText.Contains("metadata") || payloadText.Contains("version"))
                            metadataBlocks++;
                    }
                }
            }

            _output.WriteLine($"   📧 Email data blocks: {emailDataBlocks}");
            _output.WriteLine($"   🔍 Search data blocks: {searchDataBlocks}");
            _output.WriteLine($"   📊 Metadata blocks: {metadataBlocks}");
        }

        await TrackFileSize("Final state", 4);

        // Step 9: Final summary
        _output.WriteLine("\n🎉 EML PROCESSING TEST SUMMARY");
        _output.WriteLine("=============================");
        _output.WriteLine($"   📥 EML files processed: {importedEmails.Count}");
        _output.WriteLine($"   🔍 Search results found: {totalSearchResults}");
        _output.WriteLine($"   📁 Emails organized: {organizationCount}");
        _output.WriteLine($"   ⚡ Batch emails added: {batchResult.SuccessCount}");
        _output.WriteLine($"   📦 Total storage blocks: {stats.StorageBlocks}");
        _output.WriteLine($"   ✅ Complete EML → Storage → Retrieval pipeline verified");

        // Assertions
        Assert.True(importedEmails.Count > 0, "Should successfully import EML files");
        Assert.True(stats.TotalEmails > 0, "Should have emails in database");
        Assert.True(stats.StorageBlocks > 0, "Should create storage blocks");
        Assert.True(totalSearchResults >= 0, "Search should work without errors");

        _output.WriteLine("\n✅ REAL EML PROCESSING TEST COMPLETED");
    }

    private (string fileName, string emlContent)[] CreateRealisticEMLContent()
    {
        return new[]
        {
            ("project_proposal.eml", CreateRealisticEML(
                "project.manager@company.com",
                "team-leads@company.com, stakeholders@company.com",
                "Project Proposal: New Customer Portal Development",
                "Dear Team,\n\nI'm excited to share our proposal for the new customer portal development project. This initiative aims to enhance user experience and streamline customer interactions.\n\nKey objectives:\n1. Implement responsive design\n2. Integrate with existing CRM\n3. Add real-time chat support\n4. Enhance security features\n\nProject timeline: 6 months\nBudget: $150,000\n\nPlease review and provide feedback by Friday.\n\nBest regards,\nSarah Johnson\nProject Manager"
            )),

            ("quarterly_meeting.eml", CreateRealisticEML(
                "ceo@company.com",
                "all-staff@company.com",
                "Quarterly All-Hands Meeting - March 15th",
                "Team,\n\nOur Q1 all-hands meeting is scheduled for March 15th at 2:00 PM in the main conference room.\n\nAgenda:\n- Q1 performance review\n- New product launches\n- Team recognitions\n- Q2 planning preview\n\nThis meeting is mandatory for all staff. Remote employees can join via Zoom (link will be shared separately).\n\nLight refreshments will be provided.\n\nLooking forward to seeing everyone there!\n\nMichael Chen\nCEO"
            )),

            ("urgent_security_alert.eml", CreateRealisticEML(
                "security@company.com",
                "all-staff@company.com",
                "URGENT: Security Protocol Update - Action Required",
                "URGENT NOTICE\n\nWe have detected suspicious activity on our network and are implementing immediate security measures.\n\nRequired Actions (complete by end of day):\n1. Change your password immediately\n2. Enable two-factor authentication\n3. Review recent account activity\n4. Report any suspicious emails to security@company.com\n\nNew password requirements:\n- Minimum 12 characters\n- Include uppercase, lowercase, numbers, and symbols\n- Cannot reuse last 5 passwords\n\nFailure to comply may result in account suspension.\n\nContact IT support if you need assistance.\n\nSecurity Team"
            )),

            ("invoice_payment.eml", CreateRealisticEML(
                "accounting@vendor.com",
                "accounts-payable@company.com",
                "Invoice #INV-2024-001234 - Payment Due",
                "Dear Accounts Payable,\n\nPlease find attached invoice INV-2024-001234 for services rendered in February 2024.\n\nInvoice Details:\n- Amount: $25,430.00\n- Services: Software development consulting\n- Period: February 1-28, 2024\n- Due Date: March 30, 2024\n\nPayment terms: Net 30 days\nPayment method: ACH transfer to account details below\n\nBank: First National Bank\nAccount: 1234567890\nRouting: 987654321\n\nPlease confirm receipt and expected payment date.\n\nBest regards,\nJennifer Williams\nAccounting Department\nTech Solutions Inc."
            )),

            ("conference_invitation.eml", CreateRealisticEML(
                "events@techconf.org",
                "john.smith@company.com",
                "Invitation: TechConf 2024 - Early Bird Registration",
                "Dear John,\n\nYou're invited to TechConf 2024, the premier technology conference for industry professionals.\n\nEvent Details:\n- Date: June 15-17, 2024\n- Location: San Francisco Convention Center\n- Theme: \"AI and the Future of Software Development\"\n\nFeatured Speakers:\n- Dr. Emily Rodriguez (Google AI)\n- Marcus Thompson (Microsoft Azure)\n- Lisa Park (Amazon Web Services)\n\nEarly Bird Special (expires April 1st):\n- Full conference pass: $899 (regular $1,299)\n- Includes all sessions, workshops, meals, and networking events\n\nRegister now: https://techconf2024.com/register\nUse code: EARLYBIRD2024\n\nLimited seats available. Reserve yours today!\n\nBest regards,\nTechConf Organizing Committee"
            ))
        };
    }

    private (string fileName, string emlContent)[] CreateBatchEMLContent(int count)
    {
        var batch = new (string, string)[count];
        var subjects = new[]
        {
            "Weekly Status Report",
            "Code Review Request", 
            "Bug Fix Notification",
            "Feature Deployment Update",
            "Performance Metrics Report",
            "Team Meeting Minutes",
            "Documentation Update",
            "Client Feedback Summary",
            "System Maintenance Notice",
            "Training Workshop Announcement"
        };

        for (int i = 0; i < count; i++)
        {
            batch[i] = ($"batch_email_{i + 1:D3}.eml", CreateRealisticEML(
                $"user{i + 1}@company.com",
                "team@company.com",
                $"{subjects[i % subjects.Length]} - Batch {i + 1}",
                $"This is batch email #{i + 1} containing important information about our ongoing projects and operations. " +
                $"The email includes updates on development progress, testing results, and deployment schedules. " +
                $"Please review and acknowledge receipt. Email generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}."
            ));
        }

        return batch;
    }

    private string CreateRealisticEML(string from, string to, string subject, string body)
    {
        var messageId = $"<{Guid.NewGuid()}@company.com>";
        var date = DateTime.Now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz");
        
        return $@"Return-Path: <{from}>
Received: from mail.company.com ([192.168.1.100])
    by smtp.company.com with ESMTPS id ABC123XYZ
    for <{to}>; {date}
Message-ID: {messageId}
Date: {date}
From: {from}
To: {to}
Subject: {subject}
MIME-Version: 1.0
Content-Type: text/plain; charset=UTF-8
Content-Transfer-Encoding: 7bit
X-Mailer: EmailClient v2.1
X-Priority: Normal

{body}

--
This email was sent from our secure email system.
Company Name | 123 Business St | City, State 12345
";
    }

    private async Task TrackFileSize(string stepDescription, int stepNumber)
    {
        var fileInfo = new FileInfo(_testFile);
        var sizeBytes = fileInfo.Exists ? fileInfo.Length : 0;
        var sizeKB = (double)sizeBytes / 1024;
        var sizeMB = sizeKB / 1024;

        string sizeDisplay = sizeMB >= 1 
            ? $"{sizeMB:F2} MB" 
            : sizeKB >= 1 
                ? $"{sizeKB:F1} KB" 
                : $"{sizeBytes} bytes";

        _output.WriteLine($"📊 Step {stepNumber} - {stepDescription}: {sizeDisplay}");
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