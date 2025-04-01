using EmailDB.Format.CapnProto.Models;
using Force.Crc32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;

namespace EmailDB.Format.CapnProto
{
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
        public const int HeaderSize = 36;
        public const int HeaderChecksumSize = 4;
        public const int PayloadChecksumSize = 4;
        public const int FooterSize = 16;
        public const int TotalFixedOverhead = HeaderSize + HeaderChecksumSize + PayloadChecksumSize + FooterSize;

        #endregion

        #region Private Fields

        private readonly string filePath;
        private readonly FileStream fileStream;
        private readonly ReaderWriterLockSlim fileLock;
        private readonly ConcurrentDictionary<long, BlockLocation> blockLocations;
        private long currentPosition;
        private bool isDisposed;

        #endregion

        #region Constructor & Disposal

        public RawBlockManager(string filePath, bool createIfNotExists = true)
        {
            this.filePath = filePath;
            FileMode fileMode = createIfNotExists ? FileMode.OpenOrCreate : FileMode.Open;
            this.fileStream = new FileStream(filePath, fileMode, FileAccess.ReadWrite, FileShare.Read);
            this.fileLock = new ReaderWriterLockSlim();
            this.blockLocations = new ConcurrentDictionary<long, BlockLocation>();
            this.currentPosition = this.fileStream.Length;
        }

