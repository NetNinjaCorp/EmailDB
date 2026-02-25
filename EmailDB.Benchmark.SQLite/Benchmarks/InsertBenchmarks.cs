using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using EmailDB.Benchmark.SQLite.Abstractions;
using EmailDB.Benchmark.SQLite.DataGeneration;
using EmailDB.Benchmark.SQLite.Stores;

namespace EmailDB.Benchmark.SQLite.Benchmarks;

[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
public class BulkInsertBenchmarks
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
        _tempDir = Path.Combine(Path.GetTempPath(), "emdb_bench", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "SQLite Bulk Insert")]
    public async Task SqliteBulkInsert()
    {
        using var store = new SqliteEmailStore(Path.Combine(_tempDir, "bench.db"));
        await store.InitializeAsync();
        await store.InsertEmailsBulkAsync(_emails);
    }

    [Benchmark(Description = "EmailDB Bulk Insert")]
    public async Task EmailDbBulkInsert()
    {
        using var store = new EmailDbStore(_tempDir);
        await store.InitializeAsync();
        await store.InsertEmailsBulkAsync(_emails);
    }
}

[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
public class SingleInsertBenchmarks
{
    private readonly EmailDataGenerator _generator = new();
    private string _tempDir = null!;

    [Params(1_000, 10_000)]
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
        _tempDir = Path.Combine(Path.GetTempPath(), "emdb_bench_single", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "SQLite Single Insert")]
    public async Task SqliteSingleInsert()
    {
        using var store = new SqliteEmailStore(Path.Combine(_tempDir, "bench.db"));
        await store.InitializeAsync();
        foreach (var email in _emails)
            await store.InsertEmailAsync(email);
    }

    [Benchmark(Description = "EmailDB Single Insert")]
    public async Task EmailDbSingleInsert()
    {
        using var store = new EmailDbStore(_tempDir);
        await store.InitializeAsync();
        foreach (var email in _emails)
            await store.InsertEmailAsync(email);
    }
}
