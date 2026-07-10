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
    private readonly FolderPageStore? _folderPageStore;
    private readonly FolderPageDirectoryStore? _folderDirectoryStore;
    private readonly FolderDeltaLogStore? _folderDeltaStore;
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
        ulong lastCheckpointSequence)
    {
        Path = path;
        _stream = stream;
        BlockManager = openState.BlockManager;
        _runtimeMap = openState.RuntimeMap;
        _superblock = openState.Superblock;
        _provider = provider;
        _openState = openState;
        _folderPageStore = folderPageStore;
        _folderDirectoryStore = folderDirectoryStore;
        _folderDeltaStore = folderDeltaStore;
        LastCheckpoint = lastCheckpoint;
        LastCheckpointSequence = lastCheckpointSequence;
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

    /// <summary>ULID+offset pointer to the last committed Checkpoint (the first one, immediately after create).</summary>
    public CheckpointRootPointer LastCheckpoint { get; }

    /// <summary>Sequence of the last committed Checkpoint (0, immediately after create).</summary>
    public ulong LastCheckpointSequence { get; }

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
                    //    scan + WAL replay to the last durable commit and heal the superblock.
                    //    No fresh-Checkpoint sink is supplied — the EmailManager write path (which
                    //    produces WAL) is a later story, so recovery here heals a file with no
                    //    uncommitted WAL back to its last commit (spec Section 10.2 step 4).
                    var dirty = DirtyOpener.Open(stream, encryptionBootstrap: bootstrap);
                    if (dirty.IsFailure)
                        return Result<EmailManager>.Failure($"Open failed during recovery: {dirty.Error}");
                    if (!dirty.Value.HealedClean || dirty.Value.State is null)
                        return Result<EmailManager>.Failure(
                            "Open failed: recovery replayed uncommitted WAL that could not be committed to a clean point " +
                            $"(the next open re-runs recovery). {dirty.Value.Detail}");
                    state = dirty.Value.State;
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

            committed = true;
            return Result<EmailManager>.Success(new EmailManager(
                path, stream, state, provider,
                folderPageStore, folderDirectoryStore, folderDeltaStore,
                lastCheckpoint, lastCheckpointSequence));
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

        var result = WriteFinalCheckpointAndCleanSuperblock(_openState);
        _closed = true;
        ReleaseResources();
        return result;
    }

    /// <summary>
    /// The durable Close protocol steps 1-4 over the opened composition: fold this session's appends
    /// into the location index, write the final Checkpoint reflecting the current roots, and write the
    /// CleanShutdown = 1 superblock repointing its LastCheckpoint hint. Resource release is the
    /// caller's (<see cref="Close"/>) so the writer lock is freed on both success and failure.
    /// </summary>
    private Result WriteFinalCheckpointAndCleanSuperblock(OpenState openState)
    {
        if (BlockManager.IsPoisoned)
            return Result.Failure(
                "Close aborted: the block stream is poisoned by an earlier failed write/fsync; the file " +
                "is left for crash recovery, with the previous Checkpoint authoritative (spec Section 10.3).");

        var index = openState.LocationIndex;

        // 1. Flush buffered index state: fold every block appended this session (the runtime map —
        //    empty on a clean open, populated by this session's appends) into the durable location
        //    index in one COW batch. Snapshot BEFORE the batch so the new B+-tree nodes it appends are
        //    not themselves re-folded (offset-addressed nodes are not indexed by BlockId).
        var appended = openState.RuntimeMap.SnapshotOrderedByOffset();
        long addedLiveBytes = 0;
        if (appended.Count > 0)
        {
            var folded = index.PutBatch(appended);
            if (folded.IsFailure)
                return Result.Failure(
                    $"Close aborted folding this session's {appended.Count} append(s) into the location index: {folded.Error}");
            foreach (var block in appended)
                addedLiveBytes += block.TotalBlockLength;
        }

        // 2. Compile pending folder deltas if warranted — a documented no-op here (see the Close
        //    summary): the current composition tracks no open folder with a pending delta count.

        // 3. Final Checkpoint reflecting the current roots. The location root is the (possibly
        //    advanced) location index root node, addressed by its offset + block-header BlockId; every
        //    other root is carried forward from the loaded Checkpoint (healing any stale hint to its
        //    resolved location). LiveBlockCount MUST equal the index entry count — the reopen
        //    reconstructs the tree with it (CleanOpener).
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
                    $"Close aborted reading the location index root node at offset {rootOffset}: {rootBlock.Error}");
            locationPointer = CheckpointRootPointer.Create(
                (byte[])rootBlock.Value.Header.BlockId.Clone(), rootOffset);
        }

        var resolved = openState.Checkpoint;
        var secondaries = new CheckpointSecondaryIndex[resolved.SecondaryIndexes.Count];
        for (int i = 0; i < secondaries.Length; i++)
        {
            var secondary = resolved.SecondaryIndexes[i];
            secondaries[i] = CheckpointSecondaryIndex.Create(
                secondary.IndexKind, (byte[])secondary.Root.BlockId.Clone(), secondary.Root.Offset);
        }

        var writer = new CheckpointWriter(BlockManager, FileId, LastCheckpointSequence, LastCheckpoint);
        var checkpoint = writer.WriteCheckpoint(new CheckpointContents
        {
            FolderTreeRoot = ToPointer(resolved.FolderTreeRoot),
            PrimaryIndexRoot = ToPointer(resolved.PrimaryIndexRoot),
            LocationIndexRoot = locationPointer,
            MetadataRoot = ToPointer(resolved.MetadataRoot),
            KeyStoreRoot = ToPointer(resolved.KeyStoreRoot),
            SecondaryIndexes = secondaries,
            LiveBlockCount = index.Count,
            LiveByteCount = resolved.Checkpoint.LiveByteCount + addedLiveBytes,
            DeadByteCount = resolved.Checkpoint.DeadByteCount,
        });
        if (checkpoint.IsFailure)
            return Result.Failure($"Close aborted writing the final Checkpoint: {checkpoint.Error}");

        var finalPointer = writer.LastCheckpointPointer;

        // 4. CleanShutdown = 1 superblock write, repointing LastCheckpoint at the final Checkpoint.
        //    The dual-slot manager fsyncs each write; a torn slot never destroys the previous good one.
        using var superblockManager = new SuperblockManager(_stream, ownsStream: false);
        var loaded = superblockManager.Load();
        if (loaded.IsFailure)
            return Result.Failure($"Close aborted loading the superblock to finalize it: {loaded.Error}");

        var updated = loaded.Value.Clone();
        updated.CleanShutdown = 1;
        updated.LastCheckpointBlockId = (byte[])finalPointer.BlockId.Clone();
        updated.LastCheckpointOffset = finalPointer.Offset;

        var written = superblockManager.Write(updated);
        if (written.IsFailure)
            return Result.Failure($"Close aborted writing the CleanShutdown superblock: {written.Error}");

        return Result.Success();
    }

    private static CheckpointRootPointer ToPointer(ResolvedRoot? root) =>
        root is null
            ? CheckpointRootPointer.None
            : CheckpointRootPointer.Create((byte[])root.BlockId.Clone(), root.Offset);

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
