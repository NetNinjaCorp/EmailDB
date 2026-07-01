```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-7700T CPU 2.90GHz (Kaby Lake), 1 CPU, 4 logical and 4 physical cores
.NET SDK 9.0.114
  [Host]   : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2
  ShortRun : .NET 9.0.13 (9.0.1326.6317), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                        | Mean     | Error     | StdDev    | Median    | Allocated  |
|------------------------------ |---------:|----------:|----------:|----------:|-----------:|
| &#39;SQLite Delete (100 emails)&#39;  | 85.60 ms | 621.98 ms | 34.093 ms | 103.20 ms |   74.64 KB |
| &#39;EmailDB Delete (100 emails)&#39; | 15.12 ms |  65.66 ms |  3.599 ms |  13.75 ms | 3128.55 KB |
| &#39;SQLite Move (100 emails)&#39;    | 57.24 ms | 106.95 ms |  5.862 ms |  55.93 ms |  110.58 KB |
| &#39;EmailDB Move (100 emails)&#39;   | 31.51 ms | 317.20 ms | 17.387 ms |  25.44 ms | 3130.48 KB |
