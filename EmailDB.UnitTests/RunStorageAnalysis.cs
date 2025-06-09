using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests
{
    public class TestOutputHelper : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
            Console.WriteLine(message);
        }

        public void WriteLine(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }
    }

    public class RunStorageAnalysis
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine("Running Storage Analysis Tests...\n");
            
            var output = new TestOutputHelper();
            
            // Run Realistic Storage Analysis
            Console.WriteLine("=== REALISTIC STORAGE ANALYSIS ===\n");
            using (var test = new RealisticStorageAnalysisTest(output))
            {
                // Test with different email sizes
                await test.Analyze_With_Variable_Email_Sizes(5120, 2048, 100);
                Console.WriteLine("\n" + new string('=', 80) + "\n");
                
                await test.Analyze_With_Variable_Email_Sizes(25600, 10240, 100);
                Console.WriteLine("\n" + new string('=', 80) + "\n");
                
                await test.Analyze_Real_World_Email_Distribution();
                Console.WriteLine("\n" + new string('=', 80) + "\n");
                
                await test.Analyze_Extreme_Cases();
            }
            
            // Run Batching Analysis
            Console.WriteLine("\n\n=== BATCHING STORAGE ANALYSIS ===\n");
            using (var test = new BatchingStorageAnalysisTest(output))
            {
                await test.Analyze_Batching_Efficiency(1024 * 1024); // 1MB batches
                Console.WriteLine("\n" + new string('=', 80) + "\n");
                
                await test.Analyze_Optimal_Batch_Size();
                Console.WriteLine("\n" + new string('=', 80) + "\n");
                
                await test.Compare_Batching_Strategies();
            }
            
            // Run Simple Storage Analysis
            Console.WriteLine("\n\n=== SIMPLE STORAGE ANALYSIS ===\n");
            using (var test = new SimpleStorageAnalysisTest(output))
            {
                await test.Compare_Storage_Overhead_Simple();
                Console.WriteLine("\n" + new string('=', 80) + "\n");
                
                await test.Analyze_Update_Patterns();
            }
            
            Console.WriteLine("\n\nAnalysis complete!");
        }
    }
}