namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of a v3 superblock slot (EmailDB_FileFormat_Spec.md Section 3.1).
/// One 4096-byte slot; two slots (A at offset 0, B at offset 4096) form the dual-slot
/// superblock. Serialization to/from the on-disk layout is handled by
/// <see cref="SuperblockSerializer"/>.
/// </summary>
public sealed class Superblock
{
    /// <summary>Default sanity bound for PayloadLength: 256 MB.</summary>
    public const long DefaultMaxPayloadLength = 268_435_456;

    /// <summary>Format version stored in the slot. Always 3 for this spec.</summary>
    public ushort FormatVersion { get; set; } = SuperblockSerializer.CurrentFormatVersion;

    /// <summary>Monotonic write sequence; the higher valid slot wins on open.</summary>
    public ulong SuperblockSequence { get; set; }

    /// <summary>ULID minted at file creation (16 raw bytes, big-endian ULID layout); identifies this shard forever.</summary>
    public byte[] FileId { get; set; } = new byte[16];

    /// <summary>Zero-based shard index within the mailbox.</summary>
    public uint ShardIndex { get; set; }

    /// <summary>UTC ticks at file creation.</summary>
    public long CreatedTimestamp { get; set; }

    /// <summary>Unknown bits: reader proceeds normally.</summary>
    public uint CompatFlags { get; set; }

    /// <summary>Unknown bits: reader may open read-only, MUST NOT write.</summary>
    public uint ReadOnlyCompatFlags { get; set; }

    /// <summary>Unknown bits: reader MUST refuse to open.</summary>
    public uint IncompatFlags { get; set; }

    /// <summary>1 = last session closed cleanly; 0 = crash recovery may be needed.</summary>
    public byte CleanShutdown { get; set; }

    /// <summary>Sanity bound for block PayloadLength.</summary>
    public long MaxPayloadLength { get; set; } = DefaultMaxPayloadLength;

    /// <summary>ULID of a recent Checkpoint block (16 raw bytes; all-zero = none yet). A hint, not a commit point.</summary>
    public byte[] LastCheckpointBlockId { get; set; } = new byte[16];

    /// <summary>File offset of the hinted Checkpoint block.</summary>
    public long LastCheckpointOffset { get; set; }

    /// <summary>0 = plaintext file, 1 = encrypted.</summary>
    public byte EncryptionEnabled { get; set; }

    /// <summary>1 = AES-256-GCM.</summary>
    public byte AlgorithmId { get; set; }

    /// <summary>1 = Argon2id.</summary>
    public byte KdfType { get; set; }

    /// <summary>Opaque per-KDF packing, 16 bytes (spec Section 3.2).</summary>
    public byte[] KdfParams { get; set; } = new byte[16];

    /// <summary>CSPRNG salt for the KDF, 16 bytes.</summary>
    public byte[] Salt { get; set; } = new byte[16];

    /// <summary>Nonce(12) + AES-GCM(KEK, "EMDB")(4) + Tag(16) = 32 bytes.</summary>
    public byte[] KeyVerificationToken { get; set; } = new byte[32];

    /// <summary>ULID of the current KeyStore block (16 raw bytes).</summary>
    public byte[] ActiveKeyStoreBlockId { get; set; } = new byte[16];

    /// <summary>File offset of the active KeyStore block.</summary>
    public long ActiveKeyStoreOffset { get; set; }

    /// <summary>
    /// Creates a deep copy (byte-array fields are duplicated), so the copy can be
    /// mutated and written without aliasing the original instance.
    /// </summary>
    public Superblock Clone() => new()
    {
        FormatVersion = FormatVersion,
        SuperblockSequence = SuperblockSequence,
        FileId = (byte[])FileId.Clone(),
        ShardIndex = ShardIndex,
        CreatedTimestamp = CreatedTimestamp,
        CompatFlags = CompatFlags,
        ReadOnlyCompatFlags = ReadOnlyCompatFlags,
        IncompatFlags = IncompatFlags,
        CleanShutdown = CleanShutdown,
        MaxPayloadLength = MaxPayloadLength,
        LastCheckpointBlockId = (byte[])LastCheckpointBlockId.Clone(),
        LastCheckpointOffset = LastCheckpointOffset,
        EncryptionEnabled = EncryptionEnabled,
        AlgorithmId = AlgorithmId,
        KdfType = KdfType,
        KdfParams = (byte[])KdfParams.Clone(),
        Salt = (byte[])Salt.Clone(),
        KeyVerificationToken = (byte[])KeyVerificationToken.Clone(),
        ActiveKeyStoreBlockId = (byte[])ActiveKeyStoreBlockId.Clone(),
        ActiveKeyStoreOffset = ActiveKeyStoreOffset,
    };
}
