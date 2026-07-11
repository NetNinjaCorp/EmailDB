using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Options controlling how <see cref="EmailManager.Create"/> initializes a new v3
/// file (EmailDB_FileFormat_Spec.md Section 11.1). All fields have sensible defaults;
/// the only choice a caller usually makes is whether to supply a
/// <see cref="Password"/> (encrypted) or not (plaintext).
/// </summary>
public sealed class EmailManagerCreateOptions
{
    /// <summary>
    /// User password. Null (the default) creates a plaintext file; a non-null value
    /// creates an encrypted file — a fresh salt, a first DEK (epoch 0), a
    /// KEK-verification token, and a KEK-encrypted KeyStore block are generated so
    /// the file reopens from file + password alone (docs/Encryption.md Section 2).
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Argon2id KDF costs for an encrypted file. Null uses
    /// <see cref="Argon2idParams.Default"/>; a non-default set is stored in the
    /// superblock and honored on reopen (proving parameters come from the file, not
    /// compiled-in constants). Ignored for a plaintext file.
    /// </summary>
    public Argon2idParams? KdfParameters { get; init; }

    /// <summary>
    /// At-rest encryption policy for an encrypted file (spec Section 9.5). Ignored for
    /// a plaintext file. Defaults to <see cref="EncryptionPolicy.Default"/>.
    /// </summary>
    public EncryptionPolicy EncryptionPolicy { get; init; } = EncryptionPolicy.Default;

    /// <summary>Zero-based shard index stamped into the superblock (spec Section 15).</summary>
    public uint ShardIndex { get; init; }

    /// <summary>Sanity bound for block PayloadLength stored in the superblock.</summary>
    public long MaxPayloadLength { get; init; } = Superblock.DefaultMaxPayloadLength;
}

/// <summary>
/// Options controlling how <see cref="EmailManager.Open"/> reopens an existing v3 file
/// (EmailDB_FileFormat_Spec.md Section 10.2). The only choice a caller makes is whether
/// to supply the <see cref="Password"/> the file was encrypted with — a plaintext file
/// opens with no options at all.
/// </summary>
public sealed class EmailManagerOpenOptions
{
    /// <summary>
    /// User password for an encrypted file. Null (the default) opens a plaintext file;
    /// opening an encrypted file with a null or wrong password fails (docs/Encryption.md
    /// Section 2). Supplying a password for a plaintext file is ignored.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// The v3 file-lifecycle owner (EmailDB_FileFormat_Spec.md Section 11.1, story
/// US-EMDB-84). It owns creating, opening, and closing an <c>.emdb</c> file; this
/// class implements <b>create/initialize</b> (US-EMDB-84-5) and <b>open</b>
/// (US-EMDB-84-6). Close (US-EMDB-84-7) is a sibling task that plugs into the seam
/// marked below.
///
/// <para><b>Create protocol (spec Section 11.1).</b> <see cref="Create"/> writes a
/// complete, crash-safe, reopenable file in the spec's durability order:</para>
/// <list type="number">
///   <item>Mint the file's <c>FileId</c> (a ULID minted once at creation).</item>
///   <item>(Encrypted only) generate the salt + first DEK and append the KEK-encrypted
///   <b>KeyStore</b> block (<see cref="EncryptionBootstrap.CreateEncryption"/>).</item>
///   <item>Append the initial <b>Metadata</b> and <b>FolderTree</b> blocks — plaintext
///   or policy-encrypted per the encryption policy (spec Section 9.5).</item>
///   <item>Write the first <b>Checkpoint</b> (sequence 0) naming those roots with
///   <b>empty index roots</b> (no Primary/Location index yet). The
///   <see cref="CheckpointWriter"/> enforces <c>contents → fsync → Checkpoint → fsync</c>,
///   so the commit point is durable before any superblock references it.</item>
///   <item>Write both superblock slots — <b>A (sequence 1)</b> and <b>B (sequence 2)</b> —
///   carrying <c>CleanShutdown = 1</c>, the encryption fields, and the LastCheckpoint hint;
///   each slot write fsyncs (<see cref="SuperblockManager"/>).</item>
///   <item><b>fsync the containing directory</b> so the new file's directory entry is
///   itself durable (<see cref="DirectoryFsync"/>, spec Section 10.3): a crash after this
///   returns can never lose the freshly created file.</item>
/// </list>
///
/// <para>The result is a file whose on-disk state <see cref="CleanOpener"/> opens with
/// zero scanning, with and without encryption. The returned instance holds the file
/// open (exclusive writer lock) with the built <see cref="Superblock"/>,
/// <see cref="BlockManager"/>, and — for an encrypted file — the loaded DEK provider;
/// <see cref="Dispose"/> zeroizes key material and releases the file. The
/// write/Open/Close paths build on these fields.</para>
/// </summary>
public sealed class EmailManager : IDisposable
{
    private readonly FileStream _stream;
    private readonly Superblock _superblock;
    private readonly EpochDekProvider? _provider;
    private readonly RuntimeBlockOffsetMap _runtimeMap;
    private readonly OpenState? _openState;
    private readonly DeadBlockAccountant? _accountant;
    private readonly FolderPageStore? _folderPageStore;
    private readonly FolderPageDirectoryStore? _folderDirectoryStore;
    private readonly FolderDeltaLogStore? _folderDeltaStore;
    // Write-path session state (US-EMDB-85): the PrimaryEmail + Date indexes the
    // AddEmail pipeline buffers inserts into, the WAL writer it logs each insert to,
    // and the encrypted block store it writes Tier 2/3 payloads through. Wired only on
    // Open (the composed read-side); null on a Create-but-never-opened instance.
    private readonly BTreeNodeStore? _indexNodeStore;
    private readonly WalBufferedIndex? _primaryIndex;
    private readonly DateIndex? _dateIndex;
    private readonly WalWriter? _walWriter;
    private readonly EncryptedBlockStore? _encryptedStore;
    private readonly BlockManagerIndexRootStore? _dateIndexRootStore;
    // FTS address trigram index (US-EMDB-91-7): the in-memory posting state, and the store that
    // reads/writes its always-encrypted segment blocks. Maintained on AddEmail/DeleteEmail like the
    // Date index; its SearchRoot is registered in the Checkpoint under IndexKind 3. Null on Create.
    private readonly FtsIndex? _ftsIndex;
    private readonly FtsBlockStore? _ftsBlockStore;
    // Per-folder Bloom filters (US-EMDB-97-5, docs/Search.md Phase 5): the in-memory filter set that
    // gates which folders a multi-folder scan touches, and the store that reads/writes its always-encrypted
    // catalog block (type 18). Rebuilt for a folder at page-compile time; the catalog is registered in the
    // Checkpoint under IndexKind 4. Null on a Create-but-never-opened instance.
    private readonly FolderBloomIndex? _bloomIndex;
    private readonly BloomFilterStore? _bloomStore;
    // Group-commit state (US-EMDB-85-6). The last persisted Date IndexRoot descriptor and the
    // block that holds it, so successive Commits increment its Sequence monotonically and the
    // Checkpoint's secondary table names the durable descriptor. Advanced by Commit.
    private IndexRoot? _dateIndexRoot;
    private BlockLocation? _dateIndexRootLocation;
    // Emails added since the last Checkpoint; when it reaches AutoCommitThreshold, AddEmail
    // auto-commits so a long AddEmail run produces a BOUNDED number of Checkpoints (group commit).
    private int _uncommittedAdds;
    private bool _disposed;
    private bool _closed;

    private EmailManager(
        string path,
        FileStream stream,
        BlockManager blockManager,
        RuntimeBlockOffsetMap runtimeMap,
        Superblock superblock,
        EpochDekProvider? provider,
        CheckpointRootPointer lastCheckpoint,
        ulong lastCheckpointSequence)
    {
        Path = path;
        _stream = stream;
        BlockManager = blockManager;
        _runtimeMap = runtimeMap;
        _superblock = superblock;
        _provider = provider;
        LastCheckpoint = lastCheckpoint;
        LastCheckpointSequence = lastCheckpointSequence;
    }

    /// <summary>
    /// The reconstructed committed version of a BlockId-addressed index at open: the
    /// in-memory <see cref="BTreeRoot"/> handle, its persistable <see cref="IndexRoot"/>
    /// descriptor, and the location of the IndexRoot block that holds it. All null for
    /// an index the committed Checkpoint names no root for (empty index).
    /// </summary>
    private readonly record struct IndexSeed(BTreeRoot? Root, IndexRoot? IndexRoot, BlockLocation? IndexRootLocation)
    {
        /// <summary>An empty index (the Checkpoint names no root).</summary>
        public static IndexSeed Empty => new(null, null, null);
    }

    /// <summary>
    /// Reconstructs a BlockId-addressed index's committed version from the durable
    /// <see cref="IndexRoot"/> block a Checkpoint named (spec Sections 6, 10.1;
    /// docs/BTree_Index.md Section 6). <paramref name="resolvedRoot"/> points at the
    /// IndexRoot block (BlockType 6); its RootBlockId + EntryCount + TreeHeight +
    /// RootHash rebuild the <see cref="BTreeRoot"/> whose nodes the resolver then
    /// resolves through the reconstructed location index. A null pointer is an empty
    /// index (<see cref="IndexSeed.Empty"/>).
    /// </summary>
    private static Result<IndexSeed> ReconstructIndexSeed(
        BlockManager manager, ResolvedRoot? resolvedRoot, BTreeIndexKind expectedKind)
    {
        if (resolvedRoot is null)
            return Result<IndexSeed>.Success(IndexSeed.Empty);

        var block = manager.ReadDecompressed(resolvedRoot.Offset);
        if (block.IsFailure)
            return Result<IndexSeed>.Failure(
                $"Open failed reading the {expectedKind} IndexRoot block at offset {resolvedRoot.Offset}: {block.Error}");
        if (block.Value.Header.Type != BlockType.IndexRoot)
            return Result<IndexSeed>.Failure(
                $"Open failed: the {expectedKind} index root block at offset {resolvedRoot.Offset} is type " +
                $"{block.Value.Header.Type}, not an IndexRoot (type {(int)BlockType.IndexRoot}).");

        var descriptor = IndexRootSerializer.Deserialize(block.Value.Payload);
        if (descriptor.IsFailure)
            return Result<IndexSeed>.Failure(
                $"Open failed deserializing the {expectedKind} IndexRoot descriptor: {descriptor.Error}");
        if (descriptor.Value.IndexKind != expectedKind)
            return Result<IndexSeed>.Failure(
                $"Open failed: the reconstructed IndexRoot names IndexKind {descriptor.Value.IndexKind}, expected {expectedKind}.");

        var btreeRoot = new BTreeRoot
        {
            RootRef = new BTreeNodeRef
            {
                Addressing = BTreeChildAddressing.BlockId,
                Reference = (byte[])descriptor.Value.RootBlockId.Clone(),
                NodeHash = (byte[])descriptor.Value.RootHash.Clone(),
            },
            Height = descriptor.Value.TreeHeight,
            EntryCount = descriptor.Value.EntryCount,
        };

        var location = new BlockLocation
        {
            BlockId = (byte[])resolvedRoot.BlockId.Clone(),
            Offset = resolvedRoot.Offset,
            TotalBlockLength = resolvedRoot.TotalBlockLength,
        };

        return Result<IndexSeed>.Success(new IndexSeed(btreeRoot, descriptor.Value, location));
    }

    /// <summary>
    /// Constructs an <b>opened</b> manager (US-EMDB-84-6): it wraps the composed read-side
    /// <see cref="OpenState"/> the open protocol produced (superblock + resolved Checkpoint +
    /// live BlockLocationIndex + resolver over the opened <see cref="BlockManager"/>), the loaded
    /// encryption provider, and the folder-layer stores wired over that same block manager.
    /// </summary>
    private EmailManager(
        string path,
        FileStream stream,
        OpenState openState,
        EpochDekProvider? provider,
        FolderPageStore folderPageStore,
        FolderPageDirectoryStore folderDirectoryStore,
        FolderDeltaLogStore folderDeltaStore,
        CheckpointRootPointer lastCheckpoint,
        ulong lastCheckpointSequence,
        IndexSeed primarySeed,
        IndexSeed dateSeed,
        FtsIndex ftsIndex,
        FtsBlockStore ftsBlockStore,
        FolderBloomIndex bloomIndex,
        BloomFilterStore bloomStore)
    {
        Path = path;
        _stream = stream;
        BlockManager = openState.BlockManager;
        _runtimeMap = openState.RuntimeMap;
        _superblock = openState.Superblock;
        _provider = provider;
        _openState = openState;
        // Restore the live/dead byte accounting from the Checkpoint this open resolved (spec
        // Sections 10.1, 11.2): reopening resumes from exactly the counters the last Checkpoint
        // committed, so supersession accounting survives close/reopen (docs/Compaction.md Section 3).
        _accountant = DeadBlockAccountant.FromCheckpoint(openState.Checkpoint.Checkpoint);
        _folderPageStore = folderPageStore;
        _folderDirectoryStore = folderDirectoryStore;
        _folderDeltaStore = folderDeltaStore;
        LastCheckpoint = lastCheckpoint;
        LastCheckpointSequence = lastCheckpointSequence;

        // Wire the AddEmail write-path (US-EMDB-85). A BlockId-addressed node store over the
        // opened resolver (runtime map → location index, spec Section 7) backs both the
        // PrimaryEmail index (EmailHashedID → ContentBlockId, IndexKind 0) and the Date index
        // (IndexKind 2). Both are SEEDED from the committed Checkpoint's durable index roots
        // (US-EMDB-85-6): the PrimaryIndexRoot / Date secondary pointer name an IndexRoot block
        // (BlockType 6) whose RootBlockId/EntryCount/TreeHeight/RootHash rebuild the tree version,
        // so a committed AddEmail survives reopen and cross-session dedupe works. An empty root
        // (a fresh file, or one never committed with emails) seeds the empty tree. The PrimaryEmail
        // index is WAL-buffered so its inserts batch into one COW pass; the WAL writer fences each
        // logged insert to the current committed Checkpoint.
        _indexNodeStore = new BTreeNodeStore(openState.BlockManager, openState.Resolver);
        var primaryTree = new CowBTree(
            _indexNodeStore, BTreeIndexKind.PrimaryEmail,
            keySize: EmailHashedID.Size, leafValueSize: UlidGenerator.UlidSize);
        _primaryIndex = new WalBufferedIndex(
            primaryTree,
            new BlockManagerIndexRootStore(openState.BlockManager),
            fsync: openState.BlockManager.Flush,
            initialRoot: primarySeed.Root,
            initialIndexRoot: primarySeed.IndexRoot,
            initialIndexRootLocation: primarySeed.IndexRootLocation);
        _dateIndex = new DateIndex(_indexNodeStore, initialRoot: dateSeed.Root);
        _dateIndexRoot = dateSeed.IndexRoot;
        _dateIndexRootLocation = dateSeed.IndexRootLocation;
        _dateIndexRootStore = new BlockManagerIndexRootStore(openState.BlockManager);
        _walWriter = new WalWriter(openState.BlockManager, () => LastCheckpoint);
        _encryptedStore = provider is null
            ? null
            : new EncryptedBlockStore(openState.BlockManager, provider, EncryptionPolicy.Default);
        _ftsIndex = ftsIndex;
        _ftsBlockStore = ftsBlockStore;
        _bloomIndex = bloomIndex;
        _bloomStore = bloomStore;
    }

    /// <summary>Filesystem path of the managed file.</summary>
    public string Path { get; }

    /// <summary>The block manager over the open file (append/read/fsync machinery).</summary>
    public BlockManager BlockManager { get; }

    /// <summary>The current in-memory superblock (slot B, sequence 2, immediately after create).</summary>
    public Superblock Superblock => _superblock;

    /// <summary>The 16-byte FileId minted at creation.</summary>
    public byte[] FileId => (byte[])_superblock.FileId.Clone();

    /// <summary>True when the file is encrypted (a DEK provider is loaded).</summary>
    public bool IsEncrypted => _provider is not null;

    /// <summary>The loaded DEK provider for an encrypted file; null for a plaintext file.</summary>
    public EpochDekProvider? EncryptionProvider => _provider;

    /// <summary>
    /// ULID+offset pointer to the last committed Checkpoint. Advances every time
    /// <see cref="Commit"/> (or <see cref="Close"/>) durably writes a new Checkpoint,
    /// so subsequent WAL entries fence to the new commit point (spec Section 10.4).
    /// </summary>
    public CheckpointRootPointer LastCheckpoint { get; private set; }

    /// <summary>Sequence of the last committed Checkpoint. Advances on each <see cref="Commit"/>/<see cref="Close"/>.</summary>
    public ulong LastCheckpointSequence { get; private set; }

    /// <summary>
    /// Number of buffered <see cref="AddEmail"/> calls that trigger an automatic
    /// <see cref="Commit"/> (group-commit batching, US-EMDB-85-6): a run of AddEmail
    /// calls shares one flush + Checkpoint until this many fresh emails accumulate,
    /// so a bulk load produces a BOUNDED number of Checkpoints rather than one per
    /// email. Explicit <see cref="Commit"/>/<see cref="Close"/> commit early. Must be
    /// at least 1; defaults to 256.
    /// </summary>
    public int AutoCommitThreshold
    {
        get => _autoCommitThreshold;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _autoCommitThreshold = value;
        }
    }
    private int _autoCommitThreshold = 256;

    /// <summary>
    /// True when this instance was produced by <see cref="Open"/> and therefore exposes the
    /// full composed read-side (<see cref="Checkpoint"/>, <see cref="LocationIndex"/>,
    /// <see cref="Resolver"/>, folder stores). A freshly <see cref="Create"/>d instance is not
    /// "opened" in this sense — it holds the just-written file but not the reconstructed indexes.
    /// </summary>
    public bool IsOpen => _openState is not null;

