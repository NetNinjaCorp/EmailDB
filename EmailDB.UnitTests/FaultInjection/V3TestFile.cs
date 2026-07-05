using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests.FaultInjection;

/// <summary>
/// Builds a realistic, CLEAN v3 file on disk — filler data blocks, a committed
/// <see cref="BlockLocationIndex"/>, a folder root, one or more chained
/// <see cref="Checkpoint"/>s, optional WAL blocks fenced to the last Checkpoint, and
/// a dual-slot superblock — and returns a <see cref="V3TestFile"/> descriptor over
/// it. This generalizes the near-identical <c>BuildFile</c> helpers that
/// DisasterOpenTests and DirtyOpenTests each grew privately, giving the Section 13
/// contract-table suite one builder to generate every fixture it corrupts. Pair it
/// with <see cref="FaultInjector"/> to byte-flip / truncate / transplant the result.
/// </summary>
internal sealed class V3TestFileBuilder
{
    private int _fillerBlocks = 16;
    private int _fillerSize = 512;
    private int _checkpointCount = 1;
    private byte _cleanShutdown = 1;
    private bool _encryptedBlock;
    private int? _hintCheckpointIndex;
    private IReadOnlyList<(ulong Seq, WalEntry[] Entries)>? _wal;
    private byte[] _fileId = Enumerable.Range(0, 16).Select(i => (byte)(0x70 + i)).ToArray();

    /// <summary>Number of leading EmailContent data blocks (each filled with its index byte).</summary>
    public V3TestFileBuilder WithFillerBlocks(int count, int size = 512)
    { _fillerBlocks = count; _fillerSize = size; return this; }

    /// <summary>Write <paramref name="count"/> chained Checkpoints (a real PreviousCheckpoint chain).</summary>
    public V3TestFileBuilder WithCheckpoints(int count)
    { _checkpointCount = count; return this; }

    /// <summary>Set the superblock CleanShutdown flag (1 = clean fast path, 0 = dirty recovery required).</summary>
    public V3TestFileBuilder WithCleanShutdown(byte value)
    { _cleanShutdown = value; return this; }

    /// <summary>Write one encrypted block (its DEK lives only in the superblock — lost if the superblock is destroyed).</summary>
    public V3TestFileBuilder WithEncryptedBlock(bool encrypted = true)
    { _encryptedBlock = encrypted; return this; }

    /// <summary>Point the superblock's LastCheckpoint hint at Checkpoint <paramref name="index"/> (default: the newest).</summary>
    public V3TestFileBuilder WithCheckpointHint(int index)
    { _hintCheckpointIndex = index; return this; }

    /// <summary>Append WAL blocks fenced to the last Checkpoint.</summary>
    public V3TestFileBuilder WithWal(IReadOnlyList<(ulong Seq, WalEntry[] Entries)> wal)
    { _wal = wal; return this; }

    public V3TestFileBuilder WithFileId(byte[] fileId)
    { _fileId = fileId; return this; }

    /// <summary>Generates the file at <paramref name="path"/> and returns its descriptor.</summary>
    public V3TestFile Build(string path)
    {
        var dataBlocks = new List<BlockLocation>();
        var checkpointPointers = new List<CheckpointRootPointer>();
        CheckpointRootPointer folderPointer = CheckpointRootPointer.None;
        CheckpointRootPointer locationPointer;

        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            var filler = new byte[_fillerSize];
            for (int i = 0; i < _fillerBlocks; i++)
            {
                Array.Fill(filler, (byte)(i & 0xFF));
                bool encrypt = _encryptedBlock && i == _fillerBlocks / 2;
                var appended = encrypt
                    ? manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, filler,
                        CompressionAlgorithm.None, encrypted: true, keyEpoch: 1)
                    : manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, filler);
                Ok(appended);
                dataBlocks.Add(appended.Value);
            }

            var folder = manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[64]);
            Ok(folder);
            folderPointer = CheckpointRootPointer.Create(folder.Value.BlockId, folder.Value.Offset);

            var store = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(store, maxLeafEntries: 4, maxInternalKeys: 3);
            Ok(index.PutBatch(dataBlocks));
            Ok(manager.Flush());

            long locationRootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root!.RootRef.Reference);
            var rootBlockId = runtimeMap.SnapshotOrderedByOffset()
                .First(b => b.Offset == locationRootOffset).BlockId;
            locationPointer = CheckpointRootPointer.Create(rootBlockId, locationRootOffset);

            var writer = new CheckpointWriter(manager, _fileId);
            for (int c = 0; c < _checkpointCount; c++)
            {
                Ok(writer.WriteCheckpoint(new CheckpointContents
                {
                    FolderTreeRoot = folderPointer,
                    PrimaryIndexRoot = CheckpointRootPointer.None,
                    LocationIndexRoot = locationPointer,
                    MetadataRoot = CheckpointRootPointer.None,
                    KeyStoreRoot = CheckpointRootPointer.None,
                    SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                    LiveBlockCount = dataBlocks.Count,
                    LiveByteCount = 4096,
                    DeadByteCount = 0,
                }));
                checkpointPointers.Add(writer.LastCheckpointPointer);
            }

            if (_wal is not null)
            {
                var fence = checkpointPointers[^1].BlockId;
                foreach (var (seq, entries) in _wal)
                {
                    var block = new WalBlock
                    {
                        WalSequence = seq,
                        CheckpointBlockId = (byte[])fence.Clone(),
                        Entries = entries,
                    };
                    Ok(manager.Append(BlockType.WAL, PayloadEncoding.Custom, WalSerializer.Serialize(block)));
                }
                Ok(manager.Flush());
            }
        }

        int hintIndex = _hintCheckpointIndex ?? (_checkpointCount - 1);
        var hint = checkpointPointers[hintIndex];
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var sbManager = new SuperblockManager(stream, ownsStream: true))
        {
            Superblock MakeSuperblock() => new()
            {
                FileId = (byte[])_fileId.Clone(),
                CleanShutdown = _cleanShutdown,
                LastCheckpointBlockId = (byte[])hint.BlockId.Clone(),
                LastCheckpointOffset = hint.Offset,
            };
            // Fill BOTH slots so a test can corrupt one and still leave the other valid.
            Ok(sbManager.Write(MakeSuperblock()));
            Ok(sbManager.Write(MakeSuperblock()));
        }

        return new V3TestFile(path, _fileId, dataBlocks, checkpointPointers, folderPointer, locationPointer);
    }

    private static void Ok(Result r)
    { if (r.IsFailure) throw new InvalidOperationException($"V3 test file build step failed: {r.Error}"); }

    private static void Ok<T>(Result<T> r)
    { if (r.IsFailure) throw new InvalidOperationException($"V3 test file build step failed: {r.Error}"); }
}

/// <summary>Descriptor of a generated v3 file: the on-disk layout facts a Section 13 fault-injection test needs to target damage precisely.</summary>
internal sealed record V3TestFile(
    string Path,
    byte[] FileId,
    IReadOnlyList<BlockLocation> DataBlocks,
    IReadOnlyList<CheckpointRootPointer> Checkpoints,
    CheckpointRootPointer FolderPointer,
    CheckpointRootPointer LocationPointer)
{
    /// <summary>The newest (highest-sequence) Checkpoint written.</summary>
    public CheckpointRootPointer NewestCheckpoint => Checkpoints[^1];

    /// <summary>An injector bound to this file.</summary>
    public FaultInjector Injector => new(Path);

    /// <summary>Opens a read/write stream over the file.</summary>
    public FileStream Open() =>
        new(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
}
