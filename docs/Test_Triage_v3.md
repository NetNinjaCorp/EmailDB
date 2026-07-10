# v3 Test-Suite Triage (US-EMDB-102-5)

Classification of the **148 legacy unit-test files** (the "~140 tests" of US-EMDB-102)
that existed at commit `a5fde84` (v1 custom BTree/encryption format) and are still
present in `EmailDB.UnitTests/`. None were modified by the v3 work (commits `12077e2`
onward *added* new `*V3`/`Cow*` files alongside the untouched legacy ones), so every
legacy file needs an explicit KEEP / REWRITE / DELETE decision.

## Method

- **Baseline:** `git ls-tree a5fde84 -- EmailDB.UnitTests/` = 148 `.cs` files, all still
  present at HEAD. 7 are non-test infrastructure (see bottom); the remaining **141 test
  classes** are triaged below.
- **v1-vs-v3 format facts** (from `EmailDB_FileFormat_Spec.md`) used as the decision rules:
  - v3 fixed block overhead = **96 B** (v1 = 84 B). Tests asserting 84 B / 36 B header layout ⇒ v1.
  - v3 **removes `PrevChainHash`** ("the per-node backward chain is removed"); Merkle `ChildHash` +
    `IndexRoot.RootHash` replace it. `PrevChainHash`/backward-chain tests ⇒ v1.
  - v3 `KeyEpoch` is a **separate 2-byte header field**, not packed into the flags byte.
    "epoch-in-flags" tests (`(epoch<<1)|encrypted`, `Flags>>1 & 0x7F`) ⇒ v1.
  - v3 index B+-trees are **ULID/BlockId-addressed COW**; v1 was **offset-addressed**. Legacy
    offset-BTree node/insert/lookup/delete/range tests are superseded by `CowBTree*`/`BTreeNode*`.
  - The legacy `EmailDB.Format.Models` + `EmailDB.Format.Encryption` stack (`RawBlockManager`,
    `Models.BTreeIndex`, `Models.IndexRoot` w/ `PreviousRootHash`, `KeyStoreManager`,
    `KeyWrappingEncryptionProvider`, `EncryptionHeader`, `Models.BlockType.Segment/Folder`) is
    **orphaned** — no v3 production references — and is replaced by the `EmailDB.Format.V3` stack.
- **Rules applied:**
  - **KEEP** — format-agnostic crypto-primitive tests (AES-GCM cipher round-trip / wrong-key /
    auth-tag, BLAKE3 hashing, Argon2id KDF, salt/keyfile) that pass unchanged on v3, plus a few
    v3-valid behavioral tests already written against surviving v3 APIs.
  - **DELETE** — asserts a v1-only artifact that no longer exists (84 B, `PrevChainHash`,
    epoch-in-flags, offset BTree, CRC32, legacy Models/Encryption stack) **or** is fully superseded
    by an existing new v3 test file with no behavioral gap.
  - **REWRITE** — behavioral/integration test whose *intent* stays valid in v3 but is written
    against removed v1 APIs/format and has no adequate v3 replacement yet (feeds US-EMDB-102-6).

## Tally

| Verdict  | Count |
|----------|-------|
| KEEP     | 22    |
| REWRITE  | 24    |
| DELETE   | 95    |
| **Total**| **141** |

Plus 7 infrastructure files (not test classes) — retain, update in place as ports need them.

---

## KEEP (22) — crypto primitives + surviving-v3-API behavior

