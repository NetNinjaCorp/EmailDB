using BenchmarkDotNet.Attributes;
using EmailDB.Benchmark.SQLite.Abstractions;
using EmailDB.Benchmark.SQLite.DataGeneration;
using EmailDB.Benchmark.SQLite.Stores;

namespace EmailDB.Benchmark.SQLite.Benchmarks;

[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
public class RetrievalBenchmarks
{
    private readonly EmailDataGenerator _generator = new();
    private string _tempDir = null!;
    private SqliteEmailStore _sqliteStore = null!;
    private EmailDbStore _emailDbStore = null!;
    private List<BenchmarkEmail> _emails = null!;
    private string _lookupId = null!;
    private const string LookupFolder = "Inbox";

    [Params(1_000, 10_000, 100_000)]
    public int PreloadCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "emdb_bench_retrieval", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _emails = _generator.GenerateForFolder(PreloadCount, LookupFolder);
        _lookupId = _emails[PreloadCount / 2].Id; // Pick a middle email for lookup

        // Pre-load SQLite
        _sqliteStore = new SqliteEmailStore(Path.Combine(_tempDir, "sqlite.db"));
        await _sqliteStore.InitializeAsync();
        await _sqliteStore.InsertEmailsBulkAsync(_emails);

        // Pre-load EmailDB
        _emailDbStore = new EmailDbStore(Path.Combine(_tempDir, "emaildb"));
        await _emailDbStore.InitializeAsync();
        await _emailDbStore.InsertEmailsBulkAsync(_emails);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sqliteStore?.Dispose();
        _emailDbStore?.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "SQLite Get-by-ID")]
    public async Task<BenchmarkEmail?> SqliteGetById()
    {
        return await _sqliteStore.GetEmailByIdAsync(_lookupId);
    }

    [Benchmark(Description = "EmailDB Get-by-ID")]
    public async Task<BenchmarkEmail?> EmailDbGetById()
    {
        return await _emailDbStore.GetEmailByIdAsync(_lookupId);
    }

    [Benchmark(Description = "SQLite Folder Listing")]
    public async Task<List<string>> SqliteFolderList()
    {
        return await _sqliteStore.GetEmailIdsInFolderAsync(LookupFolder);
    }

    [Benchmark(Description = "EmailDB Folder Listing")]
    public async Task<List<string>> EmailDbFolderList()
    {
        return await _emailDbStore.GetEmailIdsInFolderAsync(LookupFolder);
    }
}
