using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Benchmarks
{
    /// <summary>
    /// Runner for email database benchmarks
    /// </summary>
    public class BenchmarkRunner
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the BenchmarkRunner class
        /// </summary>
        public BenchmarkRunner()
        {
            // Default constructor for running from Program.cs
        }

        /// <summary>
        /// Initializes a new instance of the BenchmarkRunner class with test output
        /// </summary>
        /// <param name="output">Test output helper</param>
        public BenchmarkRunner(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Writes a message to the output
        /// </summary>
        /// <param name="message">Message to write</param>
        private void WriteLine(string message)
        {
            if (_output != null)
            {
                _output.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }

        /// <summary>
        /// Runs a small benchmark (100 emails)
        /// </summary>
        /// <param name="seed">Random seed for reproducibility</param>
        [Theory]
        [InlineData(42)]
        [InlineData(123)]
        public void RunSmallBenchmark(int seed)
        {
            WriteLine($"Running small benchmark (100 emails) with seed {seed}...");
            
            using (var benchmark = new EmailBenchmark(seed))
            {
                benchmark.AddEmails(100, "/Inbox");
                
                var report = benchmark.GenerateReport();
                WriteLine(report);
                
                benchmark.SaveReportToFile($"small_benchmark_seed_{seed}.txt");
            }
            
            WriteLine("Small benchmark completed.");
        }

        /// <summary>
        /// Runs a medium benchmark (1000 emails)
        /// </summary>
        /// <param name="seed">Random seed for reproducibility</param>
        [Theory]
        [InlineData(42)]
        [InlineData(123)]
        public void RunMediumBenchmark(int seed)
        {
            WriteLine($"Running medium benchmark (1000 emails) with seed {seed}...");
            
            using (var benchmark = new EmailBenchmark(seed))
            {
                benchmark.AddEmails(1000, "/Inbox");
                
                var report = benchmark.GenerateReport();
                WriteLine(report);
                
                benchmark.SaveReportToFile($"medium_benchmark_seed_{seed}.txt");
            }
            
            WriteLine("Medium benchmark completed.");
        }

        /// <summary>
        /// Runs a large benchmark (100000 emails)
        /// </summary>
        /// <param name="seed">Random seed for reproducibility</param>
        [Theory]
        [InlineData(42)]
        [InlineData(123)]
        public void RunLargeBenchmark(int seed)
        {
            WriteLine($"Running large benchmark (100000 emails) with seed {seed}...");
            
            using (var benchmark = new EmailBenchmark(seed))
            {
                benchmark.RunLargeScaleBenchmark(100000);
                
                var report = benchmark.GenerateReport();
                WriteLine(report);
                
                benchmark.SaveReportToFile($"large_benchmark_seed_{seed}.txt");
            }
            
            WriteLine("Large benchmark completed.");
        }

        /// <summary>
        /// Runs an absurdly large benchmark (1 million emails)
        /// </summary>
        /// <param name="seed">Random seed for reproducibility</param>
        [Theory]
        [InlineData(42)]
        public void RunAbsurdlyLargeBenchmark(int seed)
        {
            WriteLine($"Running absurdly large benchmark (1,000,000 emails) with seed {seed}...");
            WriteLine("WARNING: This benchmark requires significant memory and may take a long time to complete.");
            
            using (var benchmark = new EmailBenchmark(seed))
            {
                benchmark.RunAbsurdlyLargeBenchmark();
                
                var report = benchmark.GenerateReport();
                WriteLine(report);
                
                benchmark.SaveReportToFile($"absurdly_large_benchmark_seed_{seed}.txt");
            }
            
            WriteLine("Absurdly large benchmark completed.");
        }

        /// <summary>
        /// Runs a realistic usage scenario benchmark
        /// </summary>
        /// <param name="seed">Random seed for reproducibility</param>
        [Theory]
        [InlineData(42)]
        [InlineData(123)]
        public void RunRealisticScenario(int seed)
        {
            WriteLine($"Running realistic scenario benchmark with seed {seed}...");
            
            using (var benchmark = new EmailBenchmark(seed))
            {
                benchmark.RunRealisticScenario(seed);
                
                var report = benchmark.GenerateReport();
                WriteLine(report);
                
                benchmark.SaveReportToFile($"realistic_scenario_seed_{seed}.txt");
            }
            
            WriteLine("Realistic scenario benchmark completed.");
        }

        /// <summary>
        /// Runs multiple benchmarks with different seeds for comparison
        /// </summary>
        /// <param name="seedCount">Number of different seeds to use</param>
        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        public void RunMultiSeedBenchmark(int seedCount)
        {
            WriteLine($"Running multi-seed benchmark with {seedCount} different seeds...");
            
            var seeds = new List<int>();
            var random = new Random(42);
            
            for (int i = 0; i < seedCount; i++)
            {
                seeds.Add(random.Next(1, 10000));
            }
            
            foreach (var seed in seeds)
            {
                using (var benchmark = new EmailBenchmark(seed))
                {
                    WriteLine($"Running benchmark with seed {seed}...");
                    benchmark.RunRealisticScenario(seed);
                    benchmark.SaveReportToFile($"multi_seed_benchmark_{seed}.txt");
                }
            }
            
            WriteLine("Multi-seed benchmark completed.");
        }

        /// <summary>
        /// Runs all benchmarks
        /// </summary>
        [Fact]
        public void RunAllBenchmarks()
        {
            WriteLine("Running all benchmarks...");
            
            RunSmallBenchmark(42);
            RunMediumBenchmark(42);
            RunRealisticScenario(42);
            
            // Skip large benchmark by default as it can take a long time
            // RunLargeBenchmark(42);
            
            WriteLine("All benchmarks completed.");
        }

        /// <summary>
        /// Compares results from different benchmark runs
        /// </summary>
        /// <param name="reportDirectory">Directory containing benchmark reports</param>
        public void CompareResults(string reportDirectory = "benchmark_data")
        {
            WriteLine("Comparing benchmark results...");
            
            var directory = Path.Combine(Directory.GetCurrentDirectory(), reportDirectory);
            var files = Directory.GetFiles(directory, "*.txt");
            
            if (files.Length == 0)
            {
                WriteLine("No benchmark reports found.");
                return;
            }
            
            WriteLine($"Found {files.Length} benchmark reports:");
            
            foreach (var file in files)
            {
                WriteLine($"- {Path.GetFileName(file)}");
                var content = File.ReadAllText(file);
                WriteLine($"  {content.Split('\n').Length} lines");
            }
            
            WriteLine("Comparison completed.");
        }
    }
}