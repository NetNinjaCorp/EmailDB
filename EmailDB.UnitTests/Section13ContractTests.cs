using EmailDB.Format;
using EmailDB.Format.V3;
using EmailDB.UnitTests.FaultInjection;

namespace EmailDB.UnitTests;

/// <summary>
/// The single systematic fault-injection contract table for
/// EmailDB_FileFormat_Spec.md Section 13: one test per contract row, each generating
/// a realistic v3 file (via <see cref="V3TestFileBuilder"/>) or a minimal block
/// fixture, inflicting the row's failure with the shared <see cref="FaultInjector"/>
/// primitives (byte-flip / truncate / transplant), and asserting the row's REQUIRED
/// behavior end-to-end through the real read/open paths — the correct taxonomy error
/// (<see cref="VerificationFailureKind"/> / <see cref="CorruptionCause"/> /
/// <see cref="TamperCause"/>), the located offsets/DamagedRange, and the recovery
/// action (resynchronize / fall back / re-resolve / refuse).
///
/// <para>Section 13 has twelve rows; the mapping table just below the summary maps
/// each row (and each distinct required-behavior variant — per-slot superblock, the
/// WAL-replay half of the torn-Checkpoint row, stale hints on every root-table pointer
/// kind) to its <c>Row##_</c> test. Piecemeal per-row coverage exists elsewhere
/// (<see cref="PerFailureHandlerTests"/> wires each handler,
/// <see cref="DamagedRangeOffsetTests"/> pins exact offsets,
/// <see cref="DisasterOpenTests"/>/<see cref="DirtyOpenTests"/> exercise the open
/// paths, <see cref="ReferencedDataLossTests"/> the live-data-loss guarantee); this
/// suite is the one place the whole table is asserted systematically, reusing those
/// same code paths rather than duplicating their deep edge-case assertions.</para>
///
/// <para><b>GCM rows (Row06):</b> decrypt-on-read is wired (US-EMDB-78-6,
/// <see cref="EncryptedBlockStore.ReadDecrypted"/>), so the tag / AAD failure is now
/// asserted both at the taxonomy boundary
/// (<see cref="Row06_GcmTagFailure_IsADistinctErrorClass_NotCorruption"/>) and end-to-end
/// through the real read path (<see cref="Row06_Gap_GcmTagFailureEndToEndThroughReadPath"/>):
/// a wrong-key read passes the ciphertext checksum FIRST, then fails GCM authentication.</para>
/// </summary>
//
// ===================== Section 13 row -> test coverage map =====================
// Every spec row (and each distinct required-behavior variant it names) has a
// dedicated fault-injection test that injects the fault on a real generated v3 file
// via the FaultInjection harness and asserts the row's REQUIRED behavior:
//
//  Spec row (Section 13)                            | Test(s)
//  -------------------------------------------------|-------------------------------
//  One superblock slot invalid (either slot)        | Row01_OneSuperblockSlotInvalid_UsesOtherSlot_OpensClean  [Theory: slot A, slot B]
//  Both slots invalid                               | Row02_BothSuperblocksInvalid_RefusesNormalOpen_FullScanRebuilds_SurfacesEncryptionUnrecoverable
//  HeaderChecksum mismatch                          | Row03_HeaderChecksumMismatch_BlockDead_ResyncsForward_LogsDamagedRange
//  PayloadLength insane                             | Row04_InsaneLength_TreatedAsHeaderCorruption_NeverAllocatesFirst
//  PayloadChecksum mismatch (resync)                | Row05a_PayloadChecksumMismatch_BlockDead_Resynchronizes
//  PayloadChecksum mismatch (referenced live)       | Row05b_PayloadChecksumMismatch_OnReferencedLiveBlock_SurfacesDataLossNamingBlockId
//  GCM tag / AAD failure (taxonomy boundary)        | Row06_GcmTagFailure_IsADistinctErrorClass_NotCorruption
//  GCM tag / AAD failure (end-to-end)               | Row06_Gap_GcmTagFailureEndToEndThroughReadPath  [wrong-key read: checksum passes, GCM tag fails]
//  Merkle ChildHash mismatch                        | Row07_MerkleChildHashMismatch_FailsLookup_AsIntegrityError_KeyingTheFallback
//  Checkpoint invalid (walk previous chain)         | Row08_TornCheckpoint_WalksPreviousChainToTheLastValidCommitPoint
//  Checkpoint invalid (matching WAL replayed)       | Row08b_PostCheckpointMatchingWal_IsReplayed_ThenCommittedFresh
//  Checkpoint invalid (torn WAL mid-replay)         | Row08c_TornWalEntryMidReplay_StopsAtLastValidBlock_ReplaysOnlyTheValidPrefix
//  No valid Checkpoint                              | Row09_NoValidCheckpoint_FullScanRebuildsIndexesAndWritesFreshCheckpoint
//  Offset hint -> wrong BlockId (folder root)       | Row10_StaleOffsetHint_ReResolvesByBlockId_AndIsNotAnError
//  Offset hint -> wrong BlockId (every root kind)   | Row10b_StaleOffsetHint_OnEveryRootTablePointerKind_ReResolvesByBlockId
//  Footer magic missing at EOF (torn tail)          | Row11_TornTail_LogicalTruncationAtLastValidBlock
//  Decompressed size exceeds bomb guard             | Row12_DecompressionBomb_TreatedAsPayloadCorruption
// ================================================================================
public class Section13ContractTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p)) File.Delete(p);
    }

    // ------------------------------------------------------------- Helpers

    private string NewPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"emaildb-s13-{Guid.NewGuid():N}.emdb");
        _paths.Add(p);
        return p;
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    /// <summary>
    /// A minimal on-disk fixture: two plain data blocks at offset 0 (no superblock
    /// region), for rows asserted directly at the block read boundary. Returns the two
    /// block locations; the writer is flushed and closed so the bytes are on disk.
    /// </summary>
    private (string Path, BlockLocation First, BlockLocation Second) TwoBlockFixture(
        int firstLen = 40, int secondLen = 200, long maxPayloadLength = Superblock.DefaultMaxPayloadLength)
    {
        var path = NewPath();
        BlockLocation first, second;
        using (var writer = new BlockManager(OpenRW(path), maxPayloadLength, firstBlockOffset: 0, ownsStream: true))
        {
            first = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(firstLen, 9)).Value;
            second = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(secondLen)).Value;
            Ok(writer.Flush());
        }
        return (path, first, second);
    }

    private BlockManager Reader(string path, long maxPayloadLength = Superblock.DefaultMaxPayloadLength) =>
        new(OpenRW(path), maxPayloadLength, firstBlockOffset: 0, ownsStream: true);

    private static byte[] WalKey(byte seed) =>
        Enumerable.Range(0, WalEntry.KeySize).Select(i => (byte)(seed + i)).ToArray();

    private static byte[] WalUlid(byte seed) =>
        Enumerable.Range(0, WalEntry.BlockIdSize).Select(i => (byte)(seed + i)).ToArray();

    /// <summary>Post-replay Checkpoint contents naming the generated file's real roots.</summary>
    private static CheckpointContents Contents(V3TestFile file) => new()
    {
        FolderTreeRoot = file.FolderPointer,
        PrimaryIndexRoot = CheckpointRootPointer.None,
        LocationIndexRoot = file.LocationPointer,
        MetadataRoot = CheckpointRootPointer.None,
        KeyStoreRoot = CheckpointRootPointer.None,
        SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
        LiveBlockCount = file.DataBlocks.Count,
        LiveByteCount = 4096,
        DeadByteCount = 0,
    };

    /// <summary>Records the WAL entries the replayer dispatches during recovery, in order.</summary>
    private sealed class RecordingSink : IWalReplaySink
    {
        public readonly List<string> Ops = new();
        public Result ApplyInsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId)
        { Ops.Add($"I:{Convert.ToHexStringLower(key)}"); return Result.Success(); }
        public Result ApplyDelete(ReadOnlySpan<byte> key)
        { Ops.Add($"D:{Convert.ToHexStringLower(key)}"); return Result.Success(); }
        public Result ApplyFolderOp(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId, ReadOnlySpan<byte> aux)
        { Ops.Add($"F:{Convert.ToHexStringLower(key)}"); return Result.Success(); }
    }

    // ============================================================= Row 01
    // "One superblock slot invalid | Torn superblock write | Use other slot;
    //  rewrite bad slot on next update"

    [Theory]
    [InlineData(0)] // tear slot A -> open via slot B
    [InlineData(1)] // tear slot B -> open via slot A
    public void Row01_OneSuperblockSlotInvalid_UsesOtherSlot_OpensClean(int tornSlot)
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(12).Build(NewPath());

        // Tear ONLY the named slot. The dual-slot protocol is symmetric: whichever
        // slot survives, the other still holds a valid superblock, so the clean fast
        // path opens directly using the other slot — no recovery, for either slot.
        file.Injector.CorruptSuperblockSlot(tornSlot);

        using var stream = OpenRW(file.Path);
        var clean = CleanOpener.Open(stream);
        Ok(clean);
        Assert.Equal(OpenOutcomeKind.CleanOpen, clean.Value.Kind);
        clean.Value.State!.Dispose();
    }

    // ============================================================= Row 02
    // "Both slots invalid | Severe damage | Refuse normal open. Recovery mode: full
    //  scan from 8192 ...; encryption bootstrap ... unrecoverable — surface explicitly"

    [Fact]
    public void Row02_BothSuperblocksInvalid_RefusesNormalOpen_FullScanRebuilds_SurfacesEncryptionUnrecoverable()
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(10).WithEncryptedBlock().Build(NewPath());
        file.Injector.DestroyBothSuperblocks();

        // Refuse normal open.
        using (var pre = OpenRW(file.Path))
        {
            var clean = CleanOpener.Open(pre);
            Assert.True(clean.IsFailure);
            Assert.Contains("no valid superblock", clean.Error);
        }

        // Recovery mode: full scan from 8192 rebuilds; the encrypted block's key
        // material was lost with the superblock, surfaced explicitly.
        using var stream = OpenRW(file.Path);
        var recovered = DisasterOpener.Open(stream);
        Ok(recovered);
        Assert.Equal(DisasterRecoveryKind.RebuiltFromFullScan, recovered.Value.Kind);
        Assert.True(recovered.Value.RecoveredBlockCount >= file.DataBlocks.Count);
        Assert.True(recovered.Value.EncryptionUnrecoverable);
        Assert.Contains("UNRECOVERABLE", recovered.Value.Detail!, StringComparison.Ordinal);
        recovered.Value.State?.Dispose();
    }

    // ============================================================= Row 03
    // "HeaderChecksum mismatch | Corrupt/torn header | Block dead. Resynchronize:
    //  scan forward for next valid HeaderMagic + checksum; log damaged range"

    [Fact]
    public void Row03_HeaderChecksumMismatch_BlockDead_ResyncsForward_LogsDamagedRange()
    {
        var (path, _, second) = TwoBlockFixture();

        // Corrupt a header byte of the second block (covered by the header checksum).
        new FaultInjector(path).FlipHeaderByte(second.Offset);

        // Direct read: the block is dead, surfaced as HeaderChecksum corruption with
        // the damaged range located over the 64 header bytes.
        using (var reader = Reader(path))
        {
            var read = reader.Read(second.Offset);
            Assert.True(read.IsFailure);
            var error = Assert.IsType<CorruptionError>(read.VerificationError);
            Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
            Assert.Equal(CorruptionCause.HeaderChecksum, error.Cause);
            Assert.Equal(second.Offset, error.Offset);
            Assert.NotNull(error.DamagedRange);
            Assert.Equal(second.Offset, error.DamagedRange!.Value.Start);
            Assert.Equal(second.Offset + BlockSerializer.SerializedHeaderSize, error.DamagedRange.Value.End);
        }

        // Resynchronize: a forward scan logs the damaged range and recovers past it
        // (the first, intact block is still returned).
        using (var reader = Reader(path))
        {
            var scan = reader.ScanForward();
            Ok(scan);
            Assert.Contains(scan.Value.DamagedRanges, d => second.Offset >= d.Start && second.Offset < d.End);
            Assert.NotEmpty(scan.Value.Blocks); // the valid prefix survived resync.
        }
    }

    // ============================================================= Row 04
    // "PayloadLength insane (> MaxPayloadLength or past EOF) | ... | Same as header
    //  corruption; never allocate first"

    [Fact]
    public void Row04_InsaneLength_TreatedAsHeaderCorruption_NeverAllocatesFirst()
    {
        // Write a 1000-byte payload, then reopen with a MaxPayloadLength of 999 so the
        // block's declared length is now "insane" — larger than the reader accepts.
        var (path, _, second) = TwoBlockFixture(firstLen: 40, secondLen: 1000);

        using var reader = Reader(path, maxPayloadLength: 999);
        var read = reader.Read(second.Offset);
        Assert.True(read.IsFailure);

        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
        Assert.Equal(CorruptionCause.InsaneLength, error.Cause);
        Assert.Equal(second.Offset, error.Offset);
        // "Never allocate first": the failure names the guard rather than an OOM.
        Assert.Contains("MaxPayloadLength", error.Message);
    }

    // ============================================================= Row 05
    // "PayloadChecksum mismatch | Corrupt payload | Block dead; resynchronize. If
    //  referenced live -> data-loss error naming the BlockId"

    [Fact]
    public void Row05a_PayloadChecksumMismatch_BlockDead_Resynchronizes()
    {
        var (path, _, second) = TwoBlockFixture();
        new FaultInjector(path).FlipPayloadByte(second.Offset, payloadByteIndex: 10);

        using (var reader = Reader(path))
        {
            var read = reader.Read(second.Offset);
            Assert.True(read.IsFailure);
            var error = Assert.IsType<CorruptionError>(read.VerificationError);
            Assert.Equal(CorruptionCause.PayloadChecksum, error.Cause);
            Assert.Equal(second.Offset, error.Offset);
        }

        // Resynchronize: the forward scan records the damaged payload range.
        using (var reader = Reader(path))
        {
            var scan = reader.ScanForward();
            Ok(scan);
            Assert.Contains(scan.Value.DamagedRanges, d => second.Offset >= d.Start && second.Offset < d.End);
        }
    }

    [Fact]
    public void Row05b_PayloadChecksumMismatch_OnReferencedLiveBlock_SurfacesDataLossNamingBlockId()
    {
        var path = NewPath();
        var offsetMap = new RuntimeBlockOffsetMap();
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, offsetMap: offsetMap, ownsStream: true);
        var store = new BTreeNodeStore(manager, offsetMap);

        // A BlockId-addressed node is one the resolver reports as a live block.
        var nodeRef = store.Append(BTreeNodeKind.Leaf, SamplePayload(64), BTreeChildAddressing.BlockId).Value;
        Ok(manager.Flush());
        Assert.True(offsetMap.TryGetLocation(nodeRef.Reference, out var loc) && loc is not null);

        new FaultInjector(path).FlipPayloadByte(loc!.Offset);

        var read = store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
        Assert.True(read.IsFailure);
        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(CorruptionCause.ReferencedLiveDataLoss, error.Cause);
        Assert.True(error.ReferencedLiveData);
        Assert.True(nodeRef.Reference.AsSpan().SequenceEqual(error.BlockId!)); // names the BlockId
        Assert.Contains(Convert.ToHexStringLower(nodeRef.Reference), error.Message);
    }

    // ============================================================= Row 06
    // "GCM tag / AAD failure (checksum OK) | Wrong key or tampering | Distinct error
    //  class, not 'corruption'. Never brute other epochs beyond the header's KeyEpoch"

    [Fact]
    public void Row06_GcmTagFailure_IsADistinctErrorClass_NotCorruption()
    {
        // A GCM tag / AAD failure is authentication, not damage: it must surface as the
        // WrongKeyOrTamper class carrying the attempted KeyEpoch, NEVER as corruption.
        // (Checksum covers ciphertext and is verified FIRST, so any on-disk byte-flip
        // is caught as PayloadChecksum corruption before a tag check — see the gap
        // note on Row06_Gap below; here the taxonomy boundary itself is asserted.)
        var tamper = WrongKeyOrTamperError.GcmTagFailure(offset: 8192, keyEpoch: 2);
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, tamper.Kind);
        Assert.Equal(TamperCause.GcmTag, tamper.Cause);
        Assert.Equal(2, tamper.KeyEpoch);

        // Distinct, separately catchable class — never conflated with corruption.
        Assert.IsNotType<CorruptionError>(tamper);
        VerificationError asBase = tamper;
        Assert.False(asBase is CorruptionError);
        Assert.NotEqual(VerificationFailureKind.Corruption, asBase.Kind);
        var caught = Assert.Throws<WrongKeyOrTamperError>(void () => throw tamper);
        Assert.Equal(TamperCause.GcmTag, caught.Cause);
    }

    [Fact]
    public void Row06_Gap_GcmTagFailureEndToEndThroughReadPath()
    {
        // Decrypt-on-read is now wired (EncryptedBlockStore.ReadDecrypted, US-EMDB-78-6),
        // so the GCM tag / AAD failure is produced END-TO-END: write an encrypted block,
        // then read it back under the WRONG DEK. The on-disk ciphertext is untouched, so
        // the PayloadChecksum (over ciphertext) passes FIRST; only then does the GCM tag
        // fail — surfacing WrongKeyOrTamperError (TamperCause.GcmTag) at the header's
        // KeyEpoch, never a CorruptionError, and never bruting other epochs.
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0xB0 + i)).ToArray();
        const ushort epoch = 4;
        byte[] Dek(byte seed) => Enumerable.Range(0, AesGcmBlockCipher.KeySize).Select(i => (byte)(i + seed)).ToArray();

        var path = NewPath();
        long offset;
        using (var provider = new EpochDekProvider(fileId, epoch, new[] { new EpochDekProvider.EpochDek(epoch, Dek(1)) }))
        using (var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true))
        {
            var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
            offset = store.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(96)).Value.Offset;
            Ok(manager.Flush());
        }

        using var wrongKey = new EpochDekProvider(fileId, epoch, new[] { new EpochDekProvider.EpochDek(epoch, Dek(200)) });
        using var reader = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);

        // Checksum-first: the raw read succeeds because the ciphertext bytes are intact.
        Ok(reader.Read(offset));

        var decryptStore = new EncryptedBlockStore(reader, wrongKey, EncryptionPolicy.Default);
        var error = Assert.Throws<WrongKeyOrTamperError>(() => decryptStore.ReadDecrypted(offset));
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, error.Kind);
        Assert.Equal(TamperCause.GcmTag, error.Cause);
        Assert.Equal(epoch, error.KeyEpoch);          // attempted only under the header epoch
        Assert.IsNotType<CorruptionError>(error);     // distinct from corruption
    }

    // ============================================================= Row 07
    // "Merkle ChildHash mismatch | Index corruption/tampering | Fail lookup; fall back
    //  to previous Checkpoint's root; ... Repeated -> rebuild index from EmailMetadata"

    [Fact]
    public void Row07_MerkleChildHashMismatch_FailsLookup_AsIntegrityError_KeyingTheFallback()
    {
        var path = NewPath();
        var offsetMap = new RuntimeBlockOffsetMap();
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, offsetMap: offsetMap, ownsStream: true);
        var store = new BTreeNodeStore(manager, offsetMap);

        var nodeRef = store.Append(
            BTreeNodeKind.Leaf, SamplePayload(EmailDB.Format.V3.BTreeNodeSerializer.NodeHeaderSize + 16), BTreeChildAddressing.Offset).Value;
        Ok(manager.Flush());

        // Tamper the EXPECTED child hash (not the bytes): path verification fails even
        // though the block's own checksum is intact — Merkle integrity, not corruption.
        var tampered = new BTreeNodeRef
        {
            Addressing = nodeRef.Addressing,
            Reference = nodeRef.Reference,
            NodeHash = FlipLast(nodeRef.NodeHash),
        };
        var read = store.ReadVerified(tampered, BTreeNodeKind.Leaf);
        Assert.True(read.IsFailure);

        // The lookup fails as the contracted Merkle failure the CowBTree fallback keys
        // on (fall back to the previous Checkpoint's root), surfaced as IntegrityError.
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(read.Error), read.Error);
        var error = Assert.IsType<IntegrityError>(read.VerificationError);
        Assert.Equal(VerificationFailureKind.Integrity, error.Kind);
        Assert.Equal(tampered.NodeHash, error.ExpectedHash);
    }

    // ============================================================= Row 08
    // "Checkpoint invalid | Torn commit | Walk PreviousCheckpointBlockId chain.
    //  Post-checkpoint blocks are uncommitted except matching WAL blocks (replayed)"

    [Fact]
    public void Row08_TornCheckpoint_WalksPreviousChainToTheLastValidCommitPoint()
    {
        // Two chained Checkpoints (seq 0 <- seq 1); the superblock hint names the oldest
        // so a dirty open must scan forward and adopt a newer one. Tear the NEWEST
        // Checkpoint's commit: the walk must fall back to the last fully valid one (seq 0).
        var file = new V3TestFileBuilder()
            .WithFillerBlocks(12).WithCheckpoints(2).WithCheckpointHint(0).WithCleanShutdown(0)
            .Build(NewPath());
        file.Injector.TearCheckpoint(file.NewestCheckpoint.Offset);

        var reader = new CheckpointReader(
            new BlockManager(OpenRW(file.Path), firstBlockOffset: BlockManager.DefaultFirstBlockOffset, ownsStream: true),
            new RuntimeBlockOffsetMap());

        // The torn newest Checkpoint no longer loads...
        Assert.True(reader.Load(file.NewestCheckpoint.Offset).IsFailure);
        // ...but walking the previous-Checkpoint chain from the still-valid predecessor
        // yields the last good commit point (spec Section 13 "walk PreviousCheckpoint").
        var previous = file.Checkpoints[0];
        var walk = reader.WalkChain(previous.Offset, file.FileId);
        Ok(walk);
        Assert.Equal(0UL, walk.Value[^1].CheckpointSequence);
    }

    // Row 08 second required behavior: "Post-checkpoint blocks are uncommitted EXCEPT
    // matching WAL blocks (replayed)." Driven end-to-end through the dirty-open
    // recovery path (DirtyOpener -> WalReplayer) on a real generated v3 file whose
    // WAL is fenced to the last Checkpoint.

    [Fact]
    public void Row08b_PostCheckpointMatchingWal_IsReplayed_ThenCommittedFresh()
    {
        var wal = new List<(ulong, WalEntry[])>
        {
            (0, new[] { WalEntry.Insert(WalKey(1), WalUlid(1)) }),
            (1, new[] { WalEntry.Delete(WalKey(2)) }),
        };
        var file = new V3TestFileBuilder()
            .WithFillerBlocks(8).WithCheckpoints(1).WithCleanShutdown(0).WithWal(wal)
            .Build(NewPath());

        var sink = new RecordingSink();
        using var stream = OpenRW(file.Path);
        var opened = DirtyOpener.Open(stream, replaySink: sink,
            freshCheckpointContents: () => Contents(file));
        Ok(opened);

        // The two uncommitted WAL blocks fenced to the last Checkpoint replayed in
        // WalSequence order — post-checkpoint blocks are NOT dropped when they are
        // matching WAL.
        Assert.Equal(2, opened.Value.ReplayedBlockCount);
        Assert.Equal(2, opened.Value.ReplayedEntryCount);
        Assert.Equal(
            new[] { $"I:{Convert.ToHexStringLower(WalKey(1))}", $"D:{Convert.ToHexStringLower(WalKey(2))}" },
            sink.Ops);
        // ...folded into a fresh Checkpoint that commits the recovered state.
        Assert.True(opened.Value.WroteFreshCheckpoint);
        Assert.True(opened.Value.HealedClean);
        opened.Value.State?.Dispose();
    }

    [Fact]
    public void Row08c_TornWalEntryMidReplay_StopsAtLastValidBlock_ReplaysOnlyTheValidPrefix()
    {
        var wal = new List<(ulong, WalEntry[])>
        {
            (0, new[] { WalEntry.Insert(WalKey(1), WalUlid(1)) }), // valid prefix
            (1, new[] { WalEntry.Insert(WalKey(2), WalUlid(2)) }), // torn mid-replay
        };
        var file = new V3TestFileBuilder()
            .WithFillerBlocks(8).WithCheckpoints(1).WithCleanShutdown(0).WithWal(wal)
            .Build(NewPath());

        // Flip a byte inside the LAST WAL block (near EOF): a torn tail struck the
        // second WAL block after it was appended — the crash tore mid-replay data.
        long fileLength = new FileInfo(file.Path).Length;
        file.Injector.ByteFlip(fileLength - 30);

        var sink = new RecordingSink();
        using var stream = OpenRW(file.Path);
        var opened = DirtyOpener.Open(stream, replaySink: sink,
            freshCheckpointContents: () => Contents(file));
        Ok(opened);

        // Only the valid prefix replayed; replay stops at the torn block and never
        // resynchronizes across the gap to apply operations of untrustworthy ordering.
        Assert.Single(sink.Ops);
        Assert.Equal($"I:{Convert.ToHexStringLower(WalKey(1))}", sink.Ops[0]);
        Assert.Equal(1, opened.Value.ReplayedEntryCount);
        Assert.True(opened.Value.DamagedRangeCount >= 1);
        opened.Value.State?.Dispose();
    }

    // ============================================================= Row 09
    // "No valid Checkpoint | Major damage | Full sequential scan; rebuild all indexes
    //  incl. BlockLocationIndex; write fresh Checkpoint"

    [Fact]
    public void Row09_NoValidCheckpoint_FullScanRebuildsIndexesAndWritesFreshCheckpoint()
    {
        var file = new V3TestFileBuilder().WithFillerBlocks(16).WithCheckpoints(2).Build(NewPath());
        // Tear EVERY Checkpoint in the chain: no valid commit point remains.
        foreach (var cp in file.Checkpoints)
            file.Injector.TearCheckpoint(cp.Offset);

        // The clean fast path can no longer resolve any Checkpoint.
        using (var pre = OpenRW(file.Path))
            Assert.True(CleanOpener.Open(pre).IsFailure);

        // Disaster recovery does a full scan, rebuilds the indexes, and writes a fresh
        // Checkpoint (sequence 0), then the file re-opens clean.
        using (var stream = OpenRW(file.Path))
        {
            var recovered = DisasterOpener.Open(stream);
            Ok(recovered);
            Assert.Equal(DisasterRecoveryKind.RebuiltIndexFromScan, recovered.Value.Kind);
            Assert.True(recovered.Value.RecoveredBlockCount >= file.DataBlocks.Count);
            Assert.Equal(0UL, recovered.Value.State!.Checkpoint.Checkpoint.CheckpointSequence);
            recovered.Value.State!.Dispose();
        }

        using var verify = OpenRW(file.Path);
        var reopened = CleanOpener.Open(verify);
        Ok(reopened);
        Assert.Equal(OpenOutcomeKind.CleanOpen, reopened.Value.Kind);
        reopened.Value.State!.Dispose();
    }

    // ============================================================= Row 10
    // "Offset hint resolves to wrong BlockId | Stale hint / misdirected write |
    //  Re-resolve via BlockLocationIndex; refresh hint at next Checkpoint. Not an error"

    [Fact]
    public void Row10_StaleOffsetHint_ReResolvesByBlockId_AndIsNotAnError()
    {
        // Build a file with a real root block, then transplant a DIFFERENT valid block's
        // bytes over a decoy offset and forge the Checkpoint to hint the real BlockId at
        // that decoy offset — a stale hint / misdirected write. Reading it must NOT
        // error: the reader re-resolves by BlockId through the chain.
        var path = NewPath();
        var runtimeMap = new RuntimeBlockOffsetMap();
        byte[] fileId = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, offsetMap: runtimeMap, ownsStream: true);
        var real = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(48, 0x44)).Value;
        var decoy = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(48, 0x55)).Value;

        // The stale hint: the real block's BlockId with the decoy's offset.
        var staleHint = CheckpointRootPointer.Create(real.BlockId, decoy.Offset);
        var writer = new CheckpointWriter(manager, fileId);
        Ok(writer.WriteCheckpoint(new CheckpointContents
        {
            FolderTreeRoot = staleHint,
            PrimaryIndexRoot = CheckpointRootPointer.None,
            LocationIndexRoot = CheckpointRootPointer.None,
            MetadataRoot = CheckpointRootPointer.None,
            KeyStoreRoot = CheckpointRootPointer.None,
            SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
            LiveBlockCount = 2, LiveByteCount = 96, DeadByteCount = 0,
        }));

        var reader = new CheckpointReader(manager, runtimeMap);
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, fileId);
        Ok(opened); // NOT an error.

        var resolved = opened.Value.FolderTreeRoot;
        Assert.NotNull(resolved);
        Assert.True(resolved!.HintWasStale, "the stale hint should have been re-resolved by BlockId.");
        Assert.Equal(real.Offset, resolved.Offset); // re-resolved to the block's true offset.
    }

    [Fact]
    public void Row10b_StaleOffsetHint_OnEveryRootTablePointerKind_ReResolvesByBlockId()
    {
        // The re-resolve-on-stale-hint contract applies to EVERY pointer in the
        // Checkpoint root table, not just the folder tree. Plant one real block per
        // root kind (plus a secondary index) and one decoy block, then forge a
        // Checkpoint that hints each real BlockId at the shared decoy offset — every
        // hint is stale (the decoy carries a different BlockId). Opening must NOT error
        // and must re-resolve each root by BlockId to its true offset.
        var path = NewPath();
        var runtimeMap = new RuntimeBlockOffsetMap();
        byte[] fileId = Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();

        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, offsetMap: runtimeMap, ownsStream: true);

        BlockLocation Real(int seed) =>
            manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(48, seed)).Value;

        var folder = Real(0x10);
        var primary = Real(0x20);
        var location = Real(0x30);
        var metadata = Real(0x40);
        var keyStore = Real(0x50);
        var secondary = Real(0x60);
        var decoy = Real(0x77); // a different valid block every stale hint points at
        Ok(manager.Flush());

        CheckpointRootPointer Stale(BlockLocation real) =>
            CheckpointRootPointer.Create(real.BlockId, decoy.Offset);

        var writer = new CheckpointWriter(manager, fileId);
        Ok(writer.WriteCheckpoint(new CheckpointContents
        {
            FolderTreeRoot = Stale(folder),
            PrimaryIndexRoot = Stale(primary),
            LocationIndexRoot = Stale(location),
            MetadataRoot = Stale(metadata),
            KeyStoreRoot = Stale(keyStore),
            SecondaryIndexes = new[]
            {
                CheckpointSecondaryIndex.Create(BTreeIndexKind.PrimaryEmail, secondary.BlockId, decoy.Offset),
            },
            LiveBlockCount = 7, LiveByteCount = 336, DeadByteCount = 0,
        }));

        var reader = new CheckpointReader(manager, runtimeMap);
        var opened = reader.Open(writer.LastCheckpointPointer.Offset, fileId);
        Ok(opened); // NOT an error for any root kind.

        void AssertReResolved(ResolvedRoot? root, BlockLocation real, string name)
        {
            Assert.NotNull(root);
            Assert.True(root!.HintWasStale, $"{name}: stale hint should have been re-resolved by BlockId.");
            Assert.Equal(real.Offset, root.Offset); // re-resolved to the block's true offset.
            Assert.True(real.BlockId.AsSpan().SequenceEqual(root.BlockId));
        }

        AssertReResolved(opened.Value.FolderTreeRoot, folder, "FolderTreeRoot");
        AssertReResolved(opened.Value.PrimaryIndexRoot, primary, "PrimaryIndexRoot");
        AssertReResolved(opened.Value.LocationIndexRoot, location, "LocationIndexRoot");
        AssertReResolved(opened.Value.MetadataRoot, metadata, "MetadataRoot");
        AssertReResolved(opened.Value.KeyStoreRoot, keyStore, "KeyStoreRoot");
        Assert.Single(opened.Value.SecondaryIndexes);
        AssertReResolved(opened.Value.SecondaryIndexes[0].Root, secondary, "SecondaryIndexes[0]");
    }

    // ============================================================= Row 11
    // "Footer magic missing at EOF | Torn final append | Logical truncation at last
    //  valid block; the append was uncommitted by definition"

    [Fact]
    public void Row11_TornTail_LogicalTruncationAtLastValidBlock()
    {
        var (path, first, second) = TwoBlockFixture(firstLen: 40, secondLen: 500);

        // Truncate mid-second-block so not even its 64 header bytes remain: a torn tail.
        long tornEof = second.Offset + 10;
        new FaultInjector(path).TruncateMidBlock(second.Offset);

        using (var reader = Reader(path))
        {
            // The torn final block is surfaced as a torn tail, damaged range to EOF.
            var read = reader.Read(second.Offset);
            Assert.True(read.IsFailure);
            var error = Assert.IsType<CorruptionError>(read.VerificationError);
            Assert.Equal(CorruptionCause.TornTail, error.Cause);
            Assert.Equal(second.Offset, error.Offset);
            Assert.NotNull(error.DamagedRange);
            Assert.Equal(second.Offset, error.DamagedRange!.Value.Start);
            Assert.Equal(tornEof, error.DamagedRange.Value.End);

            // Logical truncation: the last VALID block (the first, committed one) is
            // intact and still reads — the torn append was uncommitted by definition.
            var good = reader.Read(first.Offset);
            Ok(good);
        }
    }

    // ============================================================= Row 12
    // "Decompressed size exceeds bomb guard | Corrupt/malicious payload | Treat as
    //  payload corruption"

    [Fact]
    public void Row12_DecompressionBomb_TreatedAsPayloadCorruption()
    {
        // A payload that decompresses far larger than the guard allows.
        var compressed = BlockCompressor.Compress(new byte[100_000], CompressionAlgorithm.Zstd);
        Ok(compressed);

        var decompressed = BlockCompressor.Decompress(compressed.Value, CompressionAlgorithm.Zstd, maxPayloadLength: 1024);
        Assert.True(decompressed.IsFailure);

        var error = Assert.IsType<CorruptionError>(decompressed.VerificationError);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind); // treated as corruption
        Assert.Equal(CorruptionCause.DecompressionBomb, error.Cause);
        Assert.Contains("bomb guard", error.Message);
    }

    private static byte[] FlipLast(byte[] hash)
    {
        var copy = (byte[])hash.Clone();
        copy[^1] ^= 0x01;
        return copy;
    }
}
