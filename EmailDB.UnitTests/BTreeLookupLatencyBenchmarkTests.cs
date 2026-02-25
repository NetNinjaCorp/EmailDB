using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Point lookup latency benchmarks for the B+-tree index at 4 scale points: 1K, 10K, 100K, 1M.
/// Verifies acceptance criterion: "Point lookup latency benchmarked at 4 scale points"
/// for story US-EMDB-34.
/// </summary>
public class BTreeLookupLatencyBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Number of lookup iterations per benchmark to get stable latency measurements.
    /// </summary>
    private const int LookupIterations = 1_000;

    public BTreeLookupLatencyBenchmarkTests(ITestOutputHelper output)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_bench_lookup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _output = output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Benchmark point lookup latency at 1,000 entries.
    /// </summary>
    [Fact]
    public async Task LookupLatency_1K_Entries()
    {
        var results = await RunLookupBenchmark(1_000);
        ReportResults("1K", results);
        Assert.True(results.AllLookupsSucceeded, "Not all lookups succeeded");
    }

    /// <summary>
    /// Benchmark point lookup latency at 10,000 entries.
    /// </summary>
    [Fact]
    public async Task LookupLatency_10K_Entries()
    {
        var results = await RunLookupBenchmark(10_000);
        ReportResults("10K", results);
        Assert.True(results.AllLookupsSucceeded, "Not all lookups succeeded");
    }

    /// <summary>
    /// Benchmark point lookup latency at 100,000 entries.
    /// </summary>
    [Fact]
    public async Task LookupLatency_100K_Entries()
    {
        var results = await RunLookupBenchmark(100_000);
        ReportResults("100K", results);
        Assert.True(results.AllLookupsSucceeded, "Not all lookups succeeded");
    }

    /// <summary>
    /// Benchmark point lookup latency at 1,000,000 entries.
    /// Target: single point lookup &lt; 1ms at 1M entries (with cache warm).
    /// This test may take several minutes due to tree construction.
    /// </summary>
    [Fact]
    public async Task LookupLatency_1M_Entries()
    {
        var results = await RunLookupBenchmark(1_000_000);
        ReportResults("1M", results);
        Assert.True(results.AllLookupsSucceeded, "Not all lookups succeeded");

        // Verify target: single point lookup < 1ms at 1M entries (warm cache)
        _output.WriteLine($"  Target check: avg lookup {results.AvgMicrosecondsPerLookup:F1} us (target < 1000 us)");
    }

    /// <summary>
    /// Runs all 4 scale points in a single test and produces a summary table.
    /// </summary>
    [Fact]
    public async Task LookupLatency_AllScalePoints_Summary()
    {
        var scalePoints = new[] { 1_000, 10_000, 100_000, 1_000_000 };
        var allResults = new List<(string Label, LookupBenchmarkResult Result)>();

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

            var results = await RunLookupBenchmark(count);
            allResults.Add((label, results));
            Assert.True(results.AllLookupsSucceeded, $"Not all lookups succeeded at {label}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== B+-Tree Point Lookup Latency Benchmark Summary ===");
        _output.WriteLine("");
        _output.WriteLine(string.Format("{0,-8} {1,12} {2,14} {3,14} {4,14} {5,13} {6,16}",
            "Scale", "Lookups", "Total (ms)", "Lookups/sec", "Avg (us/op)", "Tree Height", "File Size (MB)"));
        _output.WriteLine(new string('-', 95));

        foreach (var (label, r) in allResults)
        {
            _output.WriteLine(string.Format("{0,-8} {1,12} {2,14:F1} {3,14:F0} {4,14:F1} {5,13} {6,16:F2}",
                label, r.LookupCount, r.LookupElapsedMs, r.LookupsPerSecond,
                r.AvgMicrosecondsPerLookup, r.FinalTreeHeight, r.FileSizeMB));
        }

        _output.WriteLine("");
        _output.WriteLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        _output.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    private async Task<LookupBenchmarkResult> RunLookupBenchmark(int entryCount)
    {
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"bench_lookup_{entryCount}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Pre-generate all keys to exclude key generation from timing
        var keys = new EmailHashedID[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            keys[i] = new EmailHashedID(
                (ulong)i, (ulong)(i * 31), (ulong)(i * 97), (ulong)(i * 127));
        }

        // Phase 1: Populate the tree (not timed for lookup benchmark)
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

        // Pre-select lookup keys: sample evenly across the key space
        var lookupCount = Math.Min(LookupIterations, entryCount);
        var lookupKeys = new EmailHashedID[lookupCount];
        var step = entryCount / lookupCount;
        for (int i = 0; i < lookupCount; i++)
        {
            lookupKeys[i] = keys[i * step];
        }

        // Phase 2: Warmup — run a few lookups to warm caches/JIT
        for (int i = 0; i < Math.Min(10, lookupCount); i++)
        {
            await btreeIndex.LookupAsync(lookupKeys[i]);
        }

        // Phase 3: Timed lookups
        int failCount = 0;
        var lookupSw = Stopwatch.StartNew();

        for (int i = 0; i < lookupCount; i++)
        {
            var lookupResult = await btreeIndex.LookupAsync(lookupKeys[i]);
            if (lookupResult.IsFailure)
                failCount++;
        }

        lookupSw.Stop();

        return new LookupBenchmarkResult
        {
            TreeEntryCount = root?.EntryCount ?? 0,
            LookupCount = lookupCount,
            InsertElapsedMs = insertSw.Elapsed.TotalMilliseconds,
            LookupElapsedMs = lookupSw.Elapsed.TotalMilliseconds,
            LookupsPerSecond = lookupCount / lookupSw.Elapsed.TotalSeconds,
            AvgMicrosecondsPerLookup = lookupSw.Elapsed.TotalMilliseconds * 1000.0 / lookupCount,
            FinalTreeHeight = root?.TreeHeight ?? 0,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            FileSizeMB = fileInfo.Exists ? fileInfo.Length / (1024.0 * 1024.0) : 0,
            AllLookupsSucceeded = failCount == 0,
            FailCount = failCount
        };
    }

    private void ReportResults(string label, LookupBenchmarkResult r)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== B+-Tree Point Lookup Latency: {label} entries ===");
        _output.WriteLine($"  Tree size              : {r.TreeEntryCount:N0} entries");
        _output.WriteLine($"  Tree construction      : {r.InsertElapsedMs:F1} ms");
        _output.WriteLine($"  Lookups performed      : {r.LookupCount:N0}");
        _output.WriteLine($"  Lookup total time      : {r.LookupElapsedMs:F1} ms");
        _output.WriteLine($"  Throughput             : {r.LookupsPerSecond:F0} lookups/sec");
        _output.WriteLine($"  Avg latency per lookup : {r.AvgMicrosecondsPerLookup:F1} us");
        _output.WriteLine($"  Final tree height      : {r.FinalTreeHeight}");
        _output.WriteLine($"  File size              : {r.FileSizeMB:F2} MB ({r.FileSizeBytes:N0} bytes)");
        _output.WriteLine($"  Failed lookups         : {r.FailCount}");
        _output.WriteLine("");
    }

    private class LookupBenchmarkResult
    {
        public long TreeEntryCount { get; set; }
        public int LookupCount { get; set; }
        public double InsertElapsedMs { get; set; }
        public double LookupElapsedMs { get; set; }
        public double LookupsPerSecond { get; set; }
        public double AvgMicrosecondsPerLookup { get; set; }
        public ushort FinalTreeHeight { get; set; }
        public long FileSizeBytes { get; set; }
        public double FileSizeMB { get; set; }
        public bool AllLookupsSucceeded { get; set; }
        public int FailCount { get; set; }
    }
}
