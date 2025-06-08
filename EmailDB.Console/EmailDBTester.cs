using System.Diagnostics;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Console;

public class EmailDBTester
{
    private readonly TestConfiguration _config;
    private readonly Random _random;
    private readonly string _testPath;
    private readonly List<EmailData> _generatedEmails;
    private readonly List<SizeSnapshot> _sizeSnapshots;
    private readonly StringBuilder _output;

    public EmailDBTester(TestConfiguration config)
    {
        _config = config;
        _random = new Random(config.Seed);
        _testPath = Path.Combine(Path.GetTempPath(), $"emaildb_test_{Guid.NewGuid():N}");
        _generatedEmails = new List<EmailData>();
        _sizeSnapshots = new List<SizeSnapshot>();
        _output = new StringBuilder();
        
        Directory.CreateDirectory(_testPath);
    }

    public async Task RunAsync()
    {
        try
        {
            PrintHeader();
            
            if (_config.PerformanceMode)
            {
                await RunPerformanceTestAsync();
            }
            else
            {
                await RunSizeAnalysisAsync();
            }
            
            SaveResults();
        }
        finally
        {
            Cleanup();
        }
    }

    private void PrintHeader()
    {
        _output.AppendLine("EmailDB Storage Test");
        _output.AppendLine("===================");
        _output.AppendLine($"Configuration:");
        _output.AppendLine($"  Storage Type: {_config.StorageType}");
        _output.AppendLine($"  Email Count: {_config.EmailCount:N0}");
        _output.AppendLine($"  Block Size: {_config.BlockSizeKB} KB");
        _output.AppendLine($"  Seed: {_config.Seed}");
        _output.AppendLine($"  Operations: Add={_config.AllowAdd}, Delete={_config.AllowDelete}, Edit={_config.AllowEdit}");
        _output.AppendLine($"  Hash Chain: {_config.EnableHashChain}");
        _output.AppendLine($"  Mode: {(_config.PerformanceMode ? "Performance" : "Size Analysis")}");
        _output.AppendLine();
        
        System.Console.WriteLine(_output.ToString());
    }

    private async Task RunSizeAnalysisAsync()
    {
        _output.AppendLine("Size Analysis Mode");
        _output.AppendLine("------------------");
        
        // Generate all emails upfront
        GenerateEmails();
        
        // Create storage
        var store = await CreateStorageAsync();
        
        // Track initial size
        await TakeSizeSnapshot(0, "Initial");
        
        // Add emails
        if (_config.AllowAdd)
        {
            await AddEmailsAsync(store);
        }
        
        // Perform deletes
        if (_config.AllowDelete)
        {
            await DeleteEmailsAsync(store);
        }
        
        // Perform edits
        if (_config.AllowEdit)
        {
            await EditEmailsAsync(store);
        }
        
        // Final analysis
        await PerformFinalAnalysis(store);
        
        // Dispose
        await DisposeStorageAsync(store);
    }

    private async Task RunPerformanceTestAsync()
    {
        _output.AppendLine("Performance Test Mode");
        _output.AppendLine("--------------------");
        
        GenerateEmails();
        var store = await CreateStorageAsync();
        
        // Write performance
        var sw = Stopwatch.StartNew();
        var writeStart = sw.Elapsed;
        
        foreach (var email in _generatedEmails)
        {
            await StoreEmailAsync(store, email);
        }
        
        var writeTime = sw.Elapsed - writeStart;
        var writeThroughput = (_generatedEmails.Sum(e => e.Content.Length) / 1024.0 / 1024.0) / writeTime.TotalSeconds;
        
        _output.AppendLine($"\nWrite Performance:");
        _output.AppendLine($"  Total Time: {writeTime.TotalSeconds:F2}s");
        _output.AppendLine($"  Emails/sec: {_generatedEmails.Count / writeTime.TotalSeconds:F0}");
        _output.AppendLine($"  Throughput: {writeThroughput:F2} MB/s");
        
        // Read performance
        var readStart = sw.Elapsed;
        var readCount = Math.Min(1000, _generatedEmails.Count);
        
        for (int i = 0; i < readCount; i++)
        {
            var email = _generatedEmails[_random.Next(_generatedEmails.Count)];
            await ReadEmailAsync(store, email.Id);
        }
        
        var readTime = sw.Elapsed - readStart;
        
        _output.AppendLine($"\nRead Performance:");
        _output.AppendLine($"  Total Time: {readTime.TotalSeconds:F2}s");
        _output.AppendLine($"  Reads/sec: {readCount / readTime.TotalSeconds:F0}");
        _output.AppendLine($"  Avg Latency: {readTime.TotalMilliseconds / readCount:F2}ms");
        
        // Search performance
        if (_config.StorageType == StorageType.Hybrid)
        {
            var searchStart = sw.Elapsed;
            var searchCount = 100;
            
            for (int i = 0; i < searchCount; i++)
            {
                await SearchEmailsAsync(store, $"subject{_random.Next(100)}");
            }
            
            var searchTime = sw.Elapsed - searchStart;
            
            _output.AppendLine($"\nSearch Performance:");
            _output.AppendLine($"  Total Time: {searchTime.TotalSeconds:F2}s");
            _output.AppendLine($"  Searches/sec: {searchCount / searchTime.TotalSeconds:F0}");
        }
        
        await DisposeStorageAsync(store);
        
        System.Console.WriteLine(_output.ToString());
    }

