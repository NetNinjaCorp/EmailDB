using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.Indexing;
using EmailDB.Format.Search;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models.EmailContent;
using MimeKit;

namespace EmailDB.UnitTests;

[TestCategory("Phase3")]
public class Phase3ComponentTests : IDisposable
{
    private readonly string _testDirectory;
    private IndexManager _indexManager;
    
    public Phase3ComponentTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        
        var indexDirectory = Path.Combine(_testDirectory, "indexes");
        _indexManager = new IndexManager(indexDirectory);
    }
    
    [Fact]
    public async Task Phase3IndexManagerCreatesIndexes()
    {
        // Arrange
        var emailId = new EmailHashedID
        {
            BlockId = 1,
            LocalId = 0,
            EnvelopeHash = new byte[32],
            ContentHash = new byte[32]
        };
        
        var message = new MimeMessage();
        message.MessageId = "test@example.com";
        message.Subject = "Test Email";
        message.From.Add(new MailboxAddress("Test Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Test Recipient", "recipient@example.com"));
        message.Body = new TextPart("plain") { Text = "This is a test email body." };
        
        // Act
        var result = await _indexManager.IndexEmailAsync(emailId, message, "Inbox", 100);
        
        // Assert
        Assert.True(result.IsSuccess);
        
        // Test lookup
        var lookupResult = _indexManager.GetEmailByMessageId("test@example.com");
        Assert.True(lookupResult.IsSuccess);
        Assert.Equal("1:0", lookupResult.Value);
    }
    
    [Fact]
    public async Task Phase3IndexManagerHandlesSearchTerms()
    {
        // Arrange
        var emailId = new EmailHashedID
        {
            BlockId = 1,
            LocalId = 0,
            EnvelopeHash = new byte[32],
            ContentHash = new byte[32]
        };
        
        var message = new MimeMessage();
        message.MessageId = "search-test@example.com";
        message.Subject = "Important Meeting Tomorrow";
        message.From.Add(new MailboxAddress("John Doe", "john@example.com"));
        message.To.Add(new MailboxAddress("Jane Smith", "jane@example.com"));
        message.Body = new TextPart("plain") { Text = "Let's discuss the quarterly report." };
        
        // Act
        var result = await _indexManager.IndexEmailAsync(emailId, message, "Inbox", 100);
        
        // Assert
        Assert.True(result.IsSuccess);
        
        // Test search term lookup
        var searchResult = _indexManager.GetEmailsBySearchTerm("meeting");
        Assert.True(searchResult.IsSuccess);
        Assert.Contains("1:0", searchResult.Value);
        
        var searchResult2 = _indexManager.GetEmailsBySearchTerm("quarterly");
        Assert.True(searchResult2.IsSuccess);
        Assert.Contains("1:0", searchResult2.Value);
    }
    
    [Fact]
    public async Task Phase3IndexManagerUpdatesFolder()
    {
        // Arrange & Act
        var result = await _indexManager.UpdateFolderIndexAsync("Inbox", 42);
        
        // Assert
        Assert.True(result.IsSuccess);
    }
    
    [Fact]
    public void Phase3IndexManagerRetrievesEmailLocation()
    {
        // Arrange
        var compoundKey = "123:5";
        var location = new EmailDB.Format.Indexing.EmailLocation { BlockId = 123, LocalId = 5 };
        
        // We can't directly insert into the private index, but we can test the structure
        // This test verifies the basic functionality of location retrieval
        
        // Act & Assert
        var result = _indexManager.GetEmailLocation("nonexistent");
        Assert.False(result.IsSuccess);
    }
    
    [Fact]
    public void Phase3IndexManagerChecksEnvelopeLocation()
    {
        // Act & Assert
        var result = _indexManager.GetEnvelopeBlockLocation("nonexistent");
        Assert.False(result.IsSuccess);
        Assert.Equal("Envelope location not found", result.Error);
    }
    
    [Fact]
    public void Phase3IndexManagerChecksEmailByEnvelopeHash()
    {
        // Arrange
        var hash = new byte[32];
        for (int i = 0; i < 32; i++) hash[i] = (byte)i;
        
        // Act
        var result = _indexManager.GetEmailByEnvelopeHash(hash);
        
        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Email not found", result.Error);
    }
    
    public void Dispose()
    {
        _indexManager?.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class TestCategoryAttribute : Attribute
{
    public string Category { get; }
    
    public TestCategoryAttribute(string category)
    {
        Category = category;
    }
}