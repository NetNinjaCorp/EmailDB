using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Running;
using EmailDB.Benchmark.SQLite.Abstractions;
using EmailDB.Benchmark.SQLite.Benchmarks;
using EmailDB.Benchmark.SQLite.DataGeneration;
using EmailDB.Benchmark.SQLite.Stores;

if (args.Length > 0 && args[0] == "--sanity")
{
    await RunSanityTest();
    return;
}

var config = ManualConfig.CreateEmpty()
    .AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance)
    .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
    .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
    .WithArtifactsPath(Path.Combine(Directory.GetCurrentDirectory(), "benchmark_data"))
    .AddExporter(MarkdownExporter.GitHub)
    .AddExporter(CsvExporter.Default);

if (args.Length > 0)
{
    BenchmarkSwitcher.FromAssembly(typeof(BulkInsertBenchmarks).Assembly).Run(args, config);
}
else
{
    BenchmarkRunner.Run<BulkInsertBenchmarks>(config);
    BenchmarkRunner.Run<SingleInsertBenchmarks>(config);
    BenchmarkRunner.Run<RetrievalBenchmarks>(config);
    BenchmarkRunner.Run<SearchBenchmarks>(config);
    BenchmarkRunner.Run<MutationBenchmarks>(config);
    BenchmarkRunner.Run<StorageSizeBenchmarks>(config);
}

static async Task RunSanityTest()
{
    var tempDir = Path.Combine(Path.GetTempPath(), "emdb_sanity_" + Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);

    try
    {
        var gen = new EmailDataGenerator();
        var emails = gen.GenerateForFolder(10, "Inbox");

        // --- SQLite ---
        Console.WriteLine("=== SQLite Store ===");
        using (var sqlite = new SqliteEmailStore(Path.Combine(tempDir, "test.db")))
        {
            await sqlite.InitializeAsync();
            await sqlite.InsertEmailsBulkAsync(emails);
            Console.WriteLine($"  Inserted {emails.Count} emails");

            var retrieved = await sqlite.GetEmailByIdAsync(emails[0].Id);
            Console.WriteLine($"  Get-by-ID: {retrieved?.Subject?[..Math.Min(50, retrieved.Subject.Length)]}...");

            var folderIds = await sqlite.GetEmailIdsInFolderAsync("Inbox");
            Console.WriteLine($"  Folder listing: {folderIds.Count} emails in Inbox");

            var searchResults = await sqlite.SearchAsync(emails[3].Subject.Split(' ')[0]);
            Console.WriteLine($"  Search results: {searchResults.Count}");

            await sqlite.DeleteEmailAsync(emails[0].Id);
            var afterDelete = await sqlite.GetEmailByIdAsync(emails[0].Id);
            Console.WriteLine($"  Delete verified: {afterDelete == null}");

            await sqlite.MoveEmailAsync(emails[1].Id, "Inbox", "Sent");
            var moved = await sqlite.GetEmailByIdAsync(emails[1].Id);
            Console.WriteLine($"  Move verified: folder={moved?.FolderPath}");

            Console.WriteLine($"  DB size: {sqlite.GetDatabaseSizeBytes():N0} bytes");
        }

        // --- EmailDB ---
        Console.WriteLine("\n=== EmailDB Store ===");
        using (var emdb = new EmailDbStore(Path.Combine(tempDir, "emaildb")))
        {
            await emdb.InitializeAsync();
            await emdb.InsertEmailsBulkAsync(emails);
            Console.WriteLine($"  Inserted {emails.Count} emails");

            var retrieved = await emdb.GetEmailByIdAsync(emails[0].Id);
            Console.WriteLine($"  Get-by-ID: {retrieved?.Subject?[..Math.Min(50, retrieved.Subject.Length)]}...");

            var folderIds = await emdb.GetEmailIdsInFolderAsync("Inbox");
            Console.WriteLine($"  Folder listing: {folderIds.Count} emails in Inbox");

            var searchResults = await emdb.SearchAsync(emails[3].Subject.Split(' ')[0]);
            Console.WriteLine($"  Search results: {searchResults.Count}");

            await emdb.DeleteEmailAsync(emails[0].Id);
            var afterDelete = await emdb.GetEmailByIdAsync(emails[0].Id);
            Console.WriteLine($"  Delete verified: {afterDelete == null}");

            await emdb.MoveEmailAsync(emails[1].Id, "Inbox", "Sent");
            Console.WriteLine($"  Move completed");

            Console.WriteLine($"  DB size: {emdb.GetDatabaseSizeBytes():N0} bytes");
        }

        Console.WriteLine("\n=== SANITY TEST PASSED ===");
    }
    finally
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }
}
