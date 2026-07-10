using System.Buffers.Binary;
using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Acceptance-criterion coverage for story US-EMDB-89 criterion 5 (task US-EMDB-89-5):
/// <b>FolderVersions and CheckpointSequence continue across the swap so sync is unaffected.</b>
///
/// <para>The sibling suites (<see cref="CompactorRebuildTests"/>, <see cref="CompactorSwapTests"/>)
/// prove the mechanical swap and assert that the fresh Checkpoint continues the source's
/// <see cref="Checkpoint.CheckpointSequence"/>, but none of them puts a real
/// <see cref="FolderPageDirectory"/> block — the block that carries the per-folder
/// <see cref="FolderPageDirectory.FolderVersion"/> replication counter sync diffs against — through a
/// full compact + swap and re-reads the counter afterwards. This suite closes that gap.</para>
///
/// <para><see cref="FolderVersions_and_CheckpointSequence_continue_across_a_full_compact_and_swap"/>
/// builds a clean v3 source that carries two real folder directory blocks at DISTINCT, non-zero
/// FolderVersions (the blocks are ULID-addressed live entries of the BlockLocationIndex, exactly as a
/// committed mailbox stores them — the FolderId doubles as the block's stable BlockId), runs the real
/// <see cref="Compactor"/> end to end (copy → rebuild → atomic swap), reopens the compacted file, and
/// asserts a sync client sees a <b>coherent continuation</b>: the CheckpointSequence advanced by
/// exactly one (no reset, no regression) and every folder's FolderVersion crossed the swap with its
/// exact value (verbatim copy, so no reset, no regression, no cross-folder swap).</para>
///
/// <para><see cref="A_real_EmailManager_mailbox_compacts_end_to_end_preserving_folder_versions_and_sequence"/>
/// drives the full user-facing path: a real <see cref="EmailManager"/> mailbox with two folders of
/// emails, compacted in place via <see cref="EmailManager.Compact"/> — which copies every directly-
/// addressed Checkpoint root (folder tree, metadata, key store) alongside the location-index live
/// blocks — then reopened. It asserts each folder's FolderVersion and the CheckpointSequence continue
/// across the swap, the FileId is unchanged, and every email re-reads intact: the criterion proven
/// through the operator API, not just the Compactor primitive.</para>
///
/// docs/Sync.md, EmailDB_FileFormat_Spec.md Section 11.2 ("same FileId, continued sequences"),
/// docs/Compaction.md Section 5. Every handle and <see cref="Compactor"/> is disposed before a file is
/// reopened, so the suite passes in isolation and in the full parallel suite.
/// </summary>
public class CompactionCriterion5Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-c5-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    public void Dispose()
    {
        Delete(_path);
        Delete(SideFile);
        GC.SuppressFinalize(this);
    }

    private static void Delete(string p)
    {
        if (File.Exists(p))
            File.Delete(p);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static T Require<T>(Result<T> r)
    {
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
        return r.Value;
    }

    // ======================================================= Criterion coverage

    [Fact]
    public void FolderVersions_and_CheckpointSequence_continue_across_a_full_compact_and_swap()
    {
        // Two folders at DISTINCT, non-zero versions: a reset (→0), a regression, or a cross-folder
        // swap of the counters all fail these assertions.
        var folderA = new UlidGenerator().Next();
        var folderB = new UlidGenerator().Next();
        const ulong versionA = 3;
        const ulong versionB = 7;

        var source = BuildSourceWithFolders(
            new (byte[] FolderId, ulong Version)[] { (folderA, versionA), (folderB, versionB) },
            dataCount: 8);

        // A non-zero source CheckpointSequence so "continue, incremented" is a real advance.
        Assert.True(source.CheckpointSequence > 0);

        Checkpoint fresh;
        using (var compactor = Require(Compactor.Begin(_path)))
        {
            Require(compactor.CopyLiveBlocks());
            fresh = Require(compactor.RebuildLocationIndexAndWriteCheckpoint());
            Ok(compactor.FinalizeAndSwap());
            Assert.True(compactor.HasSwapped);
        }
        Assert.False(File.Exists(SideFile), "the swap must rename the side file away");

        // The fresh Checkpoint continues the source's sequence: exactly +1 (Compaction.md Section 5).
        Assert.Equal(source.CheckpointSequence + 1, fresh.CheckpointSequence);

        // Reopen the compacted file the way a sync client would and confirm the continuation is durable.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var opened = Require(CleanOpener.Open(stream));
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
        using var state = opened.State!;

        // Same file identity across the swap, and the CheckpointSequence advanced by one, never rewound.
        Assert.Equal(source.FileId, state.Superblock.FileId);
        Assert.Equal(source.CheckpointSequence + 1, state.Checkpoint.Checkpoint.CheckpointSequence);
        Assert.True(state.Checkpoint.Checkpoint.CheckpointSequence > source.CheckpointSequence,
            "the CheckpointSequence must never regress across compaction.");

        // Every folder's FolderVersion survives the swap with its exact value: resolve the directory
        // block by its stable FolderId (a live location-index entry), re-read it, and unpack the counter.
        AssertFolderVersion(state, folderA, versionA);
        AssertFolderVersion(state, folderB, versionB);
    }

    private static void AssertFolderVersion(OpenState state, byte[] folderId, ulong expectedVersion)
    {
        var lookup = Require(state.LocationIndex.Lookup(folderId));
        Assert.True(lookup.Found,
            $"folder {Convert.ToHexString(folderId)} must resolve in the compacted file's location index");

        var block = Require(state.BlockManager.Read(lookup.Offset));
        Assert.Equal(BlockType.FolderPageDirectory, block.Header.Type);

        var directory = FolderPageDirectory.UnpackPayload(block.Payload);
        Assert.Equal(folderId, directory.FolderId);
        Assert.Equal(expectedVersion, directory.FolderVersion); // continues across the swap, unchanged.
    }

    // ======================================================= Real mailbox, end to end

    [Fact]
    public void A_real_EmailManager_mailbox_compacts_end_to_end_preserving_folder_versions_and_sequence()
    {
        byte[] folderIdA, folderIdB;
        ulong versionBefore_A, versionBefore_B, sequenceBefore;
        byte[] fileIdBefore;
        var idsA = new List<V3Id>();
        var idsB = new List<V3Id>();

        // A real mailbox with two folders at DISTINCT, non-zero FolderVersions (folder A: 3 adds,
        // folder B: 5 adds — each AddEmail rewrites the directory once), cleanly committed.
        Require(EmailManager.Create(_path)).Dispose();
        using (var mgr = Require(EmailManager.Open(_path)))
        {
            var folderA = FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());
            folderIdA = folderA.FolderId;
            for (int i = 0; i < 3; i++)
            {
                var added = Require(mgr.AddEmail(MailboxRequest(folderA, i, 1_000 + i)));
                idsA.Add(added.EmailId);
                folderA = added.Folder!;
            }

            var folderB = FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());
            folderIdB = folderB.FolderId;
            for (int i = 0; i < 5; i++)
            {
                var added = Require(mgr.AddEmail(MailboxRequest(folderB, 1_000 + i, 500_000 + i)));
                idsB.Add(added.EmailId);
                folderB = added.Folder!;
            }

            Ok(mgr.Commit());

            versionBefore_A = Require(mgr.ListFolder(folderIdA, 0, 1_000)).FolderVersion;
            versionBefore_B = Require(mgr.ListFolder(folderIdB, 0, 1_000)).FolderVersion;
            sequenceBefore = mgr.LastCheckpointSequence;
            fileIdBefore = (byte[])mgr.Superblock.FileId.Clone();
            Assert.True(versionBefore_A > 0 && versionBefore_B > 0);
            Assert.NotEqual(versionBefore_A, versionBefore_B);

            // Compact in place via the operator API. Returns a fresh manager over the compacted file;
            // the original instance is spent. The side file is renamed away by the atomic swap.
            using var compacted = Require(mgr.Compact());
            Assert.False(File.Exists(SideFile), "the swap must rename the side file away");

            // Same file identity, and the CheckpointSequence continued (strictly greater, never reset).
            Assert.Equal(fileIdBefore, compacted.Superblock.FileId);
            Assert.True(compacted.LastCheckpointSequence > sequenceBefore,
                "the CheckpointSequence must continue (never regress) across compaction.");

            // Every folder's FolderVersion crossed the swap UNCHANGED (the directory blocks were copied
            // verbatim), so a sync client diffing on (FolderId, FolderVersion) sees no spurious change.
            var afterA = Require(compacted.ListFolder(folderIdA, 0, 1_000));
            var afterB = Require(compacted.ListFolder(folderIdB, 0, 1_000));
            Assert.Equal(versionBefore_A, afterA.FolderVersion);
            Assert.Equal(versionBefore_B, afterB.FolderVersion);
            Assert.Equal(3, afterA.TotalCount);
            Assert.Equal(5, afterB.TotalCount);

            // Every email survives compaction and re-reads intact through the primary index.
            foreach (var id in idsA.Concat(idsB))
            {
                var lookup = Require(compacted.PrimaryIndex!.TryGet(id.GetBytes()));
                Assert.True(lookup.Found, "every committed email must survive compaction");
            }
            var all = Require(compacted.DateIndex!.RangeQuery(0, long.MaxValue));
            Assert.Equal(idsA.Count + idsB.Count, all.Count);

            Ok(compacted.Close());
        }
    }

    private static AddEmailRequest MailboxRequest(FolderPageDirectory folder, int n, long ticks) => new()
    {
        RawContent = Encoding.UTF8.GetBytes($"From: s{n}@example.com\r\nSubject: probe {n}\r\n\r\nbody number {n}"),
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = "sender@example.com",
        Subject = "probe",
        Preview = "preview",
    };

    // ======================================================= Synthetic source builder

    private sealed record SourceFacts(byte[] FileId, ulong CheckpointSequence);

    /// <summary>
    /// Builds a clean v3 file at <see cref="_path"/> that carries real <see cref="FolderPageDirectory"/>
    /// blocks (one per <paramref name="folders"/> entry, appended under its FolderId as the block's
    /// BlockId with the requested FolderVersion) plus data, FolderTree, and Metadata blocks — all of them
    /// live BlockLocationIndex entries, the shape a committed mailbox stores and the shape
    /// <see cref="CompactorRebuildTests"/> relies on so a full compaction can copy and remap every root.
    /// Two Checkpoints are written so the durable CheckpointSequence is non-zero.
    /// </summary>
    private SourceFacts BuildSourceWithFolders(
        IReadOnlyList<(byte[] FolderId, ulong Version)> folders, int dataCount)
    {
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)(0x50 + i)).ToArray();
        var live = new List<BlockLocation>();
        ulong sequence;
        CheckpointRootPointer checkpointPointer;

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            for (int i = 0; i < dataCount; i++)
            {
                var buf = new byte[128];
                Array.Fill(buf, (byte)(i & 0xFF));
                live.Add(Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, buf)));
            }

            // Real folder directory blocks: FolderId IS the block's stable BlockId (docs/Folder_Listing.md
            // Section 2), and the packed payload carries the FolderVersion counter.
            foreach (var (folderId, version) in folders)
            {
                var payload = FolderPageDirectory
                    .Create(folderId, Array.Empty<PageEntry>(), headDeltaBlockId: null, folderVersion: version)
                    .PackPayload();
                live.Add(Require(manager.Append(
                    BlockType.FolderPageDirectory, PayloadEncoding.Custom, payload, blockId: folderId)));
            }

            // Named structural roots kept as live index entries so the rebuild can remap them (the shape
            // the existing Compactor tests use).
            var folderTreeBuf = new byte[64];
            Array.Fill(folderTreeBuf, (byte)0xF0);
            var folderTree = Require(manager.Append(BlockType.FolderTree, PayloadEncoding.RawBytes, folderTreeBuf));
            live.Add(folderTree);

            var metadataBuf = new byte[48];
            Array.Fill(metadataBuf, (byte)0x0F);
            var metadata = Require(manager.Append(BlockType.Metadata, PayloadEncoding.RawBytes, metadataBuf));
            live.Add(metadata);

            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
            Ok(index.PutBatch(live));
            Ok(manager.Flush());

            long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root!.RootRef.Reference);
            var rootBlockId = runtimeMap.SnapshotOrderedByOffset().First(b => b.Offset == rootOffset).BlockId;

            CheckpointContents Contents() => new()
            {
                FolderTreeRoot = CheckpointRootPointer.Create(folderTree.BlockId, folderTree.Offset),
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = CheckpointRootPointer.Create(rootBlockId, rootOffset),
                MetadataRoot = CheckpointRootPointer.Create(metadata.BlockId, metadata.Offset),
                KeyStoreRoot = CheckpointRootPointer.None,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = live.Count,
                LiveByteCount = 424_242,
                DeadByteCount = 100_000,
            };

            // Two Checkpoints so the durable sequence is non-zero (0 then 1); the second is the hint.
            var writer = new CheckpointWriter(manager, fileId);
            Require(writer.WriteCheckpoint(Contents()));
            Require(writer.WriteCheckpoint(Contents()));
            sequence = writer.LastSequence!.Value;
            checkpointPointer = writer.LastCheckpointPointer;
        }

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var sbManager = new SuperblockManager(stream, ownsStream: true))
        {
            Superblock MakeSuperblock() => new()
            {
                FileId = (byte[])fileId.Clone(),
                CleanShutdown = 1,
                LastCheckpointBlockId = (byte[])checkpointPointer.BlockId.Clone(),
                LastCheckpointOffset = checkpointPointer.Offset,
            };
            Require(sbManager.Write(MakeSuperblock()));
            Require(sbManager.Write(MakeSuperblock()));
        }

        return new SourceFacts(fileId, sequence);
    }
}
