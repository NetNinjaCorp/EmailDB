using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Insert throughput benchmarks for the B+-tree index at 4 scale points: 1K, 10K, 100K, 1M.
/// Verifies acceptance criterion: "Insert throughput benchmarked at 4 scale points with results documented"
/// for story US-EMDB-34.
/// </summary>
public class BTreeInsertThroughputBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public BTreeInsertThroughputBenchmarkTests(ITestOutputHelper output)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_bench_insert_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _output = output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Benchmark insert throughput at 1,000 entries.
    /// </summary>
    [Fact]
    public async Task InsertThroughput_1K_Entries()
    {
        var results = await RunInsertBenchmark(1_000);
        ReportResults("1K", results);
        Assert.True(results.AllSucceeded, "Not all inserts succeeded");
        Assert.Equal(1_000L, results.EntryCount);
    }

    /// <summary>
    /// Benchmark insert throughput at 10,000 entries.
    /// </summary>
    [Fact]
    public async Task InsertThroughput_10K_Entries()
    {
        var results = await RunInsertBenchmark(10_000);
        ReportResults("10K", results);
        Assert.True(results.AllSucceeded, "Not all inserts succeeded");
        Assert.Equal(10_000L, results.EntryCount);
    }

    /// <summary>
    /// Benchmark insert throughput at 100,000 entries.
    /// </summary>
    [Fact]
    public async Task InsertThroughput_100K_Entries()
    {
        var results = await RunInsertBenchmark(100_000);
        ReportResults("100K", results);
        Assert.True(results.AllSucceeded, "Not all inserts succeeded");
        Assert.Equal(100_000L, results.EntryCount);
    }

    /// <summary>
    /// Benchmark insert throughput at 1,000,000 entries.
    /// This test may take several minutes.
    /// </summary>
    [Fact]
    public async Task InsertThroughput_1M_Entries()
    {
        var results = await RunInsertBenchmark(1_000_000);
        ReportResults("1M", results);
        Assert.True(results.AllSucceeded, "Not all inserts succeeded");
        Assert.Equal(1_000_000L, results.EntryCount);
    }

    /// <summary>
    /// Runs all 4 scale points in a single test and produces a summary table.
    /// </summary>
    [Fact]
    public async Task InsertThroughput_AllScalePoints_Summary()
    {
        var scalePoints = new[] { 1_000, 10_000, 100_000, 1_000_000 };
        var allResults = new List<(string Label, BenchmarkResult Result)>();

        foreach (var count in scalePoints)
        {
            BlockIdGenerator.Instance.Reset();
            var label = count switch
            {
                1_000 => "1K",
                10_000 => "10K",
                100_000 => "100K",
                1_000_000 => "1M",
                _ => count.ToString()
            };

            var results = await RunInsertBenchmark(count);
            allResults.Add((label, results));
            Assert.True(results.AllSucceeded, $"Not all inserts succeeded at {label}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== B+-Tree Insert Throughput Benchmark Summary ===");
        _output.WriteLine("");
        _output.WriteLine(string.Format("{0,-8} {1,12} {2,14} {3,14} {4,13} {5,16}",
            "Scale", "Total (ms)", "Inserts/sec", "Avg (us/op)", "Tree Height", "File Size (MB)"));
        _output.WriteLine(new string('-', 81));

        foreach (var (label, r) in allResults)
        {
            _output.WriteLine(string.Format("{0,-8} {1,12:F1} {2,14:F0} {3,14:F1} {4,13} {5,16:F2}",
                label, r.ElapsedMs, r.InsertsPerSecond, r.AvgMicrosecondsPerInsert, r.FinalTreeHeight, r.FileSizeMB));
        }

        _output.WriteLine("");
        _output.WriteLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        _output.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    private async Task<BenchmarkResult> RunInsertBenchmark(int entryCount)
    {
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"bench_{entryCount}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Pre-generate all keys to exclude key generation from timing
        var keys = new EmailHashedID[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            keys[i] = new EmailHashedID(
                (ulong)i, (ulong)(i * 31), (ulong)(i * 97), (ulong)(i * 127));
        }

        int failCount = 0;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < entryCount; i++)
        {
            var result = await btreeIndex.InsertAsync(keys[i], (long)(i * 4096), i + 1000);
            if (result.IsFailure)
                failCount++;
        }

        sw.Stop();

        var root = btreeIndex.CurrentRoot;
        var fileInfo = new FileInfo(filePath);

        return new BenchmarkResult
        {
            EntryCount = root?.EntryCount ?? 0,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            InsertsPerSecond = entryCount / sw.Elapsed.TotalSeconds,
            AvgMicrosecondsPerInsert = sw.Elapsed.TotalMilliseconds * 1000.0 / entryCount,
            FinalTreeHeight = root?.TreeHeight ?? 0,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            FileSizeMB = fileInfo.Exists ? fileInfo.Length / (1024.0 * 1024.0) : 0,
            AllSucceeded = failCount == 0,
            FailCount = failCount
        };
    }

    private void ReportResults(string label, BenchmarkResult r)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== B+-Tree Insert Throughput: {label} entries ===");
        _output.WriteLine($"  Total entries inserted : {r.EntryCount:N0}");
        _output.WriteLine($"  Total time             : {r.ElapsedMs:F1} ms");
        _output.WriteLine($"  Throughput             : {r.InsertsPerSecond:F0} inserts/sec");
        _output.WriteLine($"  Avg per insert         : {r.AvgMicrosecondsPerInsert:F1} us");
        _output.WriteLine($"  Final tree height      : {r.FinalTreeHeight}");
        _output.WriteLine($"  File size              : {r.FileSizeMB:F2} MB ({r.FileSizeBytes:N0} bytes)");
        _output.WriteLine($"  Failed inserts         : {r.FailCount}");
        _output.WriteLine("");
    }

    private class BenchmarkResult
    {
        public long EntryCount { get; set; }
        public double ElapsedMs { get; set; }
        public double InsertsPerSecond { get; set; }
        public double AvgMicrosecondsPerInsert { get; set; }
        public ushort FinalTreeHeight { get; set; }
        public long FileSizeBytes { get; set; }
        public double FileSizeMB { get; set; }
        public bool AllSucceeded { get; set; }
        public int FailCount { get; set; }
    }
}
