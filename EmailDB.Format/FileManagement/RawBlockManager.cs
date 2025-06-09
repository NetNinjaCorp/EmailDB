using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using Force.Crc32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq; // For OrderByDescending
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.FileManagement;

// Define the various block types.


// Represents a block with header fields, payload, and checksums.


// Represents the position and length of a block in the file.


/// <summary>
/// The BlockManager class encapsulates writing, reading, verifying, and scanning blocks.
/// The block format is as follows:
/// 
/// Header:
///   [HEADER_MAGIC (8 bytes)]
///   [Version (2 bytes)]
///   [BlockType (1 byte)]
///   [Flags (1 byte)]
///   [Timestamp (8 bytes)]
///   [BlockId (8 bytes)]
///   [PayloadLength (8 bytes)]
///   --- Total: 36 bytes ---
///   [Header Checksum (4 bytes)]
/// 
/// Payload:
///   [Payload Data (variable)]
///   [Payload Checksum (4 bytes)]
/// 
/// Footer:
///   [FOOTER_MAGIC (8 bytes)]  // defined as ~HEADER_MAGIC
///   [Total Block Length (8 bytes)]
/// </summary>

public class RawBlockManager : IDisposable
{
    #region Constants

    public const ulong HEADER_MAGIC = 0xEE411DBBD114EEUL;
    public const ulong FOOTER_MAGIC = ~HEADER_MAGIC;
    public const int HeaderSize = 37;  // Updated to include PayloadEncoding field
    public const int HeaderChecksumSize = 4;
    public const int PayloadChecksumSize = 4;
    public const int FooterSize = 16;
    public const int TotalFixedOverhead = HeaderSize + HeaderChecksumSize + PayloadChecksumSize + FooterSize;  // 61 bytes total

    #endregion

    #region Private Fields

    private readonly string filePath;
    private readonly FileStream fileStream;
    private readonly AsyncReaderWriterLock fileLock;
    private readonly ConcurrentDictionary<long, BlockLocation> blockLocations;
    private long currentPosition;
    private bool isDisposed;
    private long latestMetadataBlockId = -1; // Initialize to invalid ID
    private readonly bool isReadOnly;

    #endregion

    #region Constructor & Disposal

    public RawBlockManager(string filePath, bool createIfNotExists = true, bool isReadOnly = false)
    {
        this.filePath = filePath;
        this.isReadOnly = isReadOnly;
        
        FileMode fileMode = createIfNotExists && !isReadOnly ? FileMode.OpenOrCreate : FileMode.Open;
        FileAccess fileAccess = isReadOnly ? FileAccess.Read : FileAccess.ReadWrite;
        FileShare fileShare = isReadOnly ? FileShare.ReadWrite : FileShare.Read;
        
        try
        {
            fileStream = new FileStream(filePath, fileMode, fileAccess, fileShare);
            fileLock = new AsyncReaderWriterLock();
            blockLocations = new ConcurrentDictionary<long, BlockLocation>();
            currentPosition = fileStream.Length;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to open file '{filePath}' with mode={fileMode}, access={fileAccess}, share={fileShare}: {ex.Message}", ex);
        }
        
        // Only scan if file has content
        if (fileStream.Length > 0)
        {
            try
            {
                // Scan for existing blocks to populate locations and find latest metadata
                ScanExistingBlocks();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to scan existing blocks during initialization: {ex.Message}");
                // Continue anyway - the file might be empty or corrupted
            }
        }
    }

    public void Dispose()
    {
        if (!isDisposed)
        {
            if (fileStream != null)
            {
                if (fileStream.CanWrite)
                {
                    try
                    {
                        fileStream.Flush();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed
                    }
                }
                fileStream.Dispose();
            }
            fileLock?.Dispose();
            isDisposed = true;
        }
    }

    #endregion

    #region Public Properties

    public string FilePath => filePath;

    #endregion

    #region Public Methods

