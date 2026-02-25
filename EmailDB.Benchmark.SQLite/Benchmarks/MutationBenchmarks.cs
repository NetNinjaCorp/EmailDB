using BenchmarkDotNet.Attributes;
using EmailDB.Benchmark.SQLite.Abstractions;
using EmailDB.Benchmark.SQLite.DataGeneration;
using EmailDB.Benchmark.SQLite.Stores;

namespace EmailDB.Benchmark.SQLite.Benchmarks;

[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[CsvExporter]
public class MutationBenchmarks
{
    private readonly EmailDataGenerator _generator = new();
    private string _tempDir = null!;
    private SqliteEmailStore _sqliteStore = null!;
    private EmailDbStore _emailDbStore = null!;
    private List<BenchmarkEmail> _emails = null!;

    private const int PreloadCount = 1_000;
    private const string SourceFolder = "Inbox";
    private const string TargetFolder = "Archive";

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "emdb_bench_mutation", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _emails = _generator.GenerateForFolder(PreloadCount, SourceFolder);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh stores each iteration since deletes/moves are destructive
        var iterDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        Directory.CreateDirectory(iterDir);

        _sqliteStore = new SqliteEmailStore(Path.Combine(iterDir, "sqlite.db"));
        _sqliteStore.InitializeAsync().GetAwaiter().GetResult();
        _sqliteStore.InsertEmailsBulkAsync(_emails).GetAwaiter().GetResult();

        _emailDbStore = new EmailDbStore(Path.Combine(iterDir, "emaildb"));
        _emailDbStore.InitializeAsync().GetAwaiter().GetResult();
        _emailDbStore.InsertEmailsBulkAsync(_emails).GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _sqliteStore?.Dispose();
        _emailDbStore?.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "SQLite Delete (100 emails)")]
    public async Task SqliteDelete()
    {
        for (int i = 0; i < 100; i++)
            await _sqliteStore.DeleteEmailAsync(_emails[i].Id);
    }

    [Benchmark(Description = "EmailDB Delete (100 emails)")]
    public async Task EmailDbDelete()
    {
        for (int i = 0; i < 100; i++)
            await _emailDbStore.DeleteEmailAsync(_emails[i].Id);
    }

    [Benchmark(Description = "SQLite Move (100 emails)")]
    public async Task SqliteMove()
    {
        for (int i = PreloadCount / 2; i < PreloadCount / 2 + 100; i++)
            await _sqliteStore.MoveEmailAsync(_emails[i].Id, SourceFolder, TargetFolder);
    }

    [Benchmark(Description = "EmailDB Move (100 emails)")]
    public async Task EmailDbMove()
    {
        for (int i = PreloadCount / 2; i < PreloadCount / 2 + 100; i++)
            await _emailDbStore.MoveEmailAsync(_emails[i].Id, SourceFolder, TargetFolder);
    }
}
