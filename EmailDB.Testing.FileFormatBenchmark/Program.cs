// File-format micro-benchmark harness.
//
// The original benchmark measured serialization of the duplicate
// EmailDB.Format.Protobuf.Models types, which were retired per ADR-016
// (US-EMDB-101). Real serialization benchmarks belong in the EmailDB.Benchmark.*
// projects. This entrypoint is kept as a buildable placeholder.

Console.WriteLine("EmailDB.Testing.FileFormatBenchmark: duplicate Protobuf models retired; see EmailDB.Benchmark.* for v3 benchmarks.");