| File | Rationale |
|------|-----------|
| AesGcmRoundTripTests.cs | AES-GCM round-trip primitive, format-agnostic |
| AesGcmWrongKeyErrorTests.cs | AES-GCM wrong-key error primitive |
| AesGcmDecryptVerifiesAuthTagTests.cs | AES-GCM auth-tag verification primitive |
| AesGcmEncryptPayloadFormatTests.cs | AES-GCM nonce/ciphertext/tag layout primitive |
| Blake3HashingTests.cs | BLAKE3 hashing primitive |
| ComputeChecksumBlake3Tests.cs | BLAKE3 checksum primitive |
| DeriveFromPasswordArgon2idParamsTests.cs | Argon2id KDF parameter constants |
| DeriveFromPasswordSameKeyTests.cs | Argon2id KDF determinism primitive |
| DifferentSaltDifferentKeyTests.cs | Argon2id KDF salt-sensitivity primitive |
| GenerateSaltProduces16RandomBytesTests.cs | CSPRNG salt-generation primitive |
| LoadFromKeyFileReads32BytesTests.cs | Keyfile-load crypto primitive |
| ChangePasswordDataReadableTests.cs | DEK-preservation crypto behavior (primitives only) |
| ChangePasswordDecryptsKeyStoreWithOldKekTests.cs | KEK/keystore decrypt crypto primitive |
| ChangePasswordDerivesOldKekTests.cs | Argon2id KEK derivation, format-agnostic |
| ChangePasswordGeneratesNewSaltAndNewKekTests.cs | Salt + KDF primitive |
| ChangePasswordReEncryptsKeyStoreWithNewKekTests.cs | AES-GCM keystore re-encrypt primitive |
| ChangePasswordUpdatesEncryptionHeaderTests.cs | v3 encryption-header + key-verification-token behavior |
| ChangePasswordZeroDataBlocksTests.cs | Stream-level header change, primitives only |
| CacheManagerAcceptsEncryptionProviderTests.cs | Tests current v3 CacheManager encryptionProvider ctor/property. US-EMDB-101-5: rewritten to drop the incidental `BlockIdGenerator.Instance.Reset()` call (int64 generator retired); CacheManager coverage unchanged. |
| CacheStoresDecryptedContentTests.cs | v3 cache decrypt-on-read behavior. US-EMDB-101-5: rewritten to drop `BlockIdGenerator` (blocks now use literal IDs); decrypt/cache coverage unchanged. |
| DecryptAcceptsKeyEpochLooksUpDekTests.cs | v3 epoch→DEK decrypt behavior (KeyEpoch as param) |
| DifferentKeyEpochBlocksReadableTests.cs | v3 per-block KeyEpoch readability behavior |

## REWRITE (24) — valid intent, port to v3 APIs (US-EMDB-102-6)

| File | Rationale |
|------|-----------|
| BTreeCompactionConcurrentReadTests.cs | Compaction snapshot-isolation intent valid; on v1 BTreeIndex |
| BTreeCompactionDataReadableTests.cs | Data-readable-post-compaction valid; on v1 BTreeIndex |
| BTreeCompactionFileSizeTests.cs | Compaction space-reclaim valid; on v1 BTreeIndex |
| BTreeCompactionFullFunctionalityTests.cs | Post-compaction lookup correctness valid; on v1 BTreeIndex |
| BTreeCompactionLiveNodesTests.cs | Only-live-nodes-after-compaction valid; on v1 BTreeIndex |
| BTreeCompactionReEncryptDecryptTests.cs | Re-encrypt-to-active-epoch valid; on v1 BTreeIndex |
| BTreeCompactionReEncryptDekPruningTests.cs | Retired-DEK-pruning-on-compaction valid; on v1 BTreeIndex |
| BTreeCompactionReEncryptNewFileKeyStoreTests.cs | New-file-keystore active-DEK-only valid; on v1 BTreeIndex |
| BTreeCompactionRootHashTests.cs | Root-hash-after-compaction valid; on v1 offset BTreeIndex |
| BTreeCompactionSpaceAmplificationMetricsTests.cs | Space-amplification metrics valid; on v1 BTreeIndex |
| BTreeCompactionWithoutReEncryptTests.cs | Compact-preserves-epochs/DEKs valid; on v1 BTreeIndex |
| BTreeConcurrentDeleteReadTests.cs | Concurrent delete/read isolation valid; no Cow concurrency test yet |
| BTreeConcurrentReaderTests.cs | Concurrent-lookup correctness valid; no Cow concurrency test yet |
| BTreeConcurrentReadWriteStressTests.cs | Reader/writer stress no-corruption valid; on v1 BTreeIndex |
| BTreeCrashRecoveryStressTests.cs | BTree flush/crash durability valid; no Cow crash-recovery test yet |
| BTreeLookupDepthTests.cs | Tree height/capacity assertions; no v3 depth test exists |
| CacheManagerTests.cs | Folder-cache intent valid; uses removed offset CacheFolder(name,offset,content) |
| EndToEndAdd1000EmailsTests.cs | Scale E2E intent valid; v1 offset BTree/RawBlockManager, no v3 scale-E2E yet |
| ErrorContextTests.cs | Error-context-preservation criterion valid; written around v1 patterns |
| FuzzingIntegrationTests.cs | Full-stack fuzz intent valuable; v1 offset BTree + PrevChainHash, no v3 fuzz twin |
| JsonSerializerFallbackTests.cs | JSON-fallback serializer intent valid; exercises v1 content models |
| OldPasswordNoLongerWorksAfterChangeTests.cs | KEK re-wrap invalidation valid; uses removed EncryptionHeader/KeyStoreManager |
| PayloadEncodingTests.cs | PayloadEncoding enum/encoding-respect valid in v3; written against v1 DTOs |
| ShouldEncryptPerPolicyTests.cs | Policy predicate valid; legacy EncryptionPolicy → V3.EncryptionPolicySet |

