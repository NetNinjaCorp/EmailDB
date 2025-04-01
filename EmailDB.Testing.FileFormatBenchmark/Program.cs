// Run the benchmarks.
using BenchmarkDotNet.Running;
using CapnpBenchmarkTest;

var summary = BenchmarkRunner.Run<CapnpBenchmark>();
var summary2 = BenchmarkRunner.Run<ProtobufTests.ProtobufTests>();