    /// <summary>
    /// The validated, FileId-cross-checked Checkpoint the open resolved every root of (the
    /// commit-point snapshot naming FolderTree, Metadata, KeyStore, and the primary/location
    /// index roots). Null on a <see cref="Create"/>d instance that was never opened.
    /// </summary>
    public ResolvedCheckpoint? Checkpoint => _openState?.Checkpoint;

    /// <summary>
    /// The live <see cref="BlockLocationIndex"/> the open reconstructed from the Checkpoint's
    /// location root (empty for a file with no live blocks). Null on an unopened instance.
    /// </summary>
    public BlockLocationIndex? LocationIndex => _openState?.LocationIndex;

    /// <summary>
    /// The BlockId resolution precedence chain the open wired (runtime map → location index,
    /// spec Section 7). Null on an unopened instance.
    /// </summary>
    public CompositeBlockIdResolver? Resolver => _openState?.Resolver;

    /// <summary>The B+-tree node store the reconstructed indexes read through. Null on an unopened instance.</summary>
    public BTreeNodeStore? NodeStore => _openState?.NodeStore;

    /// <summary>The Tier 1 <see cref="FolderPageStore"/> wired over the opened block manager. Null on an unopened instance.</summary>
    public FolderPageStore? Folders => _folderPageStore;

    /// <summary>The <see cref="FolderPageDirectoryStore"/> wired over the opened block manager. Null on an unopened instance.</summary>
    public FolderPageDirectoryStore? FolderDirectory => _folderDirectoryStore;

    /// <summary>The <see cref="FolderDeltaLogStore"/> wired over the opened block manager. Null on an unopened instance.</summary>
    public FolderDeltaLogStore? FolderDeltas => _folderDeltaStore;

    /// <summary>
    /// The live/dead byte accountant for this session (docs/Compaction.md Section 3, spec
    /// Section 11.2), seeded on <see cref="Open"/> from the resolved Checkpoint's counters and
    /// advanced by <see cref="RecordSupersession"/> as blocks are retired. Its
    /// <see cref="DeadBlockAccountant.DeadByteCount"/>/<see cref="DeadBlockAccountant.LiveByteCount"/>
    /// are written into the next Checkpoint by <see cref="Close"/>. Null on an unopened instance.
    /// </summary>
    public DeadBlockAccountant? Accountant => _accountant;

    /// <summary>
    /// Evaluates the compaction triggers for this open session with <b>no file scan</b>
    /// (docs/Compaction.md Section 3, spec Section 11.2): it reads the session's current
    /// live/dead byte counters straight from the <see cref="Accountant"/> and the
    /// DEK-pruning-pending signal from the loaded <see cref="EncryptionProvider"/> (always
    /// false for a plaintext file), then hands both to <see cref="CompactionTriggerEvaluator"/>.
    /// This is the signal the maintenance scheduler consumes to decide whether — and how
    /// urgently — to compact, and whether to run with <c>reEncrypt = true</c>.
    /// </summary>
    /// <param name="idleDeadToLiveRatio">Dead:Live ratio above which idle compaction is due (default 1.0).</param>
    /// <param name="urgentDeadToLiveRatio">Dead:Live ratio above which urgent compaction is due (default 3.0).</param>
    /// <exception cref="InvalidOperationException">
    /// The manager was produced by <see cref="Create"/> and never opened, so it carries no
    /// live/dead accounting to evaluate.
    /// </exception>
    public CompactionSignal EvaluateCompactionTrigger(
        double idleDeadToLiveRatio = CompactionTriggerEvaluator.DefaultIdleDeadToLiveRatio,
        double urgentDeadToLiveRatio = CompactionTriggerEvaluator.DefaultUrgentDeadToLiveRatio)
    {
        if (_accountant is null)
            throw new InvalidOperationException(
                "EvaluateCompactionTrigger requires an opened EmailManager (Open, not Create): the " +
                "live/dead accounting is wired only over the composed read-side.");
        bool dekPruningPending = _provider?.DekPruningPending ?? false;
        return CompactionTriggerEvaluator.Evaluate(
            _accountant, dekPruningPending, idleDeadToLiveRatio, urgentDeadToLiveRatio);
    }

    /// <summary>
    /// Records that <paramref name="superseded"/> blocks have gone dead — every COW rewrite and
    /// delete calls this to move the retired blocks' bytes from the live to the dead counter
    /// (docs/Compaction.md Section 3, story US-EMDB-88). It performs both halves of the contract
    /// atomically for the caller:
    /// <list type="number">
    ///   <item>appends a <b>Cleanup block</b> (BlockType 3, always plaintext — spec Section 9.5)
    ///   recording the superseded BlockIds and their sizes for durable audit, stamped with the
    ///   currently committed Checkpoint's sequence; and</item>
    ///   <item>advances the <see cref="Accountant"/> so those bytes are dead — the shift that
    ///   <see cref="Close"/> persists into the next Checkpoint's <c>DeadByteCount</c>.</item>
    /// </list>
    /// The appended Cleanup block is itself a live append this session, folded into the location
    /// index and counted live by <see cref="Close"/>. An empty list is a success no-op that writes
    /// nothing. The counters are updated only after the Cleanup block is durably appended, so a
    /// failed append leaves the accounting unchanged.
    /// </summary>
    /// <param name="superseded">The blocks retired by a COW rewrite or delete; each contributes its BlockId and on-disk size.</param>
    /// <returns>
    /// The location of the appended Cleanup block on success; <see cref="CheckpointRootPointer.None"/>-shaped
    /// no-op is signalled by a null value inside a success only for the empty-input case. A failure
    /// (accounting untouched) otherwise.
    /// </returns>
    public Result<BlockLocation?> RecordSupersession(IReadOnlyList<BlockLocation> superseded)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        if (_openState is null || _accountant is null)
            return Result<BlockLocation?>.Failure(
                "RecordSupersession requires an opened EmailManager (Open, not Create): the accounting " +
                "and Cleanup-block write path is wired only over the composed read-side.");
        if (_closed || _disposed)
            return Result<BlockLocation?>.Failure(
                "RecordSupersession called on a closed or disposed EmailManager.");
        if (BlockManager.IsPoisoned)
            return Result<BlockLocation?>.Failure(
                "RecordSupersession aborted: the block stream is poisoned by an earlier failed write/fsync " +
                "(spec Section 10.3).");
        if (superseded.Count == 0)
            return Result<BlockLocation?>.Success(null);

        for (int i = 0; i < superseded.Count; i++)
        {
            if (superseded[i] is null)
                return Result<BlockLocation?>.Failure($"Superseded block [{i}] must not be null.");
            if (superseded[i].TotalBlockLength <= 0)
                return Result<BlockLocation?>.Failure(
                    $"Superseded block [{i}] has a non-positive on-disk length {superseded[i].TotalBlockLength}.");
        }

        // 1. Append the audit Cleanup block (BlockType 3, always plaintext — recovery reads it
        //    before any key exists, spec Section 9.5) recording the superseded BlockIds + sizes.
        var cleanup = CleanupBlock.FromLocations(LastCheckpointSequence, superseded);
        byte[] payload = CleanupSerializer.Serialize(cleanup);
        var written = BlockManager.Append(BlockType.Cleanup, PayloadEncoding.RawBytes, payload);
        if (written.IsFailure)
            return Result<BlockLocation?>.Failure(
                $"RecordSupersession failed appending the Cleanup block: {written.Error}");

        // 2. Only now, after the audit record is durably appended, move the bytes live→dead.
        for (int i = 0; i < superseded.Count; i++)
            _accountant.RecordSupersession(superseded[i]);

