using System;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.UnitTests;
using Xunit.Abstractions;

public class ConsoleTestOutput : ITestOutputHelper
{
    public void WriteLine(string message) => Console.WriteLine(message);
    public void WriteLine(string format, params object[] args) => Console.WriteLine(format, args);
}

public class RunAllNewTests
{
    public static async Task Main(string[] args)
    {
        var output = new ConsoleTestOutput();
        
        Console.WriteLine("🧪 RUNNING COMPREHENSIVE TEST SUITE");
        Console.WriteLine("===================================\n");
        
        var tests = new[]
        {
            ("Concurrent Access", async () => 
            {
                using var test = new ConcurrentAccessStressTest(output);
                await test.Test_Concurrent_Index_Consistency();
            }),
            
            ("Edge Cases", async () => 
            {
                using var test = new EdgeCaseHandlingTest(output);
                await test.Test_Empty_And_Null_Values();
            }),
            
            ("Memory Usage", async () => 
            {
                using var test = new MemoryUsageMonitoringTest(output);
                await test.Test_Memory_Leak_Detection();
            }),
            
            ("Cross-Format", async () => 
            {
                using var test = new CrossFormatCompatibilityTest(output);
                await test.Test_PayloadEncoding_Compatibility();
            }),
            
            ("Corruption Recovery", async () => 
            {
                using var test = new CorruptionRecoveryTest(output);
                await test.Test_Partial_Write_Recovery();
            })
        };
        
        var passed = 0;
        var failed = 0;
        
        foreach (var (name, test) in tests)
        {
            Console.WriteLine($"\n🔷 Running: {name}");
            Console.WriteLine(new string('-', 40));
            
            try
            {
                await test();
                Console.WriteLine($"✅ {name} - PASSED");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {name} - FAILED: {ex.Message}");
                failed++;
            }
        }
        
        Console.WriteLine($"\n\n📊 TEST SUMMARY");
        Console.WriteLine("===============");
        Console.WriteLine($"  Total tests: {tests.Length}");
        Console.WriteLine($"  Passed: {passed} ✅");
        Console.WriteLine($"  Failed: {failed} ❌");
        Console.WriteLine($"  Success rate: {passed * 100.0 / tests.Length:F1}%");
    }
}