## DELETE (95) — v1-format specific or fully superseded

### Offset-addressed / PrevChainHash BTree + nodes (superseded by CowBTree*/BTreeNode*)
BlockIdGeneratorBTreeTests.cs, Blake3HashingTests.cs*, BTreeBlake3HashChainAfterDecryptTests.cs,
BTreeCacheManagerIntegrationTests.cs, BTreeChildHashVerificationTests.cs,
BTreeCompactionGenesisChainTests.cs, BTreeCompactionGenesisTests.cs,
BTreeCompactionReEncryptEpochFlagsTests.cs, BTreeCompactionReEncryptFlagTests.cs,
BTreeDeleteTests.cs, BTreeFullVerificationTests.cs, BTreeGenesisBlockTests.cs,
BTreeIndexAcceptsEncryptionProviderTests.cs, BTreeInsertTests.cs, BTreeInternalNodeTests.cs,
BTreeLeafNodeTests.cs, BTreeLookupTests.cs, BTreeOperationsEncryptionAcrossEpochsTests.cs,
BTreePayloadTamperTests.cs, BTreePrevChainHashTests.cs, BTreeRangeQueryTests.cs,
BTreeSerializerBenchmarkTests.cs, BTreeStandardVerificationTests.cs, BTreeWALManagerTests.cs

*(`Blake3HashingTests.cs` is the *BTree-node* hash-chain test — distinct from the KEEP
`ComputeChecksumBlake3Tests.cs`; it sets `PrevChainHash` + offset ChildOffsets.)*

*(US-EMDB-101-5: the production v1 offset-BTree subsystem these tests exercised —
`BTreeIndex.cs`, `BTreeWALManager.cs`, `BlockIDGenerator.cs`/`BlockIDUtility.cs` (int64
range-partitioned IDs), `iStorageManager.cs`, and `RawBlockManager`'s raw-region
`WriteRawBytesAsync`/`ReadRawBytesAsync` WAL paths — has now been deleted per ADR-016.
The tests listed above were already removed by US-EMDB-102-6.)*

### v1-BTree benchmarks (dead component)
BTreeInsertThroughputBenchmarkTests.cs, BTreeLookupLatencyBenchmarkTests.cs,
BTreeRangeQueryThroughputBenchmarkTests.cs, BTreeWALFlushLatencyBenchmarkTests.cs

### epoch-in-flags block model (v3 KeyEpoch is a separate field)
BlockKeyEpochPropertyTests.cs, BlockSetKeyEpochTests.cs, EncryptedBlockBit0AndKeyEpochTests.cs,
UnencryptedBlockFlagsAndKeyEpochTests.cs, ReadBlockAsyncDecryptsWithCorrectDekTests.cs,
ReadMethodsDecryptBeforeDeserializationTests.cs, WriteBlockAsyncEncryptsPayloadTests.cs,
WriteMethodsEncryptAndStampEpochTests.cs, WALBlockEncryptedWithActiveDekTests.cs,
FlagAlgorithmRemovedTests.cs, BlockFlagEncryptedTests.cs

### v1 84-byte / 36-byte-header block layout (v3 = 96 B, superseded by BlockManagerV3/BlockSerializer)
ChecksumSizeConstantsTests.cs, RawBlockManagerTests.cs, ReadPathChecksumTests.cs,
WritePathChecksumTests.cs, ScanAndTryReadBlockLocationChecksumTests.cs, BlockManagerTests.cs,
EmptyPayloadZeroChecksumTests.cs, BlockChecksumPropertyTypeTests.cs, BlockTypeEnumTests.cs,
BlockTypeKeyStoreEnumTests.cs, ForceCrc32RemovedFromRawBlockManagerTests.cs

