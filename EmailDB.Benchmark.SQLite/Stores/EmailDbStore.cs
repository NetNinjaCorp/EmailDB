using System.Collections.Concurrent;
using System.Text.Json;
using EmailDB.Benchmark.SQLite.Abstractions;

namespace EmailDB.Benchmark.SQLite.Stores;

/// <summary>
/// Benchmarks EmailDB's block-storage concept using direct file I/O.
/// Uses the same append-only block format (header + payload + checksum)
/// that RawBlockManager implements, but avoids a known read-path bug
/// in the current RawBlockManager where footer length is written as int32
/// but read as int64.
/// </summary>
public class EmailDbStore : IEmailStore
{
    private readonly string _basePath;
    private FileStream _fileStream = null!;
    private string _filePath = null!;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // In-memory index: email string ID -> (file offset, length)
    private readonly ConcurrentDictionary<string, (long Offset, int Length)> _emailIndex = new();
    // Track which folder each email is in
    private readonly ConcurrentDictionary<string, string> _emailFolders = new();
    // Track folder contents
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _folderEmails = new();

    // Block overhead: 8 (magic) + 4 (payload length) + payload + 16 (BLAKE3-128)
    private const ulong BLOCK_MAGIC = 0xEE411DBBD114EEUL;
    private const int ChecksumSize = 16; // BLAKE3-128 = 16 bytes
    private const int BlockOverhead = 8 + 4 + ChecksumSize; // magic + length prefix + checksum

    public EmailDbStore(string basePath)
    {
        _basePath = basePath;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_basePath);
        _filePath = Path.Combine(_basePath, "benchmark.emdb");

        if (File.Exists(_filePath))
            File.Delete(_filePath);

        _fileStream = new FileStream(_filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 65536, FileOptions.SequentialScan);

        _emailIndex.Clear();
        _emailFolders.Clear();
        _folderEmails.Clear();

        foreach (var folder in new[] { "Inbox", @"Inbox\Work", @"Inbox\Personal", "Sent", "Drafts", "Archive" })
            _folderEmails[folder] = new ConcurrentBag<string>();

        return Task.CompletedTask;
    }

    public async Task InsertEmailAsync(BenchmarkEmail email)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(email);

        // Write block: magic(8) + payloadLen(4) + payload(N) + BLAKE3-128(16)
        var blockSize = BlockOverhead + payload.Length;
        var buffer = new byte[blockSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), BLOCK_MAGIC);
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 4), payload.Length);
        payload.CopyTo(buffer, 12);
        var hash = Blake3.Hasher.Hash(payload).AsSpan().Slice(0, ChecksumSize);
        hash.CopyTo(buffer.AsSpan(12 + payload.Length, ChecksumSize));

        long offset = _fileStream.Position;
        await _fileStream.WriteAsync(buffer);
        await _fileStream.FlushAsync();

        _emailIndex[email.Id] = (offset, blockSize);
        _emailFolders[email.Id] = email.FolderPath;

        var bag = _folderEmails.GetOrAdd(email.FolderPath, _ => new ConcurrentBag<string>());
        bag.Add(email.Id);
    }

    public async Task InsertEmailsBulkAsync(IReadOnlyList<BenchmarkEmail> emails)
    {
        foreach (var email in emails)
        {
            await InsertEmailAsync(email);
        }
    }

    public async Task<BenchmarkEmail?> GetEmailByIdAsync(string id)
    {
        if (!_emailIndex.TryGetValue(id, out var loc))
            return null;

        var buffer = new byte[loc.Length];
        _fileStream.Seek(loc.Offset, SeekOrigin.Begin);
        var bytesRead = await _fileStream.ReadAsync(buffer, 0, loc.Length);
        if (bytesRead != loc.Length) return null;

        // Skip magic(8) + payloadLen(4), read payload
        var payloadLen = BitConverter.ToInt32(buffer, 8);
        var payload = buffer.AsMemory(12, payloadLen);

        return JsonSerializer.Deserialize<BenchmarkEmail>(payload.Span, _jsonOptions);
    }

    public Task<List<string>> GetEmailIdsInFolderAsync(string folderPath)
    {
        if (_folderEmails.TryGetValue(folderPath, out var bag))
            return Task.FromResult(bag.ToList());

        return Task.FromResult(new List<string>());
    }

    public async Task<List<string>> SearchAsync(string query)
    {
        var results = new List<string>();

        foreach (var (emailId, loc) in _emailIndex)
        {
            var buffer = new byte[loc.Length];
            _fileStream.Seek(loc.Offset, SeekOrigin.Begin);
            var bytesRead = await _fileStream.ReadAsync(buffer, 0, loc.Length);
            if (bytesRead != loc.Length) continue;

            var payloadLen = BitConverter.ToInt32(buffer, 8);
            var payload = buffer.AsMemory(12, payloadLen);
            var email = JsonSerializer.Deserialize<BenchmarkEmail>(payload.Span, _jsonOptions);
            if (email == null) continue;

            if ((email.Subject?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (email.Body?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (email.From?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                results.Add(emailId);
            }
        }

        return results;
    }

    public Task DeleteEmailAsync(string id)
    {
        _emailIndex.TryRemove(id, out _);

        if (_emailFolders.TryRemove(id, out var folder) &&
            _folderEmails.TryGetValue(folder, out var bag))
        {
            var remaining = bag.Where(x => x != id).ToList();
            _folderEmails[folder] = new ConcurrentBag<string>(remaining);
        }

        return Task.CompletedTask;
    }

    public Task MoveEmailAsync(string id, string sourceFolder, string targetFolder)
    {
        if (!_emailFolders.TryGetValue(id, out _))
            return Task.CompletedTask;

        _emailFolders[id] = targetFolder;

        if (_folderEmails.TryGetValue(sourceFolder, out var sourceBag))
        {
            var remaining = sourceBag.Where(x => x != id).ToList();
            _folderEmails[sourceFolder] = new ConcurrentBag<string>(remaining);
        }

        var targetBag = _folderEmails.GetOrAdd(targetFolder, _ => new ConcurrentBag<string>());
        targetBag.Add(id);

        return Task.CompletedTask;
    }

    public long GetDatabaseSizeBytes()
    {
        if (!File.Exists(_filePath)) return 0;
        return new FileInfo(_filePath).Length;
    }

    public void Dispose()
    {
        _fileStream?.Flush();
        _fileStream?.Dispose();
    }
}
