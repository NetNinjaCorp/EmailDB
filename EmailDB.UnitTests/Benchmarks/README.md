# Email Database Benchmarks

This directory contains benchmark tools for the EmailDB system. These benchmarks simulate various email operations and measure performance metrics such as execution time and database size.

## Overview

The benchmark system consists of:

1. `EmailBenchmark.cs` - Core benchmark class that simulates email operations
2. `BenchmarkRunner.cs` - Runner for executing different benchmark scenarios
3. Email models in `../Models/EmailModels.cs`

## Features

- Simulates adding different numbers of emails (100, 1000, 100000, 1000000)
- Measures execution time and database size
- Simulates real-world usage patterns:
  - Adding emails to different folders
  - Moving emails between folders
  - Deleting emails
  - Marking emails as read/unread
  - Searching for emails
- Realistic benchmark with randomized operations based on seed
- Uses seeded random generation for reproducible results
- Generates detailed benchmark reports

## Running Benchmarks

You can run the benchmarks in several ways:

### From Command Line

```
dotnet run --project EmailDB.UnitTests.csproj -- --benchmark [options]
```

Available options:
- `--small` - Run small benchmark (100 emails)
- `--medium` - Run medium benchmark (1000 emails)
- `--large` - Run large benchmark (100000 emails)
- `--absurdly-large` - Run absurdly large benchmark (1000000 emails)
- `--realistic` - Run realistic usage scenario with randomized operations
- `--multi-seed` - Run benchmarks with multiple seeds
- `--compare` - Compare results from different benchmark runs
- `--wait` or `-w` - Wait for key press before exiting

If no specific benchmark option is provided, the default suite (small, medium, realistic) will run.

### From xUnit Test Runner

The benchmark methods are decorated with `[Theory]` attributes, so they can be run as xUnit tests:

1. Open the test explorer in your IDE
2. Find the `BenchmarkRunner` tests
3. Run the desired benchmark test

### Programmatically

```csharp
// Create a benchmark instance with a specific seed
using (var benchmark = new EmailBenchmark(seed: 42))
{
    // Add emails
    benchmark.AddEmails(100, "/Inbox");
    
    // Move emails
    var emailIds = benchmark.SearchEmails("important");
    benchmark.MoveEmails(emailIds, "/Work");
    
    // Generate and save report
    var report = benchmark.GenerateReport();
    benchmark.SaveReportToFile("my_benchmark_report.txt");
}
```

## Benchmark Reports

Benchmark reports are saved to the `benchmark_data` directory by default. Each report includes:

- Seed value used for reproducibility
- Total number of emails and folders
- Database size
- Detailed results for each operation:
  - Operation name
  - Number of items processed
  - Execution time
  - Database size after operation

## Customizing Benchmarks

You can create custom benchmark scenarios by:

1. Creating a new instance of `EmailBenchmark`
2. Calling various methods to simulate operations
3. Generating a report with `GenerateReport()`

Example:

```csharp
using (var benchmark = new EmailBenchmark(seed: 123))
{
    // Add initial emails
    benchmark.AddEmails(500, "/Inbox");
    
    // Perform operations
    var emails = benchmark.SearchEmails("meeting");
    benchmark.MoveEmails(emails, "/Work/Meetings");
    
    // Add more emails
    benchmark.AddEmails(200, "/Inbox");
    
    // Generate report
    var report = benchmark.GenerateReport();
    Console.WriteLine(report);
}
```

## Seeded Random Generation

The benchmarks use seeded random generation to ensure reproducible results. By providing the same seed value, you can reproduce the exact same benchmark scenario, which is useful for:

- Comparing performance across different implementations
- Tracking performance changes over time
- Debugging issues in specific scenarios

### Randomized Realistic Benchmark

The realistic benchmark now uses the seed to generate a truly unique sequence of operations:

1. It starts with an initial batch of 200 emails
2. Then performs a random number of operations (between 15-30) determined by the seed
3. Each operation is randomly selected from:
   - Adding emails to random folders
   - Moving emails between random folders
   - Deleting emails
   - Permanently deleting emails
   - Marking emails as read/unread
   - Searching for emails with different terms

This creates a more realistic and varied benchmark that still remains reproducible with the same seed.

### Absurdly Large Benchmark

The absurdly large benchmark creates 1 million emails to test system performance at extreme scale. This benchmark:

- Adds emails in larger batches (5000 at a time)
- Shows progress during execution
- Performs operations on a sample of the emails to keep execution time reasonable

**Warning:** This benchmark requires significant memory and may take a long time to complete.

## Performance Metrics

The benchmarks measure:

1. **Execution Time**: How long each operation takes in milliseconds
2. **Database Size**: The total size of the database after each operation
3. **Email Count**: The total number of emails in the database
4. **Folder Count**: The total number of folders in the database