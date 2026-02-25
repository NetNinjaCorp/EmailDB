using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using EmailDB.Benchmark.SQLite.Abstractions;
using EmailDB.Benchmark.SQLite.DataGeneration;
using EmailDB.Benchmark.SQLite.Stores;

namespace EmailDB.Benchmark.SQLite.Benchmarks;

[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
public class StorageSizeBenchmarks
{
    private readonly EmailDataGenerator _generator = new();
    private string _tempDir = null!;

    [Params(1_000, 10_000, 100_000)]
    public int EmailCount;

    private List<BenchmarkEmail> _emails = null!;

    [GlobalSetup]
    public void Setup()
    {
        _emails = _generator.Generate(EmailCount);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "emdb_bench_storage", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "SQLite Storage Size")]
    public async Task<long> SqliteStorageSize()
    {
        using var store = new SqliteEmailStore(Path.Combine(_tempDir, "bench.db"));
        await store.InitializeAsync();
        await store.InsertEmailsBulkAsync(_emails);
        return store.GetDatabaseSizeBytes();
    }

    [Benchmark(Description = "EmailDB Storage Size")]
    public async Task<long> EmailDbStorageSize()
    {
        using var store = new EmailDbStore(_tempDir);
        await store.InitializeAsync();
        await store.InsertEmailsBulkAsync(_emails);
        return store.GetDatabaseSizeBytes();
    }
}
