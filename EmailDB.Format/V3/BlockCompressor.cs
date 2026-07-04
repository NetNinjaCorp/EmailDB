using K4os.Compression.LZ4.Streams;
using ZstdSharp;

namespace EmailDB.Format.V3;

/// <summary>
/// v3 payload compression pipeline (EmailDB_FileFormat_Spec.md Section 4.4).
/// Dispatches on the header compression byte: None (0x00), LZ4 frame (0x01),
/// and Zstd frame (0x02) are implemented; Brotli/Deflate and reserved values
/// fail cleanly until a build ships them.
///
/// Position in the spec write order (Serialize → Compress → Encrypt →
/// Checksum → Append): compression runs on the serialized plaintext, before
/// encryption. On read it is the inverse: decrypt first, then decompress.
/// The LZ4/Zstd frame formats carry the uncompressed length themselves, so it
/// is not duplicated in the block header.
///
/// Decompression bomb guard: a declared frame length is attacker-controlled
/// data, so decoding never trusts it — output is counted as it streams out and
/// decoding fails once it exceeds MaxPayloadLength × 16 (spec Section 4.4).
/// Corrupt or malicious input yields a failed <see cref="Result{T}"/>, never
/// an exception (spec Section 13).
/// </summary>
public static class BlockCompressor
{
    /// <summary>
    /// Bomb guard multiplier: decompressed output may not exceed
    /// MaxPayloadLength × 16 (spec Section 4.4).
    /// </summary>
    public const int BombGuardMultiplier = 16;

    /// <summary>Chunk size for streaming decompressed output past the bomb guard counter.</summary>
    private const int DecodeBufferSize = 81920;

    /// <summary>
    /// Compresses a serialized (plaintext) payload with the given algorithm.
    /// <see cref="CompressionAlgorithm.None"/> passes the bytes through
    /// unchanged. Unsupported algorithms yield a failed result.
    /// </summary>
    /// <param name="payload">Serialized payload bytes (may be empty).</param>
    /// <param name="compression">Algorithm recorded in the header compression byte.</param>
    /// <returns>The on-frame bytes to encrypt (if enabled) and append.</returns>
    public static Result<byte[]> Compress(ReadOnlySpan<byte> payload, CompressionAlgorithm compression)
    {
        switch (compression)
        {
            case CompressionAlgorithm.None:
                return Result<byte[]>.Success(payload.ToArray());

            case CompressionAlgorithm.Lz4:
            {
                var output = new MemoryStream();
                using (var encoder = LZ4Stream.Encode(output, leaveOpen: true))
                    encoder.Write(payload);
                return Result<byte[]>.Success(output.ToArray());
            }

            case CompressionAlgorithm.Zstd:
            {
                var output = new MemoryStream();
                using (var encoder = new CompressionStream(output, leaveOpen: true))
                    encoder.Write(payload);
                return Result<byte[]>.Success(output.ToArray());
            }

            default:
                return Result<byte[]>.Failure(
                    $"Compression algorithm 0x{(byte)compression:X2} ({compression}) is not supported by this build.");
        }
    }

    /// <summary>
    /// Decompresses a (decrypted) payload per its header compression byte,
    /// enforcing the bomb guard: output beyond
    /// <paramref name="maxPayloadLength"/> × <see cref="BombGuardMultiplier"/>
    /// is treated as payload corruption (spec Sections 4.4, 13) and yields a
    /// failed result. <see cref="CompressionAlgorithm.None"/> passes the bytes
    /// through unchanged.
    /// </summary>
    /// <param name="payload">Compressed payload bytes (plaintext — already decrypted).</param>
    /// <param name="compression">Algorithm from the verified block header.</param>
    /// <param name="maxPayloadLength">
    /// Sanity bound for PayloadLength, from the superblock
    /// (<see cref="Superblock.MaxPayloadLength"/>); the bomb guard is 16× this.
    /// </param>
    public static Result<byte[]> Decompress(
        ReadOnlySpan<byte> payload, CompressionAlgorithm compression, long maxPayloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadLength);

        switch (compression)
        {
            case CompressionAlgorithm.None:
                return Result<byte[]>.Success(payload.ToArray());

            case CompressionAlgorithm.Lz4:
            case CompressionAlgorithm.Zstd:
            {
                var guard = maxPayloadLength <= long.MaxValue / BombGuardMultiplier
                    ? maxPayloadLength * BombGuardMultiplier
                    : long.MaxValue;
                var input = new MemoryStream(payload.ToArray(), writable: false);
                try
                {
                    using Stream decoder = compression == CompressionAlgorithm.Lz4
                        ? LZ4Stream.Decode(input)
                        : new DecompressionStream(input);
                    return DecodeBounded(decoder, guard, compression);
                }
                catch (Exception ex)
                {
                    // Decoders surface corrupt frames as library-specific exceptions;
                    // corruption must be a failed result, not a throw (spec Section 13).
                    return Result<byte[]>.Failure(
                        $"{compression} decompression failed; treating payload as corrupt: {ex.Message}");
                }
            }

            default:
                return Result<byte[]>.Failure(
                    $"Compression algorithm 0x{(byte)compression:X2} ({compression}) is not supported by this build.");
        }
    }

    /// <summary>
    /// Streams decompressed bytes out of <paramref name="decoder"/>, counting
    /// them against the bomb guard so the guard trips while decoding, before
    /// an oversized payload is ever fully buffered.
    /// </summary>
    private static Result<byte[]> DecodeBounded(
        Stream decoder, long maxDecompressedLength, CompressionAlgorithm compression)
    {
        var output = new MemoryStream();
        var buffer = new byte[DecodeBufferSize];
        long total = 0;
        int read;
        while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxDecompressedLength)
                return Result<byte[]>.Failure(
                    $"{compression} decompression exceeded the bomb guard of {maxDecompressedLength} bytes (MaxPayloadLength × {BombGuardMultiplier}); treating payload as corrupt (spec Sections 4.4, 13).");
            output.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(output.ToArray());
    }
}
