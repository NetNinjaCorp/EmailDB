using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// Why a decrypt could not obtain a DEK for a block's header <c>KeyEpoch</c>. These are
/// deliberately kept apart from a <see cref="WrongKeyOrTamperError"/>: the GCM tag never
/// even ran because there was no key to try. The header checksum has already verified the
/// KeyEpoch bytes, so reaching either case means the file genuinely references an epoch
/// this key store cannot serve — not a bit-flip.
/// </summary>
public enum EpochDekUnavailableReason
{
    /// <summary>
    /// No entry for the epoch exists in the DEK table at all. Either the wrong file/key
    /// store was opened, or the reference is bogus. Never silently fall back to another
    /// epoch (spec Section 13: MUST NOT brute-force other epochs).
    /// </summary>
    Missing = 0,

    /// <summary>
    /// The epoch's entry exists but its DEK has been retired (pruned after compaction
    /// re-encryption): the key bytes are gone. A live block still pointing at a retired
    /// epoch is a dangling reference — that block should have been re-encrypted before its
    /// DEK was dropped.
    /// </summary>
    Retired = 1,
}

/// <summary>
/// A decrypt could not resolve a usable DEK for the block header's <c>KeyEpoch</c>:
/// either the epoch is <see cref="EpochDekUnavailableReason.Missing"/> from the DEK table
/// or its DEK has been <see cref="EpochDekUnavailableReason.Retired"/>. Distinct from
/// <see cref="WrongKeyOrTamperError"/> (a real GCM/AAD authentication failure) — here the
/// cipher never ran because there was no key. Distinct from
/// <see cref="CorruptionError"/> (damaged bytes) — the KeyEpoch field passed its header
/// checksum. Derives from <see cref="CryptographicException"/> so it surfaces in the
/// crypto error domain and never leaks as a bare <see cref="KeyNotFoundException"/>.
/// </summary>
public sealed class EpochDekUnavailableError : CryptographicException
{
    /// <summary>Whether the epoch was missing entirely or present-but-retired.</summary>
    public EpochDekUnavailableReason Reason { get; }

    /// <summary>The header KeyEpoch that could not be resolved to a live DEK.</summary>
    public int KeyEpoch { get; }

    /// <summary>The affected block's ULID (16 raw bytes), when known.</summary>
    public byte[]? BlockId { get; }

    private EpochDekUnavailableError(string message, EpochDekUnavailableReason reason, int keyEpoch, byte[]? blockId)
        : base(message)
    {
        Reason = reason;
        KeyEpoch = keyEpoch;
        BlockId = blockId is null ? null : (byte[])blockId.Clone();
    }

    /// <summary>The epoch has no entry in the DEK table (wrong file/key store, or bogus reference).</summary>
    public static EpochDekUnavailableError Missing(int keyEpoch, byte[]? blockId = null)
        => new($"No DEK for KeyEpoch {keyEpoch}{BlockSuffix(blockId)}: the epoch is not present in this file's key store " +
               "(wrong key store, or a bogus reference). Not falling back to other epochs (spec Section 13).",
            EpochDekUnavailableReason.Missing, keyEpoch, blockId);

    /// <summary>The epoch's DEK was pruned/retired; a live block still references it (dangling reference).</summary>
    public static EpochDekUnavailableError Retired(int keyEpoch, byte[]? blockId = null)
        => new($"DEK for KeyEpoch {keyEpoch}{BlockSuffix(blockId)} has been retired (pruned after compaction re-encryption); " +
               "a block still referencing it is a dangling reference and cannot be decrypted.",
            EpochDekUnavailableReason.Retired, keyEpoch, blockId);

    private static string BlockSuffix(byte[]? blockId)
        => blockId is null ? "" : $" (block {Convert.ToHexStringLower(blockId)})";
}
