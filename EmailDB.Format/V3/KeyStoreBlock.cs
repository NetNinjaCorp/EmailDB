using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of the decrypted KeyStore block payload (BlockType 8,
/// EmailDB_FileFormat_Spec.md Section 9.2, docs/Encryption.md Section 1). The block's
/// on-disk payload is <b>entirely KEK-encrypted</b>; this model is what you get after the
/// bootstrap decrypts it with the password-derived KEK. It is the DEK table the
/// <see cref="EpochDekProvider"/> is built from: one entry per epoch, plus the epoch new
/// blocks are encrypted under (<see cref="ActiveEpoch"/>).
///
/// <para>Serialization to/from the plaintext byte layout is handled by
/// <see cref="KeyStoreSerializer"/>.</para>
/// </summary>
public sealed class KeyStoreBlock
{
    /// <summary>Current KeyStore payload format version.</summary>
    public const ushort CurrentVersion = 1;

    /// <summary>
    /// The highest epoch a rotation can produce: the width of the 2-byte block-header
    /// <c>KeyEpoch</c> field. Rotating from here would overflow (docs/Encryption.md Section 5),
    /// so <see cref="Rotate"/> refuses at this ceiling with an <see cref="EpochExhaustedError"/>.
    /// </summary>
    public const ushort MaxEpoch = ushort.MaxValue;

    /// <summary>KeyStore payload format version (spec Section 9.2 <c>KeyStoreVersion</c>).</summary>
    public ushort KeyStoreVersion { get; set; } = CurrentVersion;

    /// <summary>The epoch whose DEK new blocks are encrypted under. Must name a live entry.</summary>
    public ushort ActiveEpoch { get; set; }

    /// <summary>The DEK table: one <see cref="KeyStoreEntry"/> per epoch (spec Section 9.2).</summary>
    public List<KeyStoreEntry> Entries { get; set; } = new();

    /// <summary>
    /// Advances the table by one epoch (docs/Encryption.md Section 5, "Key rotation — O(1)"):
    /// mints a fresh CSPRNG DEK at <c>ActiveEpoch + 1</c>, appends it as a new live entry, and
    /// makes it the active epoch. Every existing entry — DEKs and retired flags alike — is left
    /// untouched, so blocks already written under an older epoch stay decryptable. The new DEK is
    /// drawn from <see cref="RandomNumberGenerator"/>, so it differs from every prior epoch's DEK
    /// with overwhelming probability.
    /// </summary>
    /// <returns>The newly minted entry (its <see cref="KeyStoreEntry.Dek"/> is the fresh key).</returns>
    /// <exception cref="EpochExhaustedError">
    /// <see cref="ActiveEpoch"/> is already <see cref="MaxEpoch"/>: rotating would overflow the
    /// 2-byte epoch field and wrap to 0. Thrown before any mutation, so the table is unchanged.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The table already contains an entry at <c>ActiveEpoch + 1</c> (a malformed, non-monotonic
    /// table). Thrown before any mutation.
    /// </exception>
    public KeyStoreEntry Rotate()
    {
        if (ActiveEpoch == MaxEpoch)
            throw new EpochExhaustedError(ActiveEpoch);

        var newEpoch = (ushort)(ActiveEpoch + 1);
        if (Entries.Any(e => e.Epoch == newEpoch))
            throw new InvalidOperationException(
                $"Cannot rotate to epoch {newEpoch}: the KeyStore already has an entry for it (non-monotonic table).");

        var newEntry = new KeyStoreEntry
        {
            Epoch = newEpoch,
            Dek = RandomNumberGenerator.GetBytes(KeyStoreEntry.DekSize),
            CreatedTimestamp = DateTime.UtcNow.Ticks,
            Retired = false,
        };
        Entries.Add(newEntry);
        ActiveEpoch = newEpoch;
        return newEntry;
    }

    /// <summary>
    /// Projects the table into the <see cref="EpochDekProvider.EpochDek"/> entries the provider
    /// consumes: retired entries carry no key bytes, live entries carry their 32-byte DEK.
    /// </summary>
    public IEnumerable<EpochDekProvider.EpochDek> ToProviderEntries()
    {
        foreach (var e in Entries)
            yield return e.Retired
                ? new EpochDekProvider.EpochDek(e.Epoch, ReadOnlyMemory<byte>.Empty, retired: true)
                : new EpochDekProvider.EpochDek(e.Epoch, e.Dek, retired: false);
    }
}

/// <summary>
/// One epoch's entry in the KeyStore DEK table (spec Section 9.2
/// <c>Entries[] { Epoch, DEK (32 B), CreatedTimestamp, Retired }</c>).
/// </summary>
public sealed class KeyStoreEntry
{
    /// <summary>DEK length in bytes (AES-256).</summary>
    public const int DekSize = AesGcmBlockCipher.KeySize;

    /// <summary>The epoch this DEK belongs to (0-65535); matches the block header <c>KeyEpoch</c>.</summary>
    public ushort Epoch { get; set; }

    /// <summary>The 32-byte AES-256 DEK for this epoch, or an empty array when <see cref="Retired"/>.</summary>
    public byte[] Dek { get; set; } = Array.Empty<byte>();

    /// <summary>UTC ticks the epoch's DEK was minted (audit only; not security-bearing).</summary>
    public long CreatedTimestamp { get; set; }

    /// <summary>True once this epoch's DEK has been pruned; it can no longer decrypt.</summary>
    public bool Retired { get; set; }
}