    private void GenerateEmails()
    {
        var folders = new[] { "inbox", "sent", "drafts", "archive", "important" };
        var domains = new[] { "example.com", "test.org", "mail.net" };
        
        for (int i = 0; i < _config.EmailCount; i++)
        {
            var size = GetEmailSize();
            var folder = folders[_random.Next(folders.Length)];
            
            var email = new EmailData
            {
                Id = $"msg{i:D6}@{domains[_random.Next(domains.Length)]}",
                Folder = folder,
                Subject = $"Test Email {i} - Subject {_random.Next(100)}",
                From = $"sender{_random.Next(100)}@{domains[_random.Next(domains.Length)]}",
                To = $"recipient{_random.Next(100)}@{domains[_random.Next(domains.Length)]}",
                Date = DateTime.UtcNow.AddDays(-_random.Next(365)),
                Content = GenerateContent(size)
            };
            
            _generatedEmails.Add(email);
        }
    }

    private int GetEmailSize()
    {
        // Realistic email size distribution
        var r = _random.NextDouble();
        if (r < 0.4) return _random.Next(500, 2_000);      // 40% small
        if (r < 0.75) return _random.Next(2_000, 10_000); // 35% medium
        if (r < 0.95) return _random.Next(10_000, 50_000); // 20% large
        return _random.Next(50_000, 200_000);               // 5% very large
    }

    private byte[] GenerateContent(int size)
    {
        var content = new byte[size];
        _random.NextBytes(content);
        return content;
    }

    private async Task<object> CreateStorageAsync()
    {
        switch (_config.StorageType)
        {
            case StorageType.Traditional:
                return new RawBlockManager(Path.Combine(_testPath, "traditional.db"));
                
            case StorageType.Hybrid:
                return new HybridEmailStore(
                    Path.Combine(_testPath, "hybrid.db"),
                    Path.Combine(_testPath, "indexes"),
                    blockSizeThreshold: _config.BlockSizeKB * 1024,
                    enableHashChain: _config.EnableHashChain);
                
            case StorageType.AppendOnly:
                return new AppendOnlyBlockStore(
                    Path.Combine(_testPath, "appendonly.db"),
                    blockSizeThreshold: _config.BlockSizeKB * 1024);
                
            default:
                throw new NotSupportedException($"Storage type {_config.StorageType} not supported");
        }
    }

    private async Task AddEmailsAsync(object store)
    {
        _output.AppendLine($"\nAdding {_config.EmailCount} emails...");
        
        for (int i = 0; i < _generatedEmails.Count; i++)
        {
            await StoreEmailAsync(store, _generatedEmails[i]);
            
            if ((i + 1) % _config.StepSize == 0)
            {
                await TakeSizeSnapshot(i + 1, $"After {i + 1} adds");
            }
        }
    }

    private async Task DeleteEmailsAsync(object store)
    {
        var deleteCount = _generatedEmails.Count / 10; // Delete 10%
        _output.AppendLine($"\nDeleting {deleteCount} emails...");
        
        var toDelete = _generatedEmails
            .OrderBy(_ => _random.Next())
            .Take(deleteCount)
            .ToList();
        
        for (int i = 0; i < toDelete.Count; i++)
        {
            await DeleteEmailAsync(store, toDelete[i]);
            
            if ((i + 1) % (_config.StepSize / 10) == 0)
            {
                await TakeSizeSnapshot(_generatedEmails.Count + i + 1, $"After {i + 1} deletes");
            }
        }
    }

