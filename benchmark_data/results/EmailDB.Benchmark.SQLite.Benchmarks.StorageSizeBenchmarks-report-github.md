```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-7700T CPU 2.90GHz (Kaby Lake), 1 CPU, 4 logical and 4 physical cores
.NET SDK 9.0.114
  [Host]   : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2
  ShortRun : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                 | EmailCount | Mean         | Error       | StdDev     | Gen0       | Gen1       | Allocated |
|----------------------- |----------- |-------------:|------------:|-----------:|-----------:|-----------:|----------:|
| **&#39;SQLite Storage Size&#39;**  | **1000**       |    **189.82 ms** |   **340.58 ms** |  **18.668 ms** |          **-** |          **-** |   **1.78 MB** |
| &#39;EmailDB Storage Size&#39; | 1000       |     39.45 ms |   101.12 ms |   5.543 ms |          - |          - |   2.94 MB |
| **&#39;SQLite Storage Size&#39;**  | **10000**      |  **1,329.00 ms** | **2,098.54 ms** | **115.028 ms** |  **2000.0000** |          **-** |  **17.61 MB** |
| &#39;EmailDB Storage Size&#39; | 10000      |    213.01 ms |   685.91 ms |  37.597 ms |  3000.0000 |  2000.0000 |  28.41 MB |
| **&#39;SQLite Storage Size&#39;**  | **100000**     | **11,950.31 ms** | **8,240.36 ms** | **451.682 ms** | **22000.0000** |  **1000.0000** | **175.84 MB** |
| &#39;EmailDB Storage Size&#39; | 100000     |  1,042.60 ms |   233.20 ms |  12.783 ms | 34000.0000 | 10000.0000 | 270.23 MB |
