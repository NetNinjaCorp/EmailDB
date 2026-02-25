// Run the benchmarks.
using BenchmarkDotNet.Running;

var summary = BenchmarkRunner.Run<ProtobufTests.ProtobufTests>();