        return Result<BlockLocation?>.Success(written.Value);
    }

    /// <summary>
    /// The session's WAL-buffered PrimaryEmail index (EmailHashedID → ContentBlockId, IndexKind 0)
    /// that <see cref="AddEmail"/> checks for dedupe and buffers each insert into. Null on an
    /// unopened instance.
    /// </summary>
    public WalBufferedIndex? PrimaryIndex => _primaryIndex;

    /// <summary>
    /// The session's Date index (DateTicks ‖ BlockId, IndexKind 2) that <see cref="AddEmail"/> adds
    /// each email's timestamp to for range queries. Null on an unopened instance.
    /// </summary>
    public DateIndex? DateIndex => _dateIndex;

    /// <summary>
    /// The session's FTS address trigram index (IndexKind 3, docs/Search.md Phase 1) that
    /// <see cref="AddEmail"/> folds each email's From/To/Cc trigrams into and <see cref="DeleteEmail"/>
    /// tombstones from; flushed to a segment and registered in the Checkpoint at each commit. Null on an
    /// unopened instance.
    /// </summary>
    public FtsIndex? Fts => _ftsIndex;

    /// <summary>
    /// The session's per-folder Bloom filters (IndexKind 4, docs/Search.md Phase 5) that
    /// <see cref="SearchMailbox"/> consults to skip folders that cannot match. Rebuilt for a folder by
    /// <see cref="RebuildFolderBloomFilter"/> at page-compile time; flushed to a catalog block and
    /// registered in the Checkpoint at each commit. Null on an unopened instance.
    /// </summary>
    public FolderBloomIndex? Bloom => _bloomIndex;

    /// <summary>
    /// The session's WAL writer: <see cref="AddEmail"/> logs each insert as a WAL block fenced to the
    /// current committed Checkpoint (spec Section 10.4) so a committed add survives crash recovery.
    /// Null on an unopened instance.
    /// </summary>
    public WalWriter? Wal => _walWriter;

    /// <summary>
    /// Persists one email end-to-end (story US-EMDB-85), the single-email write pipeline the
    /// group-commit batching task (US-EMDB-85-6) layers a shared Checkpoint over. In order it:
    /// <list type="number">
    ///   <item><b>Hashes + dedupes.</b> Computes the <see cref="EmailHashedID"/> from the raw MIME
    ///   and checks the PrimaryEmail index (read-your-writes, so a buffered insert counts). An
    ///   already-present identity returns <see cref="AddEmailResult.IsDuplicate"/> with no writes —
    ///   content-addressed dedupe never stores the same message twice (spec Sections 5-6).</item>
    ///   <item><b>Writes Tier 3 + Tier 2 blocks.</b> Appends the raw MIME as an <c>EmailContent</c>
    ///   block (BlockType 7) and the metadata payload as an <c>EmailMetadata</c> block (BlockType 10),
    ///   both policy-encrypted for an encrypted file and plaintext otherwise (spec Section 9.5).</item>
    ///   <item><b>Logs a WAL entry.</b> Appends a WAL block with an Insert entry binding the
    ///   EmailHashedID to the ContentBlockId, fenced to the current Checkpoint (spec Section 10.4).</item>
    ///   <item><b>Buffers index inserts.</b> Buffers the PrimaryEmail upsert (EmailHashedID →
    ///   ContentBlockId) and adds the Date index entry (DateTicks ‖ ContentBlockId).</item>
    ///   <item><b>Appends a folder delta.</b> Appends an Add <see cref="ListingRecord"/> to the target
    ///   folder's delta chain and persists the COW-advanced directory (docs/Folder_Listing.md).</item>
    /// </list>
    ///
    /// <para>Every write is a buffered append (spec Section 11.1); durability and the observing
    /// Checkpoint commit come at the writer's next flush boundary (US-EMDB-85-6). A mid-pipeline
    /// failure returns a failure — the partial appends are unreferenced orphans a compaction reclaims,
    /// never committed (spec Sections 10.3, 14).</para>
    /// </summary>
    /// <param name="request">The email to persist: raw MIME, Tier 2 payload, listing fields, and target folder.</param>
    /// <returns>
    /// The identity + produced block/log ids and the advanced folder directory on a fresh insert; a
    /// duplicate result (no writes) when the content is already present; a failure otherwise.
    /// </returns>
    public Result<AddEmailResult> AddEmail(AddEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = AddEmailCore(request, request.Folder);
        if (result.IsFailure)
            return result;

        // Group-commit batching (US-EMDB-85-6): a fresh insert counts toward the auto-commit
        // threshold so a run of AddEmail calls shares one flush + Checkpoint until the threshold
        // is reached — a bulk load produces a BOUNDED number of Checkpoints, not one per email.
        // A duplicate commits nothing.
        if (!result.Value.IsDuplicate && ++_uncommittedAdds >= _autoCommitThreshold)
        {
            var committed = Commit();
            if (committed.IsFailure)
                return Result<AddEmailResult>.Failure(
                    $"AddEmail buffered the email but the auto-commit at the batch threshold failed: {committed.Error}");
        }
        return result;
    }

    /// <summary>
    /// Adds a whole batch of emails under ONE group commit (US-EMDB-85-6, the bulk-add path): every
    /// email is buffered through the same pipeline as <see cref="AddEmail"/> and the batch is made
    /// durable by a single <see cref="Commit"/> at the end (plus any auto-commits the
    /// <see cref="AutoCommitThreshold"/> forces along the way), so N emails share a bounded number of
    /// Checkpoints instead of one each. The target folder is threaded across the batch — the first
    /// request's <see cref="AddEmailRequest.Folder"/> seeds it and each add's COW-advanced directory
    /// carries into the next — so a bulk import into one folder needs no manual directory chaining.
    /// Duplicates are reported in place and store nothing.
    /// </summary>
    /// <param name="requests">The emails to persist, in order; all land in the first request's folder.</param>
    /// <returns>The per-email results in order on success; a failure (partial appends orphaned, uncommitted) otherwise.</returns>
    public Result<IReadOnlyList<AddEmailResult>> AddEmails(IEnumerable<AddEmailRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (_openState is null)
            return Result<IReadOnlyList<AddEmailResult>>.Failure(
                "AddEmails requires an opened EmailManager (Open, not Create): the write pipeline is wired only over the composed read-side.");

        var results = new List<AddEmailResult>();
        FolderPageDirectory? folder = null;
        foreach (var request in requests)
        {
            if (request is null)
                return Result<IReadOnlyList<AddEmailResult>>.Failure("AddEmails: a request in the batch was null.");

            var result = AddEmailCore(request, folder ?? request.Folder);
            if (result.IsFailure)
                return Result<IReadOnlyList<AddEmailResult>>.Failure(result.Error);
            results.Add(result.Value);
            folder = result.Value.Folder; // thread the advanced (or, on a duplicate, unchanged) directory

            // Bound in-memory buffering for a huge batch: auto-commit when the threshold is reached.
            if (!result.Value.IsDuplicate && ++_uncommittedAdds >= _autoCommitThreshold)
            {
                var interim = Commit();
                if (interim.IsFailure)
                    return Result<IReadOnlyList<AddEmailResult>>.Failure(
                        $"AddEmails: an interim group commit failed after {results.Count} email(s): {interim.Error}");
            }
        }

        var committed = Commit();
        if (committed.IsFailure)
            return Result<IReadOnlyList<AddEmailResult>>.Failure(
                $"AddEmails buffered {results.Count} email(s) but the final group commit failed: {committed.Error}");
        return Result<IReadOnlyList<AddEmailResult>>.Success(results);
    }

    /// <summary>
    /// The single-email write pipeline (story US-EMDB-85) shared by <see cref="AddEmail"/> and
    /// <see cref="AddEmails"/>: it buffers one email into <paramref name="folder"/> WITHOUT the
    /// group-commit bookkeeping (the callers own the auto-commit decision). See <see cref="AddEmail"/>
    /// for the step-by-step pipeline.
    /// </summary>
    private Result<AddEmailResult> AddEmailCore(AddEmailRequest request, FolderPageDirectory folder)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_openState is null || _primaryIndex is null || _dateIndex is null
            || _walWriter is null || _folderDeltaStore is null || _folderDirectoryStore is null)
            return Result<AddEmailResult>.Failure(
                "AddEmail requires an opened EmailManager (Open, not Create): the write pipeline is wired only over the composed read-side.");
        if (_closed || _disposed)
            return Result<AddEmailResult>.Failure("AddEmail called on a closed or disposed EmailManager.");
        if (BlockManager.IsPoisoned)
            return Result<AddEmailResult>.Failure(
                "AddEmail aborted: the block stream is poisoned by an earlier failed write/fsync (spec Section 10.3).");
        if (folder is null)
            return Result<AddEmailResult>.Failure("AddEmail requires a target folder directory.");
        if (request.DateTicks < 0)
            return Result<AddEmailResult>.Failure(
                $"AddEmail DateTicks must be non-negative, got {request.DateTicks}.");

        // 1. Hash + dedupe. Read-your-writes: a still-buffered insert of the same content this
        //    session is seen, so a duplicate is caught before any block is written.
        var id = EmailHashedID.ComputeFromRawContent(request.RawContent.Span);
        byte[] idKey = id.GetBytes();
        var existing = _primaryIndex.TryGet(idKey);
        if (existing.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail dedupe lookup failed: {existing.Error}");
        if (existing.Value.Found)
            return Result<AddEmailResult>.Success(new AddEmailResult
            {
                IsDuplicate = true,
                EmailId = id,
                Folder = folder,
            });

        // 2. Tier 3 EmailContent (raw MIME) + Tier 2 EmailMetadata blocks. Both encrypt under both
        //    policies (spec Section 9.5): an encrypted file writes ciphertext via the EncryptedBlockStore,
        //    a plaintext file appends plaintext directly.
        var content = AppendEmailBlock(BlockType.EmailContent, request.RawContent.Span);
        if (content.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail failed writing the Tier 3 EmailContent block: {content.Error}");
        byte[] contentBlockId = content.Value.BlockId;

        var metadata = AppendEmailBlock(BlockType.EmailMetadata, request.MetadataPayload.Span);
        if (metadata.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail failed writing the Tier 2 EmailMetadata block: {metadata.Error}");
        byte[] metadataBlockId = metadata.Value.BlockId;

        // 3. WAL entry: bind the EmailHashedID to the ContentBlockId, fenced to the current Checkpoint.
        var wal = _walWriter.Append(new[] { WalEntry.Insert(idKey, contentBlockId) });
        if (wal.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail failed logging the WAL entry: {wal.Error}");

        // 4. Buffer the PrimaryEmail insert (EmailHashedID → ContentBlockId) and add the Date entry.
        var primary = _primaryIndex.Upsert(idKey, contentBlockId);
        if (primary.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail failed buffering the primary index insert: {primary.Error}");
        var date = _dateIndex.Add(request.DateTicks, contentBlockId);
        if (date.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail failed adding the date index entry: {date.Error}");

        // FTS address trigram index (US-EMDB-91-7): fold this email's From/To/Cc trigrams into the
        // in-memory posting set; it is flushed to a segment and re-registered under IndexKind 3 at the
        // next commit. Rebuildable (docs/Search.md), so a failure here never fails the durable AddEmail.
        _ftsIndex!.AddEmail(id, request.From, request.To, request.Cc);

        // 5. Folder delta append: an Add listing row chained onto the folder's delta head, then the
        //    COW-advanced directory persisted.
        var record = new ListingRecord
        {
            EmailHashedId = id,
            ContentBlockId = contentBlockId,
            DateTicks = request.DateTicks,
            Flags = request.Flags,
            MessageSize = request.RawContent.Length,
            From = request.From,
            Subject = request.Subject,
            Preview = request.Preview,
        };
        var appended = _folderDeltaStore.AppendChained(folder, new[] { FolderDeltaEntry.Add(record) });
        if (appended.IsFailure)
            return Result<AddEmailResult>.Failure($"AddEmail failed appending the folder delta: {appended.Error}");
        var persisted = _folderDirectoryStore.WriteDirectory(appended.Value.Directory);
        if (persisted.IsFailure)
            return Result<AddEmailResult>.Failure(
                $"AddEmail failed persisting the advanced folder directory: {persisted.Error}");

        return Result<AddEmailResult>.Success(new AddEmailResult
        {
            IsDuplicate = false,
            EmailId = id,
            ContentBlockId = contentBlockId,
            MetadataBlockId = metadataBlockId,
            WalSequence = wal.Value.Wal.WalSequence,
            DeltaBlockId = appended.Value.DeltaLocation.BlockId,
            Folder = appended.Value.Directory,
        });
    }

    /// <summary>
    /// Appends a Tier 2/3 email payload block through the policy-driven encryption path: an encrypted
    /// file writes ciphertext (both EmailContent and EmailMetadata encrypt under both policies, spec
    /// Section 9.5), a plaintext file appends plaintext directly.
    /// </summary>
    private Result<BlockLocation> AppendEmailBlock(BlockType type, ReadOnlySpan<byte> payload) =>
        _encryptedStore is not null
            ? _encryptedStore.Append(type, PayloadEncoding.RawBytes, payload)
            : BlockManager.Append(type, PayloadEncoding.RawBytes, payload);

    /// <summary>
    /// Moves one email's folder membership from <see cref="MoveEmailRequest.SourceFolder"/> to
    /// <see cref="MoveEmailRequest.TargetFolder"/> WITHOUT rewriting any Tier 2/3 content block
    /// (story US-EMDB-87, docs/Folder_Listing.md Section 3 "Move/delete"). Folder membership lives
    /// only in the Tier 1 listing structures, so a move is two folder deltas and nothing else:
    /// <list type="number">
    ///   <item>a <see cref="FolderDeltaOp.Delete"/> entry (keyed by the record's EmailHashedID)
    ///   appended to the source folder's delta chain, with the advanced source directory persisted;</item>
    ///   <item>an <see cref="FolderDeltaOp.Add"/> entry (the same <see cref="ListingRecord"/>,
    ///   referencing the SAME ContentBlockId) appended to the target folder's chain, with the advanced
    ///   target directory persisted.</item>
    /// </list>
    /// The email's content/metadata blocks and every index entry are untouched — the email still exists
    /// and still dedupes; only which folders list it changes. Both directories are returned COW-advanced
    /// and already persisted. Durability comes at the next <see cref="Commit"/>/<see cref="Close"/>.
    /// </summary>
    /// <param name="request">The move: source directory, target directory, and the listing row to move.</param>
    /// <returns>The two advanced, persisted directories and the appended delta block ids; a failure otherwise.</returns>
    public Result<MoveEmailResult> MoveEmail(MoveEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var guard = EnsureFolderMutationReady("MoveEmail");
        if (guard is not null)
            return Result<MoveEmailResult>.Failure(guard);
        if (request.SourceFolder is null || request.TargetFolder is null || request.Record is null)
            return Result<MoveEmailResult>.Failure("MoveEmail requires a source folder, a target folder, and the listing record to move.");

        // 1. Delete delta in the source folder (drop the row), then persist the advanced directory.
        var removed = AppendFolderDelta(request.SourceFolder, FolderDeltaEntry.Delete(request.Record.EmailHashedId));
        if (removed.IsFailure)
            return Result<MoveEmailResult>.Failure($"MoveEmail failed removing the row from the source folder: {removed.Error}");

        // 2. Add delta in the target folder (same record, same ContentBlockId), then persist it.
        var added = AppendFolderDelta(request.TargetFolder, FolderDeltaEntry.Add(request.Record));
        if (added.IsFailure)
            return Result<MoveEmailResult>.Failure($"MoveEmail failed adding the row to the target folder: {added.Error}");

        return Result<MoveEmailResult>.Success(new MoveEmailResult
        {
            SourceFolder = removed.Value.Directory,
            TargetFolder = added.Value.Directory,
            SourceDeltaBlockId = removed.Value.DeltaLocation.BlockId,
            TargetDeltaBlockId = added.Value.DeltaLocation.BlockId,
        });
    }

    /// <summary>
    /// Deletes one email completely (story US-EMDB-87): removes its folder listing row, its index
    /// entries, and accounts its on-disk bytes dead (docs/Folder_Listing.md Section 3, docs/Compaction.md
    /// Section 3). In order it:
    /// <list type="number">
    ///   <item><b>Resolves + removes the identity.</b> Looks the <see cref="DeleteEmailRequest.EmailId"/>
    ///   up in the PrimaryEmail index; an unknown identity is a clean <see cref="DeleteEmailResult.WasPresent"/>
    ///   == false no-op (no writes). Otherwise it captures the ContentBlockId and the Tier 3 + Tier 2 block
    ///   locations for the dead-byte accounting.</item>
    ///   <item><b>Accounts the bytes dead.</b> <see cref="RecordSupersession"/> appends a Cleanup block and
    ///   moves the content + metadata blocks' bytes live → dead — the retired-byte half of delete.</item>
    ///   <item><b>Removes the index entries.</b> Logs a WAL delete fenced to the current Checkpoint, buffers
    ///   the PrimaryEmail delete (read-your-writes: the email now reads as not-found), and removes the Date
    ///   index's <c>DateTicks ‖ ContentBlockId</c> entry.</item>
    ///   <item><b>Removes the folder membership.</b> Appends a <see cref="FolderDeltaOp.Delete"/> delta and
    ///   persists the advanced directory, so the row disappears from the next listing read.</item>
    /// </list>
    /// The physical content bytes are not rewritten in place — they are marked dead and reclaimed by a later
    /// compaction (spec Section 14). Durability of the index/folder state comes at the next
    /// <see cref="Commit"/>/<see cref="Close"/>; the Cleanup block and accounting are effective immediately.
    /// </summary>
    /// <param name="request">The delete: the folder to drop the row from, the identity, and the email's timestamp.</param>
    /// <returns>The advanced directory, the dead-byte total, and whether the identity was present; a failure otherwise.</returns>
    public Result<DeleteEmailResult> DeleteEmail(DeleteEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var guard = EnsureFolderMutationReady("DeleteEmail");
        if (guard is not null)
            return Result<DeleteEmailResult>.Failure(guard);
        if (request.Folder is null)
            return Result<DeleteEmailResult>.Failure("DeleteEmail requires a target folder directory.");
        if (request.DateTicks < 0)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail DateTicks must be non-negative, got {request.DateTicks}.");

        byte[] idKey = request.EmailId.GetBytes();

        // 1. Resolve the identity → ContentBlockId and the retired Tier 3 + Tier 2 block locations.
        //    An unknown identity is a clean no-op: nothing to delete, no folder delta appended.
        var lookup = _primaryIndex!.TryGet(idKey);
        if (lookup.IsFailure)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail dedupe lookup failed: {lookup.Error}");
        if (!lookup.Value.Found)
            return Result<DeleteEmailResult>.Success(new DeleteEmailResult
            {
                WasPresent = false,
                Folder = request.Folder,
            });

        byte[] contentBlockId = lookup.Value.Value!;
        var retired = ResolveRetiredEmailBlocks(contentBlockId);
        if (retired.IsFailure)
            return Result<DeleteEmailResult>.Failure(retired.Error);

        // 2. Account the retired bytes dead (appends the audit Cleanup block, advances the accountant).
        var supersession = RecordSupersession(retired.Value);
        if (supersession.IsFailure)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail failed accounting the dead bytes: {supersession.Error}");
        long deadBytes = 0;
        foreach (var block in retired.Value)
            deadBytes += block.TotalBlockLength;

        // 3. Remove the index entries: WAL-log the delete (fenced to the current Checkpoint), buffer the
        //    PrimaryEmail delete, and remove the Date index (DateTicks ‖ ContentBlockId) composite key.
        var wal = _walWriter!.Append(new[] { WalEntry.Delete(idKey) });
        if (wal.IsFailure)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail failed logging the WAL delete: {wal.Error}");
        var primaryDelete = _primaryIndex.Delete(idKey);
        if (primaryDelete.IsFailure)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail failed buffering the primary index delete: {primaryDelete.Error}");
        var dateRemove = _dateIndex!.Remove(request.DateTicks, contentBlockId);
        if (dateRemove.IsFailure)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail failed removing the date index entry: {dateRemove.Error}");

        // FTS address trigram index (US-EMDB-91-7): tombstone the identity so it is masked from address
        // search immediately and dropped from the segment at the next commit's flush.
        _ftsIndex!.RemoveEmail(request.EmailId);

        // 4. Remove the folder membership: a Delete delta, then persist the advanced directory.
        var removed = AppendFolderDelta(request.Folder, FolderDeltaEntry.Delete(request.EmailId));
        if (removed.IsFailure)
            return Result<DeleteEmailResult>.Failure($"DeleteEmail failed removing the folder row: {removed.Error}");

        return Result<DeleteEmailResult>.Success(new DeleteEmailResult
        {
            WasPresent = true,
            Folder = removed.Value.Directory,
            DeltaBlockId = removed.Value.DeltaLocation.BlockId,
            DateEntryRemoved = dateRemove.Value,
            DeadBytes = deadBytes,
        });
    }

    /// <summary>
    /// Changes one email's listing flags (read/flagged/answered/draft) in a folder (story US-EMDB-87,
    /// docs/Folder_Listing.md Section 3). Like move and delete this is a pure Tier 1 delta — a
    /// <see cref="FolderDeltaOp.FlagChange"/> entry appended to the folder's delta chain, with the
    /// advanced directory persisted; no page or content block is rewritten. The new flags become visible
    /// the next time a listing read merges the pending delta chain over the compiled pages.
    /// </summary>
    /// <param name="folder">The folder whose listing row's flags change.</param>
    /// <param name="emailId">The identity of the row to re-flag.</param>
    /// <param name="flags">The new listing flags to set on the row.</param>
    /// <returns>The advanced, persisted directory and the appended delta block id; a failure otherwise.</returns>
    public Result<ChangeFlagsResult> ChangeFlags(FolderPageDirectory folder, EmailHashedID emailId, ListingFlags flags)
    {
        var guard = EnsureFolderMutationReady("ChangeFlags");
        if (guard is not null)
            return Result<ChangeFlagsResult>.Failure(guard);
        if (folder is null)
            return Result<ChangeFlagsResult>.Failure("ChangeFlags requires a target folder directory.");

        var changed = AppendFolderDelta(folder, FolderDeltaEntry.FlagChange(emailId, flags));
        if (changed.IsFailure)
            return Result<ChangeFlagsResult>.Failure($"ChangeFlags failed appending the flag-change delta: {changed.Error}");

        return Result<ChangeFlagsResult>.Success(new ChangeFlagsResult
        {
            Folder = changed.Value.Directory,
            DeltaBlockId = changed.Value.DeltaLocation.BlockId,
        });
    }

    /// <summary>
    /// Lists a folder's effective rows as a date-descending page slice (story US-EMDB-87,
    /// docs/Folder_Listing.md Section 3, "List a page"). It runs the documented read path:
    /// <list type="number">
    ///   <item><b>Resolve the directory.</b> <paramref name="folderId"/> is the folder's stable
    ///   <see cref="FolderPageDirectory"/> BlockId, so the composite resolver (runtime map → location
    ///   index, spec Section 7) places its latest version; the directory block (BlockType 11) is read
    ///   and decrypted.</item>
    ///   <item><b>Read the compiled pages.</b> Each <see cref="PageEntry.PageBlockId"/> resolves to a
    ///   <see cref="FolderPage"/> (BlockType 12), read newest-first.</item>
    ///   <item><b>Walk the pending delta chain.</b> The <see cref="FolderPageDirectory.HeadDeltaBlockId"/>
    ///   is followed head-to-root via <see cref="FolderDeltaLog.PreviousDeltaBlockId"/>.</item>
    ///   <item><b>Merge + slice.</b> <see cref="FolderListingMerger"/> overlays the pending Adds/Deletes/
    ///   FlagChanges on the page rows, producing the effective listing in canonical date-descending order;
    ///   the window <c>[pageOffset, pageOffset + pageSize)</c> is returned.</item>
    /// </list>
    /// The merge is over the folder's full record set (pending Adds are folder-global, not page-local), so
    /// the returned offset is stable: any two reads of an unchanged folder at the same offset return the
    /// same rows. <paramref name="pageOffset"/> past the end is a clean empty slice, never a failure.
    /// </summary>
    /// <param name="folderId">The folder's 16-byte ULID (its directory block's stable BlockId).</param>
    /// <param name="pageOffset">Zero-based record offset into the effective listing to start the slice at.</param>
    /// <param name="pageSize">Maximum rows to return (defaults to <see cref="FolderPage.TargetRecordsPerPage"/>).</param>
    /// <returns>The date-descending slice with its offset/total/version; a failure on a bad argument or a read fault.</returns>
    public Result<FolderListingPage> ListFolder(
        byte[] folderId, int pageOffset, int pageSize = FolderPage.TargetRecordsPerPage)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        var guard = EnsureFolderReadReady("ListFolder");
        if (guard is not null)
            return Result<FolderListingPage>.Failure(guard);
        if (pageOffset < 0)
            return Result<FolderListingPage>.Failure($"ListFolder pageOffset must be non-negative, got {pageOffset}.");
        if (pageSize <= 0)
            return Result<FolderListingPage>.Failure($"ListFolder pageSize must be positive, got {pageSize}.");

        var listing = LoadEffectiveListing(folderId, out ulong folderVersion);
        if (listing.IsFailure)
            return Result<FolderListingPage>.Failure(listing.Error);

        return Result<FolderListingPage>.Success(
            FolderListingPage.Slice(listing.Value, pageOffset, pageSize, folderVersion));
    }

    /// <summary>
    /// The date-jump variant of <see cref="ListFolder"/> (docs/Folder_Listing.md Section 3, "Jumping to a
    /// date"): instead of a numeric offset, it seeks to <paramref name="dateTicks"/> and returns the slice
    /// starting at the newest row whose date is at or before it — i.e. the first row a browser scrolled to
    /// that date would show. A target newer than every row starts at the top (offset 0); a target older
    /// than every row lands past the end (an empty slice), mirroring the directory's page-level date clamp
    /// (<see cref="FolderPageDirectory.FindPageByDate"/>) at record granularity over the merged listing.
    /// </summary>
    /// <param name="folderId">The folder's 16-byte ULID (its directory block's stable BlockId).</param>
    /// <param name="dateTicks">The UTC-ticks date to seek to; the slice begins at the first row at or before it.</param>
    /// <param name="pageSize">Maximum rows to return (defaults to <see cref="FolderPage.TargetRecordsPerPage"/>).</param>
    /// <returns>The date-descending slice from the seeked position; a failure on a bad argument or a read fault.</returns>
    public Result<FolderListingPage> ListFolderFromDate(
        byte[] folderId, long dateTicks, int pageSize = FolderPage.TargetRecordsPerPage)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        var guard = EnsureFolderReadReady("ListFolderFromDate");
        if (guard is not null)
            return Result<FolderListingPage>.Failure(guard);
        if (pageSize <= 0)
            return Result<FolderListingPage>.Failure($"ListFolderFromDate pageSize must be positive, got {pageSize}.");

        var listing = LoadEffectiveListing(folderId, out ulong folderVersion);
        if (listing.IsFailure)
            return Result<FolderListingPage>.Failure(listing.Error);

        int start = SeekToDate(listing.Value, dateTicks);
        return Result<FolderListingPage>.Success(
            FolderListingPage.Slice(listing.Value, start, pageSize, folderVersion));
    }

    /// <summary>
    /// Folder-scoped keyword scan over the Tier 1 Subject/From/Preview fields (docs/Search.md Phase 2,
    /// story US-EMDB-92) — the zero-index search baseline and the accuracy backstop other phases confirm
    /// candidates against. It loads the folder's <b>effective</b> listing (the same read path
    /// <see cref="ListFolder"/> runs: compiled pages with the pending <see cref="FolderDeltaLog"/> chain
    /// merged over them) and returns the rows whose selected fields contain <paramref name="query"/>
    /// case-insensitively, newest-first. Because the scanned listing is the merged effective listing,
    /// matches naturally <b>include pending delta rows</b> that no page has compiled yet.
    /// </summary>
    /// <param name="folderId">The folder's 16-byte ULID (its directory block's stable BlockId).</param>
    /// <param name="query">The keyword to match; a non-empty substring searched case-insensitively.</param>
    /// <param name="fields">Which Tier 1 fields to match (defaults to Subject, From, and Preview).</param>
    /// <param name="maxResults">Cap on the number of hits returned, newest-first (defaults to unbounded).</param>
    /// <returns>The date-descending matches with the scan count; a failure on a bad argument or read fault.</returns>
    public Result<ListingScanResult> SearchFolder(
        byte[] folderId, string query,
        ListingSearchField fields = ListingSearchField.All, int maxResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        var guard = EnsureFolderReadReady("SearchFolder");
        if (guard is not null)
            return Result<ListingScanResult>.Failure(guard);
        var argError = ValidateScanArgs(query, maxResults);
        if (argError is not null)
            return Result<ListingScanResult>.Failure($"SearchFolder {argError}");

        var scanned = ScanFolder(folderId, query, fields, maxResults);
        if (scanned.IsFailure)
            return Result<ListingScanResult>.Failure(scanned.Error);

        return Result<ListingScanResult>.Success(new ListingScanResult
        {
            Hits = scanned.Value.Hits,
            RecordsScanned = scanned.Value.Scanned,
            WholeMailbox = false,
        });
    }

    /// <summary>
    /// Whole-mailbox keyword scan (docs/Search.md Phase 2, story US-EMDB-92): the fallback that runs when
    /// no better phase applies, scanning every folder in <paramref name="folderIds"/> with the same
    /// effective-listing match as <see cref="SearchFolder"/> and merging the hits into one canonical
    /// date-descending result (newest-first, ties broken by <see cref="EmailHashedID"/>). Each hit keeps
    /// the folder it was found in, so a message listed in more than one folder yields a hit per folder.
    /// Pending delta rows are included per folder exactly as in the folder-scoped scan.
    /// </summary>
    /// <param name="folderIds">The folders to scan; each a 16-byte ULID. An empty set is a clean empty result.</param>
    /// <param name="query">The keyword to match; a non-empty substring searched case-insensitively.</param>
    /// <param name="fields">Which Tier 1 fields to match (defaults to Subject, From, and Preview).</param>
    /// <param name="maxResults">Cap on the number of hits returned, newest-first (defaults to unbounded).</param>
    /// <returns>The merged date-descending matches with the total scan count; a failure on a bad argument or read fault.</returns>
    public Result<ListingScanResult> SearchMailbox(
        IEnumerable<byte[]> folderIds, string query,
        ListingSearchField fields = ListingSearchField.All, int maxResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(folderIds);
        var guard = EnsureFolderReadReady("SearchMailbox");
        if (guard is not null)
            return Result<ListingScanResult>.Failure(guard);
        var argError = ValidateScanArgs(query, maxResults);
        if (argError is not null)
            return Result<ListingScanResult>.Failure($"SearchMailbox {argError}");

        var hits = new List<ListingScanHit>();
        int scannedTotal = 0;
        int foldersSkipped = 0;
        foreach (var folderId in folderIds)
        {
            if (folderId is null)
                return Result<ListingScanResult>.Failure("SearchMailbox: a folder id in the set was null.");

            // Phase 5 (docs/Search.md): consult the folder's Bloom filter first. A covering filter (built
            // over this folder's compiled pages, matching the live directory version with no pending delta)
            // that eliminates the query proves no record here can match, so the folder is skipped without a
            // page scan. Bloom filters have no false negatives, so a skip is always safe; a folder with
            // pending deltas or a stale/absent filter is never skipped (it falls through to a full scan).
            if (_bloomIndex is not null)
            {
                var directory = TryReadFolderDirectory(folderId);
                if (directory is not null
                    && !_bloomIndex.MightMatch(folderId, query, directory.FolderVersion, directory.HasPendingDelta))
                {
                    foldersSkipped++;
                    continue;
                }
            }

            // Gather every folder's matches unbounded, then apply the mailbox-wide cap after the global
            // date-descending merge — a per-folder cap would drop newer hits from later folders.
            var scanned = ScanFolder(folderId, query, fields, int.MaxValue);
            if (scanned.IsFailure)
                return Result<ListingScanResult>.Failure(scanned.Error);
            hits.AddRange(scanned.Value.Hits);
            scannedTotal += scanned.Value.Scanned;
        }

        // Merge across folders into one canonical newest-first order (date desc, tie-break by id), then cap.
        hits.Sort(static (a, b) =>
        {
            int byDate = b.Record.DateTicks.CompareTo(a.Record.DateTicks); // newest first
            return byDate != 0 ? byDate : a.Record.EmailHashedId.CompareTo(b.Record.EmailHashedId);
        });
        if (hits.Count > maxResults)
            hits.RemoveRange(maxResults, hits.Count - maxResults);

        return Result<ListingScanResult>.Success(new ListingScanResult
        {
            Hits = hits,
            RecordsScanned = scannedTotal,
            WholeMailbox = true,
            FoldersSkipped = foldersSkipped,
        });
    }

    /// <summary>
    /// Rebuilds folder <paramref name="folderId"/>'s Bloom filter (docs/Search.md Phase 5, story
    /// US-EMDB-97) from its <b>compiled pages</b> and stamps it with the folder's current
    /// <see cref="FolderPageDirectory.FolderVersion"/> — the "filters rebuilt with page compile" hook: a
    /// caller runs it right after compiling the folder's pending deltas into pages
    /// (<see cref="FolderCompiler.Compile"/>), when the folder has no pending delta, so the filter covers
    /// the folder's whole effective listing. The rebuilt filter is held in the session index and made
    /// durable (catalog block + IndexKind-4 registration) by the next <see cref="Commit"/>/<see cref="Close"/>.
    ///
    /// <para>Building over the compiled pages ONLY — never the pending delta chain — is the delta-safety
    /// contract: a folder with pending deltas is never skipped by <see cref="SearchMailbox"/> (the filter's
    /// covered version will not match the live directory), so the deltas are always scanned. Rebuilding a
    /// folder that still has pending deltas simply produces a filter that will not be consulted until the
    /// folder is compiled and rebuilt at the compiled version.</para>
    /// </summary>
    /// <param name="folderId">The folder's 16-byte ULID (its directory block's stable BlockId).</param>
    /// <returns>Success once the session filter is rebuilt; a failure on a bad argument or read fault.</returns>
    public Result RebuildFolderBloomFilter(byte[] folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        var guard = EnsureFolderReadReady("RebuildFolderBloomFilter");
        if (guard is not null)
            return Result.Failure(guard);
        if (folderId.Length != UlidGenerator.UlidSize)
            return Result.Failure(
                $"RebuildFolderBloomFilter folderId must be exactly {UlidGenerator.UlidSize} bytes, got {folderId.Length}.");

        if (!_openState!.Resolver.TryGetLocation(folderId, out var dirLoc) || dirLoc is null)
            return Result.Failure(
                "RebuildFolderBloomFilter failed: the folder id does not resolve to a FolderPageDirectory block " +
                "(the folder was never written this session or committed, spec Section 7).");
        var directory = _folderDirectoryStore!.ReadDirectory(dirLoc.Offset);
        if (directory.IsFailure)
            return Result.Failure($"RebuildFolderBloomFilter failed reading the folder directory: {directory.Error}");

        var pageRecords = LoadCompiledPageRecords(directory.Value);
        if (pageRecords.IsFailure)
            return Result.Failure(pageRecords.Error);

        _bloomIndex!.RebuildFolder(folderId, directory.Value.FolderVersion, pageRecords.Value);
        return Result.Success();
    }

    /// <summary>
    /// Instant substring search over From/To/Cc addresses via the Phase 1 trigram FTS index
    /// (docs/Search.md Phase 1, story US-EMDB-91) — the address counterpart to the Tier 1 keyword scans
    /// <see cref="SearchFolder"/>/<see cref="SearchMailbox"/>. It runs the documented query path:
    /// <list type="number">
    ///   <item><b>Split the query into trigrams.</b> The query is normalized (NFC + casefold) and
    ///   windowed into trigrams with the SAME <see cref="TrigramExtractor"/> ingest used, so a query
    ///   trigram matches an indexed trigram exactly when the characters match.</item>
    ///   <item><b>Intersect posting lists.</b> <see cref="FtsIndex.FindCandidates"/> returns the emails
    ///   whose addresses hold EVERY query trigram within a single requested field (the AND semantics),
    ///   restricted to <paramref name="fields"/>. Trigram co-occurrence is necessary but not sufficient.</item>
    ///   <item><b>Verify against Tier 1.</b> Each candidate's Tier 1 listing row (loaded from
    ///   <paramref name="folderIds"/>, the same effective-listing read path <see cref="SearchFolder"/>
    ///   uses) confirms the match: a From candidate is kept only when the row's actual
    ///   <see cref="ListingRecord.From"/> address contains the query substring, dropping trigram false
    ///   positives. To/Cc address text is not persisted at Tier 1, so a To/Cc candidate is kept at the
    ///   trigram level (best-effort) — a documented limitation of the current schema.</item>
    ///   <item><b>Rank.</b> Verified hits are returned newest-first (date descending, ties broken by
    ///   <see cref="EmailHashedID"/> ascending — the house order), then capped at
    ///   <paramref name="maxResults"/>.</item>
    /// </list>
    ///
    /// <para><b>Short queries.</b> A query shorter than a trigram (fewer than three runes after
    /// normalization) has nothing to intersect, so it falls back to a Tier 1 substring scan of the
    /// searched folders' From addresses (<see cref="AddressSearchResult.UsedTrigramIndex"/> is false).
    /// Only From is persisted at Tier 1, so the fallback matches the From address regardless of the
    /// requested To/Cc bits.</para>
    ///
    /// <para>Because candidates are verified against the folders' <b>effective</b> listings (compiled
    /// pages with the pending delta chain merged), a deleted email — masked in both the FTS index and
    /// the folder listing — never surfaces, and an email not listed in any searched folder is skipped
    /// (its Tier 1 row, and hence its date and From address, are not reachable).</para>
    /// </summary>
    /// <param name="folderIds">The folders whose Tier 1 listings verify candidates; each a 16-byte ULID. Empty verifies nothing.</param>
    /// <param name="query">The address substring to search; a non-empty string.</param>
    /// <param name="fields">Which address fields to search (defaults to <see cref="AddressField.All"/>).</param>
    /// <param name="maxResults">Cap on the number of hits returned, newest-first (defaults to unbounded).</param>
    /// <returns>The date-descending verified matches; a failure on a bad argument, unopened manager, or read fault.</returns>
    public Result<AddressSearchResult> SearchAddresses(
        IEnumerable<byte[]> folderIds, string query,
        AddressField fields = AddressField.All, int maxResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(folderIds);
        var guard = EnsureFolderReadReady("SearchAddresses");
        if (guard is not null)
            return Result<AddressSearchResult>.Failure(guard);
        if (_ftsIndex is null)
            return Result<AddressSearchResult>.Failure(
                "SearchAddresses requires an opened EmailManager (Open, not Create): the FTS index is wired only over the composed read-side.");
        if (string.IsNullOrEmpty(query))
            return Result<AddressSearchResult>.Failure("SearchAddresses requires a non-empty query.");
        if (maxResults <= 0)
            return Result<AddressSearchResult>.Failure($"SearchAddresses maxResults must be positive, got {maxResults}.");
        var mask = fields & AddressField.All;
        if (mask == AddressField.None)
            return Result<AddressSearchResult>.Failure("SearchAddresses requires at least one address field (From/To/Cc).");

        // 1. Load Tier 1: the effective listing rows of every searched folder, keyed by email. The first
        //    folder an email appears in tags its hit; its From/DateTicks are identical across folders.
        var records = new Dictionary<EmailHashedID, (ListingRecord Record, byte[] FolderId)>();
        foreach (var folderId in folderIds)
        {
            if (folderId is null)
                return Result<AddressSearchResult>.Failure("SearchAddresses: a folder id in the set was null.");
            var listing = LoadEffectiveListing(folderId, out _);
            if (listing.IsFailure)
                return Result<AddressSearchResult>.Failure(listing.Error);
            foreach (var rec in listing.Value)
                records.TryAdd(rec.EmailHashedId, (rec, folderId));
        }

        string normalizedQuery = TrigramExtractor.Normalize(query);
        var trigrams = TrigramExtractor.Extract(query);

        var hits = new List<AddressSearchHit>();
        bool usedIndex;
        int examined;

        if (trigrams.Count == 0)
        {
            // Short query (fewer than three runes after normalization): the trigram index has nothing to
            // intersect. Fall back to a Tier 1 From-address substring scan (To/Cc text is not persisted).
            usedIndex = false;
            examined = records.Count;
            foreach (var (email, entry) in records)
            {
                if ((mask & AddressField.From) != 0
                    && TrigramExtractor.Normalize(entry.Record.From).Contains(normalizedQuery, StringComparison.Ordinal))
                {
                    hits.Add(new AddressSearchHit
                    {
                        EmailId = email,
                        FolderId = entry.FolderId,
                        Record = entry.Record,
                        MatchedFields = AddressField.From,
                    });
                }
            }
        }
        else
        {
            // Trigram-index path: intersect posting lists (field-scoped AND), then verify each candidate.
            usedIndex = true;
            var candidates = _ftsIndex.FindCandidates(trigrams, mask);
            examined = candidates.Count;
            foreach (var cand in candidates)
            {
                // An email not listed in the searched folders has no reachable Tier 1 row to verify or rank.
                if (!records.TryGetValue(cand.Email, out var entry))
                    continue;

                AddressField matched = AddressField.None;
                // From: verify the actual Tier 1 address contains the query substring — drops trigram
                // false positives (all trigrams present, but not as a contiguous substring).
                if ((cand.Fields & AddressField.From) != 0
                    && TrigramExtractor.Normalize(entry.Record.From).Contains(normalizedQuery, StringComparison.Ordinal))
                    matched |= AddressField.From;
                // To/Cc: not persisted at Tier 1, so accept the trigram co-occurrence as-is (best-effort).
                matched |= cand.Fields & (AddressField.To | AddressField.Cc);

                if (matched != AddressField.None)
                    hits.Add(new AddressSearchHit
                    {
                        EmailId = cand.Email,
                        FolderId = entry.FolderId,
                        Record = entry.Record,
                        MatchedFields = matched,
                    });
            }
        }

        // 2. Rank: date descending, ties broken by EmailHashedID ascending (the house order), then cap.
        hits.Sort(static (a, b) =>
        {
            int byDate = b.Record.DateTicks.CompareTo(a.Record.DateTicks); // newest first
            return byDate != 0 ? byDate : a.EmailId.CompareTo(b.EmailId);
        });
        if (hits.Count > maxResults)
            hits.RemoveRange(maxResults, hits.Count - maxResults);

        return Result<AddressSearchResult>.Success(new AddressSearchResult
        {
            Hits = hits,
            UsedTrigramIndex = usedIndex,
            CandidatesExamined = examined,
        });
    }

    /// <summary>
    /// The one query-planner entry point (story US-EMDB-94, docs/Search.md "Query planning"): it classifies a
    /// <see cref="SearchQuery"/> by shape, routes it across the search phases, and merges + dedupes the results
    /// into one ranked list of <see cref="SearchHit"/>s. The routing follows the docs matrix:
    /// <list type="bullet">
    ///   <item><b>Address-shaped</b> (an <see cref="SearchQuery.AddressFields"/> hint, or a <see cref="SearchQuery.Text"/>
    ///   holding an <c>@</c>) → Phase 1, the trigram FTS index (<see cref="SearchAddresses"/>). If the FTS index is
    ///   absent (no committed root and nothing indexed this session) the plan <b>degrades to a Tier 1 From scan</b>
    ///   rather than erroring.</item>
    ///   <item><b>Keyword / free text</b> → Phase 2, the listing scan (<see cref="SearchFolder"/> for a single-folder
    ///   scope, <see cref="SearchMailbox"/> — Bloom-gated — for a multi-folder scope).</item>
    ///   <item><b>Date-bounded</b> (<see cref="SearchQuery.FromTicks"/>/<see cref="SearchQuery.ToTicks"/>) → Phase 3,
    ///   the Date B+-tree pre-filter: the in-range <c>ContentBlockId</c>s narrow whichever text phase ran. A
    ///   date-only query (no text) lists the scoped folders filtered to the range. When the Date index is absent the
    ///   plan <b>degrades to an in-memory <see cref="ListingRecord.DateTicks"/> filter</b> — the bound still holds.</item>
    /// </list>
    ///
    /// <para>Results from the executed phase(s) are deduped by <see cref="EmailHashedID"/> (a message listed in
    /// several scoped folders becomes one hit, attributed to the lexicographically smallest folder ULID) and ranked
    /// in the house canonical order: date descending, ties broken by id ascending, then capped at
    /// <see cref="SearchQuery.MaxResults"/>. The executed phases and the index-vs-degraded decisions are reported on
    /// the <see cref="SearchResult"/> so the routing is observable.</para>
    /// </summary>
    /// <param name="query">The query spec: text, optional address-field hint, optional date range, and folder scope.</param>
    /// <returns>The ranked, deduped hits with the plan diagnostics; a failure on a bad argument, unopened manager, or read fault.</returns>
    public Result<SearchResult> Search(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var guard = EnsureFolderReadReady("Search");
        if (guard is not null)
            return Result<SearchResult>.Failure(guard);
        if (query.Folders is null)
            return Result<SearchResult>.Failure("Search requires a folder scope (Folders must not be null).");
        if (query.MaxResults <= 0)
            return Result<SearchResult>.Failure($"Search maxResults must be positive, got {query.MaxResults}.");

        var folderIds = new List<byte[]>();
        foreach (var folderId in query.Folders)
        {
            if (folderId is null)
                return Result<SearchResult>.Failure("Search: a folder id in the scope was null.");
            folderIds.Add(folderId);
        }

        string text = query.Text ?? string.Empty;
        bool hasText = text.Length > 0;
        bool hasDate = query.FromTicks.HasValue || query.ToTicks.HasValue;
        if (!hasText && !hasDate)
            return Result<SearchResult>.Failure("Search requires query text or a date range.");

        var addressMask = (query.AddressFields ?? AddressField.None) & AddressField.All;
        bool addressShaped = addressMask != AddressField.None || (hasText && text.Contains('@'));

        // 1. Date pre-filter (Phase 3). Query the Date index for the in-range ContentBlockId set; if the index
        //    is absent (never populated this session), degrade to an in-memory DateTicks filter — the bound
        //    still holds, the plan just does not error (graceful degradation).
        long fromTicks = query.FromTicks ?? 0;
        long toTicks = query.ToTicks ?? long.MaxValue;
        HashSet<string>? dateSet = null;
        bool usedDateIndex = false;
        bool dateDegraded = false;
        if (hasDate)
        {
            if (fromTicks < 0 || toTicks < 0)
                return Result<SearchResult>.Failure(
                    $"Search date bounds must be non-negative, got [{fromTicks}, {toTicks}].");
            if (fromTicks > toTicks)
                return Result<SearchResult>.Failure(
                    $"Search date range is inverted: fromTicks ({fromTicks}) must not exceed toTicks ({toTicks}).");

            if (_dateIndex is not null && _dateIndex.Root is not null)
            {
                var ranged = _dateIndex.RangeQuery(fromTicks, toTicks);
                if (ranged.IsFailure)
                    return Result<SearchResult>.Failure($"Search date pre-filter failed: {ranged.Error}");
                dateSet = new HashSet<string>(ranged.Value.Count);
                foreach (var entry in ranged.Value)
                    dateSet.Add(Convert.ToHexStringLower(entry.BlockId));
                usedDateIndex = true;
            }
            else
            {
                dateDegraded = true;
            }
        }

        // 2. Text/candidate phase. Address-shaped → FTS (Phase 1), degrading to a From scan when FTS is absent;
        //    other text → listing scan (Phase 2); no text (date-only) → the scoped folders' effective listings.
        var raw = new List<SearchHit>();
        SearchPhase executed = SearchPhase.None;
        bool usedTrigram = false;
        int candidatesExamined = 0;
        int recordsScanned = 0;
        int foldersSkipped = 0;

        bool ftsAvailable = _ftsIndex is not null
            && (_ftsIndex.IndexedEmailCount > 0 || _ftsIndex.CommittedSearchRootLocation is not null);

        if (hasText && addressShaped && ftsAvailable)
        {
            var addr = SearchAddresses(folderIds, text, addressMask == AddressField.None ? AddressField.All : addressMask);
            if (addr.IsFailure)
                return Result<SearchResult>.Failure(addr.Error);
            executed |= SearchPhase.Fts;
            usedTrigram = addr.Value.UsedTrigramIndex;
            candidatesExamined = addr.Value.CandidatesExamined;
            foreach (var hit in addr.Value.Hits)
                raw.Add(new SearchHit
                {
                    EmailId = hit.EmailId,
                    FolderId = hit.FolderId,
                    Record = hit.Record,
                    MatchedPhase = SearchPhase.Fts,
                });
        }
        else if (hasText)
        {
            // Keyword / free text, OR an address-shaped query degrading to scan because the FTS index is absent.
            // The degraded address scan restricts to From (address text lives only in From at Tier 1); a plain
            // keyword scan uses the requested ScanFields.
            var scanFields = addressShaped ? ListingSearchField.From : query.ScanFields;
            var scan = folderIds.Count == 1
                ? SearchFolder(folderIds[0], text, scanFields)
                : SearchMailbox(folderIds, text, scanFields);
            if (scan.IsFailure)
                return Result<SearchResult>.Failure(scan.Error);
            executed |= SearchPhase.Scan;
            recordsScanned = scan.Value.RecordsScanned;
            foldersSkipped = scan.Value.FoldersSkipped;
            foreach (var hit in scan.Value.Hits)
                raw.Add(new SearchHit
                {
                    EmailId = hit.Record.EmailHashedId,
                    FolderId = hit.FolderId,
                    Record = hit.Record,
                    MatchedPhase = SearchPhase.Scan,
                });
        }
        else
        {
            // Date-only query: no text phase — the scoped folders' effective listings are the candidate set,
            // which the date pre-filter below narrows to the range.
            foreach (var folderId in folderIds)
            {
                var listing = LoadEffectiveListing(folderId, out _);
                if (listing.IsFailure)
                    return Result<SearchResult>.Failure(listing.Error);
                foreach (var record in listing.Value)
                    raw.Add(new SearchHit
                    {
                        EmailId = record.EmailHashedId,
                        FolderId = folderId,
                        Record = record,
                        MatchedPhase = SearchPhase.DateIndex,
                    });
            }
        }

        // 3. Apply the date bound. Either intersect with the Date index's in-range set (pre-filter) or, when the
        //    index was absent, filter the candidate rows by their own DateTicks (the degraded path).
        if (hasDate)
        {
            executed |= SearchPhase.DateIndex;
            raw = raw.Where(h => usedDateIndex
                    ? dateSet!.Contains(Convert.ToHexStringLower(h.Record.ContentBlockId))
                    : h.Record.DateTicks >= fromTicks && h.Record.DateTicks <= toTicks)
                .ToList();
        }

        // 4. Merge + dedupe by EmailHashedID (a message in several scoped folders is one hit), attributing the
        //    hit to the lexicographically smallest folder ULID for a deterministic result.
        var deduped = new Dictionary<EmailHashedID, SearchHit>();
        foreach (var hit in raw)
        {
            if (!deduped.TryGetValue(hit.EmailId, out var kept)
                || CompareUnsigned(hit.FolderId, kept.FolderId) < 0)
                deduped[hit.EmailId] = hit;
        }

        // 5. Rank: date descending, ties broken by EmailHashedID ascending (the house order), then cap.
        var hits = deduped.Values.ToList();
        hits.Sort(static (a, b) =>
        {
            int byDate = b.Record.DateTicks.CompareTo(a.Record.DateTicks); // newest first
            return byDate != 0 ? byDate : a.EmailId.CompareTo(b.EmailId);
        });
        if (hits.Count > query.MaxResults)
            hits.RemoveRange(query.MaxResults, hits.Count - query.MaxResults);

        return Result<SearchResult>.Success(new SearchResult
        {
            Hits = hits,
            PhasesExecuted = executed,
            UsedTrigramIndex = usedTrigram,
            UsedDateIndex = usedDateIndex,
            DateFilterDegraded = dateDegraded,
            CandidatesExamined = candidatesExamined,
            RecordsScanned = recordsScanned,
            FoldersSkipped = foldersSkipped,
        });
    }

    /// <summary>Unsigned lexicographic comparison of two equal-or-unequal-length byte arrays (folder-id tie-break).</summary>
    private static int CompareUnsigned(byte[] a, byte[] b)
    {
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// Scans one folder's effective listing for <paramref name="query"/> over the selected fields, returning
    /// the matching rows (already date-descending — the listing's canonical order is preserved by the filter)
    /// tagged with <paramref name="folderId"/>, plus how many rows were examined. The <paramref name="maxResults"/>
    /// cap lets the folder-scoped caller stop early; the whole-mailbox caller passes int.MaxValue and caps after
    /// its cross-folder merge. Shared by <see cref="SearchFolder"/> and <see cref="SearchMailbox"/>.
    /// </summary>
    private Result<(IReadOnlyList<ListingScanHit> Hits, int Scanned)> ScanFolder(
        byte[] folderId, string query, ListingSearchField fields, int maxResults)
    {
        var listing = LoadEffectiveListing(folderId, out _);
        if (listing.IsFailure)
            return Result<(IReadOnlyList<ListingScanHit>, int)>.Failure(listing.Error);

        var hits = new List<ListingScanHit>();
        var rows = listing.Value;
        for (int i = 0; i < rows.Count; i++)
        {
            if (hits.Count >= maxResults)
                break;
            if (ListingScanMatcher.Matches(rows[i], query, fields))
                hits.Add(new ListingScanHit { FolderId = folderId, Record = rows[i] });
        }
        return Result<(IReadOnlyList<ListingScanHit>, int)>.Success((hits, rows.Count));
    }

    /// <summary>
    /// Shared argument validation for the listing scan searches: the query must be a non-empty keyword and
    /// the result cap must be positive. Returns an error fragment (appended after the operation name) or null.
    /// </summary>
    private static string? ValidateScanArgs(string query, int maxResults)
    {
        if (string.IsNullOrEmpty(query))
            return "requires a non-empty query.";
        if (maxResults <= 0)
            return $"maxResults must be positive, got {maxResults}.";
        return null;
    }

    /// <summary>
    /// Loads a folder's effective, date-descending listing (compiled pages with the pending delta chain
    /// merged over them) shared by <see cref="ListFolder"/> and <see cref="ListFolderFromDate"/>. Resolves
    /// the directory by its stable BlockId, reads every page newest-first, walks the delta chain head-to-
    /// root, and hands both to <see cref="FolderListingMerger"/>. Also returns the directory's
    /// <see cref="FolderPageDirectory.FolderVersion"/> the listing was read at.
    /// </summary>
    /// <summary>
    /// Best-effort read of a folder's <see cref="FolderPageDirectory"/> for the Bloom-filter consult in
    /// <see cref="SearchMailbox"/> — returns null on any resolve/read miss so the caller simply falls
    /// through to a normal scan (which surfaces the real error identically). Never skips a folder on an
    /// error, so the correctness of the scan path is unaffected.
    /// </summary>
    private FolderPageDirectory? TryReadFolderDirectory(byte[] folderId)
    {
        if (folderId.Length != UlidGenerator.UlidSize)
            return null;
        if (!_openState!.Resolver.TryGetLocation(folderId, out var dirLoc) || dirLoc is null)
            return null;
        var directory = _folderDirectoryStore!.ReadDirectory(dirLoc.Offset);
        return directory.IsSuccess ? directory.Value : null;
    }

    /// <summary>
    /// Reads a folder's <b>compiled page</b> records — the page rows only, WITHOUT the pending delta chain —
    /// in directory order. This is the exact token source a Bloom filter covers (<see cref="RebuildFolderBloomFilter"/>):
    /// the filter is authoritative only for compiled pages, so pending deltas are deliberately excluded.
    /// </summary>
    private Result<IReadOnlyList<ListingRecord>> LoadCompiledPageRecords(FolderPageDirectory directory)
    {
        var pageRecords = new List<ListingRecord>();
        foreach (var entry in directory.PageEntries)
        {
            if (!_openState!.Resolver.TryGetLocation(entry.PageBlockId, out var pageLoc) || pageLoc is null)
                return Result<IReadOnlyList<ListingRecord>>.Failure(
                    "Bloom rebuild failed: a FolderPage the directory names does not resolve to a physical offset " +
                    "(directory/page inconsistency, spec Section 13).");
            var page = _folderPageStore!.ReadPage(pageLoc.Offset);
            if (page.IsFailure)
                return Result<IReadOnlyList<ListingRecord>>.Failure($"Bloom rebuild failed reading a folder page: {page.Error}");
            pageRecords.AddRange(page.Value.Records);
        }
        return Result<IReadOnlyList<ListingRecord>>.Success(pageRecords);
    }

    private Result<IReadOnlyList<ListingRecord>> LoadEffectiveListing(byte[] folderId, out ulong folderVersion)
    {
        folderVersion = 0;
        if (folderId.Length != UlidGenerator.UlidSize)
            return Result<IReadOnlyList<ListingRecord>>.Failure(
                $"ListFolder folderId must be exactly {UlidGenerator.UlidSize} bytes, got {folderId.Length}.");

        // 1. Resolve + read the folder's directory (its FolderId is the directory block's BlockId).
        if (!_openState!.Resolver.TryGetLocation(folderId, out var dirLoc) || dirLoc is null)
            return Result<IReadOnlyList<ListingRecord>>.Failure(
                "ListFolder failed: the folder id does not resolve to a FolderPageDirectory block — the folder " +
                "was never written this session or committed (spec Section 7).");
        var directory = _folderDirectoryStore!.ReadDirectory(dirLoc.Offset);
        if (directory.IsFailure)
            return Result<IReadOnlyList<ListingRecord>>.Failure(
                $"ListFolder failed reading the folder directory: {directory.Error}");
        folderVersion = directory.Value.FolderVersion;

        // 2. Read the compiled pages newest-first, gathering their records.
        var pageRecords = new List<ListingRecord>();
        foreach (var entry in directory.Value.PageEntries)
        {
            if (!_openState.Resolver.TryGetLocation(entry.PageBlockId, out var pageLoc) || pageLoc is null)
                return Result<IReadOnlyList<ListingRecord>>.Failure(
                    "ListFolder failed: a FolderPage the directory names does not resolve to a physical offset " +
                    "(directory/page inconsistency, spec Section 13).");
            var page = _folderPageStore!.ReadPage(pageLoc.Offset);
            if (page.IsFailure)
                return Result<IReadOnlyList<ListingRecord>>.Failure($"ListFolder failed reading a folder page: {page.Error}");
            pageRecords.AddRange(page.Value.Records);
        }

        // 3. Walk the pending delta chain head-to-root (HeadDeltaBlockId → PreviousDeltaBlockId → ... → root).
        var chainHeadToRoot = new List<FolderDeltaLog>();
        if (directory.Value.HasPendingDelta)
        {
            var cursor = directory.Value.HeadDeltaBlockId;
            while (true)
            {
                if (!_openState.Resolver.TryGetLocation(cursor, out var deltaLoc) || deltaLoc is null)
                    return Result<IReadOnlyList<ListingRecord>>.Failure(
                        "ListFolder failed: a FolderDeltaLog block in the folder's pending chain does not resolve " +
                        "to a physical offset (broken delta chain, spec Section 13).");
                var read = _folderDeltaStore!.ReadDeltaBlock(deltaLoc.Offset);
                if (read.IsFailure)
                    return Result<IReadOnlyList<ListingRecord>>.Failure($"ListFolder failed reading a delta block: {read.Error}");
                chainHeadToRoot.Add(read.Value);
                if (!read.Value.HasPrevious)
                    break;
                cursor = read.Value.PreviousDeltaBlockId;
            }
        }

        // 4. Merge the pending deltas over the page rows into the canonical date-descending listing.
        return Result<IReadOnlyList<ListingRecord>>.Success(
            FolderListingMerger.Merge(pageRecords, chainHeadToRoot));
    }

    /// <summary>
    /// Binary-searches the date-descending <paramref name="listing"/> for the index of the newest row whose
    /// <see cref="ListingRecord.DateTicks"/> is at or before <paramref name="dateTicks"/> — the date-jump
    /// landing position. Returns the row count (an empty tail) when every row is newer than the target.
    /// </summary>
    private static int SeekToDate(IReadOnlyList<ListingRecord> listing, long dateTicks)
    {
        // DateTicks is non-increasing (newest first). Find the leftmost index with DateTicks <= target.
        int lo = 0, hi = listing.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (listing[mid].DateTicks <= dateTicks)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// Validates that the composed read-side folder stores are wired and the session is open, returning a
    /// failure message when a listing read cannot proceed, or null when it may.
    /// </summary>
    private string? EnsureFolderReadReady(string operation)
    {
        if (_openState is null || _folderPageStore is null
            || _folderDirectoryStore is null || _folderDeltaStore is null)
            return $"{operation} requires an opened EmailManager (Open, not Create): the folder read path is wired only over the composed read-side.";
        if (_closed || _disposed)
            return $"{operation} called on a closed or disposed EmailManager.";
        return null;
    }

    /// <summary>
    /// Validates that the composed write-side is wired and the session is writable, returning a failure
    /// message when a mailbox mutation (move/delete/flag) cannot proceed, or null when it may. Mirrors the
    /// front-of-pipeline guards <see cref="AddEmailCore"/> applies.
    /// </summary>
    private string? EnsureFolderMutationReady(string operation)
    {
        if (_openState is null || _primaryIndex is null || _dateIndex is null
            || _walWriter is null || _folderDeltaStore is null || _folderDirectoryStore is null)
            return $"{operation} requires an opened EmailManager (Open, not Create): the write pipeline is wired only over the composed read-side.";
        if (_closed || _disposed)
            return $"{operation} called on a closed or disposed EmailManager.";
        if (BlockManager.IsPoisoned)
            return $"{operation} aborted: the block stream is poisoned by an earlier failed write/fsync (spec Section 10.3).";
        return null;
    }

    /// <summary>
    /// Appends a single folder delta entry chained to <paramref name="folder"/>'s current head and
    /// persists the COW-advanced directory (the same two-step "append delta, persist directory" the
    /// AddEmail pipeline uses). Returns the appended block location and the persisted directory.
    /// </summary>
    private Result<FolderDeltaAppendResult> AppendFolderDelta(FolderPageDirectory folder, FolderDeltaEntry entry)
    {
        var appended = _folderDeltaStore!.AppendChained(folder, new[] { entry });
        if (appended.IsFailure)
            return Result<FolderDeltaAppendResult>.Failure($"appending the folder delta: {appended.Error}");
        var persisted = _folderDirectoryStore!.WriteDirectory(appended.Value.Directory);
        if (persisted.IsFailure)
            return Result<FolderDeltaAppendResult>.Failure($"persisting the advanced folder directory: {persisted.Error}");
        return Result<FolderDeltaAppendResult>.Success(appended.Value);
    }

    /// <summary>
    /// Resolves the Tier 3 <c>EmailContent</c> and Tier 2 <c>EmailMetadata</c> block locations for a
    /// committed email, given its ContentBlockId, so a delete can account both blocks' bytes dead. The
    /// content block resolves through the composite resolver (offset + on-disk length); the metadata block
    /// is the immediately-following append (spec Section 14, immutable append-only layout), so its location
    /// is read from the block header at <c>contentOffset + contentTotalBlockLength</c> — matching the layout
    /// <see cref="GetMetadata"/> relies on.
    /// </summary>
    private Result<IReadOnlyList<BlockLocation>> ResolveRetiredEmailBlocks(byte[] contentBlockId)
    {
        if (!_openState!.Resolver.TryGetLocation(contentBlockId, out var contentLocation) || contentLocation is null)
            return Result<IReadOnlyList<BlockLocation>>.Failure(
                "DeleteEmail failed: the primary index resolved the identity to a ContentBlockId that the " +
                "location index cannot place at a physical offset (index/location inconsistency, spec Section 13).");

        long metadataOffset = contentLocation.Offset + contentLocation.TotalBlockLength;
        var metaRead = BlockManager.Read(metadataOffset);
        if (metaRead.IsFailure)
            return metaRead.VerificationError is not null
                ? Result<IReadOnlyList<BlockLocation>>.Failure(metaRead.VerificationError)
                : Result<IReadOnlyList<BlockLocation>>.Failure(
                    $"DeleteEmail failed reading the Tier 2 metadata block at offset {metadataOffset}: {metaRead.Error}");
        if (metaRead.Value.Header.Type != BlockType.EmailMetadata)
            return Result<IReadOnlyList<BlockLocation>>.Failure(
                $"DeleteEmail failed: the block after the Tier 3 content block is {metaRead.Value.Header.Type}, " +
                "expected EmailMetadata (stale/misdirected hint or corrupt layout, spec Section 13).");

        var metadataLocation = new BlockLocation
        {
            BlockId = (byte[])metaRead.Value.Header.BlockId.Clone(),
            Offset = metadataOffset,
            TotalBlockLength = BlockSerializer.GetTotalBlockLength(metaRead.Value.Header.PayloadLength),
        };

        return Result<IReadOnlyList<BlockLocation>>.Success(new[] { contentLocation, metadataLocation });
    }

    /// <summary>
    /// Reads one email's Tier 3 content (story US-EMDB-86, the open-email read path). It runs the
    /// documented O(log n) retrieval chain (spec Sections 6-7, 11.1, docs/BTree_Index.md Section 5):
    /// <list type="number">
    ///   <item><b>Primary index lookup.</b> The <see cref="EmailHashedID"/> is looked up in the
    ///   WAL-buffered PrimaryEmail index (read-your-writes, so a buffered-but-uncommitted add this
    ///   session resolves too). The B+-tree traversal is Merkle-verified — each traversed node's hash
    ///   is checked against its parent's ChildHash and the root against the IndexRoot's RootHash. An
    ///   <b>unknown identity is a clean not-found</b> (<see cref="EmailReadResult.NotFound"/>), never a
    ///   failure or exception (spec Section 13).</item>
    ///   <item><b>Location resolve.</b> The resolved ContentBlockId is turned into a physical offset
    ///   through the composite resolver (runtime map → BlockLocationIndex, spec Section 7).</item>
    ///   <item><b>Verified block read.</b> The EmailContent block is read with its header and payload
    ///   checksums verified, then — for an encrypted file — decrypted (GCM tag + AAD verified), then
    ///   decompressed (read order Decrypt → Decompress, spec Sections 4, 9.4).</item>
    /// </list>
    /// </summary>
    /// <param name="emailId">The content identity to retrieve.</param>
    /// <returns>
    /// A hit carrying the raw MIME bytes; a clean not-found for an unknown identity; a failure only on
    /// I/O error, corruption, or an index that resolves to an unreadable/wrong-type block.
    /// </returns>
    public Result<EmailReadResult> GetEmail(EmailHashedID emailId)
    {
        var located = ResolveEmail(emailId);
        if (located.IsFailure)
            return Result<EmailReadResult>.Failure(located.Error);
        if (located.Value is not { } contentLocation)
            return Result<EmailReadResult>.Success(EmailReadResult.NotFound);

        // Verified read of the Tier 3 EmailContent block: checksum → decrypt → decompress.
        var payload = ReadEmailBlockPayload(contentLocation.Offset, BlockType.EmailContent);
        return payload.IsFailure
            ? Result<EmailReadResult>.Failure(payload.Error)
            : Result<EmailReadResult>.Success(EmailReadResult.Hit(payload.Value));
    }

    /// <summary>
    /// Reads one email's Tier 2 metadata for opening it <b>without touching Tier 3</b> (story
    /// US-EMDB-86): the same primary-index lookup and location resolve as <see cref="GetEmail"/>, but
    /// it stops at the Tier 2 <c>EmailMetadata</c> block and never reads the Tier 3 body/attachments.
    ///
    /// <para><b>Locating Tier 2 without reading Tier 3.</b> <see cref="AddEmail"/> appends the Tier 2
    /// <c>EmailMetadata</c> block as the immediately-following block after the Tier 3
    /// <c>EmailContent</c> block, and v3 blocks are append-only and immutable (spec Section 14) — so a
    /// committed email's metadata block sits at <c>contentOffset + contentTotalBlockLength</c>. This
    /// method reads only the content block's <b>index location</b> (offset + on-disk length), never its
    /// payload, then reads the metadata block directly by that computed offset and asserts its type is
    /// <see cref="BlockType.EmailMetadata"/> — so Tier 3 is never decrypted or decompressed. The read
    /// is otherwise identical to <see cref="GetEmail"/>: checksum → decrypt → decompress.</para>
    /// </summary>
    /// <param name="emailId">The content identity whose metadata to retrieve.</param>
    /// <returns>
    /// A hit carrying the Tier 2 metadata bytes; a clean not-found for an unknown identity; a failure
    /// only on I/O error, corruption, or an index/layout that resolves to an unreadable block.
    /// </returns>
    public Result<EmailReadResult> GetMetadata(EmailHashedID emailId)
    {
        var located = ResolveEmail(emailId);
        if (located.IsFailure)
            return Result<EmailReadResult>.Failure(located.Error);
        if (located.Value is not { } contentLocation)
            return Result<EmailReadResult>.Success(EmailReadResult.NotFound);

        // The Tier 2 block is the append immediately after the Tier 3 block. We use ONLY the content
        // block's resolved location (offset + length) — never its payload — so Tier 3 stays untouched.
        long metadataOffset = contentLocation.Offset + contentLocation.TotalBlockLength;
        var payload = ReadEmailBlockPayload(metadataOffset, BlockType.EmailMetadata);
        return payload.IsFailure
            ? Result<EmailReadResult>.Failure(payload.Error)
            : Result<EmailReadResult>.Success(EmailReadResult.Hit(payload.Value));
    }

    /// <summary>
    /// The shared front half of the read path: validate the manager is opened, look the
    /// <see cref="EmailHashedID"/> up in the Merkle-verified PrimaryEmail index, and resolve the
    /// ContentBlockId to a physical <see cref="BlockLocation"/>. Returns a success with a null value for
    /// a clean not-found (the identity is absent from the index), a success with the location on a hit,
    /// and a failure only for a real error (unopened manager, index read fault, or a resolved BlockId
    /// with no physical location — an index/location inconsistency, spec Section 13).
    /// </summary>
    private Result<BlockLocation?> ResolveEmail(EmailHashedID emailId)
    {
        if (_openState is null || _primaryIndex is null)
            return Result<BlockLocation?>.Failure(
                "GetEmail/GetMetadata require an opened EmailManager (Open, not Create): the read path is wired only over the composed read-side.");
        if (_closed || _disposed)
            return Result<BlockLocation?>.Failure("GetEmail/GetMetadata called on a closed or disposed EmailManager.");

        // 1. Primary index lookup (Merkle-verified traversal inside the tree; read-your-writes over the
        //    WAL buffer). A missing key is a clean not-found, NOT a failure (spec Section 13).
        var lookup = _primaryIndex.TryGet(emailId.GetBytes());
        if (lookup.IsFailure)
            return Result<BlockLocation?>.Failure($"Read failed during the primary index lookup: {lookup.Error}");
        if (!lookup.Value.Found)
            return Result<BlockLocation?>.Success(null);

        // 2. Location resolve: ContentBlockId → physical offset (runtime map → location index, spec
        //    Section 7). A key present in the index but unresolvable is index/location corruption.
        byte[] contentBlockId = lookup.Value.Value!;
        if (!_openState.Resolver.TryGetLocation(contentBlockId, out var location) || location is null)
            return Result<BlockLocation?>.Failure(
                "Read failed: the primary index resolved the identity to a ContentBlockId that the location " +
                "index cannot place at a physical offset (index/location inconsistency, spec Section 13).");

        return Result<BlockLocation?>.Success(location);
    }

    /// <summary>
    /// Reads and fully verifies the email-tier block at <paramref name="offset"/> and returns its
    /// plaintext: header + payload checksums (spec Section 4), then — for an encrypted block — decrypt
    /// by the header's KeyEpoch (GCM tag + AAD, spec Section 9.4), then decompress (read order Decrypt →
    /// Decompress). The block's <see cref="BlockHeader.Type"/> MUST equal <paramref name="expectedType"/>;
    /// a mismatch is a stale/misdirected hint or a layout error surfaced as a failure, never a misread
    /// (spec Section 13).
    /// </summary>
    private Result<byte[]> ReadEmailBlockPayload(long offset, BlockType expectedType)
    {
        var read = BlockManager.Read(offset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<byte[]>.Failure(read.VerificationError)
                : Result<byte[]>.Failure(read.Error);

        var block = read.Value;
        if (block.Header.Type != expectedType)
            return Result<byte[]>.Failure(
                $"Read at offset {offset} resolved a {block.Header.Type} block, expected {expectedType} " +
                "(stale/misdirected hint or corrupt layout, spec Section 13).");

        // Decrypt an encrypted block (GCM tag/AAD verified here); a plaintext block passes through.
        // The provider THROWS the distinct crypto errors (spec Section 13): a valid-checksum block
        // whose GCM tag/AAD does not authenticate is a WrongKeyOrTamperError, and a block referencing
        // a KeyEpoch with no live DEK is an EpochDekUnavailableError. The read path is Result-based
        // ("corruption is reported as a failed Result, never by throwing"), so catch both here and
        // surface them as clean failures — a tampered ciphertext must fail GetEmail cleanly, never
        // escape as an unhandled exception or return wrong data.
        byte[] payload;
        if (block.Header.IsEncrypted)
        {
            if (_provider is null)
                return Result<byte[]>.Failure(
                    $"Read at offset {offset}: the block is encrypted but no key provider is loaded (open the file with its password).");
            try
            {
                payload = _provider.Decrypt(
                    block.Payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch);
            }
            catch (WrongKeyOrTamperError ex)
            {
                // Checksum passed but authenticity failed: wrong key or tampering (spec Section 13).
                return ex.ToResult<byte[]>();
            }
            catch (EpochDekUnavailableError ex)
            {
                // No live DEK for the header's KeyEpoch (missing/retired): a clean read failure, not a throw.
                return Result<byte[]>.Failure($"Read at offset {offset}: {ex.Message}");
            }
        }
        else
        {
            payload = block.Payload;
        }

        // Decompress after decrypt (spec Section 4 read order). Email blocks are written uncompressed
        // today, so this is normally a no-op; handled generically so a future compressed write reads back.
        if (block.Header.Compression != CompressionAlgorithm.None)
        {
            var decompressed = BlockCompressor.Decompress(
                payload, block.Header.Compression, BlockManager.MaxPayloadLength);
            if (decompressed.IsFailure)
                return decompressed.VerificationError is not null
                    ? Result<byte[]>.Failure(decompressed.VerificationError)
                    : Result<byte[]>.Failure($"Read at offset {offset}: {decompressed.Error}");
            payload = decompressed.Value;
        }

        return Result<byte[]>.Success(payload);
    }

    /// <summary>
    /// Creates and initializes a new v3 file at <paramref name="path"/> per spec
    /// Section 11.1 (see the class summary for the full protocol). The file MUST NOT
    /// already exist — creation is exclusive (<see cref="FileMode.CreateNew"/>) and
    /// takes the writer lock (<see cref="FileShare.None"/>). Any failure part-way
    /// through deletes the partial file so no half-created file is left behind.
    /// </summary>
    /// <param name="path">Path of the file to create; must not already exist.</param>
    /// <param name="options">Create options (encryption, KDF costs, shard index, limits). Null uses defaults.</param>
    /// <returns>
    /// A live, ready <see cref="EmailManager"/> holding the durable, reopenable file; a
    /// failure (with no file left on disk) otherwise.
    /// </returns>
    public static Result<EmailManager> Create(string path, EmailManagerCreateOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new EmailManagerCreateOptions();
        if (options.MaxPayloadLength <= 0)
            return Result<EmailManager>.Failure(
                $"MaxPayloadLength must be positive, got {options.MaxPayloadLength}.");

        // 1. Create the file exclusively (fail if it already exists) and take the
        //    single-writer lock for the file's lifetime (spec Section 12).
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            return Result<EmailManager>.Failure(
                $"Cannot create '{path}': it may already exist or the writer lock is held. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<EmailManager>.Failure($"Cannot create '{path}': {ex.Message}");
        }

        var runtimeMap = new RuntimeBlockOffsetMap();
        var manager = new BlockManager(
            stream, maxPayloadLength: options.MaxPayloadLength, offsetMap: runtimeMap, ownsStream: false);

        EpochDekProvider? provider = null;
        bool committed = false;
        try
        {
            // FileId: minted once at creation; identifies this shard forever.
            var fileId = new UlidGenerator().Next();

            // 2. Encryption bootstrap (encrypted files only): fresh salt + first DEK (epoch 0),
            //    KEK-verification token, and a KEK-encrypted KeyStore block. The returned
            //    fields are stamped onto the superblock below.
            EncryptionBootstrap.CreatedEncryption? encryption = null;
            var keyStoreRoot = CheckpointRootPointer.None;
            if (options.Password is not null)
            {
                var created = EncryptionBootstrap.CreateEncryption(
                    manager, fileId, options.Password, options.KdfParameters);
                if (created.IsFailure)
                    return Result<EmailManager>.Failure(
                        $"File creation failed writing the KeyStore: {created.Error}");
                encryption = created.Value;
                provider = encryption.Provider;
                keyStoreRoot = CheckpointRootPointer.Create(
                    (byte[])encryption.KeyStore.BlockId.Clone(), encryption.KeyStore.Offset);
            }

            // 3. Initial Metadata + FolderTree blocks. Payloads are empty at creation
            //    (their schemas are defined by later stories); a reopen resolves them by
            //    the Checkpoint's root pointers (BlockId verified at the offset hint), so
            //    an empty payload is a valid, resolvable root. Encryption is policy-driven:
            //    Metadata is always plaintext (read during recovery before any key exists),
            //    FolderTree is encrypted under both policies (spec Section 9.5).
            Result<BlockLocation> metadata, folderTree;
            if (provider is not null)
            {
                var store = new EncryptedBlockStore(manager, provider, options.EncryptionPolicy);
                metadata = store.Append(BlockType.Metadata, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty);
                if (metadata.IsSuccess)
                    folderTree = store.Append(BlockType.FolderTree, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty);
                else
                    folderTree = metadata;
            }
            else
            {
                metadata = manager.Append(BlockType.Metadata, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty);
                if (metadata.IsSuccess)
                    folderTree = manager.Append(BlockType.FolderTree, PayloadEncoding.Custom, ReadOnlySpan<byte>.Empty);
                else
                    folderTree = metadata;
            }
            if (metadata.IsFailure)
                return Result<EmailManager>.Failure($"File creation failed writing the Metadata block: {metadata.Error}");
            if (folderTree.IsFailure)
                return Result<EmailManager>.Failure($"File creation failed writing the FolderTree block: {folderTree.Error}");

            var metadataRoot = CheckpointRootPointer.Create(
                (byte[])metadata.Value.BlockId.Clone(), metadata.Value.Offset);
            var folderTreeRoot = CheckpointRootPointer.Create(
                (byte[])folderTree.Value.BlockId.Clone(), folderTree.Value.Offset);

            // 4. First Checkpoint (sequence 0), empty index roots. LiveBlockCount is the
            //    BlockLocationIndex entry count — zero, because that index starts empty and
            //    the initial structural blocks are addressed directly by these root pointers.
            long liveBytes = metadata.Value.TotalBlockLength + folderTree.Value.TotalBlockLength
                + (encryption is not null ? encryption.KeyStore.TotalBlockLength : 0);
            var checkpointWriter = new CheckpointWriter(manager, fileId);
            var checkpoint = checkpointWriter.WriteCheckpoint(new CheckpointContents
            {
                FolderTreeRoot = folderTreeRoot,
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = CheckpointRootPointer.None,
                MetadataRoot = metadataRoot,
                KeyStoreRoot = keyStoreRoot,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = 0,
                LiveByteCount = liveBytes,
                DeadByteCount = 0,
            });
            if (checkpoint.IsFailure)
                return Result<EmailManager>.Failure($"File creation failed writing the first Checkpoint: {checkpoint.Error}");

            var lastCheckpoint = checkpointWriter.LastCheckpointPointer;

            // 5. Superblocks A (sequence 1) + B (sequence 2): identical content, alternating
            //    slots, each fsynced. CleanShutdown = 1 so the file reopens on the clean-open
            //    fast path; the encryption fields (if any) let it reopen from file + password.
            var superblock = new Superblock
            {
                FileId = (byte[])fileId.Clone(),
                ShardIndex = options.ShardIndex,
                CreatedTimestamp = DateTime.UtcNow.Ticks,
                CleanShutdown = 1,
                MaxPayloadLength = options.MaxPayloadLength,
                LastCheckpointBlockId = (byte[])lastCheckpoint.BlockId.Clone(),
                LastCheckpointOffset = lastCheckpoint.Offset,
            };
            encryption?.ApplyTo(superblock);

            using (var superblockManager = new SuperblockManager(stream, ownsStream: false))
            {
                var slotA = superblockManager.Write(superblock); // sequence 1, slot A
                if (slotA.IsFailure)
                    return Result<EmailManager>.Failure($"File creation failed writing superblock slot A: {slotA.Error}");
                var slotB = superblockManager.Write(superblock); // sequence 2, slot B
                if (slotB.IsFailure)
                    return Result<EmailManager>.Failure($"File creation failed writing superblock slot B: {slotB.Error}");
            }

            // 6. fsync the containing directory so the new file's directory entry is durable
            //    (spec Sections 10.3, 11.1). Without this a crash can lose the whole file.
            var directorySync = DirectoryFsync.SyncContainingDirectory(path);
            if (directorySync.IsFailure)
                return Result<EmailManager>.Failure($"File creation failed fsyncing the directory: {directorySync.Error}");

            committed = true;
            return Result<EmailManager>.Success(new EmailManager(
                path, stream, manager, runtimeMap, superblock, provider,
                lastCheckpoint, checkpointWriter.LastSequence!.Value));
        }
        finally
        {
            if (!committed)
            {
                // Roll back a partial create: zeroize any key material, release the file,
                // and delete the half-written file so no invalid file is left behind.
                provider?.Dispose();
                manager.Dispose();
                stream.Dispose();
                try { File.Delete(path); }
                catch (IOException) { /* best effort */ }
                catch (UnauthorizedAccessException) { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Opens an existing v3 file at <paramref name="path"/>, running the full open protocol
    /// (EmailDB_FileFormat_Spec.md Section 10.2) and composing every layer into one ready
    /// instance holding the exclusive writer lock (spec Section 12):
    /// <list type="number">
    ///   <item><b>Superblock read + validate.</b> The valid superblock (higher valid sequence)
    ///   is selected; an unknown IncompatFlags refuses the open.</item>
    ///   <item><b>Encryption bootstrap.</b> When the superblock marks the file encrypted, the
    ///   supplied <see cref="EmailManagerOpenOptions.Password"/> is run through the KEK
    ///   derivation → key-verification → KeyStore decrypt chain
    ///   (<see cref="PasswordEncryptionBootstrap"/>), yielding the live DEK provider. A missing
    ///   or wrong password fails the open rather than reading ciphertext as plaintext.</item>
    ///   <item><b>Checkpoint load + index construction.</b> The superblock's LastCheckpoint hint
    ///   is followed to the committed Checkpoint (FileId cross-checked), and the live
    ///   BlockLocationIndex is reconstructed from its location root; the primary/secondary index
    ///   roots are resolved on the <see cref="Checkpoint"/>. This is the tested
    ///   <see cref="CleanOpener"/> fast path — zero file scanning on a clean file.</item>
    ///   <item><b>WAL replay (recovery).</b> When the superblock records
    ///   <c>CleanShutdown = 0</c> (a crash between operations), the bounded
    ///   <see cref="DirtyOpener"/> recovery runs instead: it adopts the newest durable Checkpoint,
    ///   replays the uncommitted WAL fenced to it (spec Section 10.4), heals the superblock to
    ///   that commit point, and hands back the clean read-side state — so a kill -9 always reopens
    ///   to the last commit.</item>
    ///   <item><b>Folder manager wiring.</b> The folder-layer stores
    ///   (<see cref="FolderPageStore"/>, <see cref="FolderPageDirectoryStore"/>,
    ///   <see cref="FolderDeltaLogStore"/>) are wired over the opened block manager with the
    ///   loaded provider (plaintext when unencrypted).</item>
    /// </list>
    /// The returned instance exposes the composed components (<see cref="Superblock"/>,
    /// <see cref="Checkpoint"/>, <see cref="LocationIndex"/>, <see cref="Resolver"/>,
    /// <see cref="EncryptionProvider"/>, <see cref="Folders"/> and siblings) and holds the file
    /// open; <see cref="Dispose"/> releases the writer lock and zeroizes key material.
    /// </summary>
    /// <param name="path">Path of an existing v3 file to open.</param>
    /// <param name="options">Open options (the password for an encrypted file). Null opens a plaintext file.</param>
    /// <returns>A live, ready <see cref="EmailManager"/>; a failure (with the file left closed) otherwise.</returns>
    public static Result<EmailManager> Open(string path, EmailManagerOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new EmailManagerOpenOptions();

        if (!File.Exists(path))
            return Result<EmailManager>.Failure($"Cannot open '{path}': the file does not exist.");

        // Discard any leftover compaction side file (spec Section 11.2): a <name>.compact
        // present here is from a compaction interrupted before its atomic rename, so it was
        // never current and is stale by definition. Deleting it before adopting the source
        // completes the crash-safe swap contract — an interrupted compaction leaves the
        // complete old file, and the next open cleans up the abandoned side file.
        var sideCleanup = Compactor.CleanupLeftoverSideFile(path);
        if (sideCleanup.IsFailure)
            return Result<EmailManager>.Failure($"Cannot open '{path}': {sideCleanup.Error}");

        // Take the single-writer lock for the file's lifetime (spec Section 12).
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            return Result<EmailManager>.Failure(
                $"Cannot open '{path}': the writer lock may be held by another process. {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<EmailManager>.Failure($"Cannot open '{path}': {ex.Message}");
        }

        // The real encryption bootstrap chain (file + password); null for a plaintext open.
        var bootstrap = options.Password is null
            ? null
            : new PasswordEncryptionBootstrap(stream, options.Password);

        OpenState? state = null;
        IReadOnlyList<RecoveredWalOp>? replayToCommit = null;
        bool committed = false;
        try
        {
            // 1-3. Clean-open fast path: superblock select/validate, encryption bootstrap,
            //      Checkpoint load, and index construction — zero scanning on a clean file.
            var clean = CleanOpener.Open(stream, bootstrap);
            if (clean.IsFailure)
                return Result<EmailManager>.Failure($"Open failed: {clean.Error}");

            switch (clean.Value.Kind)
            {
                case OpenOutcomeKind.CleanOpen:
                    state = clean.Value.State!;
                    break;

                case OpenOutcomeKind.EncryptionBootstrapRequired:
                    return Result<EmailManager>.Failure(
                        "Open failed: the file is encrypted; supply the password in EmailManagerOpenOptions (spec Section 10.2 step 2).");

                case OpenOutcomeKind.DirtyOpenRequired:
                {
                    // 4. Recovery: the last session did not close cleanly. Run the bounded dirty
                    //    scan + WAL replay to the last durable commit (spec Section 10.2 step 4).
                    //    A recording sink captures the uncommitted WAL ops fenced to the adopted
                    //    Checkpoint; buildStateForCallerCommit hands back a read-side state at that
                    //    Checkpoint so this open can re-apply the ops to its live indexes and commit
                    //    a fresh Checkpoint — folding them in and healing the file clean (spec
                    //    Section 13: torn checkpoint falls back to the previous commit and replays
                    //    the matching WAL). A file with no uncommitted WAL simply heals clean here.
                    var recoverySink = new RecoveryReplaySink();
                    var dirty = DirtyOpener.Open(
                        stream,
                        replaySink: recoverySink,
                        encryptionBootstrap: bootstrap,
                        buildStateForCallerCommit: true);
                    if (dirty.IsFailure)
                        return Result<EmailManager>.Failure($"Open failed during recovery: {dirty.Error}");
                    if (dirty.Value.State is null
                        || (!dirty.Value.HealedClean && !dirty.Value.RequiresCallerCommit))
                        return Result<EmailManager>.Failure(
                            "Open failed: recovery replayed uncommitted WAL that could not be committed to a clean point " +
                            $"(the next open re-runs recovery). {dirty.Value.Detail}");
                    state = dirty.Value.State;
                    if (dirty.Value.RequiresCallerCommit)
                        replayToCommit = recoverySink.Ops;
                    break;
                }

                default:
                    return Result<EmailManager>.Failure($"Open failed: unexpected open outcome {clean.Value.Kind}.");
            }

            var provider = bootstrap?.Provider;

            // 5. Folder-layer wiring over the opened block manager. Both policies encrypt folder
            //    blocks, so Default is correct for an encrypted file; a plaintext file (null
            //    provider) writes/reads them plaintext.
            var folderPageStore = new FolderPageStore(state.BlockManager, provider, EncryptionPolicy.Default);
            var folderDirectoryStore = new FolderPageDirectoryStore(state.BlockManager, provider, EncryptionPolicy.Default);
            var folderDeltaStore = new FolderDeltaLogStore(state.BlockManager, provider, EncryptionPolicy.Default);

            // The recovered/selected superblock's hint names the committed Checkpoint; its
            // sequence is the resolved Checkpoint's own sequence.
            var lastCheckpoint = CheckpointRootPointer.Create(
                (byte[])state.Superblock.LastCheckpointBlockId.Clone(), state.Superblock.LastCheckpointOffset);
            var lastCheckpointSequence = state.Checkpoint.Checkpoint.CheckpointSequence;

            // Seed the PrimaryEmail + Date indexes from the committed Checkpoint's durable index
            // roots (US-EMDB-85-6): a committed AddEmail must survive reopen and cross-session
            // dedupe must observe it. The primary root is the Checkpoint's PrimaryIndexRoot; the
            // date root is its IndexKind-2 secondary entry.
            var primarySeed = ReconstructIndexSeed(
                state.BlockManager, state.Checkpoint.PrimaryIndexRoot, BTreeIndexKind.PrimaryEmail);
            if (primarySeed.IsFailure)
                return Result<EmailManager>.Failure(primarySeed.Error);

            ResolvedRoot? dateResolved = null;
            foreach (var secondary in state.Checkpoint.SecondaryIndexes)
            {
                if (secondary.IndexKind == BTreeIndexKind.Date)
                {
                    dateResolved = secondary.Root;
                    break;
                }
            }
            var dateSeed = ReconstructIndexSeed(state.BlockManager, dateResolved, BTreeIndexKind.Date);
            if (dateSeed.IsFailure)
                return Result<EmailManager>.Failure(dateSeed.Error);

            // Reconstruct the FTS address trigram index from its IndexKind-3 secondary entry
            // (US-EMDB-91-7): resolve the FTSSearchRoot the Checkpoint named, then read its segments'
            // posting lists back into the in-memory index so a committed ingest/delete survives reopen.
            ResolvedRoot? ftsResolved = null;
            foreach (var secondary in state.Checkpoint.SecondaryIndexes)
            {
                if (secondary.IndexKind == BTreeIndexKind.Fts)
                {
                    ftsResolved = secondary.Root;
                    break;
                }
            }
            var ftsBlockStore = new FtsBlockStore(state.BlockManager, provider, state.Resolver);
            var ftsIndex = FtsIndex.Reconstruct(ftsBlockStore, ftsResolved);
            if (ftsIndex.IsFailure)
                return Result<EmailManager>.Failure(ftsIndex.Error);

            // Reconstruct the per-folder Bloom filters from their IndexKind-4 secondary entry
            // (US-EMDB-97-5): resolve the BloomFilterCatalog the Checkpoint named and load every folder's
            // filter, so a committed rebuild-at-compile survives reopen. Absent ⇒ an empty filter set.
            ResolvedRoot? bloomResolved = null;
            foreach (var secondary in state.Checkpoint.SecondaryIndexes)
            {
                if (secondary.IndexKind == BTreeIndexKind.Bloom)
                {
                    bloomResolved = secondary.Root;
                    break;
                }
            }
            var bloomStore = new BloomFilterStore(state.BlockManager, provider);
            var bloomIndex = FolderBloomIndex.Reconstruct(bloomStore, bloomResolved);
            if (bloomIndex.IsFailure)
                return Result<EmailManager>.Failure(bloomIndex.Error);

            var manager = new EmailManager(
                path, stream, state, provider,
                folderPageStore, folderDirectoryStore, folderDeltaStore,
                lastCheckpoint, lastCheckpointSequence,
                primarySeed.Value, dateSeed.Value,
                ftsIndex.Value, ftsBlockStore,
                bloomIndex.Value, bloomStore);

            // Torn-checkpoint recovery (spec Section 13, Section 10.4): recovery replayed the
            // uncommitted WAL fenced to the adopted Checkpoint into the recording sink but left the
            // file dirty. Re-apply those ops to the live indexes, then commit — the group-commit
            // Checkpoint folds them into a durable primary root and heals the superblock clean, so
            // the previously-committed email and the WAL-logged one both survive and reopen.
            if (replayToCommit is not null)
            {
                var applied = manager.ApplyRecoveredWalOps(replayToCommit);
                if (applied.IsFailure)
                    return Result<EmailManager>.Failure($"Open failed re-applying recovered WAL: {applied.Error}");
                var recommit = manager.Commit();
                if (recommit.IsFailure)
                    return Result<EmailManager>.Failure($"Open failed committing recovered WAL: {recommit.Error}");
            }

            committed = true;
            return Result<EmailManager>.Success(manager);
        }
        finally
        {
            if (!committed)
            {
                // Release everything the failed open built: the DEK provider (zeroizing keys),
                // the block manager the opener created, and the file (the writer lock).
                bootstrap?.Provider?.Dispose();
                state?.Dispose();
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Closes the file durably (US-EMDB-84-7, EmailDB_FileFormat_Spec.md Section 11.1): the clean
    /// counterpart to <see cref="Open"/>. In order it
    /// <list type="number">
    ///   <item><b>flushes buffered index state</b> — folds every block appended this session (the
    ///   runtime map; empty on a clean open) into the durable BlockLocationIndex in one
    ///   copy-on-write batch, so the final Checkpoint's location root reflects them;</item>
    ///   <item><b>compiles pending folder deltas if warranted</b> (<see cref="FolderCompiler.ShouldCompile"/>,
    ///   docs/Folder_Listing.md Section 3) — a documented no-op in the current composition, which
    ///   tracks no open folder with a pending delta count (the folder lifecycle wires the trigger);</item>
    ///   <item><b>writes a final Checkpoint</b> reflecting the current index roots — the (possibly
    ///   advanced) location root plus every other root carried forward from the loaded Checkpoint,
    ///   with <c>LiveBlockCount</c> = the location index entry count the reopen reconstructs with;</item>
    ///   <item><b>writes a CleanShutdown = 1 superblock</b> repointing <c>LastCheckpoint</c> at that
    ///   final Checkpoint (the dual-slot manager fsyncs each write);</item>
    ///   <item><b>releases the writer lock</b> and zeroizes any key material.</item>
    /// </list>
    ///
    /// <para><b>Idempotent.</b> A second <see cref="Close"/> is a success no-op, and
    /// <see cref="Dispose"/> after a <see cref="Close"/> is safe. A plain <see cref="Dispose"/>
    /// WITHOUT a <see cref="Close"/> is a <i>dirty</i> close: it releases the file without writing the
    /// final Checkpoint or touching CleanShutdown, so whatever the file's on-disk state records stands
    /// — a session that had marked the file dirty (CleanShutdown = 0) recovers via WAL replay on the
    /// next <see cref="Open"/> (spec Section 10.2 step 4). A failed <see cref="Close"/> still releases
    /// the file (leaving the previous durable Checkpoint authoritative) and returns the failure.</para>
    ///
    /// <para>A <see cref="Create"/>d-but-never-opened instance has no composed read-side to
    /// flush/checkpoint and was already left clean and reopenable by <see cref="Create"/>; Close on it
    /// simply releases the writer lock.</para>
    /// </summary>
    /// <returns>Success once the file is durably clean-closed; a failure (file released) otherwise.</returns>
    public Result Close()
    {
        if (_closed)
            return Result.Success();
        if (_disposed)
            return Result.Failure(
                "Close called after Dispose: the file was already dirty-closed and released (spec Section 11.1).");

        // A created-but-never-opened instance is already durable and clean from Create (spec
        // Section 11.1); there is no composed read-side to flush or checkpoint, so just release.
        if (_openState is null)
        {
            _closed = true;
            ReleaseResources();
            return Result.Success();
        }

        var result = WriteCommitCheckpoint(_openState);
        _closed = true;
        ReleaseResources();
        return result;
    }

    /// <summary>
    /// Group commit (US-EMDB-85-6): makes every buffered mutation since the last commit durable in
    /// ONE flush + Checkpoint, the shared commit point <see cref="AddEmail"/>/<see cref="AddEmails"/>
    /// batch into. It flushes the WAL-buffered PrimaryEmail index and persists the Date index's root
    /// descriptor, folds this session's appends into the BlockLocationIndex, writes a Checkpoint naming
    /// all three fresh roots, and repoints the superblock's <c>LastCheckpoint</c> hint at it — so a
    /// committed AddEmail survives a crash (a clean reopen resolves the new Checkpoint) and reopen (the
    /// indexes seed from these roots). After the commit <see cref="LastCheckpoint"/> advances, so later
    /// WAL entries fence to the new commit point (spec Section 10.4). Explicit; also fires automatically
    /// once <see cref="AutoCommitThreshold"/> fresh emails accumulate, and once as part of <see cref="Close"/>.
    ///
    /// <para>A <see cref="Create"/>d-but-never-opened instance has no composed write-side; Commit on it
    /// is a success no-op. A failed commit leaves the previous durable Checkpoint authoritative.</para>
    /// </summary>
    /// <returns>Success once the batch is durably committed; a failure (previous Checkpoint authoritative) otherwise.</returns>
    public Result Commit()
    {
        if (_closed || _disposed)
            return Result.Failure("Commit called on a closed or disposed EmailManager.");
        if (_openState is null)
            return Result.Success(); // created-but-never-opened: nothing buffered to commit.
        return WriteCommitCheckpoint(_openState);
    }

    /// <summary>
    /// Reclaims dead space by compacting this mailbox in place (EmailDB_FileFormat_Spec.md
    /// Section 11.2, docs/Compaction.md Section 2): full-file side-file compaction plus atomic swap.
    /// This is the operator-facing wiring over <see cref="Compactor"/> — the level-1 space reclaimer
    /// v3 has (an append-only file cannot free interior dead space in place). In order it
    /// <list type="number">
    ///   <item><b>commits</b> every buffered mutation so compaction copies a complete committed
    ///   snapshot (uncommitted appends are not in the durable Checkpoint compaction reads);</item>
    ///   <item><b>releases this manager's writer handle</b> so the atomic rename can replace the file
    ///   on every platform (Windows refuses to rename over a file with open handles) — after this the
    ///   current instance is spent;</item>
    ///   <item><b>runs the full compaction</b> — <see cref="Compactor.Begin"/> →
    ///   <see cref="Compactor.CopyLiveBlocks"/> → <see cref="Compactor.RebuildLocationIndexAndWriteCheckpoint"/>
    ///   → <see cref="Compactor.FinalizeAndSwap"/> — copying every live block AND every directly-
    ///   addressed Checkpoint root (folder tree, metadata, key store) into a side file with the same
    ///   FileId and continued sequences, rebuilding the location index, and atomically swapping it in;</item>
    ///   <item><b>reopens the compacted file</b> and returns a fresh, ready manager.</item>
    /// </list>
    ///
    /// <para>The returned manager replaces this one: on success the caller must use it and must not
    /// reuse this (now-released) instance. FolderVersions and the CheckpointSequence continue across
    /// the swap (same FileId, continued sequences), so a sync client sees a coherent continuation
    /// (docs/Sync.md). Crash-safe: a failure or crash before the swap leaves the complete old file at
    /// the path, reopenable with <see cref="Open"/> (the next open discards any leftover side file).</para>
    /// </summary>
    /// <param name="reEncryptProvider">
    /// When supplied, the copy pass re-encrypts every DEK-encrypted block at the provider's active
    /// epoch with a fresh nonce (docs/Compaction.md Section 4, US-EMDB-90-5); the caller owns the
    /// provider and it must hold a DEK for every epoch the live blocks reference. Omit for a verbatim copy.
    /// </param>
    /// <param name="encryptionBootstrap">
    /// Encryption bootstrap used to open the clean snapshot compaction reads (spec Section 10.2 step 2);
    /// omit for a plaintext file or a Default-policy encrypted file (whose structural blocks are plaintext).
    /// </param>
    /// <param name="reopenOptions">Options for reopening the compacted file (e.g. the password of an encrypted file); omit for plaintext.</param>
    /// <param name="keyStoreKek">
    /// The 32-byte KEK sealing the file's KeyStore, supplied alongside <paramref name="reEncryptProvider"/>
    /// to prune the now-unreferenced DEK epochs from the compacted file's KeyStore (docs/Compaction.md
    /// Section 4, US-EMDB-90-6). Omit it to copy the KeyStore verbatim and prune nothing.
    /// </param>
    /// <returns>A fresh manager over the compacted file on success; a failure (with the intact original file left in place) otherwise.</returns>
    public Result<EmailManager> Compact(
        EpochDekProvider? reEncryptProvider = null,
        IEncryptionBootstrap? encryptionBootstrap = null,
        EmailManagerOpenOptions? reopenOptions = null,
        ReadOnlyMemory<byte> keyStoreKek = default)
    {
        if (_disposed)
            return Result<EmailManager>.Failure("Compact called on a disposed EmailManager.");
        if (_closed)
            return Result<EmailManager>.Failure("Compact called on a closed EmailManager.");

        string path = Path;

        // 1. Make every buffered mutation durable so compaction copies a complete committed snapshot.
        if (_openState is not null)
        {
            var commit = WriteCommitCheckpoint(_openState);
            if (commit.IsFailure)
                return Result<EmailManager>.Failure($"Compact aborted committing pending state: {commit.Error}");
        }

        // 2. Release this manager's exclusive handle so the swap can rename the side file over the file.
        _closed = true;
        ReleaseResources();

        // 3. Run the full side-file compaction over the now-closed file.
        var begin = Compactor.Begin(path, encryptionBootstrap);
        if (begin.IsFailure)
            return Result<EmailManager>.Failure(
                $"Compact could not start over '{path}' (the original file is intact and reopenable): {begin.Error}");
        using (var compactor = begin.Value)
        {
            var copy = reEncryptProvider is null
                ? compactor.CopyLiveBlocks()
                : compactor.CopyLiveBlocks(reEncrypt: true, reEncryptProvider, keyStoreKek);
            if (copy.IsFailure)
                return Result<EmailManager>.Failure(
                    $"Compact could not copy live blocks (the original file is intact): {copy.Error}");

            var rebuild = compactor.RebuildLocationIndexAndWriteCheckpoint();
            if (rebuild.IsFailure)
                return Result<EmailManager>.Failure(
                    $"Compact could not rebuild the index/Checkpoint (the original file is intact): {rebuild.Error}");

            var swap = compactor.FinalizeAndSwap();
            if (swap.IsFailure)
                return Result<EmailManager>.Failure(
                    $"Compact could not swap in the compacted file (the original file is intact): {swap.Error}");
        }

        // 4. Reopen the compacted file and hand back a fresh, ready manager.
        return Open(path, reopenOptions);
    }

    /// <summary>
    /// The shared group-commit protocol over the opened composition (US-EMDB-85-6), driving both
    /// <see cref="Commit"/> and <see cref="Close"/>: flush the PrimaryEmail + Date index roots, fold
    /// this session's appends into the location index, write a Checkpoint naming the fresh roots, and
    /// repoint the CleanShutdown = 1 superblock's LastCheckpoint hint. On success the runtime map is
    /// cleared (its blocks now resolve through the durable location index, spec Section 7) and the
    /// session's commit state advances so it can keep writing. Resource release is the caller's.
    /// </summary>
    private Result WriteCommitCheckpoint(OpenState openState)
    {
        if (BlockManager.IsPoisoned)
            return Result.Failure(
                "Commit aborted: the block stream is poisoned by an earlier failed write/fsync; the file " +
                "is left for crash recovery, with the previous Checkpoint authoritative (spec Section 10.3).");

        var index = openState.LocationIndex;

        // 1. Flush the WAL-buffered PrimaryEmail index (nodes → fsync → IndexRoot block, BTree_Index.md
        //    Section 4). Its CommittedIndexRootLocation names the durable IndexRoot block the Checkpoint
        //    points at; null when the index is still empty (only duplicates, or no emails yet).
        var primaryFlush = _primaryIndex!.Flush();
        if (primaryFlush.IsFailure)
            return Result.Failure($"Commit aborted flushing the primary index: {primaryFlush.Error}");
        CheckpointRootPointer primaryPointer = _primaryIndex.CommittedIndexRootLocation is { } primaryLoc
            ? CheckpointRootPointer.Create((byte[])primaryLoc.BlockId.Clone(), primaryLoc.Offset)
            : CheckpointRootPointer.None;

        // 2. Persist the Date index root descriptor when it advanced this batch (its nodes were already
        //    appended by AddEmail). The IndexRoot block carries the shape the reopen rebuilds the tree
        //    from; its Sequence increments monotonically per index (BTree_Index.md Section 6).
        var secondaryList = new List<CheckpointSecondaryIndex>();
        if (_dateIndex!.Root is not null)
        {
            byte[] dateRootBlockId = _dateIndex.Root.RootRef.Reference;
            bool changed = _dateIndexRoot is null
                || !_dateIndexRoot.RootBlockId.AsSpan().SequenceEqual(dateRootBlockId);
            if (changed)
            {
                var candidate = _dateIndexRoot is null
                    ? IndexRoot.CreateInitial(
                        BTreeIndexKind.Date, (byte[])dateRootBlockId.Clone(),
                        _dateIndex.Root.EntryCount, _dateIndex.Root.Height, (byte[])_dateIndex.Root.RootHash.Clone())
                    : _dateIndexRoot.NextVersion(
                        (byte[])dateRootBlockId.Clone(),
                        _dateIndex.Root.EntryCount, _dateIndex.Root.Height, (byte[])_dateIndex.Root.RootHash.Clone());
                var written = _dateIndexRootStore!.WriteIndexRoot(candidate);
                if (written.IsFailure)
                    return Result.Failure($"Commit aborted writing the Date IndexRoot block: {written.Error}");
                _dateIndexRoot = candidate;
                _dateIndexRootLocation = written.Value;
            }
            secondaryList.Add(CheckpointSecondaryIndex.Create(
                BTreeIndexKind.Date,
                (byte[])_dateIndexRootLocation!.BlockId.Clone(), _dateIndexRootLocation.Offset));
        }

        // 2b. Flush the FTS address trigram index (US-EMDB-91-7): write the live posting set as a fresh
        //     segment + FTSSearchRoot when it changed this batch (a clean index re-registers its existing
        //     root), then register the root under IndexKind 3 so the index reopens from the Checkpoint.
        //     Done BEFORE the runtime-map snapshot below so the segment blocks fold into the location index.
        var ftsFlush = _ftsIndex!.Flush(_ftsBlockStore!);
        if (ftsFlush.IsFailure)
            return Result.Failure($"Commit aborted flushing the FTS index: {ftsFlush.Error}");
        if (ftsFlush.Value is { } ftsRootLoc)
            secondaryList.Add(FtsSearchRoot.ToCheckpointSecondaryIndex(
                (byte[])ftsRootLoc.BlockId.Clone(), ftsRootLoc.Offset));

        // 2c. Flush the per-folder Bloom filters (US-EMDB-97-5): write the whole filter set as a fresh
        //     always-encrypted catalog block when it changed this batch (a clean index re-registers its
        //     existing catalog), then register it under IndexKind 4 so the filters reopen from the
        //     Checkpoint. Done BEFORE the runtime-map snapshot so the catalog block folds into the location
        //     index.
        var bloomFlush = _bloomIndex!.Flush(_bloomStore!);
        if (bloomFlush.IsFailure)
            return Result.Failure($"Commit aborted flushing the Bloom filters: {bloomFlush.Error}");
        if (bloomFlush.Value is { } bloomRootLoc)
            secondaryList.Add(BloomFilterCatalog.ToCheckpointSecondaryIndex(
                (byte[])bloomRootLoc.BlockId.Clone(), bloomRootLoc.Offset));

        CheckpointSecondaryIndex[] secondaries = secondaryList.ToArray();

        // 3. Fold every block appended since the last commit (the runtime map) into the durable location
        //    index in one COW batch — the index/IndexRoot/email/folder/WAL blocks all become resolvable
        //    by BlockId after reopen. Snapshot BEFORE PutBatch so the offset-addressed location nodes it
        //    appends are not themselves folded; the trailing Clear() then drops them so a later commit
        //    never re-folds this batch's location nodes.
        var appended = openState.RuntimeMap.SnapshotOrderedByOffset();
        if (appended.Count > 0)
        {
            var folded = index.PutBatch(appended);
            if (folded.IsFailure)
                return Result.Failure(
                    $"Commit aborted folding this batch's {appended.Count} append(s) into the location index: {folded.Error}");
            foreach (var block in appended)
                _accountant!.RecordAppend(block.TotalBlockLength);
        }

        // 4. Location index root pointer (offset + block-header BlockId), or None for an empty index.
        CheckpointRootPointer locationPointer;
        if (index.Root is null)
        {
            locationPointer = CheckpointRootPointer.None;
        }
        else
        {
            long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root.RootRef.Reference);
            var rootBlock = BlockManager.Read(rootOffset);
            if (rootBlock.IsFailure)
                return Result.Failure(
                    $"Commit aborted reading the location index root node at offset {rootOffset}: {rootBlock.Error}");
            locationPointer = CheckpointRootPointer.Create(
                (byte[])rootBlock.Value.Header.BlockId.Clone(), rootOffset);
        }

        // 5. Checkpoint naming the fresh primary/date/location roots plus the structural roots carried
        //    forward from the loaded Checkpoint. LiveBlockCount MUST equal the location index entry count
        //    (the reopen reconstructs the tree with it, CleanOpener).
        var resolved = openState.Checkpoint;
        var writer = new CheckpointWriter(BlockManager, FileId, LastCheckpointSequence, LastCheckpoint);
        var checkpoint = writer.WriteCheckpoint(new CheckpointContents
        {
            FolderTreeRoot = ToPointer(resolved.FolderTreeRoot),
            PrimaryIndexRoot = primaryPointer,
            LocationIndexRoot = locationPointer,
            MetadataRoot = ToPointer(resolved.MetadataRoot),
            KeyStoreRoot = ToPointer(resolved.KeyStoreRoot),
            SecondaryIndexes = secondaries,
            LiveBlockCount = index.Count,
            LiveByteCount = _accountant!.LiveByteCount,
            DeadByteCount = _accountant.DeadByteCount,
        });
        if (checkpoint.IsFailure)
            return Result.Failure($"Commit aborted writing the Checkpoint: {checkpoint.Error}");

        var finalPointer = writer.LastCheckpointPointer;

        // 6. CleanShutdown = 1 superblock write repointing LastCheckpoint at the new Checkpoint, so a
        //    clean reopen (including after a crash) resolves this commit. The dual-slot manager fsyncs
        //    each write; a torn slot never destroys the previous good one.
        using (var superblockManager = new SuperblockManager(_stream, ownsStream: false))
        {
            var loaded = superblockManager.Load();
            if (loaded.IsFailure)
                return Result.Failure($"Commit aborted loading the superblock to finalize it: {loaded.Error}");

            var updated = loaded.Value.Clone();
            updated.CleanShutdown = 1;
            updated.LastCheckpointBlockId = (byte[])finalPointer.BlockId.Clone();
            updated.LastCheckpointOffset = finalPointer.Offset;

            var written = superblockManager.Write(updated);
            if (written.IsFailure)
                return Result.Failure($"Commit aborted writing the CleanShutdown superblock: {written.Error}");
        }

        // 7. Advance the session's commit point and forget the now-durable pre-checkpoint blocks: they
        //    resolve through the BlockLocationIndex from here on (spec Section 7). Later WAL entries and
        //    the next commit fence to / resume from this new Checkpoint.
        LastCheckpoint = finalPointer;
        LastCheckpointSequence = writer.LastSequence!.Value;
        openState.RuntimeMap.Clear();
        _uncommittedAdds = 0;
        return Result.Success();
    }

    private static CheckpointRootPointer ToPointer(ResolvedRoot? root) =>
        root is null
            ? CheckpointRootPointer.None
            : CheckpointRootPointer.Create((byte[])root.BlockId.Clone(), root.Offset);

    /// <summary>
    /// Re-applies the WAL ops crash recovery replayed at the adopted Checkpoint (spec Section 13,
    /// Section 10.4) into this session's live PrimaryEmail index, so a following <see cref="Commit"/>
    /// folds them into a durable Checkpoint. Only the PrimaryEmail bindings the AddEmail WAL path logs
    /// are re-applied — an Insert re-buffers the EmailHashedID → ContentBlockId upsert and a Delete
    /// re-buffers its removal; a FolderOp (never emitted by AddEmail) is a no-op. Runs on the single
    /// open thread before the manager is handed to the caller.
    /// </summary>
    private Result ApplyRecoveredWalOps(IReadOnlyList<RecoveredWalOp> ops)
    {
        if (_primaryIndex is null)
            return Result.Failure("Recovered-WAL re-apply requires an opened primary index.");
        foreach (var op in ops)
        {
            Result applied = op.Op switch
            {
                WalOpKind.Insert => _primaryIndex.Upsert(op.Key, op.BlockId),
                WalOpKind.Delete => _primaryIndex.Delete(op.Key),
                WalOpKind.FolderOp => Result.Success(),
                _ => Result.Failure($"recovered WAL op {op.Op} is not supported."),
            };
            if (applied.IsFailure)
                return Result.Failure($"re-applying recovered {op.Op} failed: {applied.Error}");
        }
        return Result.Success();
    }

    /// <summary>One WAL op crash recovery replayed, captured by <see cref="RecoveryReplaySink"/> for re-apply.</summary>
    private readonly record struct RecoveredWalOp(WalOpKind Op, byte[] Key, byte[] BlockId);

    /// <summary>
    /// The <see cref="IWalReplaySink"/> the dirty-open recovery feeds the uncommitted WAL into during
    /// <see cref="Open"/>: it records each op (in replay order) so the opened manager can re-apply them
    /// to its live indexes and commit a fresh Checkpoint (spec Section 13, Section 10.4).
    /// </summary>
    private sealed class RecoveryReplaySink : IWalReplaySink
    {
        public readonly List<RecoveredWalOp> Ops = new();

        public Result ApplyInsert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId)
        {
            Ops.Add(new RecoveredWalOp(WalOpKind.Insert, key.ToArray(), blockId.ToArray()));
            return Result.Success();
        }

        public Result ApplyDelete(ReadOnlySpan<byte> key)
        {
            Ops.Add(new RecoveredWalOp(WalOpKind.Delete, key.ToArray(), Array.Empty<byte>()));
            return Result.Success();
        }

        public Result ApplyFolderOp(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockId, ReadOnlySpan<byte> aux)
        {
            Ops.Add(new RecoveredWalOp(WalOpKind.FolderOp, key.ToArray(), blockId.ToArray()));
            return Result.Success();
        }
    }

    /// <summary>
    /// Releases the file (the writer lock) and zeroizes any loaded key material. This is the raw
    /// resource release <see cref="Close"/> and <see cref="Dispose"/> share. A <see cref="Dispose"/>
    /// that was NOT preceded by <see cref="Close"/> is a dirty close — it does not write the final
    /// Checkpoint or touch CleanShutdown, so the file's on-disk state stands and any dirty flag drives
    /// recovery on the next <see cref="Open"/>. Create already left a just-created file clean and
    /// reopenable, so disposing a just-created manager still yields a file that reopens cleanly.
    /// </summary>
    public void Dispose() => ReleaseResources();

    private void ReleaseResources()
    {
        if (_disposed)
            return;
        _disposed = true;
        _provider?.Dispose();
        BlockManager.Dispose();
        _stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
