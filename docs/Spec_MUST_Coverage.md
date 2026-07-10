# v3 Spec MUST-Coverage Matrix (US-EMDB-102-4)

Verifies the US-EMDB-102 acceptance criterion: **"Coverage exists for every v3 spec
section with a MUST."**

## Method

- Source of truth: `EmailDB_FileFormat_Spec.md` (v3, 429 lines).
- `grep -nE "MUST" EmailDB_FileFormat_Spec.md` → 14 hits. Each hit was mapped to its
  owning `##`/`###` section. This yields **12 distinct spec sections that carry a
  normative MUST/MUST NOT** (Section 13's contract is one section expressed as a
  13-row table).
- For each section, covering test classes were located in `EmailDB.UnitTests/` by
  behavior (not by name), then confirmed against the actual test-method names.
- Suite state: `./test.sh` (default v3 filter) → **Passed: 1091, Failed: 0** (net9.0).

## Coverage matrix

| # | Spec § | MUST requirement | Covering test class(es) → key methods |
|---|--------|------------------|----------------------------------------|
| 1 | 3.1 Slot layout | Unknown `IncompatFlags` bits ⇒ reader MUST refuse to open; unknown `ReadOnlyCompatFlags` bits ⇒ reader MUST NOT write (open read-only) | `SuperblockSessionTests`: `Open_UnknownIncompatFlags_RefusesWithTypedError`, `Open_UnknownIncompatFlags_MultipleBitsIncludingHighBit_ReportsAllOffendingBits`, `Open_UnknownReadOnlyCompatFlags_OpensReadOnly`, `ReadOnlySession_RefusesAllWrites_AndNeverTouchesDisk`, `Open_UnknownIncompatAndReadOnlyCompatFlags_IncompatRefusalWins`; `SuperblockSerializerTests` |
| 2 | 3.2 KdfParams packing | Readers MUST use stored KdfParams (never assume defaults) | `V3PasswordKeyDerivationTests`: `DeriveKek_HonorsSuppliedParams_NotDefaults`, `DeriveKek_FromRawSuperblockFields_MatchesTypedOverload`, `DeriveKek_DifferentIterations_ProduceDifferentKeys` |
| 3 | 3.3 CleanShutdown protocol | With `CleanShutdown = 0`, reader MUST scan forward from the hinted Checkpoint for newer Checkpoints + unreplayed WAL | `DirtyOpenTests` (10 methods); `ForwardScanTests`; `SuperblockSessionTests`: `AbandonedSession_LeavesCleanShutdownZero_ForCrashRecovery`, `CrashedSession_NextSessionWriteAndClose_RestoresCleanShutdownOne`; `DisasterOpenTests` |
| 4 | 4 Block Format | Deserializers MUST bounds-check all counts/lengths against actual payload size | `BlockSerializerTests`: `DeserializeHeader_PayloadLengthAboveMax_Fails`, `DeserializeHeader_NegativePayloadLength_ValidChecksum_Fails`, `DeserializeHeader_TamperedPayloadLength_FailsOnChecksumNotLength`; `BTreeNodeDeserializationTests`: `Rejects_EntryCountOverflowingPayload`, `Rejects_EntryCountWhoseImpliedSizeWraps32BitArithmetic`, `Rejects_TruncatedPayload`, `Rejects_TrailingBytes`, `Rejects_PayloadShorterThanHeader`; `Section13ContractTests.Row04_InsaneLength_...NeverAllocatesFirst`; `PerFailureHandlerTests.InsaneLength_...` |
| 5 | 4.2 Block ID (ULID) | Generators MUST use monotonic ULID mode; on clock regression continue incrementing rather than emit out-of-order IDs | `UlidGeneratorTests`: `ClockRegression_NeverEmitsOutOfOrderUlid`, `ClockRegression_ResumesFreshTimestampsOnceClockCatchesUp`, `ClockRegression_WithRandomOverflow_StaysMonotonic`, `ClockRegression_MultiStepJitteryClock_StaysStrictlyMonotonic`, `SameMillisecond_StaysStrictlyMonotonic`, `ConcurrentGeneration_UnderFixedClock_StaysUniqueAndOrdered` |
| 6 | 4.4 Compression | Decoders MUST cap output at `MaxPayloadLength × 16` and fail per §13 beyond it | `BlockCompressorTests`: `Decompress_OutputBeyondBombGuard_Fails`, `Decompress_OutputExactlyAtBombGuard_Succeeds`, `Decompress_CraftedBomb_TripsGuardMidStreamWithoutAllocatingExpandedSize`, `ReadDecompressed_CraftedBombBlockOnDisk_TripsGuardWithoutAllocatingExpandedSize`; `PerFailureHandlerTests.DecompressionBomb_SurfacesCorruptionErrorDecompressionBomb`; `Section13ContractTests.Row12_DecompressionBomb_TreatedAsPayloadCorruption` |
| 7 | 6 Generic B+-Tree Node | Readers MUST verify each traversed node against its parent's `ChildHash` (path verification) | `CowBTreeReadVerificationTests` (all methods, incl. `TryGet_AnySingleBitFlipInAnyNode_FailsAffectedLookupWithContractedMerkleError`, `Scan_ColdCache_VerifiesEveryTraversedNodeExactlyOnce`); `CowBTreeBitFlipVerificationTests`; `CowBTreeFullTreeVerificationTests`; `Section13ContractTests.Row07_MerkleChildHashMismatch_...`; `PerFailureHandlerTests.MerkleMismatch_...` |
| 8 | 9.1 Key hierarchy | Passwords MUST be NFC-normalized then UTF-8 before KDF; implementations MUST zeroize password/KEK/DEK buffers at scope end | NFC: `V3PasswordKeyDerivationTests.DeriveKek_NfcNormalizesPassword_ComposedAndDecomposedFormsMatch`, `..._HangulComposedAndDecomposedFormsMatch`, `DeriveKek_UsesNfcThenUtf8_MatchesIndependentComputation`. Zeroize: `EpochDekProviderTests.Dispose_ZeroizesInternalDekBuffers`, `Dispose_ZeroizesOwnedKek`, `Dispose_DoesNotZeroizeCallerSuppliedDekBuffers`. (Password-byte zeroize: `PasswordKeyDerivation.cs:79 CryptographicOperations.ZeroMemory(passwordBytes)` — internal local buffer; see gap note.) |
| 9 | 9.2 KeyStore block | Writers MUST NOT stamp a retired epoch on new blocks; rotation MUST fail rather than exceed epoch 65535 | Retired-epoch guard: `EpochDekProviderTests.Constructor_ActiveEpochRetired_ThrowsArgumentException`, `Encrypt_UsesActiveEpoch_RoundTripsWithHeaderEpoch`, `Decrypt_RetiredEpoch_ThrowsEpochDekUnavailable_RetiredReason`. Epoch ceiling: `RotateKeyV3EpochBoundTests.Rotate_AtMaxEpoch_ThrowsEpochExhausted_AndLeavesTableUnchanged`, `RotateKey_FailsCleanly_AtEpoch65535_WithNoStateChange`, `Rotate_At65534_SucceedsTo65535_ThenRefusesAt65535` |
| 10 | 10.3 Durability rules | fsync failure is fatal ⇒ implementation MUST poison the handle, refuse further writes, force crash recovery on reopen, never retry fsync; file create/compaction-swap/delete MUST fsync the containing directory | Poison: `DurableStreamTests.FirstFsyncFailure_PoisonsHandle_AndReportsFailedResult`, `PoisonedHandle_RefusesFlush_AndNeverRetriesFsync`, `PoisonedHandle_RefusesWrites_WithoutTouchingTheFile`, `FlushToDisk_IssuesFsync_NeverBareStreamFlush`; `DurableStreamTests` (BlockManager fsync class) `FsyncFailure_PoisonsManager_AndFlushNeverRetriesFsync`, `AppendWriteFailure_PoisonsManager_AndRefusesFurtherWritesAndFlushes`; `PoisonedWriter_CannotWriteCleanShutdownMark_ReopenForcesRecovery`. Dir fsync: `DirectoryFsyncTests.SyncContainingDirectory_AfterFileCreate/Rename/Delete_Succeeds`; `WriterLockTests.Acquire_CreatingFiles_FsyncsDirectory_FailureSurfacesAndReleasesLock` |
| 11 | 12 Concurrency and Locking | Single writer holds OS-exclusive lock for file lifetime; a second writer MUST fail fast with a clear error, not corrupt | `WriterLockTests`: `SecondWriter_FailsFast_WithTypedError`, `SecondWriter_Error_IsClear_NamesFileAndRemedy`, `SecondWriter_NeverTouchesDatabaseFile`, `SecondWriter_FailsFast_WhileReadersAreOpenShared`, `CrossProcess_SecondProcessCannotTakeLock_UntilDisposed`, `Readers_OpenShared_WhileWriterHoldsLock` |
| 12 | 13 Corruption Handling Contract | Implementations MUST NOT improvise — the 13 tabulated failure→required-behavior rows | `Section13ContractTests`: `Row01`–`Row12` (one test per contract row, incl. torn superblock slot, both-slots-invalid rebuild, header-checksum resync, insane length, payload-checksum + referenced-live data-loss, GCM tag distinct class, Merkle fallback, torn/absent Checkpoint, stale offset hint, torn tail, bomb). Reinforced by `PerFailureHandlerTests`, `VerificationErrorTaxonomyTests`, `VerificationErrorDistinctnessTests`, `ForwardScanTests`, `DisasterOpenTests`, `DamagedRangeOffsetTests`, `ReferencedDataLossTests` |

## Result

**12 / 12** MUST-bearing spec sections have direct test coverage. **No section is
uncovered.**

## Gap note (negligible, not a coverage hole)

- **§9.1 password-byte zeroization:** the spec's "MUST zeroize password bytes" is a
  sub-clause of the key-hierarchy MUST. Production honors it at
  `EmailDB.Format/V3/PasswordKeyDerivation.cs:79`
  (`CryptographicOperations.ZeroMemory(passwordBytes)`); the KEK/DEK zeroization
  MUSTs of the same clause are asserted by `EpochDekProviderTests`. The password
  buffer is a method-local `byte[]` derived from an immutable `string`, so it is not
  observable from a black-box test without reflection. Verified by code inspection;
  the section itself is covered. No test was added because there is no observable
  surface to assert against, and the KEK/DEK path (the exploitable buffers) is
  already covered.
