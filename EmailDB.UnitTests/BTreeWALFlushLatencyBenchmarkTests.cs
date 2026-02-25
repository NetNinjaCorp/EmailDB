using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// WAL batch size vs flush latency benchmarks.
/// Verifies acceptance criterion: "WAL batch size vs flush latency curve produced"
/// for story US-EMDB-34.
///
/// Tests flush latency at 4 batch sizes (10, 100, 500, 1000) to produce a latency curve
/// showing how flush time scales with the number of buffered entries.
/// </summary>
public class BTreeWALFlushLatencyBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    private const int WarmupFlushes = 3;
    private const int MeasuredFlushes = 10;

    public BTreeWALFlushLatencyBenchmarkTests(ITestOutputHelper output)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_bench_wal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _output = output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task WALFlushLatency_BatchSize10()
    {
        var result = await RunFlushLatencyBenchmark(10);
        ReportResult("10", result);
        Assert.True(result.AllSucceeded, "Not all flushes succeeded");
    }

    [Fact]
    public async Task WALFlushLatency_BatchSize100()
    {
        var result = await RunFlushLatencyBenchmark(100);
        ReportResult("100", result);
        Assert.True(result.AllSucceeded, "Not all flushes succeeded");
        // Story target: batch insert (100 entries) < 10ms flush time.
        // Log whether target is met; use relaxed threshold for CI environments.
        _output.WriteLine(result.AvgFlushMs < 10.0
            ? $"  Target MET: avg {result.AvgFlushMs:F2} ms < 10 ms"
            : $"  Target MISSED: avg {result.AvgFlushMs:F2} ms >= 10 ms (environment-dependent)");
        Assert.True(result.AvgFlushMs < 50.0,
            $"Batch flush (100 entries) should complete in < 50ms (relaxed), was {result.AvgFlushMs:F2} ms");
    }

    [Fact]
    public async Task WALFlushLatency_BatchSize500()
    {
        var result = await RunFlushLatencyBenchmark(500);
        ReportResult("500", result);
        Assert.True(result.AllSucceeded, "Not all flushes succeeded");
    }

    [Fact]
    public async Task WALFlushLatency_BatchSize1000()
    {
        var result = await RunFlushLatencyBenchmark(1000);
        ReportResult("1000", result);
        Assert.True(result.AllSucceeded, "Not all flushes succeeded");
    }

    /// <summary>
    /// Produces the full WAL batch size vs flush latency curve across all 4 batch sizes.
    /// </summary>
    [Fact]
    public async Task WALFlushLatency_AllBatchSizes_Curve()
    {
        var batchSizes = new[] { 10, 100, 500, 1000 };
        var allResults = new List<(int BatchSize, FlushLatencyResult Result)>();

        foreach (var batchSize in batchSizes)
        {
            BlockIdGenerator.Instance.Reset();
            var result = await RunFlushLatencyBenchmark(batchSize);
            allResults.Add((batchSize, result));
            Assert.True(result.AllSucceeded, $"Not all flushes succeeded at batch size {batchSize}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== WAL Batch Size vs Flush Latency Curve ===");
        _output.WriteLine("");
        _output.WriteLine(string.Format("{0,-12} {1,14} {2,14} {3,14} {4,14} {5,16}",
            "Batch Size", "Avg (ms)", "Min (ms)", "Max (ms)", "P50 (ms)", "Entries/sec"));
        _output.WriteLine(new string('-', 88));

        foreach (var (batchSize, r) in allResults)
        {
            _output.WriteLine(string.Format("{0,-12} {1,14:F3} {2,14:F3} {3,14:F3} {4,14:F3} {5,16:F0}",
                batchSize, r.AvgFlushMs, r.MinFlushMs, r.MaxFlushMs, r.P50FlushMs, r.EntriesPerSecond));
        }

        _output.WriteLine("");
        _output.WriteLine("Latency curve (batch size -> avg flush ms):");
        foreach (var (batchSize, r) in allResults)
        {
            int barLen = Math.Max(1, (int)(r.AvgFlushMs / allResults.Max(x => x.Result.AvgFlushMs) * 50));
            _output.WriteLine($"  {batchSize,5} entries: {new string('#', barLen)} {r.AvgFlushMs:F3} ms");
        }

        _output.WriteLine("");

        // Check that batch=100 meets the target from the story
        var batch100 = allResults.First(x => x.BatchSize == 100).Result;
        _output.WriteLine($"Target check: Batch insert (100 entries) flush time = {batch100.AvgFlushMs:F2} ms (target: < 10 ms)");
        _output.WriteLine(batch100.AvgFlushMs < 10.0 ? "  PASS" : "  FAIL");

        _output.WriteLine("");
        _output.WriteLine($"Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        _output.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        _output.WriteLine($"Warmup flushes: {WarmupFlushes}, Measured flushes: {MeasuredFlushes}");
    }

    private async Task<FlushLatencyResult> RunFlushLatencyBenchmark(int batchSize)
    {
        BlockIdGenerator.Instance.Reset();

        var filePath = Path.Combine(_tempDir, $"wal_bench_{batchSize}_{Guid.NewGuid():N}.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // WAL region offset placed after a generous space for the btree to grow.
        // Each flush inserts batchSize entries into the btree, so we need space.
        long walOffset = 64 * 1024 * 1024; // 64 MB offset for WAL region

        using var walManager = new BTreeWALManager(
            rawBlockManager, btreeIndex,
            walPayloadFileOffset: walOffset,
            autoFlushThreshold: batchSize + 1, // prevent auto-flush during insert phase
            autoFlushEnabled: false);

        int keyCounter = 0;
        bool allSucceeded = true;

        // Warmup phase: insert + flush to warm JIT and caches
        for (int round = 0; round < WarmupFlushes; round++)
        {
            for (int i = 0; i < batchSize; i++)
            {
                var key = new EmailHashedID(
                    (ulong)keyCounter, (ulong)(keyCounter * 31),
                    (ulong)(keyCounter * 97), (ulong)(keyCounter * 127));
                await walManager.InsertAsync(key, (long)(keyCounter * 4096), keyCounter + 1000);
                keyCounter++;
            }

            await walManager.FlushAsync();
        }

        // Measurement phase
        var flushTimesMs = new double[MeasuredFlushes];

        for (int round = 0; round < MeasuredFlushes; round++)
        {
            // Fill WAL buffer with batchSize entries
            for (int i = 0; i < batchSize; i++)
            {
                var key = new EmailHashedID(
                    (ulong)keyCounter, (ulong)(keyCounter * 31),
                    (ulong)(keyCounter * 97), (ulong)(keyCounter * 127));
                await walManager.InsertAsync(key, (long)(keyCounter * 4096), keyCounter + 1000);
                keyCounter++;
            }

            Assert.Equal(batchSize, walManager.BufferCount);

            // Time the flush
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var sw = Stopwatch.StartNew();
            await walManager.FlushAsync();
            sw.Stop();

            flushTimesMs[round] = sw.Elapsed.TotalMilliseconds;

            if (walManager.BufferCount != 0)
                allSucceeded = false;
        }

        Array.Sort(flushTimesMs);
        double avg = flushTimesMs.Average();
        double min = flushTimesMs[0];
        double max = flushTimesMs[^1];
        double p50 = flushTimesMs[MeasuredFlushes / 2];

        var fileInfo = new FileInfo(filePath);

        return new FlushLatencyResult
        {
            BatchSize = batchSize,
            AvgFlushMs = avg,
            MinFlushMs = min,
            MaxFlushMs = max,
            P50FlushMs = p50,
            AllFlushTimesMs = flushTimesMs,
            EntriesPerSecond = batchSize / (avg / 1000.0),
            TotalEntriesFlushed = keyCounter,
            FileSizeMB = fileInfo.Exists ? fileInfo.Length / (1024.0 * 1024.0) : 0,
            AllSucceeded = allSucceeded
        };
    }

    private void ReportResult(string label, FlushLatencyResult r)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== WAL Flush Latency: batch size {label} ===");
        _output.WriteLine($"  Batch size             : {r.BatchSize}");
        _output.WriteLine($"  Measured flushes       : {MeasuredFlushes}");
        _output.WriteLine($"  Avg flush time         : {r.AvgFlushMs:F3} ms");
        _output.WriteLine($"  Min flush time         : {r.MinFlushMs:F3} ms");
        _output.WriteLine($"  Max flush time         : {r.MaxFlushMs:F3} ms");
        _output.WriteLine($"  P50 flush time         : {r.P50FlushMs:F3} ms");
        _output.WriteLine($"  Entries/sec            : {r.EntriesPerSecond:F0}");
        _output.WriteLine($"  Total entries flushed  : {r.TotalEntriesFlushed:N0}");
        _output.WriteLine($"  File size              : {r.FileSizeMB:F2} MB");
        _output.WriteLine($"  All individual flush times (ms): [{string.Join(", ", r.AllFlushTimesMs.Select(t => t.ToString("F3")))}]");
        _output.WriteLine("");
    }

    private class FlushLatencyResult
    {
        public int BatchSize { get; set; }
        public double AvgFlushMs { get; set; }
        public double MinFlushMs { get; set; }
        public double MaxFlushMs { get; set; }
        public double P50FlushMs { get; set; }
        public double[] AllFlushTimesMs { get; set; } = Array.Empty<double>();
        public double EntriesPerSecond { get; set; }
        public int TotalEntriesFlushed { get; set; }
        public double FileSizeMB { get; set; }
        public bool AllSucceeded { get; set; }
    }
}