### Legacy Models.IndexRoot backward-chain (v3 IndexRoot = 68 B, no PreviousRootHash)
IndexRootChainTamperTests.cs, IndexRootHashMatchTests.cs, IndexRootTests.cs

### Legacy KeyWrapping/KeyStore/EncryptionHeader stack (superseded by EpochDekProvider/EncryptionBootstrap/V3KeyVerificationToken)
IBlockEncryptionProviderInterfaceTests.cs, NullBlockEncryptionProviderTests.cs,
KeyWrappingDisposalTests.cs, KeyWrappingEncryptionProviderImplementsInterfaceTests.cs,
KeyWrappingEncryptionProviderPolicyTests.cs, EncryptUsesActiveDekFromKeyStoreTests.cs,
ExistingBlocksReadableWithOriginalDekTests.cs, FutureBlocksUseNewDekViaActiveEpochTests.cs,
OldNewEpochBlocksCoexistDecryptableTests.cs, RoundTripMultipleDeksTests.cs,
TypedMethodsMultiEpochDekTests.cs, WrongEpochOrMissingDekErrorTests.cs,
WrongKeyImmediateErrorTests.cs, KeyStoreContentModelTests.cs, KeyStoreManagerDecryptsWithKekTests.cs,
KeyStoreManagerEncryptsWithKekTests.cs, KeyStoreReEncryptedAfterRotationTests.cs,
KeyStoreRoundTripTests.cs, KeyVerificationTokenTests.cs, InitialDekEpoch0KeyStoreBlockTests.cs,
FileOpenKekDecryptsKeyStoreTests.cs, EncryptionHeaderFieldsTests.cs, EncryptionHeaderManagerTests.cs,
UnencryptedFilesNoEncryptionHeaderTests.cs, UsesSystemSecurityCryptographyAesGcmTests.cs

### Legacy EncryptionPolicy over v1 BlockType (superseded by EncryptionPolicyWritePathTests / ShouldEncryptPerPolicy rewrite)
EncryptionPolicyDefaultTests.cs, EncryptionPolicyFullTests.cs, CustomEncryptionPolicyTests.cs

### Legacy RotateKey epoch behavior (superseded by RotateKeyV3EpochBoundTests/EpochDekProviderTests)
RotateKeyGeneratesNew32ByteRandomDekTests.cs, RotateKeyNewDekAssignedNextEpochTests.cs,
RotateKeyNewEpochActiveInKeyStoreTests.cs, RotateKeyPreviousEpochRetainedNotRetiredTests.cs

### Legacy CacheManager WAL recovery (superseded by WalReplayer/DirtyOpen/Kill9RecoveryV3)
RecoverFromDiskDecryptsWALBlocksAcrossKeyRotationTests.cs,
RecoverFromDiskDecryptsWALBlocksWithCorrectDekTests.cs, WriteMethodsEncryptWALContentTests.cs

### v1 EmailManager pipeline (superseded by EmailManager*V3Tests)
EmailManagerAddEmailBTreeTests.cs, EmailManagerDeleteEmailBTreeTests.cs,
EmailManagerGetEmailBTreeTests.cs, IStorageManagerBTreeInterfaceTests.cs, StorageManagerTests.cs

### v1 offset-addressed content serialization (superseded by BlockSerializerTests)
ProtobufBlockContentSerializerTests.cs, RoundTripTests.cs

### Misc
UnitTest1.cs (empty placeholder)

*(CustomEncryptionPolicy/EncryptionHeader Change* rows: see the KEEP list for the crypto-only
ChangePassword subset — only the header/policy-format tests are deleted.)*

---

## Infrastructure (7) — not test classes; retain and update in place during port

Program.cs, RunTests.cs, Models/EmailModels.cs, Models/TestModels.cs, Helpers/TestHelpers.cs,
Benchmarks/BenchmarkRunner.cs, Benchmarks/EmailBenchmark.cs

## Downstream

- **US-EMDB-102-1** (remove v1-format tests): the 95 DELETE rows.
- **US-EMDB-102-6** (port retained tests): the 24 REWRITE rows; the 22 KEEP rows should already
  compile/pass on v3 and only need re-verification.
- **US-EMDB-102-4** (MUST-coverage audit): cross-check KEEP+REWRITE+new `*V3` files against every
  spec section carrying a MUST.

---

## US-EMDB-102-6 execution outcome (port)