    private async Task EditEmailsAsync(object store)
    {
        var editCount = _generatedEmails.Count / 20; // Edit 5%
        _output.AppendLine($"\nEditing {editCount} emails...");
        
        var toEdit = _generatedEmails
            .OrderBy(_ => _random.Next())
            .Take(editCount)
            .ToList();
        
        for (int i = 0; i < toEdit.Count; i++)
        {
            var newSize = GetEmailSize();
            toEdit[i].Content = GenerateContent(newSize);
            await UpdateEmailAsync(store, toEdit[i]);
            
            if ((i + 1) % (_config.StepSize / 20) == 0)
            {
                await TakeSizeSnapshot(_generatedEmails.Count + deleteCount + i + 1, $"After {i + 1} edits");
            }
        }
    }

    private async Task StoreEmailAsync(object store, EmailData email)
    {
        switch (store)
        {
            case HybridEmailStore hybrid:
                email.StoredId = await hybrid.StoreEmailAsync(
                    email.Id, email.Folder, email.Content,
                    email.Subject, email.From, email.To, email.Body, email.Date);
                break;
                
            case AppendOnlyBlockStore appendOnly:
                email.StoredId = await appendOnly.AppendEmailAsync(email.Content);
                break;
                
            case RawBlockManager traditional:
                var block = new Block
                {
                    Type = BlockType.Segment,
                    BlockId = email.Id.GetHashCode(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Payload = email.Content
                };
                await traditional.WriteBlockAsync(block);
                break;
        }
    }

    private async Task<byte[]?> ReadEmailAsync(object store, object id)
    {
        switch (store)
        {
            case HybridEmailStore hybrid when id is EmailId emailId:
                var (content, _) = await hybrid.GetEmailAsync(emailId);
                return content;
                
            case AppendOnlyBlockStore appendOnly when id is EmailId emailId:
                return await appendOnly.ReadEmailAsync(emailId);
                
            case RawBlockManager traditional when id is string messageId:
                var result = await traditional.ReadBlockAsync(messageId.GetHashCode());
                return result.IsSuccess ? result.Value.Payload : null;
                
            default:
                return null;
        }
    }

    private async Task DeleteEmailAsync(object store, EmailData email)
    {
        if (store is HybridEmailStore hybrid && email.StoredId is EmailId id)
        {
            await hybrid.DeleteEmailAsync(id);
        }
        // Traditional and AppendOnly don't support delete
    }

    private async Task UpdateEmailAsync(object store, EmailData email)
    {
        if (store is HybridEmailStore hybrid && email.StoredId is EmailId)
        {
            // Re-store with same ID (creates new version)
            await StoreEmailAsync(store, email);
        }
        // Traditional and AppendOnly don't support in-place updates
    }

    private async Task SearchEmailsAsync(object store, string term)
    {
        if (store is HybridEmailStore hybrid)
        {
            var results = hybrid.SearchFullText(term).ToList();
        }
    }

    private async Task TakeSizeSnapshot(int operation, string description)
    {
        var sizes = await CalculateSizes();
        var snapshot = new SizeSnapshot
        {
            Operation = operation,
            Description = description,
            TotalSize = sizes.Total,
            DataSize = sizes.Data,
            IndexSize = sizes.Index,
            MetadataSize = sizes.Metadata,
            BlockCounts = sizes.BlockCounts
        };
        
        _sizeSnapshots.Add(snapshot);
        
        if (!_config.PerformanceMode)
        {
            _output.AppendLine($"{description}:");
            _output.AppendLine($"  Total: {FormatBytes(snapshot.TotalSize)}");
            _output.AppendLine($"  Data: {FormatBytes(snapshot.DataSize)} ({snapshot.DataSize * 100.0 / snapshot.TotalSize:F1}%)");
            _output.AppendLine($"  Index: {FormatBytes(snapshot.IndexSize)} ({snapshot.IndexSize * 100.0 / snapshot.TotalSize:F1}%)");
            _output.AppendLine($"  Metadata: {FormatBytes(snapshot.MetadataSize)} ({snapshot.MetadataSize * 100.0 / snapshot.TotalSize:F1}%)");
            
            System.Console.WriteLine($"\n{description}:");
            System.Console.WriteLine($"  Total: {FormatBytes(snapshot.TotalSize)}");
        }
    }

    private async Task<(long Total, long Data, long Index, long Metadata, Dictionary<string, int> BlockCounts)> CalculateSizes()
    {
        long total = 0;
        long data = 0;
        long index = 0; 
        long metadata = 0;
        var blockCounts = new Dictionary<string, int>();
        
        // Calculate based on storage type
        var files = Directory.GetFiles(_testPath, "*", SearchOption.AllDirectories);
        
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            total += info.Length;
            
            if (file.Contains("index", StringComparison.OrdinalIgnoreCase) || 
                file.Contains(".tree", StringComparison.OrdinalIgnoreCase))
            {
                index += info.Length;
            }
            else if (file.EndsWith(".db") || file.EndsWith(".blk"))
            {
                // For traditional/append-only, analyze block types
                if (_config.StorageType == StorageType.Traditional)
                {
                    await AnalyzeTraditionalBlocks(file, ref data, ref metadata, blockCounts);
                }
                else
                {
                    data += info.Length;
                }
            }
        }
        
        return (total, data, index, metadata, blockCounts);
    }

