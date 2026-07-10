using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// v3 append-only block writer and verifying block reader
/// (EmailDB_FileFormat_Spec.md Sections 2, 4, 13). Replaces the v1
/// RawBlockManager read/write path for the v3 block stream.
///
/// Writing: blocks are only ever appended at end of file (never before
/// <see cref="FirstBlockOffset"/>, which defaults to 8192 so the dual-slot
/// superblock region is preserved). Each append mints a monotonic ULID
/// BlockId, serializes via <see cref="BlockSerializer"/>, and notifies the
/// optional runtime <see cref="IBlockOffsetMap"/>. The payload bytes passed
/// to <see cref="Append"/> are the final on-disk bytes — already compressed
/// and/or encrypted by higher layers per the spec write order
/// (Serialize → Compress → Encrypt → Checksum → Append).
///
/// Reading follows the spec Section 4 processing order exactly:
/// read → verify HeaderChecksum BEFORE trusting any header field →
/// validate PayloadLength against MaxPayloadLength and EOF BEFORE allocating →
/// verify PayloadChecksum → hand the verified on-disk payload to higher
/// layers for decrypt/decompress/deserialize. Corruption is reported as a
/// failed <see cref="Result{T}"/>, never by throwing (spec Section 13).
///
/// Durability: appends are buffered; <see cref="Flush"/> performs the fsync.
/// Both go through <see cref="DurableStream"/>, which enforces the spec's
/// fsync discipline (Section 10.3): flush-to-disk only (never a bare stream
/// flush), and a failed write or fsync poisons the handle — durability of
/// buffered data is unknowable after that, so further writes are refused and
/// fsync is never retried.
/// </summary>
public sealed class BlockManager : IDisposable
{
    /// <summary>
    /// Default offset of the first block: the block stream begins after the
    /// two 4096-byte superblock slots (spec Section 2).
    /// </summary>
    public const long DefaultFirstBlockOffset = SuperblockManager.SuperblockRegionSize;

    private readonly DurableStream _stream;
    private readonly UlidGenerator _ulidGenerator;
    private readonly IBlockOffsetMap? _offsetMap;

    /// <summary>
    /// Guards all access to the shared stream (its position is shared state).
    /// Reads of immutable blocks need no logical lock, but seeks on one
    /// FileStream must not interleave.
    /// </summary>
    private readonly object _streamLock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a manager over an open, readable, writable, seekable stream of
    /// an EmailDB file.
    /// </summary>
    /// <param name="stream">Stream over the EmailDB file, positioned anywhere.</param>
    /// <param name="maxPayloadLength">
    /// Sanity bound for PayloadLength, from the superblock
    /// (<see cref="Superblock.MaxPayloadLength"/>).
    /// </param>
    /// <param name="ulidGenerator">
    /// BlockId generator; a fresh monotonic generator is created when null.
    /// </param>
    /// <param name="offsetMap">
    /// Optional runtime BlockId-to-offset map notified on every append.
    /// </param>
    /// <param name="firstBlockOffset">
    /// Lowest file offset blocks may occupy. Defaults to 8192 (after the
    /// superblock region); tests over bare streams may pass 0.
    /// </param>
    /// <param name="ownsStream">When true, disposing the manager disposes the stream.</param>
    public BlockManager(
        FileStream stream,
        long maxPayloadLength = Superblock.DefaultMaxPayloadLength,
        UlidGenerator? ulidGenerator = null,
        IBlockOffsetMap? offsetMap = null,
        long firstBlockOffset = DefaultFirstBlockOffset,
        bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("Block stream must be readable, writable, and seekable.", nameof(stream));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadLength);
        ArgumentOutOfRangeException.ThrowIfNegative(firstBlockOffset);

