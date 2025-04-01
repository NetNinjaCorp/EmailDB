using EmailDB.Format; // For RawBlockManager, Result, etc.
using EmailDB.Format.Models; // For Block, BlockType, PayloadEncoding
using Google.Protobuf; // For Protobuf parsing/serialization
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf
{
    /// <summary>
    /// Manages metadata operations and Block ID generation,
    /// assuming metadata payload is Protobuf encoded.
    /// </summary>
    public class MetadataManager
    {
        private readonly RawBlockManager _rawBlockManager;
        // Consider adding a lock specific to ID generation if high contention is expected,
        // although RawBlockManager's internal lock might suffice if updates are infrequent.
        private static readonly SemaphoreSlim _idGenerationLock = new SemaphoreSlim(1, 1);


        public MetadataManager(RawBlockManager rawBlockManager)
        {
            _rawBlockManager = rawBlockManager ?? throw new ArgumentNullException(nameof(rawBlockManager));
        }

        /// <summary>
        /// Gets the next available Block ID by reading and updating the latest Metadata block.
        /// This operation is designed to be atomic at the block level.
        /// </summary>
        /// <returns>A Result containing the next available Block ID on success.</returns>
        public async Task<Result<long>> GetNextBlockIdAsync(CancellationToken cancellationToken = default)
        {
            // Use a semaphore to ensure only one thread attempts to update the metadata block at a time.
            await _idGenerationLock.WaitAsync(cancellationToken);
            try
            {
                long latestMetadataId = _rawBlockManager.GetLatestMetadataBlockId();
                if (latestMetadataId <= 0) // Assuming 0 or less is invalid/not found
                {
                    // Handle initialization case: No metadata block found.
                    // This requires a strategy: either fail, or create the *first* metadata block.
                    // For now, let's assume initialization happens elsewhere or fails here.
                    return Result<long>.Failure("No valid Metadata block found. Database may need initialization.");
                }

                // 1. Read the latest metadata block
                Result<Block> readResult = await _rawBlockManager.ReadBlockAsync(latestMetadataId, cancellationToken);
                if (readResult.IsFailure)
                {
                    return Result<long>.Failure($"Failed to read latest metadata block (ID: {latestMetadataId}): {readResult.Error}");
                }
                Block currentMetadataBlock = readResult.Value;

                // 2. Validate block type and encoding
                if (currentMetadataBlock.Type != BlockType.Metadata)
                {
                    return Result<long>.Failure($"Block ID {latestMetadataId} is not a Metadata block (Type: {currentMetadataBlock.Type}).");
                }
                if (currentMetadataBlock.PayloadEncoding != PayloadEncoding.Protobuf)
                {
                     return Result<long>.Failure($"Metadata block ID {latestMetadataId} has unexpected encoding: {currentMetadataBlock.PayloadEncoding}. Expected Protobuf.");
                }
                if (currentMetadataBlock.Payload == null || currentMetadataBlock.Payload.Length == 0)
                {
                     return Result<long>.Failure($"Metadata block ID {latestMetadataId} has empty payload.");
                }


                // 3. Deserialize payload
                MetadataPayload currentPayload;
                try
                {
                    currentPayload = MetadataPayload.Parser.ParseFrom(currentMetadataBlock.Payload);
                }
                catch (InvalidProtocolBufferException ex)
                {
                    return Result<long>.Failure($"Failed to parse Protobuf payload for metadata block ID {latestMetadataId}: {ex.Message}");
                }
                catch (Exception ex) // Catch other potential exceptions during parsing
                {
                     return Result<long>.Failure($"Unexpected error parsing payload for metadata block ID {latestMetadataId}: {ex.Message}");
                }


                // 4. Get current ID and prepare updated payload
                long idToReturn = currentPayload.NextBlockId;
                if (idToReturn <= 0) // Basic sanity check
                {
                     return Result<long>.Failure($"Invalid NextBlockId ({idToReturn}) found in metadata block ID {latestMetadataId}.");
                }


                MetadataPayload nextPayload = new MetadataPayload
                {
                    FileFormatVersion = currentPayload.FileFormatVersion, // Preserve existing values
                    RootFolderTreeId = currentPayload.RootFolderTreeId,
                    CreationTimestampTicks = currentPayload.CreationTimestampTicks,
                    LastCompactionTimestampTicks = currentPayload.LastCompactionTimestampTicks,
                    NextBlockId = idToReturn + 1 // Increment the ID for the *next* request
                };

                // 5. Serialize new payload
                byte[] nextPayloadBytes;
                try
                {
                     nextPayloadBytes = nextPayload.ToByteArray();
                }
                 catch (Exception ex)
                {
                     return Result<long>.Failure($"Failed to serialize updated metadata payload: {ex.Message}");
                }


                // 6. Create the new Block object (using the *same* BlockId)
                Block nextMetadataBlock = new Block
                {
                    BlockId = latestMetadataId, // Overwrite the *same* block ID
                    Version = currentMetadataBlock.Version, // Or potentially increment format version if needed
                    Type = BlockType.Metadata,
                    Flags = currentMetadataBlock.Flags, // Preserve flags
                    PayloadEncoding = PayloadEncoding.Protobuf,
                    Timestamp = DateTime.UtcNow.Ticks, // Update timestamp
                    Payload = nextPayloadBytes
                };

                // 7. Write the updated metadata block
                Result<BlockLocation> writeResult = await _rawBlockManager.WriteBlockAsync(nextMetadataBlock, cancellationToken);
                if (writeResult.IsFailure)
                {
                    // Critical failure: We read the ID but couldn't write the update.
                    // State might be inconsistent. Log error prominently.
                    Console.Error.WriteLine($"CRITICAL: Failed to write updated metadata block (ID: {latestMetadataId}) after reading NextBlockId {idToReturn}. Error: {writeResult.Error}");
                    return Result<long>.Failure($"Failed to write updated metadata block (ID: {latestMetadataId}): {writeResult.Error}");
                }

                // 8. Return the ID that was read *before* incrementing
                return Result<long>.Success(idToReturn);
            }
            catch (Exception ex) // Catch unexpected errors in the overall process
            {
                 // Log the error
                 Console.Error.WriteLine($"Unexpected error during GetNextBlockIdAsync: {ex.Message}\n{ex.StackTrace}");
                 return Result<long>.Failure($"An unexpected error occurred during Block ID generation: {ex.Message}");
            }
            finally
            {
                _idGenerationLock.Release();
            }
        }

        // TODO: Add method for initializing the *first* metadata block if needed.
        // public async Task<Result> InitializeMetadataAsync(...)
    }
}