    private async Task AnalyzeTraditionalBlocks(string file, ref long data, ref long metadata, Dictionary<string, int> blockCounts)
    {
        try
        {
            using var manager = new RawBlockManager(file, isReadOnly: true);
            var blocks = await manager.ScanFile();
            
            foreach (var location in blocks.Values)
            {
                var result = await manager.ReadBlockAsync(location.BlockId);
                if (result.IsSuccess)
                {
                    var block = result.Value;
                    var typeName = block.Type.ToString();
                    
                    if (!blockCounts.ContainsKey(typeName))
                        blockCounts[typeName] = 0;
                    blockCounts[typeName]++;
                    
                    switch (block.Type)
                    {
                        case BlockType.Segment:
                        case BlockType.EmailContent:
                            data += block.Payload.Length + 64; // Include header
                            break;
                        case BlockType.Metadata:
                        case BlockType.FolderTree:
                        case BlockType.Folder:
                            metadata += block.Payload.Length + 64;
                            break;
                    }
                }
            }
        }
        catch { }
    }

    private async Task PerformFinalAnalysis(object store)
    {
        _output.AppendLine("\nFinal Analysis");
        _output.AppendLine("--------------");
        
        var final = _sizeSnapshots.Last();
        var efficiency = (_generatedEmails.Sum(e => e.Content.Length) * 100.0) / final.TotalSize;
        
        _output.AppendLine($"Storage Efficiency: {efficiency:F1}%");
        _output.AppendLine($"Overhead: {100 - efficiency:F1}%");
        
        if (final.BlockCounts.Any())
        {
            _output.AppendLine("\nBlock Type Distribution:");
            foreach (var (type, count) in final.BlockCounts.OrderByDescending(x => x.Value))
            {
                _output.AppendLine($"  {type}: {count:N0}");
            }
        }
        
        // Growth analysis
        if (_sizeSnapshots.Count > 2)
        {
            _output.AppendLine("\nGrowth Analysis:");
            var initial = _sizeSnapshots.First();
            var afterAdd = _sizeSnapshots.FirstOrDefault(s => s.Description.Contains("adds")) ?? initial;
            
            _output.AppendLine($"  Initial → After Adds: {FormatBytes(afterAdd.TotalSize - initial.TotalSize)}");
            _output.AppendLine($"  Average per email: {FormatBytes((afterAdd.TotalSize - initial.TotalSize) / _config.EmailCount)}");
        }
        
        System.Console.WriteLine(_output.ToString());
    }

    private async Task DisposeStorageAsync(object store)
    {
        switch (store)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
        
        await Task.Delay(100); // Let files close
    }

    private void SaveResults()
    {
        if (!string.IsNullOrEmpty(_config.OutputFile))
        {
            File.WriteAllText(_config.OutputFile, _output.ToString());
            System.Console.WriteLine($"\nResults saved to: {_config.OutputFile}");
        }
    }

    private void Cleanup()
    {
        try
        {
            Directory.Delete(_testPath, recursive: true);
        }
        catch { }
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
}

public class EmailData
{
    public string Id { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime Date { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public object? StoredId { get; set; }
}

public class SizeSnapshot
{
    public int Operation { get; set; }
    public string Description { get; set; } = "";
    public long TotalSize { get; set; }
    public long DataSize { get; set; }
    public long IndexSize { get; set; }
    public long MetadataSize { get; set; }
    public Dictionary<string, int> BlockCounts { get; set; } = new();
}