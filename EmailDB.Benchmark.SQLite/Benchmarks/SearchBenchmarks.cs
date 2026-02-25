using BenchmarkDotNet.Attributes;
using EmailDB.Benchmark.SQLite.Abstractions;
using EmailDB.Benchmark.SQLite.DataGeneration;
using EmailDB.Benchmark.SQLite.Stores;

namespace EmailDB.Benchmark.SQLite.Benchmarks;

[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
public class SearchBenchmarks
{
    private readonly EmailDataGenerator _generator = new();
    private string _tempDir = null!;
    private SqliteEmailStore _sqliteStore = null!;
    private EmailDbStore _emailDbStore = null!;
    private List<BenchmarkEmail> _emails = null!;
    private string _searchTerm = null!;

    [Params(1_000, 10_000, 100_000)]
    public int EmailCount;

    [GlobalSetup]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "emdb_bench_search", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _emails = _generator.Generate(EmailCount);

        // Inject a known searchable term into ~10% of emails
        var rng = new Random(42);
        for (int i = 0; i < _emails.Count; i++)
        {
            if (rng.NextDouble() < 0.1)
                _emails[i].Subject = "BENCHMARK_NEEDLE " + _emails[i].Subject;
        }
        _searchTerm = "BENCHMARK_NEEDLE";

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

    [Benchmark(Description = "SQLite FTS5 Search")]
    public async Task<List<string>> SqliteFtsSearch()
    {
        return await _sqliteStore.SearchAsync(_searchTerm);
    }

    [Benchmark(Description = "EmailDB Linear Search")]
    public async Task<List<string>> EmailDbLinearSearch()
    {
        return await _emailDbStore.SearchAsync(_searchTerm);
    }
}