    /// <summary>
    /// Writes a new block to the end of the file in a thread-safe manner.
    /// Returns a Result containing the BlockLocation on success, or an error message on failure.
    /// </summary>
    public async Task<Result<BlockLocation>> WriteBlockAsync(Block block, CancellationToken cancellationToken = default, long? OverrideLocation = null)
    {
        ThrowIfDisposed();
        
        if (isReadOnly)
        {
            return Result<BlockLocation>.Failure("Cannot write to a read-only block manager");
        }

        // Prepare the block data in memory first, outside the lock
        byte[] blockData;
        using (var ms = new MemoryStream())
        {
            WriteBlockToStream(ms, block);
            blockData = ms.ToArray();
        }

        // Use a local flag to track lock acquisition
        bool lockAcquired = false;

        try
        {
            // Attempt to acquire the write lock
            await fileLock.AcquireWriterLock();
            lockAcquired = true;

            // Actual write operation
            long blockStartPosition = currentPosition;
            fileStream.Seek(blockStartPosition, SeekOrigin.Begin);
            if (OverrideLocation.HasValue)
            {
                // If OverrideLocation is provided, seek to that position
                fileStream.Seek(OverrideLocation.Value, SeekOrigin.Begin);
            }
            // Write the block data to the file
            await fileStream.WriteAsync(blockData, 0, blockData.Length, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);

            currentPosition = fileStream.Position;
            var location = new BlockLocation
            {
                Position = blockStartPosition,
                Length = blockData.Length
            };

            blockLocations.AddOrUpdate(block.BlockId, location, (key, oldValue) => location);
            
            return Result<BlockLocation>.Success(location);
        }
        catch (OperationCanceledException)
        {
            return Result<BlockLocation>.Failure("Write operation was cancelled.");
        }
        catch (IOException ex)
        {
            return Result<BlockLocation>.Failure($"I/O error writing Block ID {block.BlockId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<BlockLocation>.Failure($"Unexpected error writing Block ID {block.BlockId}: {ex.Message}");
        }
        finally
        {
            if (lockAcquired)
            {
                fileLock.ReleaseWriterLock();
            }
        }
    }

   

    /// <summary>
    /// Reads a block from the file by its ID in a thread-safe manner.
    /// Returns a Result indicating success or failure.
    /// </summary>
    public async Task<Result<Block>> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!blockLocations.TryGetValue(blockId, out BlockLocation location))
        {
            return Result<Block>.Failure($"Block ID {blockId} not found in blockLocations map.");
        }

        byte[] buffer = new byte[location.Length];
        bool lockAcquired = false;
        
