using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Serializes the <b>plaintext</b> KeyStore DEK table (BlockType 8,
/// EmailDB_FileFormat_Spec.md Section 9.2). This is the payload BEFORE the KEK-encryption
/// wrapper: the bootstrap decrypts the block with the KEK, then hands the recovered
/// plaintext here to rebuild the <see cref="KeyStoreBlock"/> the provider is built from.
///
/// <para>Layout (all multi-byte integers little-endian, file-wide convention, spec Section 4):</para>
///
///   KeyStoreVersion (2) + ActiveEpoch (2) + EntryCount (4) +
///   EntryCount × { Epoch (2) + Retired (1) + CreatedTimestamp (8) + [DEK (32) unless Retired] }
///
/// <para>Following the idiom of <see cref="WalSerializer"/> / <see cref="CheckpointSerializer"/>:
/// <see cref="Serialize"/> throws <see cref="ArgumentException"/> on invariant violations
/// (serializing an inconsistent table is a programming error), while
/// <see cref="Deserialize"/> returns a <see cref="Result{T}"/> and bounds-checks the declared
/// entry count and every per-entry length against the actual payload size BEFORE reading a
/// body byte — a truncated or corrupt table is rejected, never partially loaded. The retired
/// flag drives whether 32 DEK bytes follow, so a pruned epoch stores no key material.</para>
/// </summary>
public static class KeyStoreSerializer
{
    private const int VersionOffset = 0;            // 2
    private const int ActiveEpochOffset = 2;        // 2
    private const int EntryCountOffset = 4;         // 4
    private const int EntriesOffset = 8;

    /// <summary>Bytes of the fixed prefix: KeyStoreVersion + ActiveEpoch + EntryCount.</summary>
    public const int FixedPrefixSize = EntriesOffset;

    /// <summary>Bytes of a single entry's fixed part: Epoch + Retired + CreatedTimestamp (DEK follows unless retired).</summary>
    public const int EntryFixedSize = sizeof(ushort) + sizeof(byte) + sizeof(long); // 11

    /// <summary>
    /// Serializes a KeyStore table into a new little-endian buffer at the spec offsets.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The entry list is null, an epoch appears twice, the active epoch has no live entry, or a
    /// live entry's DEK is not exactly 32 bytes.
    /// </exception>
    public static byte[] Serialize(KeyStoreBlock keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(keyStore.Entries);

        var seen = new HashSet<ushort>();
        bool activeIsLive = false;
        int total = FixedPrefixSize;
        foreach (var e in keyStore.Entries)
        {
            if (e is null)
                throw new ArgumentException("KeyStore entries must not be null.", nameof(keyStore));
            if (!seen.Add(e.Epoch))
                throw new ArgumentException($"Duplicate KeyStore entry for epoch {e.Epoch}.", nameof(keyStore));
            if (!e.Retired)
            {
                if (e.Dek is null || e.Dek.Length != KeyStoreEntry.DekSize)
                    throw new ArgumentException(
                        $"Live DEK for epoch {e.Epoch} must be exactly {KeyStoreEntry.DekSize} bytes.", nameof(keyStore));
                if (e.Epoch == keyStore.ActiveEpoch)
                    activeIsLive = true;
            }
            total += EntryFixedSize + (e.Retired ? 0 : KeyStoreEntry.DekSize);
        }

        if (!activeIsLive)
            throw new ArgumentException(
                $"Active epoch {keyStore.ActiveEpoch} has no live DEK entry; a KeyStore must be able to encrypt new blocks.",
                nameof(keyStore));

        var buffer = new byte[total];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(VersionOffset, 2), keyStore.KeyStoreVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(ActiveEpochOffset, 2), keyStore.ActiveEpoch);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(EntryCountOffset, 4), keyStore.Entries.Count);

        int pos = EntriesOffset;
        foreach (var e in keyStore.Entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(pos, 2), e.Epoch);
            pos += 2;
            span[pos++] = e.Retired ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos, 8), e.CreatedTimestamp);
            pos += 8;
            if (!e.Retired)
            {
                e.Dek.CopyTo(span.Slice(pos, KeyStoreEntry.DekSize));
                pos += KeyStoreEntry.DekSize;
            }
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes a plaintext KeyStore table, bounds-checking the declared entry count and
    /// every per-entry length against the actual payload size before reading any body byte.
    /// </summary>
    /// <param name="payload">The decrypted KeyStore payload bytes.</param>
    /// <returns>The parsed table, or a failure describing the first inconsistency found.</returns>
    public static Result<KeyStoreBlock> Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FixedPrefixSize)
            return Result<KeyStoreBlock>.Failure(
                $"KeyStore payload is {payload.Length} bytes, shorter than the {FixedPrefixSize}-byte fixed prefix.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(VersionOffset, 2));
        var activeEpoch = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(ActiveEpochOffset, 2));
        var entryCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(EntryCountOffset, 4));

        if (entryCount < 0)
            return Result<KeyStoreBlock>.Failure($"KeyStore declares a negative entry count ({entryCount}).");

        var entries = new List<KeyStoreEntry>(Math.Min(entryCount, 1024));
        var seen = new HashSet<ushort>();
        int pos = EntriesOffset;
        for (int i = 0; i < entryCount; i++)
        {
            if (pos + EntryFixedSize > payload.Length)
                return Result<KeyStoreBlock>.Failure(
                    $"KeyStore entry {i} fixed part extends past the {payload.Length}-byte payload (truncated table).");

            var epoch = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos, 2));
            pos += 2;
            var retiredByte = payload[pos++];
            if (retiredByte > 1)
                return Result<KeyStoreBlock>.Failure(
                    $"KeyStore entry {i} has an invalid Retired flag ({retiredByte}); expected 0 or 1.");
            bool retired = retiredByte == 1;
            var created = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(pos, 8));
            pos += 8;

            byte[] dek = Array.Empty<byte>();
            if (!retired)
            {
                if (pos + KeyStoreEntry.DekSize > payload.Length)
                    return Result<KeyStoreBlock>.Failure(
                        $"KeyStore entry {i} DEK extends past the {payload.Length}-byte payload (truncated table).");
                dek = payload.Slice(pos, KeyStoreEntry.DekSize).ToArray();
                pos += KeyStoreEntry.DekSize;
            }

            if (!seen.Add(epoch))
                return Result<KeyStoreBlock>.Failure($"KeyStore contains a duplicate entry for epoch {epoch}.");

            entries.Add(new KeyStoreEntry
            {
                Epoch = epoch,
                Dek = dek,
                CreatedTimestamp = created,
                Retired = retired,
            });
        }

        return Result<KeyStoreBlock>.Success(new KeyStoreBlock
        {
            KeyStoreVersion = version,
            ActiveEpoch = activeEpoch,
            Entries = entries,
        });
    }
}