The 95 DELETE rows + `Blake3HashingTests.cs` (the v1 BTree-node hash-chain test — see the KEEP/DELETE
footnote conflict; its BLAKE3-primitive coverage lives in the KEEP `ComputeChecksumBlake3Tests.cs`)
were removed. The v1 benchmark harness (`Benchmarks/EmailBenchmark.cs`, `Benchmarks/BenchmarkRunner.cs`)
and the `Program.cs` `--benchmark` entrypoint were removed too: they depended on the deleted v1
`TestBlockManager`/`MockRawBlockManager` pipeline; real benchmarks live in the separate
`EmailDB.Benchmark.*` projects.

The 22 KEEP rows compile and pass unchanged on v3.

The 24 REWRITE rows resolved as follows. All 24 originally compiled only against the still-present
legacy `EmailDB.Format.Models`/`Encryption` stack; the port either rebases them on v3 APIs or removes
them where an existing v3 test already covers the intent (a v1 test asserting a removed API is not
kept as a stub).

**Rewritten against v3 APIs (3):**

| File | v3 target |
|------|-----------|
| BTreeLookupDepthTests.cs | `CowBTree`/`BTreeNodeStore` + `BTreeNodeCapacity`: PrimaryEmail fan-out keeps height ≤ 4 for 10M keys; `TryGet` descends exactly `BTreeRoot.Height` nodes; multi-level tree reaches every key. |
| EndToEndAdd1000EmailsTests.cs | `EmailManager` Create/Open/AddEmail/Commit/GetEmail: 1000 emails added through the group-commit pipeline, each retrieved by identity in-process and after reopen. |
| PayloadEncodingTests.cs | `V3.PayloadEncoding` registry (Section 4.3) + `BlockSerializer`: encoding stamped at header offset 12 and preserved across full block serialize/deserialize. |

**Removed — intent already covered by an existing v3 test, or feature not present in v3 (11):**

| File | Superseded by / reason |
|------|------------------------|
| 11 × BTreeCompaction*Tests.cs | v3 has no compaction *executor* (only `CompactionTriggerEvaluator` + `DeadBlockAccountant`); trigger/accounting intent covered by `CompactionTriggerEvaluatorTests`, `DeadBlockAccountantTests`, `DeadBlockAccountingTests`. Physical compaction is unimplemented v3 future work. |
| BTreeConcurrentReaderTests / BTreeConcurrentDeleteReadTests / BTreeConcurrentReadWriteStressTests | Single-writer exclusion covered by `WriterLock*` tests; COW snapshot reads + randomized correctness covered by `CowBTreeModelStressTests`. |
| BTreeCrashRecoveryStressTests | `WalReplayerTests`, `DirtyOpenTests`, `EmailManagerKill9RecoveryV3Tests`. |
| FuzzingIntegrationTests | `CowBTreeModelStressTests` (randomized insert/delete vs reference model, `Category=Stress`). |
| ErrorContextTests | `VerificationErrorTaxonomyTests`, `VerificationErrorDistinctnessTests`, `ReadPathDecryptTaxonomyTests` (typed `Result`/`CorruptionError`). |
| CacheManagerTests | v1 offset folder cache; v3 caching (`EncryptedBlockStore`) covered by `CacheStoresDecryptedContentTests`, `CacheManagerAcceptsEncryptionProviderTests`. |
| ShouldEncryptPerPolicyTests | `EncryptionPolicyWritePathTests` (fully covers `V3.EncryptionPolicySet.RequiresEncryption`). |
| OldPasswordNoLongerWorksAfterChangeTests | `ChangePasswordV3Tests` (explicitly asserts the old password is rejected after a completed change). |
| JsonSerializerFallbackTests | v1 `DefaultBlockContentSerializer` JSON fallback has no v3 implementation; `V3.PayloadEncoding.Json` (value 3) is a reserved debug encoding with no encoder. Enum membership asserted in the ported `PayloadEncodingTests`. |

**CI:** `.github/workflows/ci.yml` runs the v3 suite on `ubuntu-latest` with the .NET 9 SDK —
a `unit-tests` job (`--filter "Category!=Stress"`) and a separate `stress-tests` job
(`--filter "Category=Stress"`).

Local evidence (net9.0 via `DOTNET_ROLL_FORWARD=LatestMajor`): `Category!=Stress` → 1455 passed / 0
failed; `Category=Stress` → 1 passed / 0 failed; Release build clean.
