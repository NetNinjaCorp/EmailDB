namespace EmailDB.Format.Encryption;

/// <summary>
/// Reads and writes <see cref="EncryptionHeader"/> to/from a stream.
/// The binary layout is:
///   Magic (4 bytes) | SchemeVersion (1 byte) | AlgorithmId (1 byte)
///   KdfType (1 byte) | Salt (16 bytes) | TokenLength (4 bytes) | KeyVerificationToken (variable)
/// </summary>
public static class EncryptionHeaderManager
{
    /// <summary>Fixed portion size: 4 + 1 + 1 + 1 + 16 + 4 = 27 bytes.</summary>
    public const int FixedSize = 27;

    /// <summary>
    /// Writes an <see cref="EncryptionHeader"/> to the given stream at its current position.
    /// </summary>
    public static void WriteHeader(Stream stream, EncryptionHeader header)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);

        if (!stream.CanWrite)
            throw new InvalidOperationException("Stream is not writable.");

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(header.Magic);                          // 4 bytes
        writer.Write(header.SchemeVersion);                   // 1 byte
        writer.Write(header.AlgorithmId);                     // 1 byte
        writer.Write(header.KdfType);                         // 1 byte
        writer.Write(header.Salt);                            // 16 bytes
        writer.Write(header.KeyVerificationToken.Length);      // 4 bytes (token length prefix)
        writer.Write(header.KeyVerificationToken);             // variable
    }

    /// <summary>
    /// Reads an <see cref="EncryptionHeader"/> from the given stream at its current position.
    /// </summary>
    public static Result<EncryptionHeader> ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
            return Result<EncryptionHeader>.Failure("Stream is not readable.");

        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            byte[] magic = reader.ReadBytes(4);
            if (magic.Length < 4)
                return Result<EncryptionHeader>.Failure("Stream too short to contain encryption header magic.");

            byte schemeVersion = reader.ReadByte();
            byte algorithmId = reader.ReadByte();
            byte kdfType = reader.ReadByte();

            byte[] salt = reader.ReadBytes(16);
            if (salt.Length < 16)
                return Result<EncryptionHeader>.Failure("Stream too short to contain encryption header salt.");

            int tokenLength = reader.ReadInt32();
            if (tokenLength < 0)
                return Result<EncryptionHeader>.Failure("Invalid token length in encryption header.");

            byte[] token = reader.ReadBytes(tokenLength);
            if (token.Length < tokenLength)
                return Result<EncryptionHeader>.Failure("Stream too short to contain key verification token.");

            var header = new EncryptionHeader
            {
                Magic = magic,
                SchemeVersion = schemeVersion,
                AlgorithmId = algorithmId,
                KdfType = kdfType,
                Salt = salt,
                KeyVerificationToken = token
            };

            return Result<EncryptionHeader>.Success(header);
        }
        catch (EndOfStreamException)
        {
            return Result<EncryptionHeader>.Failure("Unexpected end of stream while reading encryption header.");
        }
    }

    /// <summary>
    /// Checks whether the stream begins with the EncryptionHeader magic bytes ("EMDB").
    /// Does not advance the stream position.
    /// </summary>
    public static bool HasEncryptionHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
            return false;

        if (stream.Length < 4)
            return false;

        long originalPosition = stream.Position;
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            Span<byte> magic = stackalloc byte[4];
            int bytesRead = stream.Read(magic);
            if (bytesRead < 4)
                return false;

            return magic.SequenceEqual(EncryptionHeader.MagicBytes);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}
