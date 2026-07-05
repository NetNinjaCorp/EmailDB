using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// The parsed Argon2id parameter set carried in the superblock's 16-byte opaque
/// <c>KdfParams</c> field (EmailDB_FileFormat_Spec.md Section 3.2, docs/Encryption.md
/// Section 2). Every parameter that drives key derivation lives here so that opening a
/// file honours the values <em>stored in that file</em> — implementations MUST never fall
/// back to compiled-in constants during derivation, or a parameter upgrade would brick
/// every existing file (spec Section 3.2: "Readers MUST use stored values").
///
/// <para><b>On-disk packing</b> (16 bytes, little-endian to match the rest of the superblock):</para>
/// <code>[MemoryKB (4B)] [Iterations (2B)] [Parallelism (2B)] [Reserved (8B, zero)]</code>
///
/// <para>The <see cref="Default"/> values (64 MB / 3 iterations / 4 lanes) exist only for
/// <em>creating</em> a new file. They are never consulted when deriving a KEK for an
/// existing file — that path takes its parameters exclusively from <see cref="Unpack"/>.</para>
/// </summary>
public readonly struct Argon2idParams
{
    /// <summary>Length of the packed <c>KdfParams</c> field in bytes.</summary>
    public const int PackedSize = 16;

    /// <summary>Default Argon2id memory cost for new files: 64 MB (spec Section 3.2).</summary>
    public const uint DefaultMemoryKB = 65_536;

    /// <summary>Default Argon2id iteration (time) cost for new files.</summary>
    public const ushort DefaultIterations = 3;

    /// <summary>Default Argon2id parallelism (lane count) for new files.</summary>
    public const ushort DefaultParallelism = 4;

    /// <summary>Memory cost in kibibytes (KB). Argon2 requires this to be at least 8 × <see cref="Parallelism"/>.</summary>
    public uint MemoryKB { get; }

    /// <summary>Iteration (time) cost; at least 1.</summary>
    public ushort Iterations { get; }

    /// <summary>Degree of parallelism (lanes); at least 1.</summary>
    public ushort Parallelism { get; }

    /// <summary>
    /// Constructs a parameter set, rejecting values Argon2id cannot run with. Validation is a
    /// clear-error guard, not a clamp: stored parameters that differ from <see cref="Default"/>
    /// are honoured exactly, never silently replaced.
    /// </summary>
    public Argon2idParams(uint memoryKB, ushort iterations, ushort parallelism)
    {
        if (parallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(parallelism), parallelism,
                "Argon2id parallelism (lanes) must be at least 1.");
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations,
                "Argon2id iterations must be at least 1.");
        // Argon2 lower bound: the memory pool must hold at least 8 blocks per lane.
        if (memoryKB < 8u * parallelism)
            throw new ArgumentOutOfRangeException(nameof(memoryKB), memoryKB,
                $"Argon2id memory must be at least 8 × parallelism ({8u * parallelism} KB) for {parallelism} lane(s).");

        MemoryKB = memoryKB;
        Iterations = iterations;
        Parallelism = parallelism;
    }

    /// <summary>
    /// The default parameters for <em>creating</em> a new encrypted file (64 MB / 3 / 4). Never
    /// used to derive a KEK for an existing file — that path reads parameters from the superblock.
    /// </summary>
    public static Argon2idParams Default => new(DefaultMemoryKB, DefaultIterations, DefaultParallelism);

    /// <summary>
    /// Parses the 16-byte superblock <c>KdfParams</c> field into a validated parameter set. The
    /// 8 reserved bytes are ignored on read (forward compatibility); any tampering with them, or
    /// with the meaningful bytes, changes the derived KEK and is caught later by the
    /// KeyVerificationToken, not here.
    /// </summary>
    /// <param name="kdfParams">Exactly <see cref="PackedSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter Argon2id cannot run with.</exception>
    public static Argon2idParams Unpack(ReadOnlySpan<byte> kdfParams)
    {
        if (kdfParams.Length != PackedSize)
            throw new ArgumentException(
                $"KdfParams must be exactly {PackedSize} bytes, got {kdfParams.Length}.", nameof(kdfParams));

        var memoryKB = BinaryPrimitives.ReadUInt32LittleEndian(kdfParams[..4]);
        var iterations = BinaryPrimitives.ReadUInt16LittleEndian(kdfParams.Slice(4, 2));
        var parallelism = BinaryPrimitives.ReadUInt16LittleEndian(kdfParams.Slice(6, 2));
        // Bytes [8..16) are reserved and MUST be zero on write; ignored on read.
        return new Argon2idParams(memoryKB, iterations, parallelism);
    }

    /// <summary>
    /// Serializes into a superblock <c>KdfParams</c> field. Writes the meaningful bytes and
    /// zeroes the 8 reserved bytes.
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="PackedSize"/> bytes.</param>
    public void Pack(Span<byte> destination)
    {
        if (destination.Length < PackedSize)
            throw new ArgumentException(
                $"KdfParams destination must be at least {PackedSize} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], MemoryKB);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), Iterations);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), Parallelism);
        destination.Slice(8, 8).Clear(); // Reserved (8B, zero)
    }

    /// <summary>Allocates and returns a fresh 16-byte packed <c>KdfParams</c> field.</summary>
    public byte[] Pack()
    {
        var buffer = new byte[PackedSize];
        Pack(buffer);
        return buffer;
    }

    public override string ToString() =>
        $"Argon2id(mem={MemoryKB}KB, iter={Iterations}, lanes={Parallelism})";
}
