using System;
using System.Threading.Tasks;

namespace EmailDB.UnitTests
{
    public class ConsoleRunner
    {
        public static async Task Main(string[] args)
        {
            var output = new TestOutputHelper();
            
            Console.WriteLine("🔍 Running Storage Analysis with Randomized Email Sizes...\n");
            
            // Test 1: Realistic Storage with Variable Sizes
            using (var test = new RealisticStorageAnalysisTest(output))
            {
                Console.WriteLine("📊 Test 1: Small Emails (2KB average)\n");
                await test.Analyze_With_Variable_Email_Sizes(2048, 512, 1000);
                
                Console.WriteLine("\n" + new string('-', 80) + "\n");
                
                Console.WriteLine("📊 Test 2: Medium Emails (10KB average)\n");
                await test.Analyze_With_Variable_Email_Sizes(10240, 5120, 1000);
                
                Console.WriteLine("\n" + new string('-', 80) + "\n");
                
                Console.WriteLine("📊 Test 3: Large Emails (50KB average)\n");
                await test.Analyze_With_Variable_Email_Sizes(51200, 20480, 500);
                
                Console.WriteLine("\n" + new string('-', 80) + "\n");
                
                Console.WriteLine("📊 Test 4: Real-World Distribution\n");
                await test.Analyze_Real_World_Email_Distribution();
                
                Console.WriteLine("\n" + new string('-', 80) + "\n");
                
                Console.WriteLine("📊 Test 5: Extreme Cases\n");
                await test.Analyze_Extreme_Cases();
            }
            
            Console.WriteLine("\n\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}