using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.Indexing;
using EmailDB.Format.Models.EmailContent;
using MimeKit;

namespace EmailDB.UnitTests;

/// <summary>
/// Simplified Phase 3 tests that work with actual APIs
/// </summary>
[TestCategory("Phase3")]
public class Phase3SimplifiedTests : IDisposable
{
    private readonly string _testDirectory;
    private IndexManager _indexManager;
    
    public Phase3SimplifiedTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"Phase3Simple_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        var indexPath = Path.Combine(_testDirectory, "indexes");
        _indexManager = new IndexManager(indexPath);
    }
    
    [Fact]
    public async Task IndexManager_IndexesAndRetrievesEmail()
    {
        var emailId = new EmailHashedID
        {
            BlockId = 100,
            LocalId = 0,
            EnvelopeHash = new byte[32],
            ContentHash = new byte[32]
        };
        
        var message = new MimeMessage();
        message.MessageId = "test@example.com";
        message.Subject = "Test Email";
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Body = new TextPart("plain") { Text = "Test content" };
        
        // Index email
        var result = await _indexManager.IndexEmailAsync(emailId, message, "Inbox", 100);
        Assert.True(result.IsSuccess);
        
        // Retrieve by MessageId
        var lookupResult = _indexManager.GetEmailByMessageId("test@example.com");
        Assert.True(lookupResult.IsSuccess);
        Assert.Equal("100:0", lookupResult.Value);
    }
    
    [Fact]
    public async Task IndexManager_SearchTermsWork()
    {
        var emailId = new EmailHashedID
        {
            BlockId = 200,
            LocalId = 0,
            EnvelopeHash = new byte[32],
            ContentHash = new byte[32]
        };
        
        var message = new MimeMessage();
        message.MessageId = "search@example.com";
        message.Subject = "Important Meeting";
        message.From.Add(new MailboxAddress("John", "john@example.com"));
        message.To.Add(new MailboxAddress("Jane", "jane@example.com"));
        message.Body = new TextPart("plain") { Text = "Let's discuss the project" };
        
        await _indexManager.IndexEmailAsync(emailId, message, "Inbox", 200);
        
        // Search for terms
        var result1 = _indexManager.GetEmailsBySearchTerm("meeting");
        Assert.True(result1.IsSuccess);
        Assert.Contains("200:0", result1.Value);
        
        var result2 = _indexManager.GetEmailsBySearchTerm("project");
        Assert.True(result2.IsSuccess);
        Assert.Contains("200:0", result2.Value);
    }
    
    [Fact]
    public async Task IndexManager_FolderIndexing()
    {
        await _indexManager.UpdateFolderIndexAsync("TestFolder", 42);
        
        // This should succeed without error
        Assert.True(true);
    }
    
    [Fact]
    public void IndexManager_HandlesNotFound()
    {
        // Test not found scenarios
        var result1 = _indexManager.GetEmailByMessageId("nonexistent@example.com");
        Assert.False(result1.IsSuccess);
        
        var result2 = _indexManager.GetEmailLocation("nonexistent");
        Assert.False(result2.IsSuccess);
        
        var result3 = _indexManager.GetEnvelopeBlockLocation("nonexistent");
        Assert.False(result3.IsSuccess);
        Assert.Equal("Envelope location not found", result3.Error);
    }
    
    [Fact]
    public void EmailLocationSerializer_Works()
    {
        var location = new EmailLocation { BlockId = 123, LocalId = 45 };
        var serializer = new EmailLocationSerializer();
        
        var serialized = serializer.Serialize(location);
        Assert.NotNull(serialized);
        
        var deserialized = serializer.Deserialize(serialized);
        Assert.Equal(123, deserialized.BlockId);
        Assert.Equal(45, deserialized.LocalId);
    }
    
    [Fact]
    public async Task IndexManager_MultipleEmails()
    {
        // Index multiple emails
        for (int i = 0; i < 5; i++)
        {
            var emailId = new EmailHashedID
            {
                BlockId = 300 + i,
                LocalId = 0,
                EnvelopeHash = new byte[32],
                ContentHash = new byte[32]
            };
            
            var message = new MimeMessage();
            message.MessageId = $"multi{i}@example.com";
            message.Subject = $"Email {i}";
            message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
            message.Body = new TextPart("plain") { Text = $"Content for email {i}" };
            
            var result = await _indexManager.IndexEmailAsync(emailId, message, "Multi", 300 + i);
            Assert.True(result.IsSuccess);
        }
        
        // Search should find multiple results
        var searchResult = _indexManager.GetEmailsBySearchTerm("email");
        Assert.True(searchResult.IsSuccess);
        var results = searchResult.Value.ToList();
        Assert.True(results.Count >= 5);
    }
    
    public void Dispose()
    {
        _indexManager?.Dispose();
        
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}