        try
        {
            await fileLock.AcquireReaderLock();
            lockAcquired = true;
            
            // Check if fileStream is valid
            if (fileStream == null)
            {
                return Result<Block>.Failure($"File stream is null for reading Block ID {blockId}.");
            }
            
            if (fileStream.SafeFileHandle?.IsClosed == true)
            {
                return Result<Block>.Failure($"File stream handle is closed for reading Block ID {blockId}.");
            }
            
            if (!fileStream.CanRead)
            {
                return Result<Block>.Failure($"File stream cannot read (CanRead=false) for Block ID {blockId}.");
            }
            
            fileStream.Seek(location.Position, SeekOrigin.Begin);

            int bytesRead = await fileStream.ReadAsync(buffer, 0, (int)location.Length, cancellationToken);
            if (bytesRead != location.Length)
            {
                return Result<Block>.Failure($"Incomplete read for Block ID {blockId}. Expected {location.Length} bytes, got {bytesRead}.");
            }

            using (var ms = new MemoryStream(buffer))
            {
                // ReadBlockFromStream now needs to handle potential exceptions and return Result
                return ReadBlockFromStreamInternal(ms, blockId);
            }
        }
        catch (IOException ex)
        {
            return Result<Block>.Failure($"I/O error reading Block ID {blockId}: {ex.Message}");
        }
        catch (Exception ex) // Catch unexpected errors during read/seek
        {
            return Result<Block>.Failure($"Unexpected error reading Block ID {blockId}: {ex.Message}");
        }
        finally
        {
            if (lockAcquired)
            {
                fileLock.ReleaseReaderLock();
            }
        }
    }

    /// <summary>
    /// Returns all block locations currently tracked.
    /// </summary>
    public IReadOnlyDictionary<long, BlockLocation> GetBlockLocations()
    {
        ThrowIfDisposed();
        return blockLocations;
    }

    public async Task<List<long>> ScanFile()
    {
        return await FindMagicPositions();
    }



    /// <summary>
    /// Compacts the file by removing outdated blocks and rewriting active ones.
    /// </summary>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            await fileLock.AcquireWriterLock();

            string tempFilePath = filePath + ".temp";
            using (var tempManager = new RawBlockManager(tempFilePath)) // Use RawBlockManager for temp file
            {
                // Copy all current blocks to the temporary file
                foreach (var kvp in blockLocations)
                {
                    var blockResult = await ReadBlockAsync(kvp.Key, cancellationToken);
                    if (blockResult.IsSuccess)
                    {
                        await tempManager.WriteBlockAsync(blockResult.Value, cancellationToken);
                    }
                    else
                    {
                        // Log the error and skip this block during compaction
                        Console.WriteLine($"Compaction warning: Failed to read block {kvp.Key}, skipping. Error: {blockResult.Error}");
                        // Depending on requirements, might want to throw or handle differently
                    }
                }
            }

            // Replace the original file with the compacted one
            File.Replace(filePath + ".temp", filePath, filePath + ".bak");

            // Reinitialize the file stream with the compacted file
            var newStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var oldStream = fileStream;

            // Update the stream reference and position
            Interlocked.Exchange(ref currentPosition, newStream.Length);

            // Close the old stream
            oldStream.Dispose();
        }
        finally
        {
           
                fileLock.ReleaseWriterLock();
        }
    }

    #endregion

    #region Private Methods


    /// Returns a list of absolute positions where the magic was found.
    /// </summary>
    /// <param name="filename">The file to scan.</param>
    /// <param name="magic">The magic byte sequence to look for.</param>
    /// <returns>A list of absolute file positions where the magic sequence appears.</returns>
    private async Task<List<long>> FindMagicPositions()
    {
        List<long> magicPositions = new List<long>();
        long fileLength = new FileInfo(filePath).Length;
        const int chunkSize = 16384;
        int overlap = 8;
        byte[] buffer = new byte[chunkSize + overlap];
        long lastProcessedAbsolute = -1;
        byte[] headerMagic = BitConverter.GetBytes(HEADER_MAGIC);
        bool lockAcquired = false;
        try
        {
            await fileLock.AcquireReaderLock();
            lockAcquired = true;
            using (var mmf = MemoryMappedFile.CreateFromFile(fileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true))
            {
                // 'position' is the start of the current chunk (excluding any overlap from a previous chunk).
                long position = 0;
                while (position < fileLength)
                {
                    // Determine how many new bytes to read without exceeding the file length.
                    int bytesToRead = (int)Math.Min(chunkSize, fileLength - position);
                    // For the first chunk, fill from index 0; subsequent chunks leave room for the overlap.
                    int writeOffset = position == 0 ? 0 : overlap;

                    // Read the chunk into the buffer.
                    using (var accessor = mmf.CreateViewAccessor(position, bytesToRead, MemoryMappedFileAccess.Read))
                    {
                        accessor.ReadArray(0, buffer, writeOffset, bytesToRead);
                    }

                    // Total valid bytes: for the first chunk it's just bytesToRead; for others, include the overlap.
                    int totalBytes = position == 0 ? bytesToRead : overlap + bytesToRead;
                    ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, totalBytes);

                    int localOffset = 0;
                    while (localOffset < span.Length)
                    {
                        int index = span.Slice(localOffset).IndexOf(headerMagic);
                        if (index == -1)
                            break;

                        // Compute the absolute position in the file.
                        long basePosition = position == 0 ? position : position - overlap;
                        long absoluteIndex = basePosition + localOffset + index;

                        // Avoid duplicate processing (if an overlap causes the same index to appear again).
                        if (absoluteIndex > lastProcessedAbsolute)
                        {
                            magicPositions.Add(absoluteIndex);
                            lastProcessedAbsolute = absoluteIndex;
                        }

                        localOffset += index + headerMagic.Length;
                    }

                    // Before reading the next chunk, copy the last 'overlap' bytes into the beginning of the buffer.
                    if (totalBytes >= overlap)
                    {
                        Array.Copy(buffer, totalBytes - overlap, buffer, 0, overlap);
                    }

                    position += bytesToRead;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        finally
        {
            if (lockAcquired)
                fileLock.ReleaseReaderLock();
        }
        return magicPositions;
    }

    /// <summary>
    /// Scans the file to populate block locations and find the latest Metadata block ID.
    /// Should be called during initialization.
    /// </summary>
    private void ScanExistingBlocks()
    {
        // Don't use locks during initialization - we're in the constructor
        // and no other threads can access this instance yet
        try
        {
            
            var fileLength = fileStream.Length;
            if (fileLength == 0) return; // Nothing to scan

            long currentScanPosition = 0;
            long foundLatestMetadataId = -1;
            long maxMetadataPosition = -1;

            // Temporary list to hold locations found during scan
            var scannedLocations = new Dictionary<long, BlockLocation>();

            // This simplified scan assumes blocks are contiguous and valid from the start.
            // A more robust scan might need to handle corrupted sections.
            while (currentScanPosition < fileLength)
            {
                // Read potential header + checksum
                if (currentScanPosition + HeaderSize + HeaderChecksumSize > fileLength) break; // Not enough space left

                byte[] potentialHeader = new byte[HeaderSize];
                fileStream.Seek(currentScanPosition, SeekOrigin.Begin);
                int headerBytesRead = fileStream.Read(potentialHeader, 0, HeaderSize);
                if (headerBytesRead != HeaderSize) break; // Couldn't read full header

                uint storedHeaderChecksum = ReadUInt32FromFileStream(fileStream); // Helper needed or use BinaryReader

                // Verify magic and checksum before proceeding
                ulong headerMagic = BitConverter.ToUInt64(potentialHeader, 0);
                uint computedHeaderChecksum = ComputeChecksum(potentialHeader);

                if (headerMagic == HEADER_MAGIC && storedHeaderChecksum == computedHeaderChecksum)
                {
                    // Header looks valid, parse details
                    BlockType type = (BlockType)potentialHeader[10]; // Offset for BlockType
                    long blockId = BitConverter.ToInt64(potentialHeader, 21); // Offset for BlockId (adjusted for PayloadEncoding)
                    long payloadLength = BitConverter.ToInt64(potentialHeader, 29); // Offset for PayloadLength (adjusted for PayloadEncoding)
                    long totalBlockLength = TotalFixedOverhead + payloadLength;

                    // Basic check: does the block fit within the file?
                    if (currentScanPosition + totalBlockLength <= fileLength)
                    {
                        // TODO: Optionally add footer magic/length check here for extra validation

                        var location = new BlockLocation { Position = currentScanPosition, Length = totalBlockLength };
                        scannedLocations[blockId] = location; // Store latest location found for this ID

                        if (type == BlockType.Metadata)
                        {
                            // Track the metadata block found at the highest position
                            if (currentScanPosition > maxMetadataPosition)
                            {
                                maxMetadataPosition = currentScanPosition;
                                foundLatestMetadataId = blockId;
                            }
                        }
                        currentScanPosition += totalBlockLength; // Move to the next potential block
                    }
                    else
                    {
                        // Block declared length exceeds file bounds, assume corruption/incomplete write
                        break; // Stop scanning
                    }
                }
                else
                {
                    // Invalid header/checksum, assume corruption or end of valid blocks
                    break; // Stop scanning
                }
            }

            // Update the main dictionary and latest metadata ID outside the loop
            // Use TryUpdate or AddOrUpdate if concurrent access during init is possible,
            // but since this is likely called from constructor, direct assignment might be okay.
            foreach (var kvp in scannedLocations)
            {
                blockLocations.AddOrUpdate(kvp.Key, kvp.Value, (id, oldLoc) => kvp.Value);
            }
            latestMetadataBlockId = foundLatestMetadataId;

        }
        catch (Exception ex)
        {
            // Log error during scanning
            Console.WriteLine($"Error during file scan: {ex.Message}");
            // Depending on requirements, might clear blockLocations or throw
        }
        finally
        {
            // Reset stream position after scanning
            if (fileStream != null && fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }
        }
    }

    // Helper to read uint32 directly from filestream (assumes stream position is correct)
    private uint ReadUInt32FromFileStream(FileStream fs)
    {
        byte[] buffer = new byte[4];
        int bytesRead = fs.Read(buffer, 0, 4);
        if (bytesRead < 4) throw new EndOfStreamException("Could not read uint32 from stream.");
        return BitConverter.ToUInt32(buffer, 0);
    }

    private bool TryReadBlockLocation(MemoryMappedViewAccessor accessor, long position, long fileLength, out BlockLocation location, out long blockId)
    {
        location = default;
        blockId = 0;

        // Check if we have enough space to read the header
        if (position + HeaderSize + HeaderChecksumSize > fileLength)
        {
            return false;
        }

        // Read and verify header magic
        ulong headerMagic = accessor.ReadUInt64(position);
        if (headerMagic != HEADER_MAGIC)
        {
            return false;
        }

        // Read header fields
        byte[] headerBytes = new byte[HeaderSize];
        accessor.ReadArray(position, headerBytes, 0, HeaderSize);

        // Read header checksum
        uint storedHeaderChecksum = accessor.ReadUInt32(position + HeaderSize);
        uint computedHeaderChecksum = ComputeChecksum(headerBytes);

        if (storedHeaderChecksum != computedHeaderChecksum)
        {
            return false;
        }

        // Read block ID and payload length
        blockId = accessor.ReadInt64(position + 21); // 21 is the offset to BlockId in header (adjusted for PayloadEncoding)
        long payloadLength = accessor.ReadInt64(position + 29); // 29 is the offset to PayloadLength in header (adjusted for PayloadEncoding)

        // Calculate total block length
        long totalLength = TotalFixedOverhead + payloadLength;

        // Verify we have enough space for the entire block
        if (position + totalLength > fileLength)
        {
            return false;
        }

        // Read and verify footer magic
        long footerPosition = position + totalLength - FooterSize;
        ulong footerMagic = accessor.ReadUInt64(footerPosition);
        if (footerMagic != FOOTER_MAGIC)
        {
            return false;
        }

        // Create block location
        location = new BlockLocation
        {
            Position = position,
            Length = totalLength
        };

        return true;
    }

    private void WriteBlockToStream(Stream stream, Block block)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // Write header
        using (var headerStream = new MemoryStream())
        {
            using (var headerWriter = new BinaryWriter(headerStream, Encoding.UTF8, leaveOpen: true))
            {
                headerWriter.Write(HEADER_MAGIC);
                headerWriter.Write(block.Version);
                headerWriter.Write((byte)block.Type);
                headerWriter.Write(block.Flags);
                headerWriter.Write((byte)block.Encoding);  // PayloadEncoding at byte 12
                headerWriter.Write(block.Timestamp);
                headerWriter.Write(block.BlockId);

                long payloadLen = block.Payload?.Length ?? 0;
                headerWriter.Write(payloadLen);
            }

            byte[] headerBytes = headerStream.ToArray();
            uint headerChecksum = ComputeChecksum(headerBytes);
            writer.Write(headerBytes);
            writer.Write(headerChecksum);
        }

        // Write payload and its checksum
        if (block.Payload != null && block.Payload.Length > 0)
        {
            writer.Write(block.Payload);
            uint payloadChecksum = ComputeChecksum(block.Payload);
            writer.Write(payloadChecksum);
        }
        else
        {
            writer.Write(0U); // Zero checksum for empty payload
        }

        // Write footer
        writer.Write(FOOTER_MAGIC);
        writer.Write((long)(TotalFixedOverhead + (block.Payload?.Length ?? 0)));
    }

    // Renamed to avoid conflict and clarify internal use with Result pattern
    private Result<Block> ReadBlockFromStreamInternal(Stream stream, long blockIdForErrorMessage)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        // Read and verify header
        byte[] headerBytes = reader.ReadBytes(HeaderSize);
        if (headerBytes.Length != HeaderSize)
        {
            return Result<Block>.Failure($"Incomplete header for Block ID {blockIdForErrorMessage}");
        }

        uint storedHeaderChecksum = reader.ReadUInt32();
        uint computedHeaderChecksum = ComputeChecksum(headerBytes);
        if (storedHeaderChecksum != computedHeaderChecksum)
        {
            return Result<Block>.Failure($"Header checksum mismatch for Block ID {blockIdForErrorMessage}");
        }

        // Parse header
        using (var headerStream = new MemoryStream(headerBytes))
        using (var headerReader = new BinaryReader(headerStream))
        {
            ulong headerMagic = headerReader.ReadUInt64();
            if (headerMagic != HEADER_MAGIC)
            {
                return Result<Block>.Failure($"Invalid header magic for Block ID {blockIdForErrorMessage}");
            }

            var block = new Block
            {
                Version = headerReader.ReadUInt16(),
                Type = (BlockType)headerReader.ReadByte(),
                Flags = headerReader.ReadByte(),
                Encoding = (PayloadEncoding)headerReader.ReadByte(),  // Read PayloadEncoding at byte 12
                Timestamp = headerReader.ReadInt64(),
                BlockId = headerReader.ReadInt64()
            };

            long payloadLength = headerReader.ReadInt64();

            // Read payload if present
            if (payloadLength > 0)
            {
                block.Payload = reader.ReadBytes((int)payloadLength);
                if (block.Payload.Length != payloadLength)
                {
                    return Result<Block>.Failure($"Incomplete payload read for Block ID {blockIdForErrorMessage}");
                }

                uint storedPayloadChecksum = reader.ReadUInt32();
                uint computedPayloadChecksum = ComputeChecksum(block.Payload);
                if (storedPayloadChecksum != computedPayloadChecksum)
                {
                    return Result<Block>.Failure($"Payload checksum mismatch for Block ID {blockIdForErrorMessage}");
                }
            }
            else
            {
                uint storedPayloadChecksum = reader.ReadUInt32(); // Read the checksum for empty payload
                if (storedPayloadChecksum != 0) // Checksum for empty payload must be 0
                {
                    return Result<Block>.Failure($"Payload checksum mismatch for empty payload for Block ID {blockIdForErrorMessage}");
                }
            }

            // Verify footer
            ulong footerMagic = reader.ReadUInt64();
            if (footerMagic != FOOTER_MAGIC)
            {
                return Result<Block>.Failure($"Invalid footer magic for Block ID {blockIdForErrorMessage}");
            }

            long storedBlockLength = reader.ReadInt64();
            long computedBlockLength = HeaderSize + HeaderChecksumSize + payloadLength + PayloadChecksumSize + FooterSize;
            if (storedBlockLength != computedBlockLength)
            {
                return Result<Block>.Failure($"Block length mismatch in footer for Block ID {blockIdForErrorMessage}");
            }

            return Result<Block>.Success(block);
        }
    }

    private static uint ComputeChecksum(byte[] data)
    {
        return Crc32Algorithm.Compute(data);
    }

    private static uint ComputeChecksum(byte[] data, int offset, int count)
    {
        return Crc32Algorithm.Compute(data, offset, count);
    }

    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(RawBlockManager),
                "This RawBlockManager instance has been disposed.");
        }
    }

    #endregion
}