        public void Dispose()
        {
            if (!isDisposed)
            {
                fileStream.Flush();
                fileStream.Close();
                fileLock.Dispose();
                fileStream.Dispose();
                isDisposed = true;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Writes a new block to the end of the file in a thread-safe manner.
        /// </summary>
        public async Task<BlockLocation> WriteBlockAsync(Block block, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            // Prepare the block data in memory first, outside the lock
            byte[] blockData;
            using (var ms = new MemoryStream())
            {
                WriteBlockToStream(ms, block);
                blockData = ms.ToArray();
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
                fileStream.Write(blockData, 0, blockData.Length);
                fileStream.Flush();

                // Update the current position
                currentPosition = fileStream.Position;

                // Create and store the block location
                location = new BlockLocation
                {
                    Position = blockStartPosition,
                    Length = blockData.Length
                };

                blockLocations.AddOrUpdate(block.BlockId, location, (key, oldValue) => location);
            }
            finally
            {
                fileLock.ExitWriteLock();
            }

            return location;
        }

        /// <summary>
        /// Reads a block from the file by its ID in a thread-safe manner.
        /// </summary>
        public async Task<Block> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!blockLocations.TryGetValue(blockId, out BlockLocation location))
            {
                throw new KeyNotFoundException($"Block ID {blockId} not found");
            }

            try
            {
                fileLock.EnterReadLock();

                fileStream.Seek(location.Position, SeekOrigin.Begin);
                byte[] buffer = new byte[location.Length];

                await fileStream.ReadAsync(buffer, 0, (int)location.Length, cancellationToken);

                using (var ms = new MemoryStream(buffer))
                {
                    return ReadBlockFromStream(ms);
                }
            }
            finally
            {
                fileLock.ExitReadLock();
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

        public List<long> ScanFile()
        {
            return FindMagicPositions();
        }



        /// <summary>
        /// Compacts the file by removing outdated blocks and rewriting active ones.
        /// </summary>
        public async Task CompactAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                fileLock.EnterWriteLock();

                string tempFilePath = filePath + ".temp";
                using (var tempManager = new BlockManager(tempFilePath))
                {
                    // Copy all current blocks to the temporary file
                    foreach (var kvp in blockLocations)
                    {
                        var block = await ReadBlockAsync(kvp.Key, cancellationToken);
                        await tempManager.WriteBlockAsync(block, cancellationToken);
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
                fileLock.ExitWriteLock();
            }
        }

        #endregion

        #region Private Methods


        /// Returns a list of absolute positions where the magic was found.
        /// </summary>
        /// <param name="filename">The file to scan.</param>
        /// <param name="magic">The magic byte sequence to look for.</param>
        /// <returns>A list of absolute file positions where the magic sequence appears.</returns>
        private List<long> FindMagicPositions()
        {
            List<long> magicPositions = new List<long>();
            long fileLength = new FileInfo(filePath).Length;
            const int chunkSize = 16384;
            int overlap = 8;
            byte[] buffer = new byte[chunkSize + overlap];
            long lastProcessedAbsolute = -1;
            byte[] headerMagic = BitConverter.GetBytes(HEADER_MAGIC);
            try
            {
                fileLock.EnterReadLock();
                using (var mmf = MemoryMappedFile.CreateFromFile(fileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false))
                {
                    // 'position' is the start of the current chunk (excluding any overlap from a previous chunk).
                    long position = 0;
                    while (position < fileLength)
                    {
                        // Determine how many new bytes to read without exceeding the file length.
                        int bytesToRead = (int)Math.Min(chunkSize, fileLength - position);
                        // For the first chunk, fill from index 0; subsequent chunks leave room for the overlap.
                        int writeOffset = (position == 0) ? 0 : overlap;

                        // Read the chunk into the buffer.
                        using (var accessor = mmf.CreateViewAccessor(position, bytesToRead, MemoryMappedFileAccess.Read))
                        {
                            accessor.ReadArray(0, buffer, writeOffset, bytesToRead);
                        }

                        // Total valid bytes: for the first chunk it's just bytesToRead; for others, include the overlap.
                        int totalBytes = (position == 0) ? bytesToRead : (overlap + bytesToRead);
                        ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, totalBytes);

                        int localOffset = 0;
                        while (localOffset < span.Length)
                        {
                            int index = span.Slice(localOffset).IndexOf(headerMagic);
                            if (index == -1)
                                break;

                            // Compute the absolute position in the file.
                            long basePosition = (position == 0) ? position : (position - overlap);
                            long absoluteIndex = basePosition + localOffset + index - 16;

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
            }catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                fileLock.ExitReadLock();
            }
            return magicPositions;
        }

        private void ScanExistingBlocks()
        {
            try
            {
                fileLock.EnterWriteLock();
                var fileLength = new FileInfo(filePath)?.Length ?? 1;
                using (var mmf = MemoryMappedFile.CreateFromFile(fileStream, null, fileLength, MemoryMappedFileAccess.Read, HandleInheritability.None, false))
                using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {

                    long position = 0;

                    while (position < fileLength)
                    {
                        // Try to read block at current position
                        if (TryReadBlockLocation(accessor, position, fileLength, out BlockLocation location, out long blockId))
                        {
                            blockLocations.TryAdd(blockId, location);
                            position = location.Position + location.Length;
                        }
                        else
                        {
                            // If we couldn't read a block, move forward by one byte and try again
                            position++;
                        }
                    }
                }
            }
            finally
            {
                fileLock.ExitWriteLock();
            }
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
            blockId = accessor.ReadInt64(position + 20); // 20 is the offset to BlockId in header
            long payloadLength = accessor.ReadInt64(position + 28); // 28 is the offset to PayloadLength in header

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
            writer.Write(TotalFixedOverhead + (block.Payload?.Length ?? 0));
        }

        private Block ReadBlockFromStream(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // Read and verify header
            byte[] headerBytes = reader.ReadBytes(HeaderSize);
            if (headerBytes.Length != HeaderSize)
            {
                throw new InvalidDataException("Incomplete header");
            }

            uint storedHeaderChecksum = reader.ReadUInt32();
            uint computedHeaderChecksum = ComputeChecksum(headerBytes);
            if (storedHeaderChecksum != computedHeaderChecksum)
            {
                throw new InvalidDataException("Header checksum mismatch");
            }

            // Parse header
            using (var headerStream = new MemoryStream(headerBytes))
            using (var headerReader = new BinaryReader(headerStream))
            {
                ulong headerMagic = headerReader.ReadUInt64();
                if (headerMagic != HEADER_MAGIC)
                {
                    throw new InvalidDataException("Invalid header magic");
                }

                var block = new Block
                {
                    Version = headerReader.ReadUInt16(),
                    Type = (BlockType)headerReader.ReadByte(),
                    Flags = headerReader.ReadByte(),
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
                        throw new InvalidDataException("Incomplete payload");
                    }

                    uint storedPayloadChecksum = reader.ReadUInt32();
                    uint computedPayloadChecksum = ComputeChecksum(block.Payload);
                    if (storedPayloadChecksum != computedPayloadChecksum)
                    {
                        throw new InvalidDataException("Payload checksum mismatch");
                    }
                }
                else
                {
                    reader.ReadUInt32(); // Skip empty payload checksum
                }

                // Verify footer
                ulong footerMagic = reader.ReadUInt64();
                if (footerMagic != FOOTER_MAGIC)
                {
                    throw new InvalidDataException("Invalid footer magic");
                }

                long storedBlockLength = reader.ReadInt64();
                long computedBlockLength = HeaderSize + HeaderChecksumSize + payloadLength + PayloadChecksumSize + FooterSize;
                if (storedBlockLength != computedBlockLength)
                {
                    throw new InvalidDataException("Block length mismatch");
                }

                return block;
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
                throw new ObjectDisposedException(nameof(BlockManager),
                    "This BlockManager instance has been disposed.");
            }
        }

        #endregion
    }
}