using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests memory usage patterns and detects potential memory leaks.
/// </summary>
public class MemoryUsageMonitoringTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public MemoryUsageMonitoringTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"MemoryTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Memory_Usage_During_Operations()
    {
        _output.WriteLine("🧠 MEMORY USAGE MONITORING TEST");
        _output.WriteLine("===============================\n");
        
        var dataPath = Path.Combine(_testDir, "memory_test.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        // Force garbage collection to get baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var baseline = GC.GetTotalMemory(true);
        _output.WriteLine($"📊 Baseline memory: {FormatBytes(baseline)}");
        
        var memorySnapshots = new List<MemorySnapshot>();
        
        // Phase 1: Write operations
        _output.WriteLine("\n📝 PHASE 1: WRITE OPERATIONS");
        _output.WriteLine("===========================");
        
        using (var store = new HybridEmailStore(dataPath, indexPath, 512 * 1024))
        {
            for (int batch = 0; batch < 10; batch++)
            {
                var batchStart = GC.GetTotalMemory(false);
                
                // Write 100 emails
                for (int i = 0; i < 100; i++)
                {
                    var emailIndex = batch * 100 + i;
                    var size = _random.Next(5000, 50000);
                    var body = new string('x', size);
                    
                    await store.StoreEmailAsync(
                        $"mem-test-{emailIndex}@example.com",
                        $"folder-{emailIndex % 5}",
                        Encoding.UTF8.GetBytes(body),
                        subject: $"Memory test {emailIndex}",
                        body: body
                    );
                }
                
                await store.FlushAsync();
                
                // Take memory snapshot
                var snapshot = TakeMemorySnapshot($"After batch {batch + 1}");
                snapshot.EmailsWritten = (batch + 1) * 100;
                snapshot.MemoryDelta = snapshot.TotalMemory - baseline;
                memorySnapshots.Add(snapshot);
                
                _output.WriteLine($"  Batch {batch + 1}: {FormatBytes(snapshot.TotalMemory)} " +
                                $"(+{FormatBytes(snapshot.MemoryDelta)} from baseline)");
                
                // Force GC every few batches
                if (batch % 3 == 2)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    
                    var afterGC = GC.GetTotalMemory(true);
                    _output.WriteLine($"    After GC: {FormatBytes(afterGC)}");
                }
            }
        }
        
        // Phase 2: Memory after dispose
        _output.WriteLine("\n♻️ PHASE 2: MEMORY AFTER DISPOSE");
        _output.WriteLine("================================");
        
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var afterDispose = GC.GetTotalMemory(true);
        _output.WriteLine($"  Memory after dispose: {FormatBytes(afterDispose)}");
        _output.WriteLine($"  Memory released: {FormatBytes(memorySnapshots.Last().TotalMemory - afterDispose)}");
        
        // Phase 3: Read operations
        _output.WriteLine("\n📖 PHASE 3: READ OPERATIONS");
        _output.WriteLine("==========================");
        
        using (var store = new HybridEmailStore(dataPath, indexPath))
        {
            var beforeReads = GC.GetTotalMemory(true);
            
            // Perform many reads
            for (int i = 0; i < 500; i++)
            {
                var messageId = $"mem-test-{_random.Next(1000)}@example.com";
                try
                {
                    var (data, metadata) = await store.GetEmailByMessageIdAsync(messageId);
                }
                catch (KeyNotFoundException) { }
                
                if (i % 100 == 99)
                {
                    var current = GC.GetTotalMemory(false);
                    _output.WriteLine($"  After {i + 1} reads: {FormatBytes(current - beforeReads)} growth");
                }
            }
            
            var afterReads = GC.GetTotalMemory(false);
            _output.WriteLine($"  Total read memory growth: {FormatBytes(afterReads - beforeReads)}");
        }
        
        // Phase 4: Search operations
        _output.WriteLine("\n🔍 PHASE 4: SEARCH OPERATIONS");
        _output.WriteLine("=============================");
        
        using (var store = new HybridEmailStore(dataPath, indexPath))
        {
            var beforeSearch = GC.GetTotalMemory(true);
            
            var searchWords = new[] { "memory", "test", "email", "folder", "subject" };
            
            foreach (var word in searchWords)
            {
                var results = store.SearchFullText(word).Take(100).ToList();
                var current = GC.GetTotalMemory(false);
                _output.WriteLine($"  Search '{word}': {results.Count} results, " +
                                $"memory: +{FormatBytes(current - beforeSearch)}");
            }
            
            var afterSearch = GC.GetTotalMemory(false);
            _output.WriteLine($"  Total search memory growth: {FormatBytes(afterSearch - beforeSearch)}");
        }
        
        // Analysis
        _output.WriteLine("\n📈 MEMORY USAGE ANALYSIS");
        _output.WriteLine("========================");
        
        if (memorySnapshots.Count > 2)
        {
            var firstHalf = memorySnapshots.Take(memorySnapshots.Count / 2);
            var secondHalf = memorySnapshots.Skip(memorySnapshots.Count / 2);
            
            var firstHalfGrowth = firstHalf.Last().MemoryDelta - firstHalf.First().MemoryDelta;
            var secondHalfGrowth = secondHalf.Last().MemoryDelta - secondHalf.First().MemoryDelta;
            
            var avgGrowthPerEmail = memorySnapshots.Last().MemoryDelta / (double)memorySnapshots.Last().EmailsWritten;
            
            _output.WriteLine($"  First half growth: {FormatBytes(firstHalfGrowth)}");
            _output.WriteLine($"  Second half growth: {FormatBytes(secondHalfGrowth)}");
            _output.WriteLine($"  Average per email: {avgGrowthPerEmail:F0} bytes");
            
            // Check for memory leak pattern
            var isLeaking = secondHalfGrowth > firstHalfGrowth * 1.5;
            _output.WriteLine($"  Memory leak detected: {(isLeaking ? "⚠️ POSSIBLE" : "✅ NO")}");
            
            Assert.False(isLeaking, "Possible memory leak detected");
        }
        
        // Final cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        var finalMemory = GC.GetTotalMemory(true);
        var totalGrowth = finalMemory - baseline;
        
        _output.WriteLine($"\n🏁 FINAL RESULTS");
        _output.WriteLine($"================");
        _output.WriteLine($"  Baseline: {FormatBytes(baseline)}");
        _output.WriteLine($"  Final: {FormatBytes(finalMemory)}");
        _output.WriteLine($"  Total growth: {FormatBytes(totalGrowth)}");
        _output.WriteLine($"  Growth ratio: {totalGrowth / (double)baseline:F2}x");
        
        // Assert reasonable memory usage
        Assert.True(totalGrowth < baseline * 2, "Memory usage more than doubled");
    }

    [Fact]
    public async Task Test_Memory_Leak_Detection()
    {
        _output.WriteLine("\n🔍 MEMORY LEAK DETECTION TEST");
        _output.WriteLine("=============================");
        
        var baseline = GC.GetTotalMemory(true);
        var iterations = 10;
        var memoryAfterEachIteration = new List<long>();
        
        for (int iter = 0; iter < iterations; iter++)
        {
            var dataPath = Path.Combine(_testDir, $"leak_test_{iter}.data");
            var indexPath = Path.Combine(_testDir, $"leak_indexes_{iter}");
            
            // Create and dispose store multiple times
            using (var store = new HybridEmailStore(dataPath, indexPath))
            {
                // Write some emails
                for (int i = 0; i < 100; i++)
                {
                    await store.StoreEmailAsync(
                        $"leak-{iter}-{i}@test.com",
                        "inbox",
                        Encoding.UTF8.GetBytes($"Leak test {iter} email {i}"),
                        subject: $"Leak test {iter}-{i}"
                    );
                }
                
                // Perform searches
                var results = store.SearchFullText("leak").ToList();
                
                // List folders
                var emails = store.ListFolder("inbox").ToList();
            }
            
            // Force cleanup
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            var memory = GC.GetTotalMemory(true);
            memoryAfterEachIteration.Add(memory);
            
            _output.WriteLine($"  Iteration {iter + 1}: {FormatBytes(memory)} " +
                            $"(+{FormatBytes(memory - baseline)} from baseline)");
        }
        
        // Analyze for leaks
        var firstIterationGrowth = memoryAfterEachIteration[0] - baseline;
        var lastIterationGrowth = memoryAfterEachIteration.Last() - baseline;
        var growthRatio = lastIterationGrowth / (double)firstIterationGrowth;
        
        _output.WriteLine($"\n  Analysis:");
        _output.WriteLine($"    First iteration growth: {FormatBytes(firstIterationGrowth)}");
        _output.WriteLine($"    Last iteration growth: {FormatBytes(lastIterationGrowth)}");
        _output.WriteLine($"    Growth ratio: {growthRatio:F2}x");
        
        // Check if memory is growing linearly (indicating a leak)
        var isLinearGrowth = true;
        for (int i = 1; i < memoryAfterEachIteration.Count - 1; i++)
        {
            var expectedLinear = baseline + (firstIterationGrowth * (i + 1));
            var actual = memoryAfterEachIteration[i];
            var deviation = Math.Abs(actual - expectedLinear) / expectedLinear;
            
            if (deviation > 0.2) // 20% deviation
            {
                isLinearGrowth = false;
                break;
            }
        }
        
        _output.WriteLine($"    Linear growth pattern: {(isLinearGrowth ? "⚠️ YES" : "✅ NO")}");
        
        Assert.False(isLinearGrowth, "Linear memory growth detected - possible leak");
        Assert.True(growthRatio < 3, "Memory growth too high across iterations");
    }

    [Fact]
    public async Task Test_Large_Object_Heap_Usage()
    {
        _output.WriteLine("\n📦 LARGE OBJECT HEAP TEST");
        _output.WriteLine("=========================");
        
        var dataPath = Path.Combine(_testDir, "loh_test.data");
        var indexPath = Path.Combine(_testDir, "loh_indexes");
        
        // Monitor LOH allocations
        var lohBefore = GC.GetTotalMemory(false);
        var gen2CollectionsBefore = GC.CollectionCount(2);
        
        using (var store = new HybridEmailStore(dataPath, indexPath, 2 * 1024 * 1024)) // 2MB blocks
        {
            // Store large emails that will go to LOH (>85KB)
            for (int i = 0; i < 50; i++)
            {
                var size = _random.Next(100_000, 500_000); // 100KB - 500KB
                var largeBody = new string('x', size);
                
                await store.StoreEmailAsync(
                    $"loh-{i}@test.com",
                    "large",
                    Encoding.UTF8.GetBytes(largeBody),
                    subject: $"Large email {i}",
                    body: largeBody
                );
                
                if (i % 10 == 9)
                {
                    var current = GC.GetTotalMemory(false);
                    var gen2Collections = GC.CollectionCount(2);
                    
                    _output.WriteLine($"  After {i + 1} large emails:");
                    _output.WriteLine($"    Memory: {FormatBytes(current - lohBefore)}");
                    _output.WriteLine($"    Gen2 collections: {gen2Collections - gen2CollectionsBefore}");
                }
            }
        }
        
        // Check LOH fragmentation
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var afterCompact = GC.GetTotalMemory(true);
        
        _output.WriteLine($"\n  After compaction: {FormatBytes(afterCompact)}");
        _output.WriteLine($"  Memory recovered: {FormatBytes(lohBefore - afterCompact)}");
    }

    private MemorySnapshot TakeMemorySnapshot(string label)
    {
        return new MemorySnapshot
        {
            Label = label,
            Timestamp = DateTime.UtcNow,
            TotalMemory = GC.GetTotalMemory(false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            AllocatedBytes = GC.GetTotalAllocatedBytes()
        };
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = Math.Abs(bytes);
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{(bytes < 0 ? "-" : "")}{len:F2} {sizes[order]}";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    private class MemorySnapshot
    {
        public string Label { get; set; }
        public DateTime Timestamp { get; set; }
        public long TotalMemory { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public long AllocatedBytes { get; set; }
        public int EmailsWritten { get; set; }
        public long MemoryDelta { get; set; }
    }
}