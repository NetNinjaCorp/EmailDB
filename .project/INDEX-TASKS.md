# Tasks

| ID | Title | Status | Points | Tags | Assignee | Depends On | Story |
| -- | ----- | ------ | ------ | ---- | -------- | ---------- | ----- |
| [US-EMDB-1-1](tasks/US-EMDB-1-1.md) | Test: MetadataManager uses SemaphoreSlim or AsyncReaderWriterLock instead of object lock | ✅ done | 2 |  | claude | — | [US-EMDB-1](stories/US-EMDB-1.md) |
| [US-EMDB-1-2](tasks/US-EMDB-1-2.md) | Test: SegmentManager uses async-safe locking | ✅ done | 2 |  | claude | — | [US-EMDB-1](stories/US-EMDB-1.md) |
| [US-EMDB-1-3](tasks/US-EMDB-1-3.md) | Test: CacheManager LockAsync replaced with proper async lock pattern | ✅ done | 2 |  | claude | — | [US-EMDB-1](stories/US-EMDB-1.md) |
| [US-EMDB-1-4](tasks/US-EMDB-1-4.md) | Test: No Monitor.Enter usage in async code paths | ✅ done | 2 |  | claude | — | [US-EMDB-1](stories/US-EMDB-1.md) |
| [US-EMDB-1-5](tasks/US-EMDB-1-5.md) | Test: All existing unit tests still pass | ✅ done | 1 |  | claude | — | [US-EMDB-1](stories/US-EMDB-1.md) |
| [US-EMDB-10-1](tasks/US-EMDB-10-1.md) | Test: StorageManager implements IStorageManager | ⚪ todo | — |  | — | — | [US-EMDB-10](stories/US-EMDB-10.md) |
| [US-EMDB-10-2](tasks/US-EMDB-10-2.md) | Test: Proper initialization order per architecture docs | ⚪ todo | — |  | — | — | [US-EMDB-10](stories/US-EMDB-10.md) |
| [US-EMDB-10-3](tasks/US-EMDB-10-3.md) | Test: All IStorageManager methods delegate to correct managers | ⚪ todo | — |  | — | — | [US-EMDB-10](stories/US-EMDB-10.md) |
| [US-EMDB-10-4](tasks/US-EMDB-10-4.md) | Test: Proper disposal of all managed resources | ⚪ todo | — |  | — | — | [US-EMDB-10](stories/US-EMDB-10.md) |
| [US-EMDB-10-5](tasks/US-EMDB-10-5.md) | Test: File can be opened from existing or created new | ⚪ todo | — |  | — | — | [US-EMDB-10](stories/US-EMDB-10.md) |
| [US-EMDB-11-1](tasks/US-EMDB-11-1.md) | Test: MaintenanceManager uncommented and compiles | ⚪ todo | — |  | — | — | [US-EMDB-11](stories/US-EMDB-11.md) |
| [US-EMDB-11-2](tasks/US-EMDB-11-2.md) | Test: CompactAsync creates a new file with only latest block versions | ⚪ todo | — |  | — | — | [US-EMDB-11](stories/US-EMDB-11.md) |
| [US-EMDB-11-3](tasks/US-EMDB-11-3.md) | Test: Cleanup removes blocks listed in OutdatedOffsets | ⚪ todo | — |  | — | — | [US-EMDB-11](stories/US-EMDB-11.md) |
| [US-EMDB-11-4](tasks/US-EMDB-11-4.md) | Test: Metadata updated after compaction | ⚪ todo | — |  | — | — | [US-EMDB-11](stories/US-EMDB-11.md) |
| [US-EMDB-11-5](tasks/US-EMDB-11-5.md) | Test: File replacement is atomic (no data loss on failure) | ⚪ todo | — |  | — | — | [US-EMDB-11](stories/US-EMDB-11.md) |
| [US-EMDB-12-1](tasks/US-EMDB-12-1.md) | Test: WAL entries written before destructive operations | ⚪ todo | — |  | — | — | [US-EMDB-12](stories/US-EMDB-12.md) |
| [US-EMDB-12-2](tasks/US-EMDB-12-2.md) | Test: Recovery process detects incomplete operations on startup | ⚪ todo | — |  | — | — | [US-EMDB-12](stories/US-EMDB-12.md) |
| [US-EMDB-12-3](tasks/US-EMDB-12-3.md) | Test: Incomplete operations are rolled back or completed | ⚪ todo | — |  | — | — | [US-EMDB-12](stories/US-EMDB-12.md) |
| [US-EMDB-12-4](tasks/US-EMDB-12-4.md) | Test: WAL entries cleaned up after successful operations | ⚪ todo | — |  | — | — | [US-EMDB-12](stories/US-EMDB-12.md) |
| [US-EMDB-12-5](tasks/US-EMDB-12-5.md) | Test: System recovers correctly after simulated crash | ⚪ todo | — |  | — | — | [US-EMDB-12](stories/US-EMDB-12.md) |
| [US-EMDB-13-1](tasks/US-EMDB-13-1.md) | Test: All test projects compile without errors | ⚪ todo | — |  | — | — | [US-EMDB-13](stories/US-EMDB-13.md) |
| [US-EMDB-13-2](tasks/US-EMDB-13-2.md) | Test: Existing passing tests continue to pass | ⚪ todo | — |  | — | — | [US-EMDB-13](stories/US-EMDB-13.md) |
| [US-EMDB-13-3](tasks/US-EMDB-13-3.md) | Test: Test helper infrastructure (TestHelpers.cs) works with current APIs | ⚪ todo | — |  | — | — | [US-EMDB-13](stories/US-EMDB-13.md) |
| [US-EMDB-13-4](tasks/US-EMDB-13-4.md) | Test: Mock setups reference correct current types | ⚪ todo | — |  | — | — | [US-EMDB-13](stories/US-EMDB-13.md) |
| [US-EMDB-13-5](tasks/US-EMDB-13-5.md) | Test: dotnet test runs without compilation failures | ⚪ todo | — |  | — | — | [US-EMDB-13](stories/US-EMDB-13.md) |
| [US-EMDB-14-1](tasks/US-EMDB-14-1.md) | Test: Integration test for add email → retrieve email round trip | ⚪ todo | — |  | — | — | [US-EMDB-14](stories/US-EMDB-14.md) |
| [US-EMDB-14-2](tasks/US-EMDB-14-2.md) | Test: Integration test for folder create → add email → list emails | ⚪ todo | — |  | — | — | [US-EMDB-14](stories/US-EMDB-14.md) |
| [US-EMDB-14-3](tasks/US-EMDB-14-3.md) | Test: Integration test for move email between folders | ⚪ todo | — |  | — | — | [US-EMDB-14](stories/US-EMDB-14.md) |
| [US-EMDB-14-4](tasks/US-EMDB-14-4.md) | Test: Integration test for soft-delete email (move to Dead folder, BTree entry persists) | ⚪ todo | — |  | — | — | [US-EMDB-14](stories/US-EMDB-14.md) |
| [US-EMDB-14-5](tasks/US-EMDB-14-5.md) | Test: All tests use real file I/O (no mocks) with temp files | ⚪ todo | — |  | — | — | [US-EMDB-14](stories/US-EMDB-14.md) |
| [US-EMDB-15-1](tasks/US-EMDB-15-1.md) | Test: Benchmark for Protobuf vs JSON serialization of each content type | ⚪ todo | — |  | — | — | [US-EMDB-15](stories/US-EMDB-15.md) |
| [US-EMDB-15-2](tasks/US-EMDB-15-2.md) | Test: Benchmark for block write throughput at various sizes | ⚪ todo | — |  | — | — | [US-EMDB-15](stories/US-EMDB-15.md) |
| [US-EMDB-15-3](tasks/US-EMDB-15-3.md) | Test: Benchmark for block read with and without cache | ⚪ todo | — |  | — | — | [US-EMDB-15](stories/US-EMDB-15.md) |
| [US-EMDB-15-4](tasks/US-EMDB-15-4.md) | Test: Benchmark results logged in a reproducible format | ⚪ todo | — |  | — | — | [US-EMDB-15](stories/US-EMDB-15.md) |
| [US-EMDB-16-1](tasks/US-EMDB-16-1.md) | Test: Embedding model selected and documented in ADR | ⚪ todo | — |  | — | — | [US-EMDB-16](stories/US-EMDB-16.md) |
| [US-EMDB-16-2](tasks/US-EMDB-16-2.md) | Test: Model can generate embeddings for email subject+body+from fields | ⚪ todo | — |  | — | — | [US-EMDB-16](stories/US-EMDB-16.md) |
| [US-EMDB-16-3](tasks/US-EMDB-16-3.md) | Test: Embedding generation benchmarked (throughput at 1K and 10K emails) | ⚪ todo | — |  | — | — | [US-EMDB-16](stories/US-EMDB-16.md) |
| [US-EMDB-16-4](tasks/US-EMDB-16-4.md) | Test: NuGet dependencies added to EmailDB.Format.csproj | ⚪ todo | — |  | — | — | [US-EMDB-16](stories/US-EMDB-16.md) |
| [US-EMDB-16-5](tasks/US-EMDB-16-5.md) | Test: Model distribution strategy decided and documented (sidecar ONNX vs embedded resource) | ⚪ todo | 1 |  | — | — | [US-EMDB-16](stories/US-EMDB-16.md) |
| [US-EMDB-16-6](tasks/US-EMDB-16-6.md) | Test: Async/batched embedding generation API handles 1K+ emails without blocking caller | ⚪ todo | 2 |  | — | — | [US-EMDB-16](stories/US-EMDB-16.md) |
| [US-EMDB-17-1](tasks/US-EMDB-17-1.md) | Test: Sidecar .emdb.vec file created alongside .emdb on first email insert | ⚪ todo | — |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-2](tasks/US-EMDB-17-2.md) | Test: Embeddings persisted to .vec and restored correctly across close/reopen | ⚪ todo | — |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-3](tasks/US-EMDB-17-3.md) | Test: Email ID to vector index mapping maintained through add/delete operations | ⚪ todo | — |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-4](tasks/US-EMDB-17-4.md) | Test: .vec file format versioned — header contains format version, model ID, quantization type | ⚪ todo | — |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-5](tasks/US-EMDB-17-5.md) | Test: Sidecar can be deleted and rebuilt from .emdb source data | ⚪ todo | 2 |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-6](tasks/US-EMDB-17-6.md) | Test: Storage overhead benchmarked at 1K/10K/100K emails — .vec size vs .emdb size | ⚪ todo | 1 |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-7](tasks/US-EMDB-17-7.md) | Test: HNSW graph fully flushed to .vec on close — survives unclean shutdown via rebuild | ⚪ todo | 2 |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-17-8](tasks/US-EMDB-17-8.md) | Test: Thread-safe reader/writer lock protects concurrent search + insert on .vec | ⚪ todo | 1 |  | — | — | [US-EMDB-17](stories/US-EMDB-17.md) |
| [US-EMDB-18-1](tasks/US-EMDB-18-1.md) | Test: SearchAsync returns top-K results ranked by cosine similarity score | ⚪ todo | — |  | — | — | [US-EMDB-18](stories/US-EMDB-18.md) |
| [US-EMDB-18-2](tasks/US-EMDB-18-2.md) | Test: HNSW search at 100K emails completes in <5ms (excluding query embedding time) | ⚪ todo | — |  | — | — | [US-EMDB-18](stories/US-EMDB-18.md) |
| [US-EMDB-18-3](tasks/US-EMDB-18-3.md) | Test: Top-K parameter configurable (default 10, supports 1-100) | ⚪ todo | — |  | — | — | [US-EMDB-18](stories/US-EMDB-18.md) |
| [US-EMDB-18-4](tasks/US-EMDB-18-4.md) | Test: IVectorIndex interface abstracts HNSW — supports Add, Remove, Search, Serialize, Deserialize | ⚪ todo | — |  | — | — | [US-EMDB-18](stories/US-EMDB-18.md) |
| [US-EMDB-18-5](tasks/US-EMDB-18-5.md) | Test: Cross-shard search fans out to multiple .vec sidecars and merges results by score | ⚪ todo | 2 |  | — | — | [US-EMDB-18](stories/US-EMDB-18.md) |
| [US-EMDB-18-6](tasks/US-EMDB-18-6.md) | Test: HNSW graph serialized to .vec sidecar and restored identically on open | ⚪ todo | 2 |  | — | — | [US-EMDB-18](stories/US-EMDB-18.md) |
| [US-EMDB-19-1](tasks/US-EMDB-19-1.md) | Test: Inserting an email queues async embedding generation — does not block insert path | ⚪ todo | — |  | — | — | [US-EMDB-19](stories/US-EMDB-19.md) |
| [US-EMDB-19-2](tasks/US-EMDB-19-2.md) | Test: Deleting an email removes its vector from HNSW index and .vec sidecar | ⚪ todo | — |  | — | — | [US-EMDB-19](stories/US-EMDB-19.md) |
| [US-EMDB-19-3](tasks/US-EMDB-19-3.md) | Test: Bulk insert batches embedding generation — processes 1K emails without OOM | ⚪ todo | — |  | — | — | [US-EMDB-19](stories/US-EMDB-19.md) |
| [US-EMDB-19-4](tasks/US-EMDB-19-4.md) | Test: Index consistency verified after interleaved insert/delete/move sequences | ⚪ todo | — |  | — | — | [US-EMDB-19](stories/US-EMDB-19.md) |
| [US-EMDB-19-5](tasks/US-EMDB-19-5.md) | Test: Cold start — missing .vec triggers background rebuild from .emdb, search returns 'indexing' status | ⚪ todo | 2 |  | — | — | [US-EMDB-19](stories/US-EMDB-19.md) |
| [US-EMDB-19-6](tasks/US-EMDB-19-6.md) | Test: Model version tracked in .vec header — version mismatch triggers re-embedding | ⚪ todo | 1 |  | — | — | [US-EMDB-19](stories/US-EMDB-19.md) |
| [US-EMDB-2-1](tasks/US-EMDB-2-1.md) | Test: Silent catch blocks either propagate errors via Result or log them | ✅ done | 2 |  | claude | — | [US-EMDB-2](stories/US-EMDB-2.md) |
| [US-EMDB-2-2](tasks/US-EMDB-2-2.md) | Test: Console.WriteLine calls replaced with ILogger or similar abstraction | ✅ done | 2 |  | claude | — | [US-EMDB-2](stories/US-EMDB-2.md) |
| [US-EMDB-2-3](tasks/US-EMDB-2-3.md) | Test: Error context preserved in all failure paths | ✅ done | 2 |  | claude | — | [US-EMDB-2](stories/US-EMDB-2.md) |
| [US-EMDB-20-1](tasks/US-EMDB-20-1.md) | Test: Benchmark added to EmailDB.Benchmark.SQLite for embedding search | ⚪ todo | — |  | — | — | [US-EMDB-20](stories/US-EMDB-20.md) |
| [US-EMDB-20-2](tasks/US-EMDB-20-2.md) | Test: Results show embedding search < 50ms at 100K emails | ⚪ todo | — |  | — | — | [US-EMDB-20](stories/US-EMDB-20.md) |
| [US-EMDB-20-3](tasks/US-EMDB-20-3.md) | Test: Comparison table updated with embedding search vs FTS5 | ⚪ todo | — |  | — | — | [US-EMDB-20](stories/US-EMDB-20.md) |
| [US-EMDB-20-4](tasks/US-EMDB-20-4.md) | Test: Results documented in benchmark_results.txt | ⚪ todo | — |  | — | — | [US-EMDB-20](stories/US-EMDB-20.md) |
| [US-EMDB-21-1](tasks/US-EMDB-21-1.md) | Test: Footer length written and read as same type (both Int64 or both Int32) | ✅ done | 1 |  | claude | — | [US-EMDB-21](stories/US-EMDB-21.md) |
| [US-EMDB-21-2](tasks/US-EMDB-21-2.md) | Test: Blocks written by WriteBlockToStream can be read by ReadBlockFromStreamInternal | ✅ done | 1 |  | claude | — | [US-EMDB-21](stories/US-EMDB-21.md) |
| [US-EMDB-21-3](tasks/US-EMDB-21-3.md) | Test: Round-trip test passes: write block then read back with identical payload | ✅ done | 1 |  | claude | — | [US-EMDB-21](stories/US-EMDB-21.md) |
| [US-EMDB-21-4](tasks/US-EMDB-21-4.md) | Test: Existing unit tests still pass | ✅ done | 1 |  | claude | — | [US-EMDB-21](stories/US-EMDB-21.md) |
| [US-EMDB-22-1](tasks/US-EMDB-22-1.md) | Test: InitializeNewFile does not corrupt currentPosition in RawBlockManager | ✅ done | 2 |  | claude | — | [US-EMDB-22](stories/US-EMDB-22.md) |
| [US-EMDB-22-2](tasks/US-EMDB-22-2.md) | Test: OverrideLocation writes restore original position after seeking | ✅ done | 2 |  | claude | — | [US-EMDB-22](stories/US-EMDB-22.md) |
| [US-EMDB-22-3](tasks/US-EMDB-22-3.md) | Test: System blocks (WAL/FolderTree/Metadata) not overwritten after InitializeNewFile | ✅ done | 2 |  | claude | — | [US-EMDB-22](stories/US-EMDB-22.md) |
| [US-EMDB-22-4](tasks/US-EMDB-22-4.md) | Test: Integration test: InitializeNewFile followed by email insert reads back correctly | ✅ done | 2 |  | claude | — | [US-EMDB-22](stories/US-EMDB-22.md) |
| [US-EMDB-23-1](tasks/US-EMDB-23-1.md) | Test: SQ8 quantization reduces vector memory by ~4x | ⚪ todo | — |  | — | — | [US-EMDB-23](stories/US-EMDB-23.md) |
| [US-EMDB-23-2](tasks/US-EMDB-23-2.md) | Test: Recall at 100K emails >= 98% vs float32 baseline | ⚪ todo | — |  | — | — | [US-EMDB-23](stories/US-EMDB-23.md) |
| [US-EMDB-23-3](tasks/US-EMDB-23-3.md) | Test: SIMD int8 distance function implemented via System.Runtime.Intrinsics | ⚪ todo | — |  | — | — | [US-EMDB-23](stories/US-EMDB-23.md) |
| [US-EMDB-23-4](tasks/US-EMDB-23-4.md) | Test: Calibration parameters persisted in .vec sidecar | ⚪ todo | — |  | — | — | [US-EMDB-23](stories/US-EMDB-23.md) |
| [US-EMDB-23-5](tasks/US-EMDB-23-5.md) | Test: IVectorIndex interface allows transparent swap between float32 and SQ8 | ⚪ todo | — |  | — | — | [US-EMDB-23](stories/US-EMDB-23.md) |
| [US-EMDB-23-6](tasks/US-EMDB-23-6.md) | Test: Benchmark: SQ8 search latency at 1M emails < 3ms | ⚪ todo | — |  | — | — | [US-EMDB-23](stories/US-EMDB-23.md) |
| [US-EMDB-24-1](tasks/US-EMDB-24-1.md) | Test: Text preparation function: email → embedding-ready string documented and implemented | ⚪ todo | — |  | — | — | [US-EMDB-24](stories/US-EMDB-24.md) |
| [US-EMDB-24-2](tasks/US-EMDB-24-2.md) | Test: Truncation strategy decided and tested (256 token limit handling) | ⚪ todo | — |  | — | — | [US-EMDB-24](stories/US-EMDB-24.md) |
| [US-EMDB-24-3](tasks/US-EMDB-24-3.md) | Test: HTML stripping for HTML-body emails | ⚪ todo | — |  | — | — | [US-EMDB-24](stories/US-EMDB-24.md) |
| [US-EMDB-24-4](tasks/US-EMDB-24-4.md) | Test: Query embedding latency measured and added to end-to-end search budget | ⚪ todo | — |  | — | — | [US-EMDB-24](stories/US-EMDB-24.md) |
| [US-EMDB-24-5](tasks/US-EMDB-24-5.md) | Test: Distance metric documented as cosine similarity in ADR | ⚪ todo | — |  | — | — | [US-EMDB-24](stories/US-EMDB-24.md) |
| [US-EMDB-25-1](tasks/US-EMDB-25-1.md) | Test: BlockType enum extended with BTreeLeaf/BTreeInternal/IndexRoot/EmailContent | ✅ done | 1 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-25-2](tasks/US-EMDB-25-2.md) | Test: Leaf node binary layout serializes and deserializes correctly with 82 entries at 4036B | ✅ done | 2 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-25-3](tasks/US-EMDB-25-3.md) | Test: Internal node binary layout serializes and deserializes correctly with 55 children + Merkle hashes at 4036B | ✅ done | 2 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-25-4](tasks/US-EMDB-25-4.md) | Test: IndexRoot payload round-trips correctly | ✅ done | 2 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-25-5](tasks/US-EMDB-25-5.md) | Test: BLAKE3 hashing produces correct 32-byte digests for node content | ✅ done | 1 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-25-6](tasks/US-EMDB-25-6.md) | Test: BlockIdGenerator allocates IDs in a dedicated B+-tree range without collisions | ✅ done | 2 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-25-7](tasks/US-EMDB-25-7.md) | Test: Custom binary serializer is at least 3x faster than protobuf for fixed-size node data | ✅ done | 2 |  | claude | — | [US-EMDB-25](stories/US-EMDB-25.md) |
| [US-EMDB-26-1](tasks/US-EMDB-26-1.md) | Test: Single insert into empty tree creates root leaf node | ✅ done | 2 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-26-2](tasks/US-EMDB-26-2.md) | Test: Insert into full leaf triggers split producing two leaves and a new internal root | ✅ done | 3 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-26-3](tasks/US-EMDB-26-3.md) | Test: Cascade split through 3 levels produces correct tree structure | ✅ done | 3 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-26-4](tasks/US-EMDB-26-4.md) | Test: Inserting duplicate key updates value without creating duplicate entry | ✅ done | 2 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-26-5](tasks/US-EMDB-26-5.md) | Test: Old node blocks remain in file untouched after insert (append-only verified) | ✅ done | 2 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-26-6](tasks/US-EMDB-26-6.md) | Test: Tree maintains sorted key order after 10K random inserts | ✅ done | 2 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-26-7](tasks/US-EMDB-26-7.md) | Test: All new blocks written via RawBlockManager.WriteBlockAsync | ✅ done | 2 |  | claude | — | [US-EMDB-26](stories/US-EMDB-26.md) |
| [US-EMDB-27-1](tasks/US-EMDB-27-1.md) | Test: Point lookup returns correct BlockLocation for existing key | ✅ done | 2 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-27-2](tasks/US-EMDB-27-2.md) | Test: Point lookup returns not-found for missing key | ✅ done | 1 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-27-3](tasks/US-EMDB-27-3.md) | Test: Range query returns all entries in sorted order between start and end keys | ✅ done | 2 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-27-4](tasks/US-EMDB-27-4.md) | Test: Range query works across multiple leaf nodes (backtrack navigation) | ✅ done | 2 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-27-5](tasks/US-EMDB-27-5.md) | Test: Lookup reads ≤ 4 blocks for a tree with 10M entries | ✅ done | 2 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-27-6](tasks/US-EMDB-27-6.md) | Test: Concurrent readers do not block each other | ✅ done | 2 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-27-7](tasks/US-EMDB-27-7.md) | Test: Node reads are served from CacheManager when available | ✅ done | 2 |  | claude | — | [US-EMDB-27](stories/US-EMDB-27.md) |
| [US-EMDB-28-1](tasks/US-EMDB-28-1.md) | Test: Delete removes key from lookup results | ✅ done | 2 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-28-2](tasks/US-EMDB-28-2.md) | Test: Delete of non-existent key returns not-found without modifying tree | ✅ done | 1 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-28-3](tasks/US-EMDB-28-3.md) | Test: Underflow triggers merge with sibling via parent | ✅ done | 2 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-28-4](tasks/US-EMDB-28-4.md) | Test: Root collapse produces correct single-level tree | ✅ done | 1 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-28-5](tasks/US-EMDB-28-5.md) | Test: EntryCount decremented correctly after delete | ✅ done | 1 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-28-6](tasks/US-EMDB-28-6.md) | Test: Old leaf blocks remain in file (append-only) | ✅ done | 2 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-28-7](tasks/US-EMDB-28-7.md) | Test: Concurrent delete and read operations are safe | ✅ done | 2 |  | claude | — | [US-EMDB-28](stories/US-EMDB-28.md) |
| [US-EMDB-29-1](tasks/US-EMDB-29-1.md) | Test: Every B+-tree block contains PrevChainHash linking to previous block | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-2](tasks/US-EMDB-29-2.md) | Test: Modifying any block's payload causes chain verification failure | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-3](tasks/US-EMDB-29-3.md) | Test: Internal node child hashes match actual child node content hashes | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-4](tasks/US-EMDB-29-4.md) | Test: Root hash in IndexRoot matches actual root node hash | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-5](tasks/US-EMDB-29-5.md) | Test: Quick verification detects tampered IndexRoot chain | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-6](tasks/US-EMDB-29-6.md) | Test: Standard verification detects tampered node on key lookup path | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-7](tasks/US-EMDB-29-7.md) | Test: Full verification traverses all nodes and reports first integrity violation | ✅ done | 3 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-8](tasks/US-EMDB-29-8.md) | Test: Chain handles genesis block (first block has zeroed PrevChainHash) | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-29-9](tasks/US-EMDB-29-9.md) | Test: Compaction produces valid new chain with genesis | ✅ done | 2 |  | claude | — | [US-EMDB-29](stories/US-EMDB-29.md) |
| [US-EMDB-3-1](tasks/US-EMDB-3-1.md) | Test: ScanExistingBlocks returns Task instead of async void | ✅ done | 1 |  | claude | — | [US-EMDB-3](stories/US-EMDB-3.md) |
| [US-EMDB-3-2](tasks/US-EMDB-3-2.md) | Test: FileStream reference swap is safe with proper disposal | ✅ done | 2 |  | claude | — | [US-EMDB-3](stories/US-EMDB-3.md) |
| [US-EMDB-3-3](tasks/US-EMDB-3-3.md) | Test: MemoryMappedFile disposed in all code paths | ✅ done | 2 |  | claude | — | [US-EMDB-3](stories/US-EMDB-3.md) |
| [US-EMDB-3-4](tasks/US-EMDB-3-4.md) | Test: CapnProto duplicate EnterWriteLock fixed | ✅ done | 1 |  | claude | — | [US-EMDB-3](stories/US-EMDB-3.md) |
| [US-EMDB-30-1](tasks/US-EMDB-30-1.md) | Test: Inserts are buffered and not visible in B+-tree until flush | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-2](tasks/US-EMDB-30-2.md) | Test: Flush writes all buffered entries to B+-tree atomically | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-3](tasks/US-EMDB-30-3.md) | Test: Count threshold triggers automatic flush | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-4](tasks/US-EMDB-30-4.md) | Test: Time threshold triggers automatic flush | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-5](tasks/US-EMDB-30-5.md) | Test: Explicit FlushAsync() call works correctly | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-6](tasks/US-EMDB-30-6.md) | Test: WAL entries survive process crash (persisted to block before flush) | ✅ done | 3 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-7](tasks/US-EMDB-30-7.md) | Test: Concurrent inserts into WAL do not corrupt entries | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-8](tasks/US-EMDB-30-8.md) | Test: Batch of 100 inserts produces fewer node writes than 100 individual inserts | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-30-9](tasks/US-EMDB-30-9.md) | Test: IndexRoot written after every successful flush | ✅ done | 2 |  | claude | — | [US-EMDB-30](stories/US-EMDB-30.md) |
| [US-EMDB-31-1](tasks/US-EMDB-31-1.md) | Test: Clean shutdown followed by reopen loads correct IndexRoot and tree | ✅ done | 2 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-2](tasks/US-EMDB-31-2.md) | Test: Crash after node writes but before IndexRoot reverts to previous valid tree | ✅ done | 3 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-3](tasks/US-EMDB-31-3.md) | Test: Crash after IndexRoot write recovers to latest tree | ✅ done | 2 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-4](tasks/US-EMDB-31-4.md) | Test: WAL entries written after last IndexRoot are replayed on recovery | ✅ done | 3 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-5](tasks/US-EMDB-31-5.md) | Test: Recovery with empty file initializes empty tree | ✅ done | 1 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-6](tasks/US-EMDB-31-6.md) | Test: Corrupted IndexRoot falls back to previous valid IndexRoot with warning | ✅ done | 3 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-7](tasks/US-EMDB-31-7.md) | Test: Recovery completes in O(WAL_size) time not O(file_size) | ✅ done | 2 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-31-8](tasks/US-EMDB-31-8.md) | Test: Double-fsync write ordering is enforced (nodes before IndexRoot) | ✅ done | 2 |  | claude | — | [US-EMDB-31](stories/US-EMDB-31.md) |
| [US-EMDB-32-1](tasks/US-EMDB-32-1.md) | Test: Compaction produces file with only live B+-tree nodes | ✅ done | 2 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-32-2](tasks/US-EMDB-32-2.md) | Test: Tree is fully functional after compaction (all lookups still work) | ✅ done | 2 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-32-3](tasks/US-EMDB-32-3.md) | Test: Dead node space is reclaimed (file size reduced) | ✅ done | 2 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-32-4](tasks/US-EMDB-32-4.md) | Test: New hash chain starts with genesis in compacted file | ✅ done | 1 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-32-5](tasks/US-EMDB-32-5.md) | Test: New IndexRoot in compacted file has correct root hash | ✅ done | 2 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-32-6](tasks/US-EMDB-32-6.md) | Test: Compaction does not block concurrent reads during tree walk phase | ✅ done | 2 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-32-7](tasks/US-EMDB-32-7.md) | Test: Space amplification metrics are reported after compaction | ✅ done | 1 |  | claude | — | [US-EMDB-32](stories/US-EMDB-32.md) |
| [US-EMDB-33-1](tasks/US-EMDB-33-1.md) | Test: EmailManager.AddEmailAsync stores email and indexes it in B+-tree | ✅ done | 2 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-2](tasks/US-EMDB-33-2.md) | Test: EmailManager.GetEmailAsync retrieves email via B+-tree lookup | ✅ done | 2 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-3](tasks/US-EMDB-33-3.md) | Test: EmailManager.DeleteEmailAsync removes from index and marks content outdated | ✅ done | 2 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-4](tasks/US-EMDB-33-4.md) | Test: BTreeIndex reads upper nodes from CacheManager on subsequent lookups | ✅ done | 2 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-5](tasks/US-EMDB-33-5.md) | Test: ZoneTree NuGet dependency removed from .csproj | ✅ done | 1 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-6](tasks/US-EMDB-33-6.md) | Test: ZoneTree/*.cs files removed or archived | ✅ done | 1 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-7](tasks/US-EMDB-33-7.md) | Test: iStorageManager interface updated for B+-tree operations | ✅ done | 2 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-33-8](tasks/US-EMDB-33-8.md) | Test: End-to-end test: add 1000 emails then retrieve each by ID | ✅ done | 3 |  | claude | — | [US-EMDB-33](stories/US-EMDB-33.md) |
| [US-EMDB-34-1](tasks/US-EMDB-34-1.md) | Test: Insert throughput benchmarked at 4 scale points with results documented | ✅ done | 3 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-2](tasks/US-EMDB-34-2.md) | Test: Point lookup latency benchmarked at 4 scale points | ✅ done | 2 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-3](tasks/US-EMDB-34-3.md) | Test: Range query throughput measured | ✅ done | 2 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-4](tasks/US-EMDB-34-4.md) | Test: WAL batch size vs flush latency curve produced | ✅ done | 2 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-5](tasks/US-EMDB-34-5.md) | Test: Concurrent read/write stress test passes without corruption | ✅ done | 3 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-6](tasks/US-EMDB-34-6.md) | Test: Crash recovery stress test passes 100/100 trials | ✅ done | 2 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-7](tasks/US-EMDB-34-7.md) | Test: Space amplification metrics documented with and without compaction | ✅ done | 3 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-34-8](tasks/US-EMDB-34-8.md) | Test: Results added to benchmark_results.txt | ✅ done | 2 |  | claude | — | [US-EMDB-34](stories/US-EMDB-34.md) |
| [US-EMDB-35-1](tasks/US-EMDB-35-1.md) | Test: HeaderContent has WALRegionOffset and WALRegionSize fields | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-2](tasks/US-EMDB-35-2.md) | Test: IndexRoot has LastFlushedWALSequence field with PayloadSize=98 | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-3](tasks/US-EMDB-35-3.md) | Test: MetadataContent has BTreeRootOffset field | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-4](tasks/US-EMDB-35-4.md) | Test: WALContent.cs contains WALRegionHeader struct replacing old models | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-5](tasks/US-EMDB-35-5.md) | Test: BTreeNodeSerializer round-trips 98-byte IndexRoot correctly | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-6](tasks/US-EMDB-35-6.md) | Test: BTreeNodeSerializer handles old 90-byte IndexRoot payloads with LastFlushedWALSequence=0 | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-7](tasks/US-EMDB-35-7.md) | Test: RawBlockManager.WriteRawBytesAsync writes at specified offset using fileLock | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-35-8](tasks/US-EMDB-35-8.md) | Test: RawBlockManager.ReadRawBytesAsync reads from specified offset using fileLock | ⚪ todo | — |  | — | — | [US-EMDB-35](stories/US-EMDB-35.md) |
| [US-EMDB-36-1](tasks/US-EMDB-36-1.md) | Test: InitializeNewFile creates a file at least 16MB in size | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-2](tasks/US-EMDB-36-2.md) | Test: WAL block (BlockId=3) has exactly 16777216 bytes of payload | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-3](tasks/US-EMDB-36-3.md) | Test: WALRegionHeader at start of WAL payload has Version=1 EntryCount=0 dirty=0 | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-4](tasks/US-EMDB-36-4.md) | Test: HeaderContent.WALRegionOffset points to correct payload start offset | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-5](tasks/US-EMDB-36-5.md) | Test: HeaderContent.WALRegionSize equals 16777216 | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-6](tasks/US-EMDB-36-6.md) | Test: Block scanner sees exactly 4 blocks (Header WAL FolderTree Metadata) after init | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-7](tasks/US-EMDB-36-7.md) | Test: FolderTree and Metadata blocks are accessible at offsets after the WAL region | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-36-8](tasks/US-EMDB-36-8.md) | Test: Existing tests for InitializeNewFile still pass | ⚪ todo | — |  | — | — | [US-EMDB-36](stories/US-EMDB-36.md) |
| [US-EMDB-37-1](tasks/US-EMDB-37-1.md) | Test: Empty tree bulk insert builds tree with optimally packed leaves (near MaxEntries per leaf) | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-2](tasks/US-EMDB-37-2.md) | Test: 10000 sorted entries produce correct tree height for the branching factor | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-3](tasks/US-EMDB-37-3.md) | Test: All leaves contain entries in globally sorted order | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-4](tasks/US-EMDB-37-4.md) | Test: Merge with existing tree preserves all old entries plus new entries | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-5](tasks/US-EMDB-37-5.md) | Test: Duplicate keys across old tree and new entries are handled as upserts | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-6](tasks/US-EMDB-37-6.md) | Test: Bulk insert of 100K entries produces correct EntryCount in IndexRoot | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-7](tasks/US-EMDB-37-7.md) | Test: Single IndexRoot written per bulk insert (not one per entry) | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-37-8](tasks/US-EMDB-37-8.md) | Test: Tree built by bulk insert passes Merkle hash verification | ⚪ todo | — |  | — | — | [US-EMDB-37](stories/US-EMDB-37.md) |
| [US-EMDB-38-1](tasks/US-EMDB-38-1.md) | Test: IBlockEncryptionProvider interface with Encrypt/Decrypt/ShouldEncrypt/OverheadBytes/IsEnabled | ✅ done | 1 |  | claude | — | [US-EMDB-38](stories/US-EMDB-38.md) |
| [US-EMDB-38-2](tasks/US-EMDB-38-2.md) | Test: NullBlockEncryptionProvider passes through all payloads unchanged | ✅ done | 1 |  | claude | — | [US-EMDB-38](stories/US-EMDB-38.md) |
| [US-EMDB-38-3](tasks/US-EMDB-38-3.md) | Test: Block.cs has FlagEncrypted constant and IsEncrypted property | ✅ done | 1 |  | claude | — | [US-EMDB-38](stories/US-EMDB-38.md) |
| [US-EMDB-38-4](tasks/US-EMDB-38-4.md) | Test: All existing tests pass unchanged | ✅ done | 1 |  | claude | — | [US-EMDB-38](stories/US-EMDB-38.md) |
| [US-EMDB-39-1](tasks/US-EMDB-39-1.md) | Test: Encrypts payload to format Nonce(12) + Ciphertext(N) + AuthTag(16) | ✅ done | 2 |  | claude | — | [US-EMDB-39](stories/US-EMDB-39.md) |
| [US-EMDB-39-2](tasks/US-EMDB-39-2.md) | Test: Nonce = BlockId(8 bytes) + Random(4 bytes) | ✅ done | 1 |  | claude | — | [US-EMDB-39](stories/US-EMDB-39.md) |
| [US-EMDB-39-3](tasks/US-EMDB-39-3.md) | Test: Decrypts and verifies auth tag | ✅ done | 1 |  | claude | — | [US-EMDB-39](stories/US-EMDB-39.md) |
| [US-EMDB-39-4](tasks/US-EMDB-39-4.md) | Test: Uses System.Security.Cryptography.AesGcm | ✅ done | 1 |  | claude | — | [US-EMDB-39](stories/US-EMDB-39.md) |
| [US-EMDB-39-5](tasks/US-EMDB-39-5.md) | Test: Round-trip encrypt/decrypt produces identical plaintext | ✅ done | 1 |  | claude | — | [US-EMDB-39](stories/US-EMDB-39.md) |
| [US-EMDB-39-6](tasks/US-EMDB-39-6.md) | Test: Wrong key fails with clear error | ✅ done | 1 |  | claude | — | [US-EMDB-39](stories/US-EMDB-39.md) |
| [US-EMDB-4-1](tasks/US-EMDB-4-1.md) | Test: Single Protobuf library chosen and documented in architecture decisions | ✅ done | 2 |  | claude | — | [US-EMDB-4](stories/US-EMDB-4.md) |
| [US-EMDB-4-2](tasks/US-EMDB-4-2.md) | Test: Duplicate model files removed — one canonical location | ✅ done | 2 |  | claude | — | [US-EMDB-4](stories/US-EMDB-4.md) |
| [US-EMDB-4-3](tasks/US-EMDB-4-3.md) | Test: All Protobuf models have correct serialization attributes | ✅ done | 2 |  | claude | — | [US-EMDB-4](stories/US-EMDB-4.md) |
| [US-EMDB-4-4](tasks/US-EMDB-4-4.md) | Test: Project compiles cleanly with no ambiguous type references | ✅ done | 1 |  | claude | — | [US-EMDB-4](stories/US-EMDB-4.md) |
| [US-EMDB-40-1](tasks/US-EMDB-40-1.md) | Test: Default policy encrypts EmailContent Folder FolderTree Segment WAL | ✅ done | 2 |  | claude | — | [US-EMDB-40](stories/US-EMDB-40.md) |
| [US-EMDB-40-2](tasks/US-EMDB-40-2.md) | Test: Full policy encrypts all block types except header Metadata at offset 0 | ✅ done | 1 |  | claude | — | [US-EMDB-40](stories/US-EMDB-40.md) |
| [US-EMDB-40-3](tasks/US-EMDB-40-3.md) | Test: ShouldEncrypt returns correct result per policy | ✅ done | 1 |  | claude | — | [US-EMDB-40](stories/US-EMDB-40.md) |
| [US-EMDB-40-4](tasks/US-EMDB-40-4.md) | Test: Custom policies can be constructed | ✅ done | 1 |  | claude | — | [US-EMDB-40](stories/US-EMDB-40.md) |
| [US-EMDB-41-1](tasks/US-EMDB-41-1.md) | Test: DeriveFromPassword uses Argon2id with 64MB memory 3 iterations 4 parallelism | ✅ done | 1 |  | claude | — | [US-EMDB-41](stories/US-EMDB-41.md) |
| [US-EMDB-41-2](tasks/US-EMDB-41-2.md) | Test: Same password + salt always produces same 32-byte key | ✅ done | 1 |  | claude | — | [US-EMDB-41](stories/US-EMDB-41.md) |
| [US-EMDB-41-3](tasks/US-EMDB-41-3.md) | Test: Different salt produces different key | ✅ done | 1 |  | claude | — | [US-EMDB-41](stories/US-EMDB-41.md) |
| [US-EMDB-41-4](tasks/US-EMDB-41-4.md) | Test: LoadFromKeyFile reads exactly 32 bytes | ✅ done | 1 |  | claude | — | [US-EMDB-41](stories/US-EMDB-41.md) |
| [US-EMDB-41-5](tasks/US-EMDB-41-5.md) | Test: GenerateSalt produces 16 cryptographically random bytes | ✅ done | 1 |  | claude | — | [US-EMDB-41](stories/US-EMDB-41.md) |
| [US-EMDB-42-1](tasks/US-EMDB-42-1.md) | Test: EncryptionHeader contains magic scheme version algorithm ID KDF type salt key verification token | ✅ done | 2 |  | claude | — | [US-EMDB-42](stories/US-EMDB-42.md) |
| [US-EMDB-42-2](tasks/US-EMDB-42-2.md) | Test: Key verification token = encrypt known plaintext EMDB with derived key | ✅ done | 1 |  | claude | — | [US-EMDB-42](stories/US-EMDB-42.md) |
| [US-EMDB-42-3](tasks/US-EMDB-42-3.md) | Test: Wrong key = immediate error | ✅ done | 1 |  | claude | — | [US-EMDB-42](stories/US-EMDB-42.md) |
| [US-EMDB-42-4](tasks/US-EMDB-42-4.md) | Test: EncryptionHeaderManager reads/writes header | ✅ done | 2 |  | claude | — | [US-EMDB-42](stories/US-EMDB-42.md) |
| [US-EMDB-42-5](tasks/US-EMDB-42-5.md) | Test: Unencrypted files have no EncryptionHeader | ✅ done | 1 |  | claude | — | [US-EMDB-42](stories/US-EMDB-42.md) |
| [US-EMDB-43-1](tasks/US-EMDB-43-1.md) | Test: CacheManager accepts optional IBlockEncryptionProvider | ✅ done | 1 |  | claude | — | [US-EMDB-43](stories/US-EMDB-43.md) |
| [US-EMDB-43-2](tasks/US-EMDB-43-2.md) | Test: WriteBlockAsync encrypts payload with active DEK and sets Flags bit 0 plus key epoch | ✅ done | 2 |  | claude | — | [US-EMDB-43](stories/US-EMDB-43.md) |
| [US-EMDB-43-3](tasks/US-EMDB-43-3.md) | Test: ReadBlockAsync reads key epoch from Flags and decrypts with correct DEK | ✅ done | 2 |  | claude | — | [US-EMDB-43](stories/US-EMDB-43.md) |
| [US-EMDB-43-4](tasks/US-EMDB-43-4.md) | Test: All typed methods encrypt/decrypt correctly with multi-epoch DEKs | ✅ done | 2 |  | claude | — | [US-EMDB-43](stories/US-EMDB-43.md) |
| [US-EMDB-43-5](tasks/US-EMDB-43-5.md) | Test: Cache stores decrypted content objects | ✅ done | 1 |  | claude | — | [US-EMDB-43](stories/US-EMDB-43.md) |
| [US-EMDB-43-6](tasks/US-EMDB-43-6.md) | Test: Blocks written with different key epochs are all readable | ✅ done | 2 |  | claude | — | [US-EMDB-43](stories/US-EMDB-43.md) |
| [US-EMDB-44-1](tasks/US-EMDB-44-1.md) | Test: BTreeIndex accepts optional IBlockEncryptionProvider | ✅ done | 2 |  | claude | — | [US-EMDB-44](stories/US-EMDB-44.md) |
| [US-EMDB-44-2](tasks/US-EMDB-44-2.md) | Test: Write methods encrypt after serialization using active DEK and stamp key epoch | ✅ done | 2 |  | claude | — | [US-EMDB-44](stories/US-EMDB-44.md) |
| [US-EMDB-44-3](tasks/US-EMDB-44-3.md) | Test: Read methods read key epoch from Flags and decrypt before deserialization | ✅ done | 2 |  | claude | — | [US-EMDB-44](stories/US-EMDB-44.md) |
| [US-EMDB-44-4](tasks/US-EMDB-44-4.md) | Test: BLAKE3 hash chain verification passes after decrypt | ✅ done | 2 |  | claude | — | [US-EMDB-44](stories/US-EMDB-44.md) |
| [US-EMDB-44-5](tasks/US-EMDB-44-5.md) | Test: All BTree operations work correctly with encryption across key epochs | ✅ done | 2 |  | claude | — | [US-EMDB-44](stories/US-EMDB-44.md) |
| [US-EMDB-45-1](tasks/US-EMDB-45-1.md) | Test: WAL blocks encrypted with active DEK and key epoch stamped in Flags | ✅ done | 2 |  | claude | — | [US-EMDB-45](stories/US-EMDB-45.md) |
| [US-EMDB-45-2](tasks/US-EMDB-45-2.md) | Test: Write methods encrypt WAL content with active DEK | ✅ done | 2 |  | claude | — | [US-EMDB-45](stories/US-EMDB-45.md) |
| [US-EMDB-45-3](tasks/US-EMDB-45-3.md) | Test: RecoverFromDisk reads key epoch from each WAL block and decrypts with correct DEK | ✅ done | 1 |  | claude | — | [US-EMDB-45](stories/US-EMDB-45.md) |
| [US-EMDB-45-4](tasks/US-EMDB-45-4.md) | Test: WAL recovery works correctly with encrypted WAL blocks across multiple key epochs | ✅ done | 1 |  | claude | — | [US-EMDB-45](stories/US-EMDB-45.md) |
| [US-EMDB-46-1](tasks/US-EMDB-46-1.md) | Test: CompactAsync accepts optional re-encrypt flag (default false) | ✅ done | 1 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-46-2](tasks/US-EMDB-46-2.md) | Test: When re-encrypt enabled blocks decrypted with original DEK and re-encrypted with active DEK | ✅ done | 2 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-46-3](tasks/US-EMDB-46-3.md) | Test: After re-encryption all blocks have current active epoch in Flags | ✅ done | 1 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-46-4](tasks/US-EMDB-46-4.md) | Test: All data readable after compaction with or without re-encryption | ✅ done | 2 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-46-5](tasks/US-EMDB-46-5.md) | Test: Retired DEKs with no remaining block references pruned from key store | ✅ done | 2 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-46-6](tasks/US-EMDB-46-6.md) | Test: New file gets updated key store with only active DEKs | ✅ done | 2 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-46-7](tasks/US-EMDB-46-7.md) | Test: Compaction without re-encrypt flag works as normal space reclamation | ✅ done | 2 |  | claude | — | [US-EMDB-46](stories/US-EMDB-46.md) |
| [US-EMDB-47-1](tasks/US-EMDB-47-1.md) | Test: ADR-015 in DECISIONS.md documents KEK/DEK key-wrapping architecture | ⚪ todo | — |  | — | — | [US-EMDB-47](stories/US-EMDB-47.md) |
| [US-EMDB-47-2](tasks/US-EMDB-47-2.md) | Test: EmailDB_FileFormat_Spec.md updated with key store block format and key epoch Flags layout | ⚪ todo | — |  | — | — | [US-EMDB-47](stories/US-EMDB-47.md) |
| [US-EMDB-47-3](tasks/US-EMDB-47-3.md) | Test: ARCHITECTURE.md updated with key hierarchy diagram and encryption flow | ⚪ todo | — |  | — | — | [US-EMDB-47](stories/US-EMDB-47.md) |
| [US-EMDB-47-4](tasks/US-EMDB-47-4.md) | Test: VISION.md updated with zero-cost password changes and forward secrecy | ⚪ todo | — |  | — | — | [US-EMDB-47](stories/US-EMDB-47.md) |
| [US-EMDB-48-1](tasks/US-EMDB-48-1.md) | Test: BenchmarkDotNet tests for encrypted vs unencrypted | ⚪ todo | — |  | — | — | [US-EMDB-48](stories/US-EMDB-48.md) |
| [US-EMDB-48-2](tasks/US-EMDB-48-2.md) | Test: BTree latency with and without encryption | ⚪ todo | — |  | — | — | [US-EMDB-48](stories/US-EMDB-48.md) |
| [US-EMDB-48-3](tasks/US-EMDB-48-3.md) | Test: Bulk throughput measured in MB/s | ⚪ todo | — |  | — | — | [US-EMDB-48](stories/US-EMDB-48.md) |
| [US-EMDB-48-4](tasks/US-EMDB-48-4.md) | Test: Results documented | ⚪ todo | — |  | — | — | [US-EMDB-48](stories/US-EMDB-48.md) |
| [US-EMDB-49-1](tasks/US-EMDB-49-1.md) | Test: HeaderChecksumSize and PayloadChecksumSize constants are 16 | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-2](tasks/US-EMDB-49-2.md) | Test: TotalFixedOverhead is 84 | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-3](tasks/US-EMDB-49-3.md) | Test: ComputeChecksum returns byte[16] using BLAKE3 | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-4](tasks/US-EMDB-49-4.md) | Test: Write path writes 16-byte checksums | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-5](tasks/US-EMDB-49-5.md) | Test: Read path reads and verifies 16-byte checksums | ✅ done | 2 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-6](tasks/US-EMDB-49-6.md) | Test: Scan and TryReadBlockLocation use 16-byte checksums | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-7](tasks/US-EMDB-49-7.md) | Test: Force.Crc32 import removed from RawBlockManager | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-8](tasks/US-EMDB-49-8.md) | Test: Block.HeaderChecksum and PayloadChecksum are byte[] not uint | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-49-9](tasks/US-EMDB-49-9.md) | Test: Empty payload uses 16 zero bytes as checksum | ✅ done | 1 |  | claude | — | [US-EMDB-49](stories/US-EMDB-49.md) |
| [US-EMDB-5-1](tasks/US-EMDB-5-1.md) | Test: ProtobufBlockContentSerializer implements IBlockContentSerializer | ✅ done | 2 |  | claude | — | [US-EMDB-5](stories/US-EMDB-5.md) |
| [US-EMDB-5-2](tasks/US-EMDB-5-2.md) | Test: All 6 content types serialize/deserialize correctly via Protobuf | ✅ done | 3 |  | claude | — | [US-EMDB-5](stories/US-EMDB-5.md) |
| [US-EMDB-5-3](tasks/US-EMDB-5-3.md) | Test: PayloadEncoding enum is respected when reading blocks | ✅ done | 2 |  | claude | — | [US-EMDB-5](stories/US-EMDB-5.md) |
| [US-EMDB-5-4](tasks/US-EMDB-5-4.md) | Test: JSON serializer retained as fallback for debugging | ✅ done | 2 |  | claude | — | [US-EMDB-5](stories/US-EMDB-5.md) |
| [US-EMDB-5-5](tasks/US-EMDB-5-5.md) | Test: Round-trip tests pass for all content types | ✅ done | 3 |  | claude | — | [US-EMDB-5](stories/US-EMDB-5.md) |
| [US-EMDB-50-1](tasks/US-EMDB-50-1.md) | Test: RawBlockManagerTests WriteTestBlock helper writes BLAKE3-128 checksums | ✅ done | 2 |  | claude | — | [US-EMDB-50](stories/US-EMDB-50.md) |
| [US-EMDB-50-2](tasks/US-EMDB-50-2.md) | Test: RawBlockManagerTests ReadTestBlock helper reads BLAKE3-128 checksums | ✅ done | 2 |  | claude | — | [US-EMDB-50](stories/US-EMDB-50.md) |
| [US-EMDB-50-3](tasks/US-EMDB-50-3.md) | Test: BTreeWALManagerTests corruption test updated for new checksum offsets | ✅ done | 1 |  | claude | — | [US-EMDB-50](stories/US-EMDB-50.md) |
| [US-EMDB-50-4](tasks/US-EMDB-50-4.md) | Test: All existing unit tests pass with zero regressions | ✅ done | 1 |  | claude | — | [US-EMDB-50](stories/US-EMDB-50.md) |
| [US-EMDB-50-5](tasks/US-EMDB-50-5.md) | Test: Crc32.NET package reference removed from EmailDB.UnitTests.csproj | ✅ done | 1 |  | claude | — | [US-EMDB-50](stories/US-EMDB-50.md) |
| [US-EMDB-50-6](tasks/US-EMDB-50-6.md) | Test: No remaining references to Force.Crc32 in test projects | ✅ done | 1 |  | claude | — | [US-EMDB-50](stories/US-EMDB-50.md) |
| [US-EMDB-51-1](tasks/US-EMDB-51-1.md) | Test: EmailDbStore uses BLAKE3-128 for checksum computation | ✅ done | 1 |  | claude | — | [US-EMDB-51](stories/US-EMDB-51.md) |
| [US-EMDB-51-2](tasks/US-EMDB-51-2.md) | Test: Crc32.NET removed from benchmark csproj | ✅ done | 1 |  | claude | — | [US-EMDB-51](stories/US-EMDB-51.md) |
| [US-EMDB-51-3](tasks/US-EMDB-51-3.md) | Test: Blake3 added to benchmark csproj | ✅ done | 1 |  | claude | — | [US-EMDB-51](stories/US-EMDB-51.md) |
| [US-EMDB-51-4](tasks/US-EMDB-51-4.md) | Test: BlockOverhead constant updated | ✅ done | 1 |  | claude | — | [US-EMDB-51](stories/US-EMDB-51.md) |
| [US-EMDB-51-5](tasks/US-EMDB-51-5.md) | Test: Benchmark compiles and runs correctly | ✅ done | 1 |  | claude | — | [US-EMDB-51](stories/US-EMDB-51.md) |
| [US-EMDB-52-1](tasks/US-EMDB-52-1.md) | Test: ADR-014 exists in DECISIONS.md with full rationale | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-2](tasks/US-EMDB-52-2.md) | Test: ADR-003 updated to note checksum portion superseded | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-3](tasks/US-EMDB-52-3.md) | Test: EmailDB_FileFormat_Spec.md shows 16-byte checksums and 81-byte overhead | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-4](tasks/US-EMDB-52-4.md) | Test: ARCHITECTURE.md references BLAKE3-128 not CRC32 | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-5](tasks/US-EMDB-52-5.md) | Test: PROJECT.md references BLAKE3-128 not CRC32 | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-6](tasks/US-EMDB-52-6.md) | Test: VISION.md references BLAKE3-128 not CRC32 | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-7](tasks/US-EMDB-52-7.md) | Test: EPIC-EMDB-9 description updated | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-8](tasks/US-EMDB-52-8.md) | Test: Crc32.NET removed from all csproj files | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-52-9](tasks/US-EMDB-52-9.md) | Test: No remaining Force.Crc32 references outside ZonetreeRef/ | ✅ done | 1 |  | claude | — | [US-EMDB-52](stories/US-EMDB-52.md) |
| [US-EMDB-53-1](tasks/US-EMDB-53-1.md) | Test: BlockType.KeyStore added to enum | ✅ done | 1 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-53-2](tasks/US-EMDB-53-2.md) | Test: KeyStoreContent model holds epoch/DEK/timestamp/retired entries plus ActiveEpoch | ✅ done | 2 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-53-3](tasks/US-EMDB-53-3.md) | Test: KeyStoreManager encrypts key store payload with KEK using AES-256-GCM | ✅ done | 2 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-53-4](tasks/US-EMDB-53-4.md) | Test: KeyStoreManager decrypts key store with KEK and loads DEK table | ✅ done | 2 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-53-5](tasks/US-EMDB-53-5.md) | Test: On file creation initial DEK epoch 0 is generated and key store block written | ✅ done | 2 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-53-6](tasks/US-EMDB-53-6.md) | Test: On file open KEK derived from password decrypts key store and loads DEK table | ✅ done | 2 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-53-7](tasks/US-EMDB-53-7.md) | Test: Key store round-trips correctly through serialize/encrypt/decrypt/deserialize | ✅ done | 2 |  | claude | — | [US-EMDB-53](stories/US-EMDB-53.md) |
| [US-EMDB-54-1](tasks/US-EMDB-54-1.md) | Test: FlagAlgorithm constant removed from Block.cs | ✅ done | 1 |  | claude | — | [US-EMDB-54](stories/US-EMDB-54.md) |
| [US-EMDB-54-2](tasks/US-EMDB-54-2.md) | Test: Block.KeyEpoch property reads bits 1-7: (Flags >> 1) & 0x7F | ✅ done | 1 |  | claude | — | [US-EMDB-54](stories/US-EMDB-54.md) |
| [US-EMDB-54-3](tasks/US-EMDB-54-3.md) | Test: Block.SetKeyEpoch method writes epoch into bits 1-7 preserving bit 0 | ✅ done | 1 |  | claude | — | [US-EMDB-54](stories/US-EMDB-54.md) |
| [US-EMDB-54-4](tasks/US-EMDB-54-4.md) | Test: Unencrypted blocks have Flags = 0 and KeyEpoch = 0 | ✅ done | 1 |  | claude | — | [US-EMDB-54](stories/US-EMDB-54.md) |
| [US-EMDB-54-5](tasks/US-EMDB-54-5.md) | Test: Encrypted blocks have bit 0 set and KeyEpoch matches the active epoch at write time | ✅ done | 2 |  | claude | — | [US-EMDB-54](stories/US-EMDB-54.md) |
| [US-EMDB-54-6](tasks/US-EMDB-54-6.md) | Test: All existing block tests updated and passing | ✅ done | 2 |  | claude | — | [US-EMDB-54](stories/US-EMDB-54.md) |
| [US-EMDB-55-1](tasks/US-EMDB-55-1.md) | Test: Implements IBlockEncryptionProvider interface | ✅ done | 1 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-55-2](tasks/US-EMDB-55-2.md) | Test: Encrypt uses current active DEK from key store and returns the active epoch | ✅ done | 2 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-55-3](tasks/US-EMDB-55-3.md) | Test: Decrypt accepts key epoch parameter and looks up correct DEK from key store | ✅ done | 2 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-55-4](tasks/US-EMDB-55-4.md) | Test: Wrong epoch or missing DEK returns clear error | ✅ done | 1 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-55-5](tasks/US-EMDB-55-5.md) | Test: Round-trip encrypt/decrypt with multiple DEKs produces correct plaintext | ✅ done | 2 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-55-6](tasks/US-EMDB-55-6.md) | Test: Works with EncryptionPolicy to determine which block types to encrypt | ✅ done | 2 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-55-7](tasks/US-EMDB-55-7.md) | Test: Properly disposes all DEK key material on disposal | ✅ done | 2 |  | claude | — | [US-EMDB-55](stories/US-EMDB-55.md) |
| [US-EMDB-56-1](tasks/US-EMDB-56-1.md) | Test: ChangePassword method derives old KEK from old password and existing salt | ✅ done | 1 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-2](tasks/US-EMDB-56-2.md) | Test: Decrypts key store with old KEK | ✅ done | 1 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-3](tasks/US-EMDB-56-3.md) | Test: Generates new salt and derives new KEK from new password | ✅ done | 1 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-4](tasks/US-EMDB-56-4.md) | Test: Re-encrypts key store with new KEK | ✅ done | 1 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-5](tasks/US-EMDB-56-5.md) | Test: Updates EncryptionHeader with new salt and key verification token | ✅ done | 2 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-6](tasks/US-EMDB-56-6.md) | Test: Zero data blocks are read or written during password change | ✅ done | 1 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-7](tasks/US-EMDB-56-7.md) | Test: All data remains readable with new password after change | ✅ done | 2 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-56-8](tasks/US-EMDB-56-8.md) | Test: Old password no longer works after change | ✅ done | 1 |  | claude | — | [US-EMDB-56](stories/US-EMDB-56.md) |
| [US-EMDB-57-1](tasks/US-EMDB-57-1.md) | Test: RotateKey method generates new 32-byte random DEK | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-2](tasks/US-EMDB-57-2.md) | Test: New DEK assigned next epoch number | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-3](tasks/US-EMDB-57-3.md) | Test: Previous active epoch marked as retained not retired | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-4](tasks/US-EMDB-57-4.md) | Test: New epoch set as active in key store | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-5](tasks/US-EMDB-57-5.md) | Test: Key store block re-encrypted and written | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-6](tasks/US-EMDB-57-6.md) | Test: Future blocks use new DEK via active epoch | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-7](tasks/US-EMDB-57-7.md) | Test: Existing blocks remain readable with their original DEK | ✅ done | 1 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-57-8](tasks/US-EMDB-57-8.md) | Test: Old and new epoch blocks coexist and are both decryptable | ✅ done | 2 |  | claude | — | [US-EMDB-57](stories/US-EMDB-57.md) |
| [US-EMDB-58-1](tasks/US-EMDB-58-1.md) | Test: BlockType.EmailMetadata = 11 added to BlockType enum | ⚪ todo | — |  | — | — | [US-EMDB-58](stories/US-EMDB-58.md) |
| [US-EMDB-58-2](tasks/US-EMDB-58-2.md) | Test: EmailMetadataContent model defined with all fields (EmailHashedID Subject From To Cc Date AttachmentCount Conte... | ⚪ todo | — |  | — | — | [US-EMDB-58](stories/US-EMDB-58.md) |
| [US-EMDB-58-3](tasks/US-EMDB-58-3.md) | Test: Protobuf-annotated model in EmailDB.Format.Protobuf.Models with ProtoContract/ProtoMember attributes | ⚪ todo | — |  | — | — | [US-EMDB-58](stories/US-EMDB-58.md) |
| [US-EMDB-58-4](tasks/US-EMDB-58-4.md) | Test: EmailMetadataContent round-trips correctly through ProtobufBlockContentSerializer | ⚪ todo | — |  | — | — | [US-EMDB-58](stories/US-EMDB-58.md) |
| [US-EMDB-58-5](tasks/US-EMDB-58-5.md) | Test: BlockConverter handles BlockType.EmailMetadata deserialization | ⚪ todo | — |  | — | — | [US-EMDB-58](stories/US-EMDB-58.md) |
| [US-EMDB-58-6](tasks/US-EMDB-58-6.md) | Test: Existing tests pass unchanged | ⚪ todo | — |  | — | — | [US-EMDB-58](stories/US-EMDB-58.md) |
| [US-EMDB-59-1](tasks/US-EMDB-59-1.md) | Test: AddEmailAsync writes ContentBlock then MetadataBlock in correct order | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-59-2](tasks/US-EMDB-59-2.md) | Test: BTree insert points to metadata block location not content block | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-59-3](tasks/US-EMDB-59-3.md) | Test: MetadataBlock.ContentBlockId matches the written content block's BlockId | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-59-4](tasks/US-EMDB-59-4.md) | Test: MetadataBlock contains correct extracted fields (subject from to cc date attachment count) | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-59-5](tasks/US-EMDB-59-5.md) | Test: Email is added to target folder after all blocks written | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-59-6](tasks/US-EMDB-59-6.md) | Test: Content block payload matches original raw email bytes | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-59-7](tasks/US-EMDB-59-7.md) | Test: Duplicate EmailHashedID is rejected or handled as upsert | ⚪ todo | — |  | — | — | [US-EMDB-59](stories/US-EMDB-59.md) |
| [US-EMDB-6-1](tasks/US-EMDB-6-1.md) | Test: Decision documented in architecture decisions doc | ✅ done | 1 |  | claude | — | [US-EMDB-6](stories/US-EMDB-6.md) |
| [US-EMDB-6-2](tasks/US-EMDB-6-2.md) | Test: If keeping: fix compilation errors and add .capnp schemas | ✅ done | 3 |  | claude | — | [US-EMDB-6](stories/US-EMDB-6.md) |
| [US-EMDB-6-3](tasks/US-EMDB-6-3.md) | Test: If removing: delete project and clean solution references | ✅ done | 2 |  | claude | — | [US-EMDB-6](stories/US-EMDB-6.md) |
| [US-EMDB-6-4](tasks/US-EMDB-6-4.md) | Test: No broken code left in the repository | ✅ done | 2 |  | claude | — | [US-EMDB-6](stories/US-EMDB-6.md) |
| [US-EMDB-60-1](tasks/US-EMDB-60-1.md) | Test: Folder listing reads only metadata blocks (zero content block reads verified) | ⚪ todo | — |  | — | — | [US-EMDB-60](stories/US-EMDB-60.md) |
| [US-EMDB-60-2](tasks/US-EMDB-60-2.md) | Test: GetEmailMetadataAsync returns EmailMetadataContent from BTree lookup | ⚪ todo | — |  | — | — | [US-EMDB-60](stories/US-EMDB-60.md) |
| [US-EMDB-60-3](tasks/US-EMDB-60-3.md) | Test: GetEmailContentAsync follows ContentBlockId pointer to read content block | ⚪ todo | — |  | — | — | [US-EMDB-60](stories/US-EMDB-60.md) |
| [US-EMDB-60-4](tasks/US-EMDB-60-4.md) | Test: Content block is only read when explicitly requested | ⚪ todo | — |  | — | — | [US-EMDB-60](stories/US-EMDB-60.md) |
| [US-EMDB-60-5](tasks/US-EMDB-60-5.md) | Test: Metadata blocks are cached by CacheManager after first read | ⚪ todo | — |  | — | — | [US-EMDB-60](stories/US-EMDB-60.md) |
| [US-EMDB-60-6](tasks/US-EMDB-60-6.md) | Test: Listing 1000 emails reads at most 1000 metadata blocks plus BTree traversal blocks | ⚪ todo | — |  | — | — | [US-EMDB-60](stories/US-EMDB-60.md) |
| [US-EMDB-61-1](tasks/US-EMDB-61-1.md) | Test: SoftDeleteEmailAsync removes email from source folder and adds to Dead folder | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-61-2](tasks/US-EMDB-61-2.md) | Test: BTree entry persists after soft-delete (lookup still succeeds) | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-61-3](tasks/US-EMDB-61-3.md) | Test: BTree.EntryCount unchanged after soft-delete | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-61-4](tasks/US-EMDB-61-4.md) | Test: Content and metadata blocks remain readable after soft-delete | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-61-5](tasks/US-EMDB-61-5.md) | Test: Dead folder created automatically during file initialization | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-61-6](tasks/US-EMDB-61-6.md) | Test: HardDeleteEmailAsync removes from BTree and marks for compaction reclamation | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-61-7](tasks/US-EMDB-61-7.md) | Test: Soft-deleted email can be recovered by moving out of Dead folder | ⚪ todo | — |  | — | — | [US-EMDB-61](stories/US-EMDB-61.md) |
| [US-EMDB-62-1](tasks/US-EMDB-62-1.md) | Test: FolderTreeContent serializable by ProtobufBlockContentSerializer | ⚪ todo | — |  | — | — | [US-EMDB-62](stories/US-EMDB-62.md) |
| [US-EMDB-62-2](tasks/US-EMDB-62-2.md) | Test: FolderContent serializable by ProtobufBlockContentSerializer with EmailHashedID support | ⚪ todo | — |  | — | — | [US-EMDB-62](stories/US-EMDB-62.md) |
| [US-EMDB-62-3](tasks/US-EMDB-62-3.md) | Test: FolderManager.CreateFolderAsync works on a fresh file after InitializeFileAsync | ⚪ todo | — |  | — | — | [US-EMDB-62](stories/US-EMDB-62.md) |
| [US-EMDB-62-4](tasks/US-EMDB-62-4.md) | Test: FolderManager move/delete/add email operations persist correctly to disk | ⚪ todo | — |  | — | — | [US-EMDB-62](stories/US-EMDB-62.md) |
| [US-EMDB-62-5](tasks/US-EMDB-62-5.md) | Test: File reopen loads folder tree and folder contents correctly | ⚪ todo | — |  | — | — | [US-EMDB-62](stories/US-EMDB-62.md) |
| [US-EMDB-62-6](tasks/US-EMDB-62-6.md) | Test: FolderContent.EmailIds stores EmailHashedID consistently across base and Protobuf models | ⚪ todo | — |  | — | — | [US-EMDB-62](stories/US-EMDB-62.md) |
| [US-EMDB-7-1](tasks/US-EMDB-7-1.md) | Test: FileStreamProvider implements IFileStreamProvider and routes through BlockManager | ⚪ todo | — |  | — | — | [US-EMDB-7](stories/US-EMDB-7.md) |
| [US-EMDB-7-2](tasks/US-EMDB-7-2.md) | Test: RandomAccessDevice/Manager implements IRandomAccessDevice and routes through SegmentManager | ⚪ todo | — |  | — | — | [US-EMDB-7](stories/US-EMDB-7.md) |
| [US-EMDB-7-3](tasks/US-EMDB-7-3.md) | Test: WriteAheadLog/Provider implements IWriteAheadLog and routes through block storage | ⚪ todo | — |  | — | — | [US-EMDB-7](stories/US-EMDB-7.md) |
| [US-EMDB-7-4](tasks/US-EMDB-7-4.md) | Test: ZoneTreeFactory creates properly configured ZoneTree instances | ⚪ todo | — |  | — | — | [US-EMDB-7](stories/US-EMDB-7.md) |
| [US-EMDB-7-5](tasks/US-EMDB-7-5.md) | Test: ZoneTree can perform basic upsert/get/delete through the EMDB file | ⚪ todo | — |  | — | — | [US-EMDB-7](stories/US-EMDB-7.md) |
| [US-EMDB-8-1](tasks/US-EMDB-8-1.md) | Test: HashedSearchEngine configured with EmailHashedID key type | ⚪ todo | — |  | — | — | [US-EMDB-8](stories/US-EMDB-8.md) |
| [US-EMDB-8-2](tasks/US-EMDB-8-2.md) | Test: Email subject/from/to/body indexed on add | ⚪ todo | — |  | — | — | [US-EMDB-8](stories/US-EMDB-8.md) |
| [US-EMDB-8-3](tasks/US-EMDB-8-3.md) | Test: SearchEmailsAsync returns matching EmailHashedIDs | ⚪ todo | — |  | — | — | [US-EMDB-8](stories/US-EMDB-8.md) |
| [US-EMDB-8-4](tasks/US-EMDB-8-4.md) | Test: Search index persisted through ZoneTree storage adapters | ⚪ todo | — |  | — | — | [US-EMDB-8](stories/US-EMDB-8.md) |
| [US-EMDB-8-5](tasks/US-EMDB-8-5.md) | Test: Delete/update operations maintain search index consistency | ⚪ todo | — |  | — | — | [US-EMDB-8](stories/US-EMDB-8.md) |
| [US-EMDB-9-1](tasks/US-EMDB-9-1.md) | Test: EmailManager compiles with two-tier storage API | ⚪ todo | — |  | — | — | [US-EMDB-9](stories/US-EMDB-9.md) |
| [US-EMDB-9-2](tasks/US-EMDB-9-2.md) | Test: AddEmailAsync writes content block + metadata block + BTree insert + folder add | ⚪ todo | — |  | — | — | [US-EMDB-9](stories/US-EMDB-9.md) |
| [US-EMDB-9-3](tasks/US-EMDB-9-3.md) | Test: GetEmailMetadataAsync retrieves metadata via BTree lookup without reading content | ⚪ todo | — |  | — | — | [US-EMDB-9](stories/US-EMDB-9.md) |
| [US-EMDB-9-4](tasks/US-EMDB-9-4.md) | Test: SoftDeleteEmailAsync moves to Dead folder without BTree mutation | ⚪ todo | — |  | — | — | [US-EMDB-9](stories/US-EMDB-9.md) |
| [US-EMDB-9-5](tasks/US-EMDB-9-5.md) | Test: MoveEmailAsync updates folder membership only (BTree and blocks untouched) | ⚪ todo | — |  | — | — | [US-EMDB-9](stories/US-EMDB-9.md) |
| [US-EMDB-9-6](tasks/US-EMDB-9-6.md) | Test: EmailHashedID deduplication prevents duplicate storage | ⚪ todo | — |  | — | — | [US-EMDB-9](stories/US-EMDB-9.md) |
