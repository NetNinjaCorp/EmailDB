using EmailDB.Format.Models; // Use models from the core Format project
using Force.Crc32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading; // Added for Interlocked

namespace EmailDB.Format // Updated namespace
{
    /// <summary>
    /// Manages the low-level reading and writing of raw blocks to the data file,
    /// including header/footer magic, checksums, and block positioning.
    /// This class is agnostic to the payload serialization format.
    /// </summary>
    public class RawBlockManager : IDisposable
    {
        #region Constants

        // Based on EmailDB_FileFormat_Spec.md (with PayloadEncoding added)
        public const ulong HEADER_MAGIC = 0xEE411DBBD114EEUL;
        public const ulong FOOTER_MAGIC = ~HEADER_MAGIC;
        public const int HeaderFixedFieldsSize = 37; // Magic(8)+Ver(2)+Type(1)+Flags(1)+Encoding(1)+Timestamp(8)+ID(8)+PayloadLen(8)
        public const int HeaderChecksumSize = 4;
        public const int PayloadChecksumSize = 4;
        public const int FooterSize = 16; // FooterMagic(8)+TotalLen(8)
        public const int TotalFixedOverhead = HeaderFixedFieldsSize + HeaderChecksumSize + PayloadChecksumSize + FooterSize; // 37 + 4 + 4 + 16 = 61 bytes

        // Header Field Offsets (from start of block)
        private const int VersionOffset = 8;
        private const int BlockTypeOffset = 10;
        private const int FlagsOffset = 11;
        private const int PayloadEncodingOffset = 12;
        private const int TimestampOffset = 13;
        private const int BlockIdOffset = 21;
        private const int PayloadLengthOffset = 29;
        private const int HeaderChecksumOffset = 37; // Immediately after fixed fields

        #endregion

        #region Private Fields

        private readonly string filePath;
        private readonly FileStream fileStream;
        private readonly ReaderWriterLockSlim fileLock;
        private readonly ConcurrentDictionary<long, BlockLocation> blockLocations;
        private long currentPosition;
        private bool isDisposed;
        private long latestMetadataBlockId = -1; // Track the ID of the latest known metadata block
        // Removed latestMetadataBlockId - Raw manager shouldn't track specific block types

        #endregion

        #region Constructor & Disposal

        public RawBlockManager(string filePath, bool createIfNotExists = true)
        {
            this.filePath = filePath;
            FileMode fileMode = createIfNotExists ? FileMode.OpenOrCreate : FileMode.Open;
            // Increased buffer size for potentially better performance
            this.fileStream = new FileStream(filePath, fileMode, FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, useAsync: true);
            this.fileLock = new ReaderWriterLockSlim();
            this.blockLocations = new ConcurrentDictionary<long, BlockLocation>();
            this.currentPosition = this.fileStream.Length;
            // Scan for existing blocks to populate locations
            ScanExistingBlocks();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
             if (!isDisposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    fileStream?.Flush();
                    fileStream?.Close();
                    fileLock?.Dispose();
                    fileStream?.Dispose();
                }
                // Dispose unmanaged resources if any

                isDisposed = true;
            }
        }


        #endregion

        #region Public Methods