        _stream = new DurableStream(stream, ownsStream);
        MaxPayloadLength = maxPayloadLength;
        _ulidGenerator = ulidGenerator ?? new UlidGenerator();
        _offsetMap = offsetMap;
        FirstBlockOffset = firstBlockOffset;
    }

    /// <summary>Sanity bound for PayloadLength (from the superblock).</summary>
    public long MaxPayloadLength { get; }

    /// <summary>Lowest file offset blocks may occupy (block stream start).</summary>
    public long FirstBlockOffset { get; }

    /// <summary>
    /// True after a failed write or fsync: durability of buffered data is
    /// unknowable, so the manager refuses further writes (spec Section 10.3).
    /// </summary>
    public bool IsPoisoned => _stream.IsPoisoned;

    /// <summary>Number of successful flush-to-disk (fsync) operations.</summary>
    public long FlushToDiskCount => _stream.FlushToDiskCount;

    /// <summary>
    /// Number of physical block reads served from the stream (each <see cref="Read"/>
    /// that actually seeks and reads a block's bytes; a torn-tail short-circuit before
    /// the seek does not count). <see cref="ReadDecompressed"/> funnels through
    /// <see cref="Read"/>, so it is included exactly once. Used as the "block reads"
    /// metric to prove the read path's cost scales with tree height (O(log n)), not
    /// with the number of stored emails.
    /// </summary>
    public long ReadCount => _readCount;
    private long _readCount;

    /// <summary>
    /// Optional test/diagnostic recorder of the byte ranges verified block reads touch. When
    /// non-null, each successful <see cref="Read"/> appends the <c>[Offset, Offset + Length)</c>
    /// file range it read (the full block: header through footer). This lets a test prove
    /// <em>positionally</em> exactly which bytes a read path touched — e.g. that
    /// <see cref="EmailManager.GetMetadata"/> never reads any byte of the Tier 3 content block,
    /// complementing the <see cref="ReadCount"/> count with byte-level evidence. Left null in
    /// production so reads allocate nothing.
    /// </summary>
    internal List<(long Offset, long Length)>? ReadRangeLog { get; set; }

    /// <summary>
    /// Appends one block at end of file (never before
    /// <see cref="FirstBlockOffset"/>): mints a monotonic ULID BlockId, writes
    /// header + header checksum + payload + payload checksum + footer, and
    /// notifies the runtime offset map. The write is buffered; call
    /// <see cref="Flush"/> at commit points to make it durable.
    /// </summary>
    /// <param name="type">Block type (spec Section 5).</param>
    /// <param name="encoding">Payload serialization format of the (decoded) payload.</param>
    /// <param name="payload">
    /// Final on-disk payload bytes — already compressed/encrypted as the
    /// header fields describe. May be empty (stores 16 zero checksum bytes).
    /// </param>
    /// <param name="compression">Compression applied to the payload bytes.</param>
    /// <param name="encrypted">Whether the payload bytes are encrypted (flags bit 0).</param>
    /// <param name="keyEpoch">DEK epoch; must be 0 when not encrypted (spec Section 4).</param>
    /// <param name="blockId">
    /// Pre-minted 16-byte BlockId to stamp, or null to mint a fresh monotonic ULID. The
    /// encryption layer must know the BlockId <em>before</em> appending — it is an AAD
    /// component (spec Section 9.3) — so it mints one via <see cref="MintBlockId"/>, encrypts
    /// against it, then appends the ciphertext under that same BlockId.
    /// </param>
    /// <returns>The minted BlockId, file offset, and total on-disk length.</returns>
    public Result<BlockLocation> Append(
        BlockType type,
        PayloadEncoding encoding,
        ReadOnlySpan<byte> payload,
        CompressionAlgorithm compression = CompressionAlgorithm.None,
        bool encrypted = false,
        ushort keyEpoch = 0,
        byte[]? blockId = null)
    {
        ThrowIfDisposed();

        if (payload.Length > MaxPayloadLength)
            return Result<BlockLocation>.Failure(
                $"Payload length {payload.Length} exceeds MaxPayloadLength {MaxPayloadLength}.");
        if (!encrypted && keyEpoch != 0)
            return Result<BlockLocation>.Failure(
                $"KeyEpoch must be 0 when the payload is not encrypted, got {keyEpoch}.");
        if (blockId is not null && blockId.Length != UlidGenerator.UlidSize)
            return Result<BlockLocation>.Failure(
                $"Supplied BlockId must be exactly {UlidGenerator.UlidSize} bytes, got {blockId.Length}.");

        lock (_streamLock)
        {
            if (_stream.IsPoisoned)
                return Result<BlockLocation>.Failure(
                    "Block stream is poisoned after a failed write/fsync; close the file and run crash recovery.");

            var header = new BlockHeader
            {
                Type = type,
                Encoding = encoding,
                Compression = compression,
                IsEncrypted = encrypted,
                KeyEpoch = keyEpoch,
                BlockId = blockId is null ? _ulidGenerator.Next() : (byte[])blockId.Clone(),
                PayloadLength = payload.Length,
            };

            var blockBytes = BlockSerializer.Serialize(header, payload);
            var offset = Math.Max(_stream.Length, FirstBlockOffset);

            // A torn append may leave garbage at EOF; appending after it would
            // bury the damage, so a failed write poisons the durable stream and
            // further writes are refused (spec Sections 10.3, 13).
            var written = _stream.WriteAt(offset, blockBytes);
            if (written.IsFailure)
                return Result<BlockLocation>.Failure(
                    $"Block append failed at offset {offset}: {written.Error}");

            _offsetMap?.OnBlockAppended(header.Clone(), offset, blockBytes.Length);

            return Result<BlockLocation>.Success(new BlockLocation
            {
                BlockId = (byte[])header.BlockId.Clone(),
                Offset = offset,
                TotalBlockLength = blockBytes.Length,
            });
        }
    }

    /// <summary>
    /// Mints the next monotonic ULID BlockId from this manager's generator without appending
    /// anything. The encryption layer needs the BlockId before it can encrypt (it is an AAD
    /// component, spec Section 9.3): it mints here, encrypts against the returned id, then calls
    /// <see cref="Append"/> passing that same id as <c>blockId</c>. Drawing from the manager's
    /// own generator keeps encrypted and plaintext appends on one monotonic ULID stream.
    /// </summary>
    public byte[] MintBlockId()
    {
        ThrowIfDisposed();
        lock (_streamLock)
            return _ulidGenerator.Next();
    }

    /// <summary>
    /// Compressing append: runs the spec write-order compression step
    /// (Serialize → <b>Compress</b> → Encrypt → Checksum → Append, Section 4.4)
    /// on a serialized plaintext payload via <see cref="BlockCompressor"/>,
    /// then appends the compressed bytes with the compression byte recorded in
    /// the header. Encryption is a higher layer between compress and append,
    /// so this path writes unencrypted blocks; callers doing encryption
    /// compress explicitly, encrypt, and use <see cref="Append"/>.
    /// </summary>
    /// <param name="type">Block type (spec Section 5).</param>
    /// <param name="encoding">Payload serialization format of the (decoded) payload.</param>
    /// <param name="payload">Serialized, uncompressed payload bytes (may be empty).</param>
    /// <param name="compression">Compression algorithm to apply (spec Section 4.4).</param>
    /// <returns>The minted BlockId, file offset, and total on-disk length.</returns>
    public Result<BlockLocation> AppendCompressed(
        BlockType type,
        PayloadEncoding encoding,
        ReadOnlySpan<byte> payload,
        CompressionAlgorithm compression)
    {
        ThrowIfDisposed();

        var compressed = BlockCompressor.Compress(payload, compression);
        if (compressed.IsFailure)
            return Result<BlockLocation>.Failure(compressed.Error);

        return Append(type, encoding, compressed.Value, compression);
    }

    /// <summary>
    /// Decompressing read: <see cref="Read"/> plus the spec read-order
    /// decompression step (… → Decrypt → <b>Decompress</b> → Deserialize,
    /// Section 4), with the bomb guard capping decompressed output at
    /// <see cref="MaxPayloadLength"/> × <see cref="BlockCompressor.BombGuardMultiplier"/>
    /// (spec Section 4.4). The returned block carries the decompressed payload
    /// bytes; its header still describes the on-disk block (compression byte
    /// included). Encrypted blocks fail here — decryption is a higher layer
    /// that must run before decompression, so such callers use
    /// <see cref="Read"/>, decrypt, and decompress explicitly via
    /// <see cref="BlockCompressor"/>.
    /// </summary>
    /// <param name="offset">File offset of the block's first header byte.</param>
    public Result<Block> ReadDecompressed(long offset)
    {
        var blockResult = Read(offset);
        if (blockResult.IsFailure)
            return blockResult;

        var block = blockResult.Value;
        if (block.Header.IsEncrypted)
            return Result<Block>.Failure(
                $"Block at offset {offset} is encrypted; decrypt before decompressing (read order: Decrypt → Decompress, spec Section 4).");
        if (block.Header.Compression == CompressionAlgorithm.None)
            return blockResult;

        var decompressed = BlockCompressor.Decompress(
            block.Payload, block.Header.Compression, MaxPayloadLength);
        if (decompressed.IsFailure)
        {
            // A bomb guard trip / corrupt frame is Section 13 payload corruption. The
            // compressor works on a bare buffer, so re-stamp the typed error with this
            // block's offset and BlockId before surfacing it.
            if (decompressed.VerificationError is CorruptionError corruption)
                return new CorruptionError(
                    $"Block at offset {offset}: {corruption.Message}",
                    corruption.Cause, offset, block.Header.BlockId,
                    innerException: corruption.InnerException).ToResult<Block>();
            return Result<Block>.Failure(
                $"Block at offset {offset}: {decompressed.Error}");
        }

        return Result<Block>.Success(new Block
        {
            Header = block.Header,
            Payload = decompressed.Value,
        });
    }

    /// <summary>
    /// Reads and fully verifies the block starting at <paramref name="offset"/>,
    /// in spec Section 4 processing order: the 64 header bytes are read and the
    /// header checksum verified BEFORE any header field is trusted; PayloadLength
    /// is validated against <see cref="MaxPayloadLength"/> and EOF BEFORE any
    /// payload-sized allocation; then the payload checksum and footer are
    /// verified. The returned <see cref="Block"/> carries the on-disk payload
    /// bytes — decrypt/decompress/deserialize happens in higher layers.
    /// Corruption yields a failed result, never an exception (spec Section 13).
    /// </summary>
    /// <param name="offset">File offset of the block's first header byte.</param>
    public Result<Block> Read(long offset)
    {
        ThrowIfDisposed();

        if (offset < FirstBlockOffset)
            return Result<Block>.Failure(
                $"Block offset {offset} lies before the block stream start {FirstBlockOffset}.");

        lock (_streamLock)
        {
            long fileLength;
            Span<byte> headerBytes = stackalloc byte[BlockSerializer.SerializedHeaderSize];
            try
            {
                fileLength = _stream.Length;
                if (offset + BlockSerializer.SerializedHeaderSize > fileLength)
                    // The 64 header bytes do not fit before EOF: a torn final append
                    // (spec Section 13). Nothing to allocate from an unread length.
                    return new CorruptionError(
                        $"Block header at offset {offset} extends past EOF (file length {fileLength}); torn or truncated append.",
                        CorruptionCause.TornTail, offset,
                        // Everything from the torn block's start to EOF is unattributable.
                        damagedRange: new DamagedRange(offset, fileLength, "torn tail (header past EOF)"))
                        .ToResult<Block>();

                _readCount++;
                _stream.Seek(offset);
                _stream.ReadExactly(headerBytes);
            }
            catch (IOException ex)
            {
                return Result<Block>.Failure($"Block header read at offset {offset} failed: {ex.Message}");
            }

            // Verifies the header checksum before trusting any field, then validates
            // PayloadLength <= MaxPayloadLength — no payload-sized allocation yet. A
            // failure carries the typed Section 13 corruption cause; thread the file
            // offset so the error names where the damage is.
            var headerResult = BlockSerializer.DeserializeHeader(headerBytes, MaxPayloadLength, offset);
            if (headerResult.IsFailure)
                return headerResult.VerificationError is not null
                    ? Result<Block>.Failure(headerResult.VerificationError)
                    : Result<Block>.Failure(headerResult.Error);

            var totalLength = BlockSerializer.GetTotalBlockLength(headerResult.Value.PayloadLength);
            if (offset + totalLength > fileLength)
                // A checksum-valid header whose declared length runs past EOF: an
                // insane length / torn append (spec Section 13). Never allocate on it.
                return new CorruptionError(
                    $"Block at offset {offset} with PayloadLength {headerResult.Value.PayloadLength} extends past EOF (file length {fileLength}); corrupt header or torn append.",
                    CorruptionCause.InsaneLength, offset).ToResult<Block>();
            if (totalLength > int.MaxValue)
                return Result<Block>.Failure(
                    $"Block at offset {offset} is too large to buffer ({totalLength} bytes).");

            // Length is verified sane: safe to allocate.
            var blockBytes = new byte[totalLength];
            headerBytes.CopyTo(blockBytes);
            try
            {
                _stream.ReadExactly(blockBytes, BlockSerializer.SerializedHeaderSize,
                    (int)totalLength - BlockSerializer.SerializedHeaderSize);
            }
            catch (IOException ex)
            {
                return Result<Block>.Failure($"Block payload read at offset {offset} failed: {ex.Message}");
            }

            // Byte-level accounting hook (test/diagnostic only): record the full file range this
            // read consumed so a test can prove which bytes a read path did — and did not — touch.
            ReadRangeLog?.Add((offset, totalLength));

            // Re-verifies the header, then payload checksum, then footer. Typed
            // Section 13 corruption errors carry this block's file offset.
            return BlockSerializer.Deserialize(blockBytes, MaxPayloadLength, offset);
        }
    }

    /// <summary>
    /// Backward-walk primitive (spec Section 4): reads and validates the
    /// 16-byte footer ending at <paramref name="blockEndOffset"/> and returns
    /// the offset of that block's first header byte. To walk backward from EOF,
    /// start with <c>blockEndOffset = stream.Length</c>, then
    /// <see cref="Read"/> the returned offset and repeat with
    /// <c>blockEndOffset = returned offset</c> until it reaches
    /// <see cref="FirstBlockOffset"/>. An invalid footer at EOF means a torn
    /// final append (logical truncation, spec Section 13).
    /// </summary>
    /// <param name="blockEndOffset">Exclusive end offset of a block (its footer's last byte + 1).</param>
    public Result<long> FindBlockStart(long blockEndOffset)
    {
        ThrowIfDisposed();

        if (blockEndOffset < FirstBlockOffset + BlockSerializer.FixedOverhead)
            return Result<long>.Failure(
                $"No room for a block ending at offset {blockEndOffset}: the block stream starts at {FirstBlockOffset} and a block occupies at least {BlockSerializer.FixedOverhead} bytes.");

        Span<byte> footerBytes = stackalloc byte[BlockSerializer.FooterSize];
        lock (_streamLock)
        {
            try
            {
                if (blockEndOffset > _stream.Length)
                    return Result<long>.Failure(
                        $"Block end offset {blockEndOffset} lies past EOF (file length {_stream.Length}).");

                _stream.Seek(blockEndOffset - BlockSerializer.FooterSize);
                _stream.ReadExactly(footerBytes);
            }
            catch (IOException ex)
            {
                return Result<long>.Failure(
                    $"Block footer read ending at offset {blockEndOffset} failed: {ex.Message}");
            }
        }

        var footerResult = BlockSerializer.DeserializeFooter(footerBytes);
        if (footerResult.IsFailure)
            return Result<long>.Failure(footerResult.Error);

        var startOffset = blockEndOffset - footerResult.Value;
        if (startOffset < FirstBlockOffset)
            return Result<long>.Failure(
                $"Footer TotalBlockLength {footerResult.Value} would place the block start at {startOffset}, before the block stream start {FirstBlockOffset} (corrupt footer).");

        return Result<long>.Success(startOffset);
    }

    /// <summary>
    /// Chunk size for the backward footer hunt in
    /// <see cref="FindLastValidBlock"/>. Chunks overlap by
    /// <see cref="BlockSerializer.FooterSize"/> − 1 bytes so a footer spanning
    /// a chunk boundary is always seen whole.
    /// </summary>
    private const int BackwardScanChunkSize = 64 * 1024;

    /// <summary>
    /// Backward walk from EOF (spec Sections 4, 13): finds the last fully-valid
    /// block in the file, tolerating a torn tail append. Scans backward from
    /// EOF hunting the footer magic; each candidate footer's TotalBlockLength
    /// is sanity-checked, then the implied block is FULLY verified via
    /// <see cref="Read"/> (header checksum before any field is trusted,
    /// PayloadLength sanity, payload checksum, footer) — footer magic alone is
    /// never trusted, because those 8 bytes can occur in raw payload or tail
    /// garbage as a false positive. The first candidate that verifies, walking
    /// backward, is the last valid block; everything after
    /// <c>Offset + TotalBlockLength</c> is a torn/uncommitted tail and is
    /// logically truncated (spec Section 13: "Footer magic missing at EOF").
    /// Fails when the file holds no fully-valid block.
    /// </summary>
    public Result<BlockLocation> FindLastValidBlock()
    {
        ThrowIfDisposed();

        long fileLength;
        lock (_streamLock)
        {
            fileLength = _stream.Length;
        }

        long minBlockEnd = FirstBlockOffset + BlockSerializer.FixedOverhead;
        if (fileLength < minBlockEnd)
            return Result<BlockLocation>.Failure(
                $"No block can fit: the block stream starts at {FirstBlockOffset}, a block occupies at least {BlockSerializer.FixedOverhead} bytes, and the file is only {fileLength} bytes.");

        // Lowest offset a footer can start at: the footer of the earliest possible block.
        long minFooterStart = minBlockEnd - BlockSerializer.FooterSize;

        long chunkEnd = fileLength;
        while (true)
        {
            long chunkStart = Math.Max(minFooterStart, chunkEnd - BackwardScanChunkSize);
            int chunkLength = (int)(chunkEnd - chunkStart);
            var chunk = new byte[chunkLength];
            lock (_streamLock)
            {
                try
                {
                    _stream.Seek(chunkStart);
                    _stream.ReadExactly(chunk, 0, chunkLength);
                }
                catch (IOException ex)
                {
                    return Result<BlockLocation>.Failure(
                        $"Backward scan read of {chunkLength} bytes at offset {chunkStart} failed: {ex.Message}");
                }
            }

            // Hunt footer-magic candidates from the highest position down.
            for (int i = chunkLength - BlockSerializer.FooterSize; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(i, 8))
                    != BlockSerializer.FooterMagic)
                    continue;

                long candidateEnd = chunkStart + i + BlockSerializer.FooterSize;
                long total = BinaryPrimitives.ReadInt64LittleEndian(chunk.AsSpan(i + 8, 8));

                // Cheap sanity before any I/O: length plausible and block fits
                // entirely inside the block stream.
                if (total < BlockSerializer.FixedOverhead
                    || total - BlockSerializer.FixedOverhead > MaxPayloadLength
                    || total > candidateEnd - FirstBlockOffset)
                    continue;

                long start = candidateEnd - total;

                // Full verification — a footer match alone is never trusted.
                var blockResult = Read(start);
                if (blockResult.IsFailure)
                    continue;

                // The verified block's own footer must be THIS candidate footer;
                // otherwise the magic was a false positive (e.g. payload bytes)
                // that happens to point at some other valid block. Skip it and
                // keep scanning — that block's real footer is found at its own
                // position.
                if (BlockSerializer.GetTotalBlockLength(blockResult.Value.Header.PayloadLength) != total)
                    continue;

                return Result<BlockLocation>.Success(new BlockLocation
                {
                    BlockId = (byte[])blockResult.Value.Header.BlockId.Clone(),
                    Offset = start,
                    TotalBlockLength = total,
                });
            }

            if (chunkStart <= minFooterStart)
                break;

            // Overlap by FooterSize - 1 so a footer spanning the boundary is seen whole.
            chunkEnd = chunkStart + BlockSerializer.FooterSize - 1;
        }

        return Result<BlockLocation>.Failure(
            $"No valid block found scanning backward from EOF (file length {fileLength}, block stream start {FirstBlockOffset}): every footer-magic candidate failed full verification.");
    }

    /// <summary>
    /// Chunk size for the forward header hunt in <see cref="ScanForward"/>.
    /// Chunks overlap by 7 bytes (header magic size − 1) so a magic spanning a
    /// chunk boundary is always seen whole.
    /// </summary>
    private const int ForwardScanChunkSize = 64 * 1024;

    /// <summary>
    /// Full forward scan with resynchronization (spec Sections 11, 13): walks
    /// the block stream sequentially from <see cref="FirstBlockOffset"/>,
    /// fully verifying each block via <see cref="Read"/> (header checksum
    /// BEFORE any field is trusted, PayloadLength sanity, payload checksum,
    /// footer) and stepping by its TotalBlockLength. On any invalid block the
    /// scan hunts forward byte-wise for the next header-magic candidate and
    /// fully verifies the candidate before resynchronizing — magic alone is
    /// never trusted, because those 8 bytes can occur in payload or garbage as
    /// a false positive. Every hunted-over span is recorded as a
    /// <see cref="DamagedRange"/> (start/end/reason) in the result and
    /// reported to <paramref name="log"/>; trailing bytes too short to hold a
    /// block are reported the same way (torn tail, spec Section 13).
    ///
    /// Duplicate BlockIds among discovered blocks are legitimate (new versions
    /// of logical blocks) and resolve last-position-wins:
    /// <paramref name="offsetMap"/> receives every block in ascending offset
    /// order, and <see cref="RuntimeBlockOffsetMap"/> keeps the greater offset.
    /// The scan itself never fails on corruption (spec Section 13) — only an
    /// I/O error fails the result.
    /// </summary>
    /// <param name="offsetMap">
    /// Optional map to populate with every fully-verified block (e.g. a fresh
    /// <see cref="RuntimeBlockOffsetMap"/> when rebuilding state from raw bytes).
    /// </param>
    /// <param name="log">
    /// Optional sink receiving one human-readable message per damaged range.
    /// </param>
    /// <param name="startOffset">
    /// Block-aligned file offset to begin the walk at, so a bounded scan reads
    /// only the bytes from there to EOF (crash recovery scans forward from the
    /// last Checkpoint — bounded by post-Checkpoint data, not file size, spec
    /// Section 10.2). Negative (the default) starts at
    /// <see cref="FirstBlockOffset"/>, scanning the whole block stream.
    /// </param>
    public Result<ForwardScanResult> ScanForward(
        IBlockOffsetMap? offsetMap = null,
        Action<string>? log = null,
        long startOffset = -1)
    {
        ThrowIfDisposed();

        long fileLength;
        lock (_streamLock)
        {
            fileLength = _stream.Length;
        }

        var blocks = new List<BlockLocation>();
        var damagedRanges = new List<DamagedRange>();

        long offset = startOffset >= 0 ? startOffset : FirstBlockOffset;
        while (offset + BlockSerializer.FixedOverhead <= fileLength)
        {
            var blockResult = Read(offset);
            if (blockResult.IsSuccess)
            {
                var header = blockResult.Value.Header;
                var totalLength = BlockSerializer.GetTotalBlockLength(header.PayloadLength);
                blocks.Add(new BlockLocation
                {
                    BlockId = (byte[])header.BlockId.Clone(),
                    Offset = offset,
                    TotalBlockLength = totalLength,
                });
                offsetMap?.OnBlockAppended(header, offset, totalLength);
                offset += totalLength;
                continue;
            }

            // Invalid block: hunt forward (from the next byte, so the corrupt
            // block's own intact header magic cannot re-match) for the next
            // fully-verified block, then resynchronize there.
            var huntResult = HuntForwardForValidBlock(offset + 1, fileLength);
            if (huntResult.IsFailure)
                return Result<ForwardScanResult>.Failure(huntResult.Error);

            long damageEnd = huntResult.Value ?? fileLength;
            RecordDamage(damagedRanges, log, offset, damageEnd, blockResult.Error);
            offset = damageEnd;
        }

        // Trailing bytes too short to hold even an empty block: a torn tail
        // append (logical truncation, spec Section 13) — still damage to report.
        if (offset < fileLength)
            RecordDamage(damagedRanges, log, offset, fileLength,
                $"Trailing {fileLength - offset} bytes are too short to hold a block (minimum {BlockSerializer.FixedOverhead} bytes): torn tail append.");

        return Result<ForwardScanResult>.Success(new ForwardScanResult
        {
            Blocks = blocks,
            DamagedRanges = damagedRanges,
            FileLength = fileLength,
        });
    }

    /// <summary>Records one damaged range in the scan result and logs it.</summary>
    private static void RecordDamage(
        List<DamagedRange> damagedRanges, Action<string>? log,
        long start, long end, string reason)
    {
        damagedRanges.Add(new DamagedRange(start, end, reason));
        log?.Invoke(
            $"Forward scan: damaged range [{start}, {end}) ({end - start} bytes): {reason}");
    }

    /// <summary>
    /// Forward-hunt primitive for <see cref="ScanForward"/>: scans byte-wise
    /// from <paramref name="searchStart"/> for the next header-magic candidate
    /// whose block FULLY verifies via <see cref="Read"/> (header checksum
    /// before trusting any field, then payload checksum and footer). Returns
    /// the verified candidate's offset, or null when no valid block exists
    /// before <paramref name="fileLength"/>; fails only on I/O errors.
    /// </summary>
    private Result<long?> HuntForwardForValidBlock(long searchStart, long fileLength)
    {
        // Last offset a whole block could still start at.
        long lastCandidate = fileLength - BlockSerializer.FixedOverhead;

        long chunkStart = searchStart;
        while (chunkStart <= lastCandidate)
        {
            int chunkLength = (int)Math.Min(ForwardScanChunkSize, fileLength - chunkStart);
            var chunk = new byte[chunkLength];
            lock (_streamLock)
            {
                try
                {
                    _stream.Seek(chunkStart);
                    _stream.ReadExactly(chunk, 0, chunkLength);
                }
                catch (IOException ex)
                {
                    return Result<long?>.Failure(
                        $"Forward scan read of {chunkLength} bytes at offset {chunkStart} failed: {ex.Message}");
                }
            }

            for (int i = 0; i + 8 <= chunkLength; i++)
            {
                long candidate = chunkStart + i;
                if (candidate > lastCandidate)
                    break;
                if (BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(i, 8))
                    != BlockSerializer.HeaderMagic)
                    continue;

                // Full verification — a header-magic match alone is never
                // trusted (it can occur in payload bytes or garbage).
                if (Read(candidate).IsSuccess)
                    return Result<long?>.Success(candidate);
            }

            // Overlap by 7 bytes so a magic spanning the boundary is seen whole.
            chunkStart += chunkLength - 7;
        }

        return Result<long?>.Success(null);
    }

    /// <summary>
    /// Flushes all buffered appends to disk via
    /// <see cref="DurableStream.FlushToDisk"/> — a real fsync
    /// (<c>Flush(flushToDisk: true)</c>), never a bare stream flush. fsync
    /// failure is fatal (spec Section 10.3): the manager is poisoned, further
    /// writes are refused, fsync is never retried, and the caller must close
    /// the file and rely on crash recovery on reopen.
    /// </summary>
    public Result Flush()
    {
        ThrowIfDisposed();

        lock (_streamLock)
        {
            return _stream.FlushToDisk();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }
}
