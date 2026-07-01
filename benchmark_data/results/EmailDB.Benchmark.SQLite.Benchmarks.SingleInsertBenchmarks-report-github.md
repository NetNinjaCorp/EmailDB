```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-7700T CPU 2.90GHz (Kaby Lake), 1 CPU, 4 logical and 4 physical cores
.NET SDK 9.0.114
  [Host]   : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2
  ShortRun : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                  | EmailCount | Mean         | Error        | StdDev       | Gen0      | Gen1      | Allocated |
|------------------------ |----------- |-------------:|-------------:|-------------:|----------:|----------:|----------:|
| **&#39;SQLite Single Insert&#39;**  | **1000**       |    **785.96 ms** |  **2,986.67 ms** |   **163.710 ms** |         **-** |         **-** |   **3.18 MB** |
| &#39;EmailDB Single Insert&#39; | 1000       |     87.88 ms |    290.55 ms |    15.926 ms |         - |         - |   2.95 MB |
| **&#39;SQLite Single Insert&#39;**  | **10000**      | **10,146.03 ms** | **22,757.40 ms** | **1,247.410 ms** | **3000.0000** |         **-** |  **31.64 MB** |
| &#39;EmailDB Single Insert&#39; | 10000      |    146.49 ms |    175.61 ms |     9.626 ms | 3000.0000 | 2000.0000 |   28.5 MB |