        /// <summary>
        /// Writes a new block to the end of the file in a thread-safe manner.
        /// The BlockId should be assigned before calling this method.
        /// </summary>
        /// <returns>A Result containing the BlockLocation on success, or an error message on failure.</returns>
        public async Task<Result<BlockLocation>> WriteBlockAsync(Block block, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (block == null) return Result<BlockLocation>.Failure("Input block cannot be null.");
            if (block.BlockId == 0) return Result<BlockLocation>.Failure("BlockId must be assigned before writing."); // Assuming 0 is invalid

            // Prepare the block data in memory first, outside the lock
            byte[] blockData;
            try
            {
                using (var ms = new MemoryStream())
                {
                    WriteBlockToStream(ms, block);
                    blockData = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                return Result<BlockLocation>.Failure($"Error preparing block data for Block ID {block.BlockId}: {ex.Message}");
            }


            BlockLocation location;
            fileLock.EnterWriteLock();
            try
            {
                // Store the starting position for this block
                long blockStartPosition = currentPosition;

                // Seek to the end of the file
                fileStream.Seek(blockStartPosition, SeekOrigin.Begin);

                // Write the complete block to the file
                await fileStream.WriteAsync(blockData, 0, blockData.Length, cancellationToken);
                await fileStream.FlushAsync(cancellationToken); // Ensure data is written to disk

                // Update the current position
                currentPosition = fileStream.Position;

                // Create and store the block location
                location = new BlockLocation
                {
                    Position = blockStartPosition,
                    Length = blockData.Length
                };

                blockLocations.AddOrUpdate(block.BlockId, location, (key, oldValue) => location);

                // If we just successfully wrote a Metadata block, update our tracked ID
                if (block.Type == BlockType.Metadata)
                {
                    // Use Interlocked for thread-safe update, although write lock is held
                    Interlocked.Exchange(ref latestMetadataBlockId, block.BlockId);
                }

                return Result<BlockLocation>.Success(location);
            }
            catch (IOException ex)
            {
                 return Result<BlockLocation>.Failure($"I/O error writing Block ID {block.BlockId}: {ex.Message}");
            }
            catch (Exception ex) // Catch unexpected errors
            {
                 return Result<BlockLocation>.Failure($"Unexpected error writing Block ID {block.BlockId}: {ex.Message}");
            }
            finally
            {
                fileLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Reads the raw data block (header + payload) from the file by its ID.
        /// Does not interpret the payload based on encoding.
        /// </summary>
        /// <returns>A Result containing the Block object on success, or an error message on failure.</returns>
        public async Task<Result<Block>> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!blockLocations.TryGetValue(blockId, out BlockLocation location))
            {
                return Result<Block>.Failure($"Block ID {blockId} not found in blockLocations map.");
            }

            byte[] buffer = new byte[location.Length];
            fileLock.EnterReadLock();
            try
            {
                // Check if file stream position matches expected block start, seek if necessary
                // This might be overly cautious if reads are always sequential or by ID lookup
                if (fileStream.Position != location.Position)
                {
                     fileStream.Seek(location.Position, SeekOrigin.Begin);
                }


                int bytesRead = await fileStream.ReadAsync(buffer, 0, (int)location.Length, cancellationToken);
                if (bytesRead != location.Length)
                {
                     return Result<Block>.Failure($"Incomplete read for Block ID {blockId}. Expected {location.Length} bytes, got {bytesRead}.");
                }

                using (var ms = new MemoryStream(buffer))
                {
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
                fileLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns a read-only dictionary of all block locations currently tracked.
        /// </summary>
        public IReadOnlyDictionary<long, BlockLocation> GetBlockLocations()
        {
            ThrowIfDisposed();
            // Return a copy to prevent external modification issues if needed,
            // but ConcurrentDictionary is thread-safe for reads.
            return blockLocations;
        }

        /// &lt;summary>
        /// Gets the Block ID of the latest Metadata block found during the last scan or written.
        /// Returns -1 if no Metadata block has been found or written yet.
        /// &lt;/summary>
        public long GetLatestMetadataBlockId()
        {
            // Read the value safely, although reads on long are atomic on 64-bit systems
            return Interlocked.Read(ref latestMetadataBlockId);
        }


        /// <summary>
        /// Performs a full file scan to rebuild the block location map.
        /// This can be useful after a crash or to verify file integrity.
        /// Note: This clears the existing block location map.
        /// </summary>
        public Result ReScanFile()
        {
             ThrowIfDisposed();
             blockLocations.Clear(); // Clear existing locations before rescan
             try
             {
                 ScanExistingBlocks();
                 return Result.Success();
             }
             catch (Exception ex)
             {
                 return Result.Failure($"Error during file rescan: {ex.Message}");
             }
        }


        // CompactAsync removed - Compaction logic likely needs awareness of block types
        // and payload content, making it better suited for a higher-level manager.
        // RawBlockManager should focus solely on reading/writing individual raw blocks.

        #endregion

        #region Private Methods


        /// <summary>
        /// Scans the file from the beginning to populate the blockLocations dictionary.
        /// Assumes blocks are mostly contiguous and valid. Stops on first major error.
        /// </summary>
        private void ScanExistingBlocks()
        {
            fileLock.EnterReadLock(); // Use read lock for scanning
            try
            {
                var fileLength = fileStream.Length;
                if (fileLength < TotalFixedOverhead) return; // File too small for even one block

                long currentScanPosition = 0;
                long foundLatestMetadataId = -1; // Local variable for scan result
                long maxMetadataPosition = -1;   // Track position to find the latest
                var tempLocations = new Dictionary<long, BlockLocation>(); // Build locations here first

                while (currentScanPosition <= fileLength - TotalFixedOverhead) // Ensure enough space for fixed overhead
                {
                    // Read potential header fixed fields + header checksum
                    long headerReadEndPosition = currentScanPosition + HeaderFixedFieldsSize + HeaderChecksumSize;
                    if (headerReadEndPosition > fileLength) break; // Not enough space left

                    byte[] potentialHeaderAndChecksum = new byte[HeaderFixedFieldsSize + HeaderChecksumSize];
                    fileStream.Seek(currentScanPosition, SeekOrigin.Begin);
                    int bytesRead = fileStream.Read(potentialHeaderAndChecksum, 0, potentialHeaderAndChecksum.Length);
                    if (bytesRead != potentialHeaderAndChecksum.Length) break; // Couldn't read full header + checksum

                    // Verify magic
                    ulong headerMagic = BitConverter.ToUInt64(potentialHeaderAndChecksum, 0);
                    if (headerMagic != HEADER_MAGIC)
                    {
                        // Could implement more advanced scanning here to find next magic number
                        // For now, assume corruption and stop.
                        Console.WriteLine($"Scan warning: Invalid header magic at position {currentScanPosition}. Stopping scan.");
                        break;
                    }

                    // Verify header checksum
                    uint storedHeaderChecksum = BitConverter.ToUInt32(potentialHeaderAndChecksum, HeaderFixedFieldsSize);
                    // Create a Span<byte> for the header part only to checksum
                    Span<byte> headerBytes = potentialHeaderAndChecksum.AsSpan(0, HeaderFixedFieldsSize);
                    uint computedHeaderChecksum = ComputeChecksum(headerBytes);

                    if (storedHeaderChecksum != computedHeaderChecksum)
                    {
                         Console.WriteLine($"Scan warning: Header checksum mismatch at position {currentScanPosition}. Stopping scan.");
                         break; // Header checksum failed, assume corruption
                    }


                    // Header looks valid, parse details needed for location and type
                    BlockType type = (BlockType)headerBytes[BlockTypeOffset]; // Read type for metadata check
                    long blockId = BitConverter.ToInt64(potentialHeaderAndChecksum, BlockIdOffset);
                    long payloadLength = BitConverter.ToInt64(potentialHeaderAndChecksum, PayloadLengthOffset);

                    if (payloadLength < 0)
                    {
                        Console.WriteLine($"Scan warning: Invalid negative payload length ({payloadLength}) at position {currentScanPosition}. Stopping scan.");
                        break; // Invalid payload length
                    }

                    long totalBlockLength = HeaderFixedFieldsSize + HeaderChecksumSize + payloadLength + PayloadChecksumSize + FooterSize;

                    // Basic check: does the block fit within the file?
                    if (currentScanPosition + totalBlockLength <= fileLength)
                    {
                        // TODO: Optionally add footer magic/length check here for extra validation by reading the footer
                        // This would require another seek/read.

                        var location = new BlockLocation { Position = currentScanPosition, Length = totalBlockLength };
                        tempLocations[blockId] = location; // Store/overwrite location for this ID

                        // Track the metadata block found at the highest position
                        if (type == BlockType.Metadata && currentScanPosition > maxMetadataPosition)
                        {
                             maxMetadataPosition = currentScanPosition;
                             foundLatestMetadataId = blockId;
                        }

                        currentScanPosition += totalBlockLength; // Move to the next potential block start
                    }
                    else
                    {
                        // Block declared length exceeds file bounds, assume corruption/incomplete write
                        Console.WriteLine($"Scan warning: Declared block length exceeds file size at position {currentScanPosition}. Stopping scan.");
                        break; // Stop scanning
                    }
                }

                 // Update the main concurrent dictionary and the tracked metadata ID safely
                 // This minimizes locking contention if other threads were somehow active (though unlikely in constructor)
                 foreach(var kvp in tempLocations)
                 {
                     blockLocations.AddOrUpdate(kvp.Key, kvp.Value, (id, oldLoc) => kvp.Value);
                 }
                 Console.WriteLine($"Scan complete. Found {blockLocations.Count} unique block locations.");
                 // Update the tracked metadata ID after processing all potential blocks
                 Interlocked.Exchange(ref latestMetadataBlockId, foundLatestMetadataId);


            }
            catch (Exception ex)
            {
                // Log error during scanning
                Console.WriteLine($"Error during file scan: {ex.Message}");
                // Clear potentially inconsistent locations if scan failed badly
                blockLocations.Clear();
            }
            finally
            {
                fileLock.ExitReadLock();
            }
        }


        /// <summary>
        /// Writes the logical block data to a stream in the raw file format.
        /// </summary>
        private void WriteBlockToStream(Stream stream, Block block)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            long payloadLen = block.Payload?.Length ?? 0;

            // --- Write Header Fields ---
            using (var headerStream = new MemoryStream(HeaderFixedFieldsSize))
            {
                using (var headerWriter = new BinaryWriter(headerStream, Encoding.UTF8, leaveOpen: true))
                {
                    headerWriter.Write(HEADER_MAGIC);           // 8 bytes
                    headerWriter.Write(block.Version);          // 2 bytes
                    headerWriter.Write((byte)block.Type);       // 1 byte
                    headerWriter.Write(block.Flags);            // 1 byte
                    headerWriter.Write((byte)block.PayloadEncoding); // 1 byte - Added
                    headerWriter.Write(block.Timestamp);        // 8 bytes
                    headerWriter.Write(block.BlockId);          // 8 bytes
                    headerWriter.Write(payloadLen);             // 8 bytes
                } // Total: 37 bytes

                byte[] headerBytes = headerStream.ToArray();
                uint headerChecksum = ComputeChecksum(headerBytes);

                // Write header bytes and checksum to main stream
                writer.Write(headerBytes);
                writer.Write(headerChecksum); // 4 bytes
            } // Header + Checksum = 41 bytes written

            // --- Write Payload and Checksum ---
            if (block.Payload != null && payloadLen > 0)
            {
                writer.Write(block.Payload); // variable length
                uint payloadChecksum = ComputeChecksum(block.Payload);
                writer.Write(payloadChecksum); // 4 bytes
            }
            else
            {
                // Write zero checksum for empty/null payload
                writer.Write(0U); // 4 bytes
            }

            // --- Write Footer ---
            writer.Write(FOOTER_MAGIC); // 8 bytes
            long totalBlockLength = HeaderFixedFieldsSize + HeaderChecksumSize + payloadLen + PayloadChecksumSize + FooterSize;
            writer.Write(totalBlockLength); // 8 bytes
        }

        /// <summary>
        /// Reads a raw block from a stream (assumed to contain the full block data)
        /// and parses it into a logical Block object. Includes checksum verification.
        /// </summary>
        private Result<Block> ReadBlockFromStreamInternal(Stream stream, long blockIdForErrorMessage)
        {
            // Use BinaryReader for easier reading of fixed types
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            long streamStartPos = stream.Position; // Remember start for length checks

            try
            {
                // --- Read and Verify Header ---
                if (stream.Length - stream.Position < HeaderFixedFieldsSize + HeaderChecksumSize)
                    return Result<Block>.Failure($"Insufficient data for header and checksum for Block ID {blockIdForErrorMessage}.");

                byte[] headerBytes = reader.ReadBytes(HeaderFixedFieldsSize);
                uint storedHeaderChecksum = reader.ReadUInt32();
                uint computedHeaderChecksum = ComputeChecksum(headerBytes);

                if (storedHeaderChecksum != computedHeaderChecksum)
                    return Result<Block>.Failure($"Header checksum mismatch for Block ID {blockIdForErrorMessage}");

                // --- Parse Header Fields ---
                // Use MemoryStream for safe parsing without affecting main stream position yet
                using (var headerStream = new MemoryStream(headerBytes))
                using (var headerReader = new BinaryReader(headerStream))
                {
                    ulong headerMagic = headerReader.ReadUInt64();
                    if (headerMagic != HEADER_MAGIC)
                        return Result<Block>.Failure($"Invalid header magic for Block ID {blockIdForErrorMessage}");

                    var block = new Block
                    {
                        Version = headerReader.ReadUInt16(),
                        Type = (BlockType)headerReader.ReadByte(),
                        Flags = headerReader.ReadByte(),
                        PayloadEncoding = (PayloadEncoding)headerReader.ReadByte(), // Added
                        Timestamp = headerReader.ReadInt64(),
                        BlockId = headerReader.ReadInt64()
                        // PayloadLength read next from headerBytes directly
                    };
                    // Ensure BlockId matches the one requested (sanity check)
                    if (block.BlockId != blockIdForErrorMessage)
                         return Result<Block>.Failure($"Block ID mismatch. Expected {blockIdForErrorMessage}, found {block.BlockId} in header.");


                    long payloadLength = BitConverter.ToInt64(headerBytes, PayloadLengthOffset); // Read from original header bytes

                    if (payloadLength < 0)
                         return Result<Block>.Failure($"Invalid negative payload length ({payloadLength}) in header for Block ID {blockIdForErrorMessage}.");


                    // --- Read and Verify Payload ---
                    if (stream.Length - stream.Position < payloadLength + PayloadChecksumSize)
                         return Result<Block>.Failure($"Insufficient data for payload and checksum for Block ID {blockIdForErrorMessage}.");

                    if (payloadLength > 0)
                    {
                        block.Payload = reader.ReadBytes((int)payloadLength); // Read payload from main stream
                        if (block.Payload.Length != payloadLength)
                            return Result<Block>.Failure($"Incomplete payload read for Block ID {blockIdForErrorMessage}. Expected {payloadLength}, got {block.Payload.Length}.");

                        uint storedPayloadChecksum = reader.ReadUInt32();
                        uint computedPayloadChecksum = ComputeChecksum(block.Payload);
                        if (storedPayloadChecksum != computedPayloadChecksum)
                            return Result<Block>.Failure($"Payload checksum mismatch for Block ID {blockIdForErrorMessage}");
                    }
                    else
                    {
                        block.Payload = Array.Empty<byte>();
                        uint storedPayloadChecksum = reader.ReadUInt32(); // Read the checksum for empty payload
                        if (storedPayloadChecksum != 0) // Checksum for empty payload must be 0
                            return Result<Block>.Failure($"Payload checksum mismatch for empty payload for Block ID {blockIdForErrorMessage}. Expected 0, got {storedPayloadChecksum}.");
                    }

                    // --- Read and Verify Footer ---
                     if (stream.Length - stream.Position < FooterSize)
                         return Result<Block>.Failure($"Insufficient data for footer for Block ID {blockIdForErrorMessage}.");

                    ulong footerMagic = reader.ReadUInt64();
                    if (footerMagic != FOOTER_MAGIC)
                        return Result<Block>.Failure($"Invalid footer magic for Block ID {blockIdForErrorMessage}");

                    long storedBlockLength = reader.ReadInt64();
                    long computedBlockLength = HeaderFixedFieldsSize + HeaderChecksumSize + payloadLength + PayloadChecksumSize + FooterSize;
                    if (storedBlockLength != computedBlockLength)
                        return Result<Block>.Failure($"Block length mismatch in footer for Block ID {blockIdForErrorMessage}. Expected {computedBlockLength}, got {storedBlockLength}.");

                    // --- Final Check: Did we consume the expected number of bytes? ---
                    long bytesConsumed = stream.Position - streamStartPos;
                    if (bytesConsumed != computedBlockLength)
                         return Result<Block>.Failure($"Internal read error: Consumed {bytesConsumed} bytes, but computed block length was {computedBlockLength} for Block ID {blockIdForErrorMessage}.");


                    return Result<Block>.Success(block);
                } // End using headerStream/headerReader
            }
            catch (EndOfStreamException ex)
            {
                 return Result<Block>.Failure($"End of stream reached unexpectedly while reading Block ID {blockIdForErrorMessage}: {ex.Message}");
            }
            catch (Exception ex) // Catch other potential errors during reading/parsing
            {
                 return Result<Block>.Failure($"Unexpected error reading stream for Block ID {blockIdForErrorMessage}: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes CRC32 checksum for a read-only byte span.
        /// </summary>
        private static uint ComputeChecksum(ReadOnlySpan<byte> data)
        {
            // Force.Crc32 primarily works with arrays. Convert span to array.
            // This might allocate if the span isn't array-backed, but ensures compatibility.
            byte[] array = data.ToArray();
            return Crc32Algorithm.Compute(array);
        }

        /// <summary>
        /// Computes CRC32 checksum for a byte array.
        /// </summary>
        private static uint ComputeChecksum(byte[] data)
        {
            return Crc32Algorithm.Compute(data);
        }

        /// <summary>
        /// Computes CRC32 checksum for a segment of a byte array.
        /// </summary>
        private static uint ComputeChecksum(byte[] data, int offset, int count)
        {
            return Crc32Algorithm.Compute(data, offset, count);
        }

        /// <summary>
        /// Throws ObjectDisposedException if the manager is disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (isDisposed)
            {
                throw new ObjectDisposedException(nameof(RawBlockManager), // Updated name
                    "This RawBlockManager instance has been disposed.");
            }
        }

        #endregion
    }
}