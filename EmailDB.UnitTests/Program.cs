using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmailDB.UnitTests.Benchmarks;
using Xunit;

namespace EmailDB.UnitTests;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("EmailDB Unit Tests Runner");
        Console.WriteLine("=========================\n");
        
        if (args.Contains("--benchmark") || args.Contains("-b"))
        {
            RunBenchmarks(args);
        }
        else
        {
            RunAllTests();
        }
        
        if (args.Contains("--wait") || args.Contains("-w"))
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
    
    private static void RunBenchmarks(string[] args)
    {
        Console.WriteLine("Running Email Database Benchmarks");
        Console.WriteLine("================================\n");
        
        var benchmarkRunner = new BenchmarkRunner();
        
        // Get custom seed if provided
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
        
        if (args.Contains("--small"))
        {
            benchmarkRunner.RunSmallBenchmark(seed);
        }
        else if (args.Contains("--medium"))
        {
            benchmarkRunner.RunMediumBenchmark(seed);
        }
        else if (args.Contains("--large"))
        {
            benchmarkRunner.RunLargeBenchmark(seed);
        }
        else if (args.Contains("--absurdly-large"))
        {
            Console.WriteLine("WARNING: The absurdly large benchmark will create 1 million emails.");
            Console.WriteLine("This will require significant memory and may take a long time to complete.");
            Console.WriteLine("Are you sure you want to continue? (y/n)");
            
            var response = Console.ReadLine()?.ToLower();
            if (response == "y" || response == "yes")
            {
                benchmarkRunner.RunAbsurdlyLargeBenchmark(seed);
            }
            else
            {
                Console.WriteLine("Absurdly large benchmark cancelled.");
            }
        }
        else if (args.Contains("--realistic"))
        {
            benchmarkRunner.RunRealisticScenario(seed);
        }
        else if (args.Contains("--multi-seed"))
        {
            benchmarkRunner.RunMultiSeedBenchmark(seedCount);
        }
        else
        {
            // Default to running all benchmarks except large
            Console.WriteLine("Running default benchmark suite (small, medium, realistic)");
            benchmarkRunner.RunAllBenchmarks();
        }
        
        // Check if we should compare results
        if (args.Contains("--compare"))
        {
            benchmarkRunner.CompareResults();
        }
        
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