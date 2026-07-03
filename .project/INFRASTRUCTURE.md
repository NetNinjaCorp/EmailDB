# Infrastructure

EmailDB is a .NET library and file format — there is no deployed runtime infrastructure. Everything below runs locally or in CI.

## Build & Test

- **Solution**: `EMDBTesting.sln` (build with `dotnet build`)
- **Core library**: `EmailDB.Format/` (storage engine), `EmailDB.Format.Protobuf/` (serialization layer)
- **Tests**: `EmailDB.UnitTests/` and `Tests/` — run with `dotnet test`
- **Benchmarks**: `EmailDB.Benchmark.SQLite/`, `EmailDB.Benchmark.SearchStrategy/`, `EmailDB.Testing.FileFormatBenchmark/`; results captured in `benchmark_results*.txt` with fixtures in `benchmark_data/`

## Artifacts

- Output is a single-file storage format (`.emdb` shards, ~50 GB target, plus rebuildable `.emdb.vec` search sidecar) — no servers, databases, or cloud services are part of this project.
- No CI/CD pipeline is currently configured in-repo; builds and tests are run locally.
