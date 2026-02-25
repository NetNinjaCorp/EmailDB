using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Range query throughput benchmarks for the B+-tree index at 4 scale points: 1K, 10K, 100K, 1M.
/// Uses 100-entry ranges as specified in story US-EMDB-34.
/// Verifies acceptance criterion: "Range query throughput measured"
/// </summary>
public class BTreeRangeQueryThroughputBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    private const int RangeSize = 100;
    private const int QueryIterations = 500;

    public BTreeRangeQueryThroughputBenchmarkTests(ITestOutputHelper output)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_bench_range_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _output = output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task RangeQueryThroughput_1K_Entries()
    {
        var results = await RunRangeQueryBenchmark(1_000);
        ReportResults("1K", results);
        Assert.True(results.AllQueriesSucceeded, "Not all range queries succeeded");
    }

    [Fact]
    public async Task RangeQueryThroughput_10K_Entries()
    {
        var results = await RunRangeQueryBenchmark(10_000);
        ReportResults("10K", results);
        Assert.True(results.AllQueriesSucceeded, "Not all range queries succeeded");
    }

    [Fact]
    public async Task RangeQueryThroughput_100K_Entries()
    {
        var results = await RunRangeQueryBenchmark(100_000);
        ReportResults("100K", results);
        Assert.True(results.AllQueriesSucceeded, "Not all range queries succeeded");
    }

    [Fact]
    public async Task RangeQueryThroughput_1M_Entries()
    {
        var results = await RunRangeQueryBenchmark(1_000_000);
        ReportResults("1M", results);
        Assert.True(results.AllQueriesSucceeded, "Not all range queries succeeded");
    }

    [Fact]
    public async Task RangeQueryThroughput_AllScalePoints_Summary()
    {
        var scalePoints = new[] { 1_000, 10_000, 100_000, 1_000_000 };
        var allResults = new List<(string Label, RangeQueryBenchmarkResult Result)>();

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

            var results = await RunRangeQueryBenchmark(count);
            allResults.Add((label, results));
            Assert.True(results.AllQueriesSucceeded, $"Not all range queries succeeded at {label}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== B+-Tree Range Query Throughput Benchmark Summary ===");
        _output.WriteLine($"    Range size: {RangeSize} entries per query");
        _output.WriteLine("");
        _output.WriteLine(string.Format("{0,-8} {1,12} {2,14} {3,14} {4,18} {5,13} {6,16}",
            "Scale", "Queries", "Total (ms)", "Queries/sec", "Avg (us/query)", "Tree Height", "File Size (MB)"));
        _output.WriteLine(new string('-', 99));

        foreach (var (label, r) in allResults)
        {
            _output.WriteLine(string.Format("{0,-8} {1,12} {2,14:F1} {3,14:F0} {4,18:F1} {5,13} {6,16:F2}",
                label, r.QueryCount, r.QueryElapsedMs, r.QueriesPerSecond,
                r.AvgMicrosecondsPerQuery, r.FinalTreeHeight, r.FileSizeMB));
        }

        _output.WriteLine("");
        _output.WriteLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        _output.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    private async Task<RangeQueryBenchmarkResult> RunRangeQueryBenchmark(int entryCount)
    {
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"bench_range_{entryCount}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Pre-generate all keys (sequential for predictable range queries)
        var keys = new EmailHashedID[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            keys[i] = new EmailHashedID(
                (ulong)i, (ulong)(i * 31), (ulong)(i * 97), (ulong)(i * 127));
        }

        // Phase 1: Populate the tree (not timed for range query benchmark)
        var insertSw = Stopwatch.StartNew();
        for (int i = 0; i < entryCount; i++)
        {
            var result = await btreeIndex.InsertAsync(keys[i], (long)(i * 4096), i + 1000);
            if (result.IsFailure)
                throw new InvalidOperationException($"Insert {i} failed during tree setup: {result.Error}");
        }
        insertSw.Stop();

        var root = btreeIndex.CurrentRoot;
        var fileInfo = new FileInfo(filePath);

        _output.WriteLine($"  Tree setup: {entryCount:N0} entries in {insertSw.Elapsed.TotalMilliseconds:F1} ms " +
                          $"(height={root?.TreeHeight ?? 0}, file={fileInfo.Length / (1024.0 * 1024.0):F2} MB)");

        // Pre-compute range query start keys: evenly distributed across the key space
        // Each query covers RangeSize entries starting from the chosen key
        var queryCount = Math.Min(QueryIterations, entryCount / RangeSize);
        if (queryCount < 1) queryCount = 1;

        var maxStartIndex = entryCount - RangeSize;
        if (maxStartIndex < 0) maxStartIndex = 0;
        var step = maxStartIndex > 0 ? maxStartIndex / queryCount : 1;
        if (step < 1) step = 1;

        var startKeys = new EmailHashedID[queryCount];
        var endKeys = new EmailHashedID[queryCount];
        for (int i = 0; i < queryCount; i++)
        {
            var startIdx = i * step;
            var endIdx = Math.Min(startIdx + RangeSize - 1, entryCount - 1);
            startKeys[i] = keys[startIdx];
            endKeys[i] = keys[endIdx];
        }

        // Phase 2: Warmup
        for (int i = 0; i < Math.Min(5, queryCount); i++)
        {
            await btreeIndex.RangeQueryAsync(startKeys[i], endKeys[i]);
        }

        // Phase 3: Timed range queries
        int failCount = 0;
        long totalEntriesReturned = 0;
        var querySw = Stopwatch.StartNew();

        for (int i = 0; i < queryCount; i++)
        {
            var rangeResult = await btreeIndex.RangeQueryAsync(startKeys[i], endKeys[i]);
            if (rangeResult.IsFailure)
                failCount++;
            else
                totalEntriesReturned += rangeResult.Value.Count;
        }

        querySw.Stop();

        return new RangeQueryBenchmarkResult
        {
            TreeEntryCount = root?.EntryCount ?? 0,
            QueryCount = queryCount,
            RangeSize = RangeSize,
            TotalEntriesReturned = totalEntriesReturned,
            InsertElapsedMs = insertSw.Elapsed.TotalMilliseconds,
            QueryElapsedMs = querySw.Elapsed.TotalMilliseconds,
            QueriesPerSecond = queryCount / querySw.Elapsed.TotalSeconds,
            AvgMicrosecondsPerQuery = querySw.Elapsed.TotalMilliseconds * 1000.0 / queryCount,
            AvgEntriesPerQuery = queryCount > 0 ? (double)totalEntriesReturned / queryCount : 0,
            FinalTreeHeight = root?.TreeHeight ?? 0,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            FileSizeMB = fileInfo.Exists ? fileInfo.Length / (1024.0 * 1024.0) : 0,
            AllQueriesSucceeded = failCount == 0,
            FailCount = failCount
        };
    }

    private void ReportResults(string label, RangeQueryBenchmarkResult r)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== B+-Tree Range Query Throughput: {label} entries ===");
        _output.WriteLine($"  Tree size              : {r.TreeEntryCount:N0} entries");
        _output.WriteLine($"  Tree construction      : {r.InsertElapsedMs:F1} ms");
        _output.WriteLine($"  Range size             : {r.RangeSize} entries per query");
        _output.WriteLine($"  Queries performed      : {r.QueryCount:N0}");
        _output.WriteLine($"  Total entries returned : {r.TotalEntriesReturned:N0}");
        _output.WriteLine($"  Avg entries per query  : {r.AvgEntriesPerQuery:F1}");
        _output.WriteLine($"  Query total time       : {r.QueryElapsedMs:F1} ms");
        _output.WriteLine($"  Throughput             : {r.QueriesPerSecond:F0} queries/sec");
        _output.WriteLine($"  Avg latency per query  : {r.AvgMicrosecondsPerQuery:F1} us");
        _output.WriteLine($"  Final tree height      : {r.FinalTreeHeight}");
        _output.WriteLine($"  File size              : {r.FileSizeMB:F2} MB ({r.FileSizeBytes:N0} bytes)");
        _output.WriteLine($"  Failed queries         : {r.FailCount}");
        _output.WriteLine("");
    }

    private class RangeQueryBenchmarkResult
    {
        public long TreeEntryCount { get; set; }
        public int QueryCount { get; set; }
        public int RangeSize { get; set; }
        public long TotalEntriesReturned { get; set; }
        public double InsertElapsedMs { get; set; }
        public double QueryElapsedMs { get; set; }
        public double QueriesPerSecond { get; set; }
        public double AvgMicrosecondsPerQuery { get; set; }
        public double AvgEntriesPerQuery { get; set; }
        public ushort FinalTreeHeight { get; set; }
        public long FileSizeBytes { get; set; }
        public double FileSizeMB { get; set; }
        public bool AllQueriesSucceeded { get; set; }
        public int FailCount { get; set; }
    }
}
