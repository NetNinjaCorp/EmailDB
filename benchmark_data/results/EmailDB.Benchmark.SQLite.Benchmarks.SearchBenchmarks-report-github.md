```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-7700T CPU 2.90GHz (Kaby Lake), 1 CPU, 4 logical and 4 physical cores
.NET SDK 9.0.114
  [Host]   : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2
  ShortRun : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                  | EmailCount | Mean           | Error          | StdDev        | Gen0       | Gen1      | Allocated   |
|------------------------ |----------- |---------------:|---------------:|--------------:|-----------:|----------:|------------:|
| **&#39;SQLite FTS5 Search&#39;**    | **1000**       |       **452.5 μs** |       **214.4 μs** |      **11.75 μs** |     **0.9766** |         **-** |     **13.8 KB** |
| &#39;EmailDB Linear Search&#39; | 1000       |     9,505.3 μs |     1,200.7 μs |      65.81 μs |   453.1250 |         - |  3694.77 KB |
| **&#39;SQLite FTS5 Search&#39;**    | **10000**      |     **4,289.4 μs** |    **23,219.3 μs** |   **1,272.73 μs** |    **15.6250** |    **3.9063** |   **131.09 KB** |
| &#39;EmailDB Linear Search&#39; | 10000      |   175,532.4 μs |   529,551.0 μs |  29,026.48 μs |  4000.0000 |         - | 37026.24 KB |
| **&#39;SQLite FTS5 Search&#39;**    | **100000**     |    **31,074.9 μs** |    **75,052.3 μs** |   **4,113.87 μs** |   **125.0000** |   **93.7500** |   **1198.3 KB** |
| &#39;EmailDB Linear Search&#39; | 100000     | 1,513,694.2 μs | 2,362,947.2 μs | 129,521.11 μs | 46000.0000 | 1000.0000 | 369826.8 KB |
