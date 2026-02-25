using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that the key store round-trips correctly through the full pipeline:
/// serialize -> encrypt -> decrypt -> deserialize.
/// Acceptance criterion for US-EMDB-53.
/// </summary>
public class KeyStoreRoundTripTests
{
    private const int KeySize = 32;

    private static byte[] GenerateKek()
    {
        var kek = new byte[KeySize];
        RandomNumberGenerator.Fill(kek);
        return kek;
    }

    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    [Fact]
    public void RoundTrip_SingleEntry_AllFieldsPreserved()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var dek = GenerateDek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new()
                {
                    Epoch = 0,
                    DEK = dek,
                    Timestamp = new DateTime(2026, 2, 1, 12, 30, 0, DateTimeKind.Utc),
                    Retired = false
                }
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(original.ActiveEpoch, restored.ActiveEpoch);
        Assert.Single(restored.Entries);
        Assert.Equal(original.Entries[0].Epoch, restored.Entries[0].Epoch);
        Assert.Equal(original.Entries[0].DEK, restored.Entries[0].DEK);
        Assert.Equal(original.Entries[0].Timestamp, restored.Entries[0].Timestamp);
        Assert.Equal(original.Entries[0].Retired, restored.Entries[0].Retired);
    }

    [Fact]
    public void RoundTrip_MultipleEpochs_AllEntriesPreserved()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 3,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 2, DEK = GenerateDek(), Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 3, DEK = GenerateDek(), Timestamp = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc), Retired = false },
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(original.ActiveEpoch, restored.ActiveEpoch);
        Assert.Equal(original.Entries.Count, restored.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Epoch, restored.Entries[i].Epoch);
            Assert.Equal(original.Entries[i].DEK, restored.Entries[i].DEK);
            Assert.Equal(original.Entries[i].Timestamp, restored.Entries[i].Timestamp);
            Assert.Equal(original.Entries[i].Retired, restored.Entries[i].Retired);
        }
    }

    [Fact]
    public void RoundTrip_EmptyEntries_PreservesEmptyState()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>()
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(0, restored.ActiveEpoch);
        Assert.Empty(restored.Entries);
    }

    [Fact]
    public void RoundTrip_DoubleRoundTrip_ProducesIdenticalContent()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = GenerateDek(), Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false },
            }
        };

        // First round-trip
        var encrypted1 = manager.EncryptKeyStore(original, kek);
        var restored1 = manager.DecryptKeyStore(encrypted1, kek);

        // Second round-trip from restored data
        var encrypted2 = manager.EncryptKeyStore(restored1, kek);
        var restored2 = manager.DecryptKeyStore(encrypted2, kek);

        Assert.Equal(original.ActiveEpoch, restored2.ActiveEpoch);
        Assert.Equal(original.Entries.Count, restored2.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Epoch, restored2.Entries[i].Epoch);
            Assert.Equal(original.Entries[i].DEK, restored2.Entries[i].DEK);
            Assert.Equal(original.Entries[i].Timestamp, restored2.Entries[i].Timestamp);
            Assert.Equal(original.Entries[i].Retired, restored2.Entries[i].Retired);
        }
    }

    [Fact]
    public void RoundTrip_SerializationIsIdempotent_BytesMatchAfterTwoRoundTrips()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false }
            }
        };

        // Serialize original
        var bytes1 = serializer.Serialize(original);

        // Full round-trip: serialize -> encrypt -> decrypt -> deserialize
        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        // Re-serialize restored content
        var bytes2 = serializer.Serialize(restored);

        Assert.Equal(bytes1, bytes2);
    }

    [Fact]
    public void RoundTrip_DekBytesAreExact32Bytes_AfterRoundTrip()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var dek = GenerateDek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(32, restored.Entries[0].DEK.Length);
        Assert.Equal(dek, restored.Entries[0].DEK);
    }

    [Fact]
    public void RoundTrip_RetiredAndActiveFlags_PreservedCorrectly()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 2, DEK = GenerateDek(), Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false },
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        Assert.True(restored.Entries[0].Retired);
        Assert.True(restored.Entries[1].Retired);
        Assert.False(restored.Entries[2].Retired);
    }

    [Fact]
    public void RoundTrip_EachDekDistinct_AfterRoundTrip()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false },
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        // Each DEK should remain distinct after round-trip
        Assert.NotEqual(restored.Entries[0].DEK, restored.Entries[1].DEK);
        Assert.NotEqual(restored.Entries[1].DEK, restored.Entries[2].DEK);
        Assert.NotEqual(restored.Entries[0].DEK, restored.Entries[2].DEK);
    }

    [Fact]
    public void RoundTrip_ManualPipeline_SerializeEncryptDecryptDeserialize()
    {
        // Explicitly exercises each stage separately to prove the full pipeline
        var serializer = new DefaultBlockContentSerializer();
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = GenerateDek(), Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false },
            }
        };

        // Stage 1: Serialize
        var plaintext = serializer.Serialize(original);
        Assert.NotNull(plaintext);
        Assert.NotEmpty(plaintext);

        // Stage 2: Encrypt with AES-256-GCM
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Verify ciphertext differs from plaintext
        Assert.NotEqual(plaintext, ciphertext);

        // Stage 3: Decrypt
        var decrypted = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, decrypted);

        // Decrypted bytes should match original serialized bytes
        Assert.Equal(plaintext, decrypted);

        // Stage 4: Deserialize
        var restored = serializer.Deserialize<KeyStoreContent>(decrypted);

        Assert.Equal(original.ActiveEpoch, restored.ActiveEpoch);
        Assert.Equal(original.Entries.Count, restored.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Epoch, restored.Entries[i].Epoch);
            Assert.Equal(original.Entries[i].DEK, restored.Entries[i].DEK);
            Assert.Equal(original.Entries[i].Timestamp, restored.Entries[i].Timestamp);
            Assert.Equal(original.Entries[i].Retired, restored.Entries[i].Retired);
        }
    }

    [Fact]
    public void RoundTrip_DifferentKeksProduceDifferentCiphertext_ButSamePlaintext()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek1 = GenerateKek();
        var kek2 = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false }
            }
        };

        var encrypted1 = manager.EncryptKeyStore(original, kek1);
        var encrypted2 = manager.EncryptKeyStore(original, kek2);

        // Different KEKs produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);

        // But each decrypts correctly with its own KEK
        var restored1 = manager.DecryptKeyStore(encrypted1, kek1);
        var restored2 = manager.DecryptKeyStore(encrypted2, kek2);

        Assert.Equal(restored1.ActiveEpoch, restored2.ActiveEpoch);
        Assert.Equal(restored1.Entries[0].DEK, restored2.Entries[0].DEK);
    }

    [Fact]
    public void RoundTrip_ManyEntries_AllPreserved()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var entries = new List<KeyStoreEntry>();
        for (int i = 0; i < 50; i++)
        {
            entries.Add(new KeyStoreEntry
            {
                Epoch = i,
                DEK = GenerateDek(),
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                Retired = i < 49
            });
        }

        var original = new KeyStoreContent
        {
            ActiveEpoch = 49,
            Entries = entries
        };

        var encrypted = manager.EncryptKeyStore(original, kek);
        var restored = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(50, restored.Entries.Count);
        Assert.Equal(49, restored.ActiveEpoch);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(original.Entries[i].Epoch, restored.Entries[i].Epoch);
            Assert.Equal(original.Entries[i].DEK, restored.Entries[i].DEK);
            Assert.Equal(original.Entries[i].Timestamp, restored.Entries[i].Timestamp);
            Assert.Equal(original.Entries[i].Retired, restored.Entries[i].Retired);
        }
    }

    [Fact]
    public void RoundTrip_WrongKek_FailsDecryption()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var wrongKek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, wrongKek));
    }

    [Fact]
    public void RoundTrip_TamperedPayload_FailsDecryption()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var original = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var encrypted = manager.EncryptKeyStore(original, kek);

        // Tamper with ciphertext
        encrypted[14] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, kek));
    }
}
