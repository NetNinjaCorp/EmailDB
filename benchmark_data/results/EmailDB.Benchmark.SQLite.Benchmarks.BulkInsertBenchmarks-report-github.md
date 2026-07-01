```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-7700T CPU 2.90GHz (Kaby Lake), 1 CPU, 4 logical and 4 physical cores
.NET SDK 9.0.114
  [Host]   : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2
  ShortRun : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                | EmailCount | Mean         | Error       | StdDev     | Gen0       | Gen1       | Allocated |
|---------------------- |----------- |-------------:|------------:|-----------:|-----------:|-----------:|----------:|
| **&#39;SQLite Bulk Insert&#39;**  | **1000**       |    **143.25 ms** |   **748.62 ms** |  **41.034 ms** |          **-** |          **-** |   **1.78 MB** |
| &#39;EmailDB Bulk Insert&#39; | 1000       |     36.52 ms |    56.93 ms |   3.121 ms |          - |          - |   2.94 MB |
| **&#39;SQLite Bulk Insert&#39;**  | **10000**      |  **1,320.66 ms** | **2,116.82 ms** | **116.030 ms** |  **2000.0000** |          **-** |  **17.61 MB** |
| &#39;EmailDB Bulk Insert&#39; | 10000      |    115.77 ms |   212.24 ms |  11.634 ms |  3000.0000 |  1000.0000 |  28.35 MB |
| **&#39;SQLite Bulk Insert&#39;**  | **100000**     | **12,372.67 ms** | **3,644.34 ms** | **199.759 ms** | **22000.0000** |  **1000.0000** | **175.84 MB** |
| &#39;EmailDB Bulk Insert&#39; | 100000     |  1,260.34 ms | 3,826.84 ms | 209.762 ms | 34000.0000 | 10000.0000 | 270.34 MB |
