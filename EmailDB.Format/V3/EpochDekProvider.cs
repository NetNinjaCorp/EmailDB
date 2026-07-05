using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// The v3 epoch-aware block encryption provider (docs/Encryption.md Sections 1-3,
/// spec Section 9). It owns the in-memory DEK table decrypted from the KeyStore block and
/// wraps the stateless <see cref="AesGcmBlockCipher"/> primitive with the two pieces of
/// state that primitive deliberately does not hold:
///
/// <list type="bullet">
///   <item><b>Encrypt</b> uses the DEK for <see cref="ActiveEpoch"/> and stamps that epoch
///   into the AAD and (by the caller) the block header.</item>
///   <item><b>Decrypt</b> selects the DEK by the block header's <c>KeyEpoch</c> — never by
///   guessing. A missing or retired epoch surfaces as an <see cref="EpochDekUnavailableError"/>,
///   distinct from a real <see cref="WrongKeyOrTamperError"/> (spec Section 13: MUST NOT
///   brute-force other epochs).</item>
/// </list>
///
/// <para><b>Zeroization.</b> The provider takes defensive copies of every DEK (and, when
/// supplied, the password-derived KEK) and zeroizes all of that key material on
/// <see cref="Dispose"/> (docs/Encryption.md Section 7: "Zeroize key material buffers").
/// Callers should therefore scope the provider in a <c>using</c>.</para>
/// </summary>
public sealed class EpochDekProvider : IDisposable
{
    /// <summary>A single epoch's entry in the DEK table: the 32-byte key and its retired state.</summary>
    public readonly struct EpochDek
    {
        /// <summary>DEK epoch (0-65535), matching the block header <c>KeyEpoch</c>.</summary>
        public ushort Epoch { get; }

        /// <summary>
        /// The 32-byte AES-256 DEK, or an empty span when <see cref="Retired"/> is true and the
        /// key bytes have already been pruned.
        /// </summary>
        public ReadOnlyMemory<byte> Dek { get; }

        /// <summary>True once this epoch's DEK has been retired (pruned); it can no longer decrypt.</summary>
        public bool Retired { get; }

        public EpochDek(ushort epoch, ReadOnlyMemory<byte> dek, bool retired = false)
        {
            if (!retired && dek.Length != AesGcmBlockCipher.KeySize)
                throw new ArgumentException(
                    $"A live DEK must be exactly {AesGcmBlockCipher.KeySize} bytes for AES-256-GCM.", nameof(dek));
            Epoch = epoch;
            Dek = dek;
            Retired = retired;
        }
    }

    private sealed class Entry
    {
        public required byte[] Dek;   // owned copy, zeroized on dispose (empty when retired)
        public required bool Retired;
    }

    private readonly byte[] _fileId;
    private readonly Dictionary<ushort, Entry> _table;
    private readonly byte[]? _kek;   // owned copy, zeroized on dispose
    private bool _disposed;

    /// <summary>The epoch new blocks are encrypted under.</summary>
    public ushort ActiveEpoch { get; }

    /// <summary>Bytes encryption adds to a payload (nonce + tag): 28.</summary>
    public int OverheadBytes => AesGcmBlockCipher.Overhead;

    /// <summary>
    /// Builds a provider from a decrypted DEK table.
    /// </summary>
    /// <param name="fileId">16-byte file identifier, an AAD component for every block.</param>
    /// <param name="activeEpoch">Epoch to encrypt new blocks with; must have a live (non-retired) DEK.</param>
    /// <param name="deks">The epoch → DEK table. Each entry is defensively copied.</param>
    /// <param name="kek">
    /// Optional password-derived KEK to take ownership of and zeroize alongside the DEKs when the
    /// provider is disposed. Pass <see langword="default"/> when the caller zeroizes the KEK itself.
    /// </param>
    public EpochDekProvider(
        ReadOnlySpan<byte> fileId,
        ushort activeEpoch,
        IEnumerable<EpochDek> deks,
        ReadOnlySpan<byte> kek = default)
    {
        if (fileId.Length != AesGcmBlockCipher.IdSize)
            throw new ArgumentException($"FileId must be exactly {AesGcmBlockCipher.IdSize} bytes.", nameof(fileId));
        ArgumentNullException.ThrowIfNull(deks);

        _fileId = fileId.ToArray();
        _table = new Dictionary<ushort, Entry>();
        foreach (var e in deks)
        {
            if (_table.ContainsKey(e.Epoch))
                throw new ArgumentException($"Duplicate DEK entry for epoch {e.Epoch}.", nameof(deks));
            _table[e.Epoch] = new Entry
            {
                // Copy so we own the buffer and can zeroize it; retired entries hold no key bytes.
                Dek = e.Retired ? Array.Empty<byte>() : e.Dek.ToArray(),
                Retired = e.Retired,
            };
        }

        if (!_table.TryGetValue(activeEpoch, out var active) || active.Retired)
            throw new ArgumentException(
                $"Active epoch {activeEpoch} has no live DEK in the key store; cannot encrypt.", nameof(activeEpoch));

        ActiveEpoch = activeEpoch;
        _kek = kek.IsEmpty ? null : kek.ToArray();
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under the active epoch's DEK, returning the on-disk
    /// payload <c>Nonce ‖ Ciphertext ‖ Tag</c>. Callers MUST stamp <see cref="ActiveEpoch"/> into
    /// the block header so the matching decrypt can find the DEK again.
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> blockId, BlockType blockType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // ActiveEpoch is guaranteed live by the constructor.
        var dek = _table[ActiveEpoch].Dek;
        return AesGcmBlockCipher.Encrypt(plaintext, dek, _fileId, blockId, blockType, ActiveEpoch);
    }

    /// <summary>
    /// Decrypts an on-disk payload, selecting the DEK by the block header's <paramref name="keyEpoch"/>.
    /// </summary>
    /// <exception cref="EpochDekUnavailableError">
    /// The epoch is missing from the table or its DEK has been retired — no key to try. Distinct
    /// from an authentication failure; the cipher never runs.
    /// </exception>
    /// <exception cref="WrongKeyOrTamperError">
    /// A DEK was found but GCM/AAD authentication failed (wrong key or tampering, spec Section 13).
    /// </exception>
    public byte[] Decrypt(ReadOnlySpan<byte> onDisk, ReadOnlySpan<byte> blockId, BlockType blockType, ushort keyEpoch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_table.TryGetValue(keyEpoch, out var entry))
            throw EpochDekUnavailableError.Missing(keyEpoch, blockId.IsEmpty ? null : blockId.ToArray());
        if (entry.Retired)
            throw EpochDekUnavailableError.Retired(keyEpoch, blockId.IsEmpty ? null : blockId.ToArray());

        return AesGcmBlockCipher.Decrypt(onDisk, entry.Dek, _fileId, blockId, blockType, keyEpoch);
    }

    /// <summary>Whether the table holds a live (non-retired) DEK for <paramref name="epoch"/>.</summary>
    public bool HasLiveDek(ushort epoch) => _table.TryGetValue(epoch, out var e) && !e.Retired;

    /// <summary>
    /// Zeroizes every DEK and the KEK (when owned), then marks the provider unusable. Idempotent.
    /// After disposal all key material this instance held is scrubbed from memory.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var entry in _table.Values)
            CryptographicOperations.ZeroMemory(entry.Dek);
        _table.Clear();

        if (_kek is not null)
            CryptographicOperations.ZeroMemory(_kek);
    }
}
