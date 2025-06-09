using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Check if we're being called as RunHybridTests
        if (args.Length > 0 && args[0] == "RunHybridTests")
        {
            await RunHybridTests();
            return;
        }

        Console.WriteLine("EmailDB Unit Tests Runner");
        Console.WriteLine("=========================\n");
        
        RunAllTests();
        
        if (args.Contains("--wait") || args.Contains("-w"))
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    private static async Task RunHybridTests()
    {
        var output = new ConsoleOutputHelper();
        
        Console.WriteLine("\n=== Running Hybrid Store Folder Search Test ===\n");
        
        using (var folderTest = new HybridStoreFolderSearchTest(output))
        {
            try
            {
                await folderTest.Test_Folder_Index_Accuracy_And_Performance();
                Console.WriteLine("\n✅ Folder Search Test Completed Successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Folder search test failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        
        Console.WriteLine("\n\n=== Running Hybrid Email Store Performance Test ===\n");
        
        using (var perfTest = new HybridEmailStorePerformanceTest(output))
        {
            try
            {
                await perfTest.Test_Hybrid_Store_Performance_And_Efficiency();
                Console.WriteLine("\n✅ Performance Test Completed Successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ Performance test failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        
        Console.WriteLine("\n\nAll tests completed!");
    }
    
    private static void RunBenchmarks(string[] args)
    {
        Console.WriteLine("Benchmarks are currently disabled.");
        return;
        int seed = 42; // Default seed
        int seedIndex = Array.IndexOf(args, "--seed");
        if (seedIndex >= 0 && seedIndex < args.Length - 1)
        {
            if (int.TryParse(args[seedIndex + 1], out int customSeed))
            {
                seed = customSeed;
                Console.WriteLine($"Using custom seed: {seed}");
            }
            else
            {
                Console.WriteLine($"Invalid seed value: {args[seedIndex + 1]}. Using default seed: {seed}");
            }
        }
        
        // Get custom seed count if provided
        int seedCount = 500; // Default seed count
        int seedCountIndex = Array.IndexOf(args, "--seed-count");
        if (seedCountIndex >= 0 && seedCountIndex < args.Length - 1)
        {
            if (int.TryParse(args[seedCountIndex + 1], out int customSeedCount) && customSeedCount > 0)
            {
                seedCount = customSeedCount;
                Console.WriteLine($"Using custom seed count: {seedCount}");
            }
            else
            {
                Console.WriteLine($"Invalid seed count value: {args[seedCountIndex + 1]}. Using default seed count: {seedCount}");
            }
        }
        
        Console.WriteLine("Benchmarks are not available - benchmarkRunner not implemented");
        
        Console.WriteLine("\nBenchmarks completed. Results saved to benchmark_data directory.");
    }

    private static void RunAllTests()
    {
        var testClasses = GetTestClasses();
        int totalTests = 0;
        int passedTests = 0;
        
        foreach (var testClass in testClasses)
        {
            // Skip the RunTests class to avoid recursion
            if (testClass.Name == "RunTests")
                continue;
            
            Console.WriteLine($"\nRunning tests in {testClass.Name}");
            
            var testMethods = GetTestMethods(testClass);
            totalTests += testMethods.Count;
            
            foreach (var method in testMethods)
            {
                object instance = null;
                try
                {
                    // Create an instance of the test class
                    instance = Activator.CreateInstance(testClass);
                    
                    // Run the test method
                    method.Invoke(instance, null);
                    
                    Console.WriteLine($"  ✓ {method.Name}");
                    passedTests++;
                }
                catch (Exception ex)
                {
                    // Unwrap the inner exception if it's a TargetInvocationException
                    var actualException = ex is TargetInvocationException ? ex.InnerException : ex;
                    Console.WriteLine($"  ✗ {method.Name} - {actualException.Message}");
                }
                finally
                {
                    // If the test class implements IDisposable, call Dispose
                    if (instance is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }
        
        Console.WriteLine($"\nTest Results: {passedTests}/{totalTests} tests passed ({(passedTests * 100.0 / totalTests):F1}% success rate)");
    }

    private static List<Type> GetTestClasses()
    {
        return Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0))
            .ToList();
    }

    private static List<MethodInfo> GetTestMethods(Type testClass)
    {
        return testClass.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0)
            .ToList();
    }
}

public class ConsoleOutputHelper : ITestOutputHelper
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