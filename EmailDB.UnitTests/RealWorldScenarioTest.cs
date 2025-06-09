using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests real-world email usage scenarios with realistic patterns.
/// </summary>
public class RealWorldScenarioTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public RealWorldScenarioTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"RealWorld_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Typical_Email_Client_Usage()
    {
        _output.WriteLine("📧 REAL-WORLD EMAIL CLIENT SCENARIO");
        _output.WriteLine("===================================\n");
        
        var dataPath = Path.Combine(_testDir, "email_client.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSizeThreshold: 512 * 1024);
        
        var results = new StringBuilder();
        results.AppendLine("Simulating typical email client usage over 30 days...\n");
        
        // User profile
        var userEmail = "user@example.com";
        var contacts = GenerateContacts(50);
        var mailingLists = new[] { "newsletter@company.com", "updates@service.com", "notifications@app.com" };
        
        // Track statistics
        var stats = new UsageStatistics();
        var allEmails = new List<(EmailId id, string messageId, DateTime date, string folder)>();
        
        // Simulate 30 days of email activity
        for (int day = 0; day < 30; day++)
        {
            var dayStart = DateTime.UtcNow.AddDays(-30 + day);
            results.AppendLine($"📅 Day {day + 1} ({dayStart:yyyy-MM-dd}):");
            
            // Morning email check - receive emails
            var morningEmails = await SimulateMorningEmailCheck(store, dayStart, userEmail, contacts, mailingLists, stats);
            allEmails.AddRange(morningEmails);
            
            // Work hours - send emails, organize folders
            var workEmails = await SimulateWorkHours(store, dayStart, userEmail, contacts, stats);
            allEmails.AddRange(workEmails);
            
            // Evening - cleanup, search, archive
            await SimulateEveningCleanup(store, dayStart, allEmails, stats);
            
            // Weekly tasks
            if (day % 7 == 6)
            {
                await SimulateWeeklyTasks(store, allEmails, stats);
                results.AppendLine("  📊 Weekly cleanup performed");
            }
            
            results.AppendLine($"  Daily summary: +{stats.DailyReceived} received, {stats.DailySent} sent\n");
            stats.ResetDaily();
        }
        
        // Final analysis
        results.AppendLine("\n📊 30-DAY USAGE SUMMARY");
        results.AppendLine("======================");
        
        var finalStats = store.GetStats();
        var storageEfficiency = (stats.TotalDataSize * 100.0) / finalStats.TotalSize;
        
        results.AppendLine($"  Total emails: {stats.TotalEmails:N0}");
        results.AppendLine($"  Sent: {stats.TotalSent:N0}");
        results.AppendLine($"  Received: {stats.TotalReceived:N0}");
        results.AppendLine($"  Archived: {stats.TotalArchived:N0}");
        results.AppendLine($"  Deleted: {stats.TotalDeleted:N0}");
        results.AppendLine($"  Searches performed: {stats.TotalSearches:N0}");
        results.AppendLine($"  Folder operations: {stats.TotalFolderOps:N0}");
        
        results.AppendLine($"\n💾 STORAGE METRICS");
        results.AppendLine($"  Data size: {FormatBytes(stats.TotalDataSize)}");
        results.AppendLine($"  Storage size: {FormatBytes(finalStats.TotalSize)}");
        results.AppendLine($"  Efficiency: {storageEfficiency:F1}%");
        results.AppendLine($"  Avg email size: {FormatBytes(stats.TotalDataSize / stats.TotalEmails)}");
        
        // Performance metrics
        results.AppendLine($"\n⚡ PERFORMANCE METRICS");
        results.AppendLine($"  Avg write time: {stats.AvgWriteTime:F1}ms");
        results.AppendLine($"  Avg read time: {stats.AvgReadTime:F1}ms");
        results.AppendLine($"  Avg search time: {stats.AvgSearchTime:F1}ms");
        
        _output.WriteLine(results.ToString());
        
        // Save detailed report
        var reportPath = Path.Combine(_testDir, "real_world_report.txt");
        await File.WriteAllTextAsync(reportPath, results.ToString());
        _output.WriteLine($"Detailed report saved to: {reportPath}");
        
        // Assertions
        Assert.True(storageEfficiency > 80, $"Storage efficiency too low: {storageEfficiency:F1}%");
        Assert.True(stats.AvgWriteTime < 50, $"Write performance too slow: {stats.AvgWriteTime:F1}ms");
        Assert.True(stats.AvgReadTime < 10, $"Read performance too slow: {stats.AvgReadTime:F1}ms");
    }

    private async Task<List<(EmailId id, string messageId, DateTime date, string folder)>> SimulateMorningEmailCheck(
        HybridEmailStore store, DateTime dayStart, string userEmail, string[] contacts, string[] mailingLists, UsageStatistics stats)
    {
        var emails = new List<(EmailId id, string messageId, DateTime date, string folder)>();
        var morningTime = dayStart.AddHours(8);
        
        // Receive 10-30 emails
        var emailCount = _random.Next(10, 31);
        
        for (int i = 0; i < emailCount; i++)
        {
            var isMailingList = _random.NextDouble() < 0.3;
            var isSpam = _random.NextDouble() < 0.1;
            
            string from, subject, folder;
            int size;
            
            if (isSpam)
            {
                from = $"spam{_random.Next(1000)}@suspicious.com";
                subject = "You've won! Click here!";
                folder = "spam";
                size = _random.Next(1000, 5000);
            }
            else if (isMailingList)
            {
                from = mailingLists[_random.Next(mailingLists.Length)];
                subject = $"Newsletter: {GetRandomSubject()}";
                folder = "newsletters";
                size = _random.Next(10000, 50000);
            }
            else
            {
                from = contacts[_random.Next(contacts.Length)];
                subject = GetRandomSubject();
                folder = "inbox";
                size = _random.Next(2000, 20000);
            }
            
            var messageId = $"{Guid.NewGuid():N}@{from.Split('@')[1]}";
            var body = GenerateEmailBody(size, from, userEmail, subject);
            
            var sw = Stopwatch.StartNew();
            var emailId = await store.StoreEmailAsync(
                messageId, folder, Encoding.UTF8.GetBytes(body),
                subject: subject, from: from, to: userEmail, body: body,
                date: morningTime.AddMinutes(_random.Next(0, 120))
            );
            sw.Stop();
            
            emails.Add((emailId, messageId, morningTime, folder));
            stats.RecordWrite(sw.ElapsedMilliseconds, size);
            stats.TotalReceived++;
            stats.DailyReceived++;
        }
        
        return emails;
    }

    private async Task<List<(EmailId id, string messageId, DateTime date, string folder)>> SimulateWorkHours(
        HybridEmailStore store, DateTime dayStart, string userEmail, string[] contacts, UsageStatistics stats)
    {
        var emails = new List<(EmailId id, string messageId, DateTime date, string folder)>();
        var workStart = dayStart.AddHours(9);
        
        // Send 5-15 emails during work
        var sendCount = _random.Next(5, 16);
        
        for (int i = 0; i < sendCount; i++)
        {
            var to = contacts[_random.Next(contacts.Length)];
            var subject = GetWorkSubject();
            var size = _random.Next(3000, 30000);
            var body = GenerateWorkEmail(size, userEmail, to, subject);
            var messageId = $"{Guid.NewGuid():N}@{userEmail.Split('@')[1]}";
            
            var sw = Stopwatch.StartNew();
            var emailId = await store.StoreEmailAsync(
                messageId, "sent", Encoding.UTF8.GetBytes(body),
                subject: subject, from: userEmail, to: to, body: body,
                date: workStart.AddHours(_random.Next(0, 8))
            );
            sw.Stop();
            
            emails.Add((emailId, messageId, workStart, "sent"));
            stats.RecordWrite(sw.ElapsedMilliseconds, size);
            stats.TotalSent++;
            stats.DailySent++;
        }
        
        // Perform searches
        var searchTerms = new[] { "meeting", "report", "urgent", "project", "deadline" };
        foreach (var term in searchTerms.Take(_random.Next(1, 4)))
        {
            var sw = Stopwatch.StartNew();
            var results = store.SearchFullText(term).Take(20).ToList();
            sw.Stop();
            
            stats.RecordSearch(sw.ElapsedMilliseconds);
        }
        
        return emails;
    }

    private async Task SimulateEveningCleanup(
        HybridEmailStore store, DateTime dayStart, List<(EmailId id, string messageId, DateTime date, string folder)> allEmails, 
        UsageStatistics stats)
    {
        // Move some emails to folders
        var inboxEmails = allEmails.Where(e => e.folder == "inbox").ToList();
        var moveCount = Math.Min(_random.Next(3, 10), inboxEmails.Count);
        
        for (int i = 0; i < moveCount; i++)
        {
            var email = inboxEmails[_random.Next(inboxEmails.Count)];
            var newFolder = _random.NextDouble() < 0.5 ? "archive" : "projects";
            
            await store.MoveEmailAsync(email.id, newFolder);
            stats.TotalFolderOps++;
            
            if (newFolder == "archive")
                stats.TotalArchived++;
        }
        
        // Delete old spam
        var spamEmails = allEmails.Where(e => e.folder == "spam" && e.date < dayStart.AddDays(-7)).ToList();
        foreach (var spam in spamEmails.Take(5))
        {
            try
            {
                await store.DeleteEmailAsync(spam.id);
                stats.TotalDeleted++;
                allEmails.Remove(spam);
            }
            catch { }
        }
        
        // Read some recent emails
        var recentEmails = allEmails.Where(e => e.date > dayStart.AddDays(-2)).ToList();
        var readCount = Math.Min(_random.Next(5, 15), recentEmails.Count);
        
        for (int i = 0; i < readCount; i++)
        {
            var email = recentEmails[_random.Next(recentEmails.Count)];
            
            var sw = Stopwatch.StartNew();
            var (data, meta) = await store.GetEmailAsync(email.id);
            sw.Stop();
            
            if (data != null)
                stats.RecordRead(sw.ElapsedMilliseconds);
        }
    }

    private async Task SimulateWeeklyTasks(
        HybridEmailStore store, List<(EmailId id, string messageId, DateTime date, string folder)> allEmails,
        UsageStatistics stats)
    {
        // Archive old emails
        var oldEmails = allEmails.Where(e => e.date < DateTime.UtcNow.AddDays(-14) && e.folder == "inbox").ToList();
        
        foreach (var email in oldEmails.Take(20))
        {
            await store.MoveEmailAsync(email.id, "archive");
            stats.TotalArchived++;
            stats.TotalFolderOps++;
        }
        
        // Clean up sent folder
        var oldSent = allEmails.Where(e => e.date < DateTime.UtcNow.AddDays(-30) && e.folder == "sent").ToList();
        
        foreach (var email in oldSent.Take(10))
        {
            try
            {
                await store.DeleteEmailAsync(email.id);
                stats.TotalDeleted++;
                allEmails.Remove(email);
            }
            catch { }
        }
    }

    private string[] GenerateContacts(int count)
    {
        var domains = new[] { "example.com", "company.com", "email.com", "work.org" };
        var contacts = new string[count];
        
        for (int i = 0; i < count; i++)
        {
            contacts[i] = $"contact{i}@{domains[_random.Next(domains.Length)]}";
        }
        
        return contacts;
    }

    private string GetRandomSubject()
    {
        var subjects = new[]
        {
            "Meeting tomorrow",
            "Project update",
            "Quick question",
            "FW: Important information",
            "RE: Your request",
            "Document for review",
            "Schedule change",
            "Reminder",
            "Follow-up",
            "Thank you"
        };
        
        return subjects[_random.Next(subjects.Length)];
    }

    private string GetWorkSubject()
    {
        var subjects = new[]
        {
            "Q3 Report Draft",
            "Meeting Minutes - Project Review",
            "Action Items from Today",
            "Budget Proposal v2",
            "Client Feedback Summary",
            "Team Schedule Update",
            "Deadline Reminder: EOD Friday",
            "Code Review Comments",
            "Performance Metrics",
            "Strategic Planning Session"
        };
        
        return subjects[_random.Next(subjects.Length)];
    }

    private string GenerateEmailBody(int targetSize, string from, string to, string subject)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"From: {from}");
        sb.AppendLine($"To: {to}");
        sb.AppendLine($"Subject: {subject}");
        sb.AppendLine($"Date: {DateTime.UtcNow:R}");
        sb.AppendLine();
        sb.AppendLine("Dear recipient,");
        sb.AppendLine();
        
        var paragraphs = new[]
        {
            "I hope this email finds you well. I wanted to reach out regarding the matter we discussed.",
            "Please find attached the documents you requested. Let me know if you need any clarification.",
            "Following up on our conversation, I've prepared the necessary information.",
            "Thank you for your time and consideration. I look forward to your response.",
            "Please let me know if you have any questions or concerns about this matter."
        };
        
        while (sb.Length < targetSize)
        {
            sb.AppendLine(paragraphs[_random.Next(paragraphs.Length)]);
            sb.AppendLine();
        }
        
        return sb.ToString().Substring(0, Math.Min(sb.Length, targetSize));
    }

    private string GenerateWorkEmail(int targetSize, string from, string to, string subject)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"From: {from}");
        sb.AppendLine($"To: {to}");
        sb.AppendLine($"Subject: {subject}");
        sb.AppendLine();
        
        var openings = new[]
        {
            "Hi team,",
            "Hello,",
            "Good morning,",
            "Hi all,"
        };
        
        var content = new[]
        {
            "As discussed in our last meeting, I've updated the project timeline.",
            "Please review the attached proposal and provide your feedback by EOD.",
            "I've completed the analysis you requested. Key findings are summarized below.",
            "Following up on the action items from yesterday's discussion.",
            "Here's the status update on our current deliverables."
        };
        
        sb.AppendLine(openings[_random.Next(openings.Length)]);
        sb.AppendLine();
        sb.AppendLine(content[_random.Next(content.Length)]);
        
        while (sb.Length < targetSize * 0.8)
        {
            sb.AppendLine();
            sb.AppendLine("• " + content[_random.Next(content.Length)]);
        }
        
        sb.AppendLine();
        sb.AppendLine("Best regards,");
        sb.AppendLine(from.Split('@')[0]);
        
        return sb.ToString();
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:F2} {sizes[order]}";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    private class UsageStatistics
    {
        public int TotalEmails { get; set; }
        public int TotalSent { get; set; }
        public int TotalReceived { get; set; }
        public int TotalArchived { get; set; }
        public int TotalDeleted { get; set; }
        public int TotalSearches { get; set; }
        public int TotalFolderOps { get; set; }
        public long TotalDataSize { get; set; }
        
        public int DailySent { get; set; }
        public int DailyReceived { get; set; }
        
        private readonly List<long> _writeTimes = new();
        private readonly List<long> _readTimes = new();
        private readonly List<long> _searchTimes = new();
        
        public double AvgWriteTime => _writeTimes.Any() ? _writeTimes.Average() : 0;
        public double AvgReadTime => _readTimes.Any() ? _readTimes.Average() : 0;
        public double AvgSearchTime => _searchTimes.Any() ? _searchTimes.Average() : 0;
        
        public void RecordWrite(long ms, int size)
        {
            _writeTimes.Add(ms);
            TotalDataSize += size;
            TotalEmails++;
        }
        
        public void RecordRead(long ms)
        {
            _readTimes.Add(ms);
        }
        
        public void RecordSearch(long ms)
        {
            _searchTimes.Add(ms);
            TotalSearches++;
        }
        
        public void ResetDaily()
        {
            DailySent = 0;
            DailyReceived = 0;
        }
    }
}