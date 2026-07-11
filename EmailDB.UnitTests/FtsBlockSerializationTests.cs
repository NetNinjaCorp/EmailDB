using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;
// Disambiguate from the test project's own Models.EmailHashedID.
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the Phase 1 FTS block structures (US-EMDB-91-6, docs/Search.md,
/// EmailDB_FileFormat_Spec.md Section 5): the serialization/deserialization of block
/// types 14-17 — FTSSegmentMeta, FTSTermDictionary, FTSPostingList, FTSSearchRoot — plus
/// the always-encrypted policy enforcement for those types and the SearchRoot's
/// registration in the Checkpoint's IndexKind-3 secondary table. Ingest (task 91-7) and
/// the query path (task 91-8) build on these structures.
/// </summary>
public class FtsBlockSerializationTests
{
    private static V3Id Id(byte seed) =>
        new(Enumerable.Range(0, V3Id.Size).Select(i => (byte)(seed + i)).ToArray());

    private static byte[] Ulid(byte seed) =>
        Enumerable.Range(0, UlidGenerator.UlidSize).Select(i => (byte)(seed + i)).ToArray();

    // ------------------------------------------------------------- Posting list (16)

    [Fact]
    public void PostingList_round_trips_with_field_flags()
    {
        var list = FtsPostingList.FromPostings(new Trigram('r', 'y', 'a'), new[]
        {
            new FtsPosting(Id(50), AddressField.From),
            new FtsPosting(Id(10), AddressField.To | AddressField.Cc),
            new FtsPosting(Id(90), AddressField.Cc),
        });

        var bytes = FtsPostingListSerializer.Serialize(list);
        Assert.Equal(FtsPostingListSerializer.PayloadSize(3), bytes.Length);

        var round = FtsPostingListSerializer.Deserialize(bytes);
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(new Trigram('r', 'y', 'a'), round.Value.Trigram);
        // Sorted ascending by email; Id(10) < Id(50) < Id(90).
        Assert.Equal(Id(10), round.Value.Postings[0].Email);
        Assert.Equal(AddressField.To | AddressField.Cc, round.Value.Postings[0].Fields);
        Assert.Equal(Id(50), round.Value.Postings[1].Email);
        Assert.Equal(Id(90), round.Value.Postings[2].Email);
    }

    [Fact]
    public void PostingList_FromPostings_merges_fields_of_a_repeated_email()
    {
        var list = FtsPostingList.FromPostings(new Trigram('a', 'b', 'c'), new[]
        {
            new FtsPosting(Id(1), AddressField.From),
            new FtsPosting(Id(1), AddressField.Cc),
        });
        Assert.Single(list.Postings);
        Assert.Equal(AddressField.From | AddressField.Cc, list.Postings[0].Fields);
    }

    [Fact]
    public void PostingList_empty_round_trips()
    {
        var list = new FtsPostingList { Trigram = new Trigram('x', 'y', 'z'), Postings = Array.Empty<FtsPosting>() };
        var round = FtsPostingListSerializer.Deserialize(FtsPostingListSerializer.Serialize(list));
        Assert.True(round.IsSuccess, round.Error);
        Assert.Empty(round.Value.Postings);
    }

    [Fact]
    public void PostingList_serialize_rejects_unsorted_or_empty_fields()
    {
        var unsorted = new FtsPostingList
        {
            Trigram = new Trigram('a', 'b', 'c'),
            Postings = new[] { new FtsPosting(Id(90), AddressField.From), new FtsPosting(Id(10), AddressField.To) },
        };
        Assert.Throws<ArgumentException>(() => FtsPostingListSerializer.Serialize(unsorted));

        var noField = new FtsPostingList
        {
            Trigram = new Trigram('a', 'b', 'c'),
            Postings = new[] { new FtsPosting(Id(10), AddressField.None) },
        };
        Assert.Throws<ArgumentException>(() => FtsPostingListSerializer.Serialize(noField));
    }

    [Fact]
    public void PostingList_deserialize_rejects_truncated_and_unsorted_payloads()
    {
        var list = FtsPostingList.FromPostings(new Trigram('a', 'b', 'c'), new[]
        {
            new FtsPosting(Id(10), AddressField.From),
            new FtsPosting(Id(20), AddressField.From),
        });
        var bytes = FtsPostingListSerializer.Serialize(list);

        // Truncated by one byte.
        Assert.True(FtsPostingListSerializer.Deserialize(bytes.AsSpan(0, bytes.Length - 1)).IsFailure);

        // Corrupt the second email so it is <= the first (unsorted) → rejected.
        var corrupt = (byte[])bytes.Clone();
        int secondEmailOffset = FtsPostingListSerializer.FixedPrefixSize + FtsPostingListSerializer.EntrySize;
        Array.Clear(corrupt, secondEmailOffset, V3Id.Size); // second email = all zero < first
        Assert.True(FtsPostingListSerializer.Deserialize(corrupt).IsFailure);
    }

    // ------------------------------------------------------------- Term dictionary (15)

    [Fact]
    public void TermDictionary_round_trips_sorted_by_trigram()
    {
        var dict = FtsTermDictionary.FromMap(new Dictionary<Trigram, byte[]>
        {
            [new Trigram('y', 'a', 'n')] = Ulid(20),
            [new Trigram('r', 'y', 'a')] = Ulid(10),
        });

        var bytes = FtsTermDictionarySerializer.Serialize(dict);
        Assert.Equal(FtsTermDictionarySerializer.PayloadSize(2), bytes.Length);

        var round = FtsTermDictionarySerializer.Deserialize(bytes);
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(new Trigram('r', 'y', 'a'), round.Value.Entries[0].Trigram); // sorted
        Assert.Equal(Ulid(10), round.Value.Entries[0].PostingListBlockId);
        Assert.Equal(new Trigram('y', 'a', 'n'), round.Value.Entries[1].Trigram);
    }

    [Fact]
    public void TermDictionary_deserialize_rejects_unsorted_and_length_mismatch()
    {
        var dict = FtsTermDictionary.FromMap(new Dictionary<Trigram, byte[]>
        {
            [new Trigram('a', 'a', 'a')] = Ulid(1),
            [new Trigram('b', 'b', 'b')] = Ulid(2),
        });
        var bytes = FtsTermDictionarySerializer.Serialize(dict);

        // Declared count says 2 but strip an entry's worth of bytes → length mismatch.
        Assert.True(FtsTermDictionarySerializer
            .Deserialize(bytes.AsSpan(0, bytes.Length - FtsTermDictionarySerializer.EntrySize)).IsFailure);

        // Overwrite the second trigram with the first (equal → not strictly ascending).
        var corrupt = (byte[])bytes.Clone();
        int secondTriOffset = FtsTermDictionarySerializer.FixedPrefixSize + FtsTermDictionarySerializer.EntrySize;
        new Trigram('a', 'a', 'a').WriteTo(corrupt.AsSpan(secondTriOffset, Trigram.Size));
        Assert.True(FtsTermDictionarySerializer.Deserialize(corrupt).IsFailure);
    }

    // ------------------------------------------------------------- Segment meta (14)

    [Fact]
    public void SegmentMeta_round_trips_all_fields()
    {
        var meta = FtsSegmentMeta.Create(
            segmentSequence: 0xDEAD_BEEF_1234UL, termDictionaryBlockId: Ulid(7),
            trigramCount: 4096, emailCount: 100_000);

        var bytes = FtsSegmentMetaSerializer.Serialize(meta);
        Assert.Equal(FtsSegmentMetaSerializer.PayloadSize, bytes.Length);

        var round = FtsSegmentMetaSerializer.Deserialize(bytes);
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(0xDEAD_BEEF_1234UL, round.Value.SegmentSequence);
        Assert.Equal(Ulid(7), round.Value.TermDictionaryBlockId);
        Assert.Equal(4096u, round.Value.TrigramCount);
        Assert.Equal(100_000u, round.Value.EmailCount);
    }

    [Fact]
    public void SegmentMeta_deserialize_rejects_a_wrong_length_payload()
    {
        Assert.True(FtsSegmentMetaSerializer.Deserialize(new byte[FtsSegmentMetaSerializer.PayloadSize - 1]).IsFailure);
        Assert.True(FtsSegmentMetaSerializer.Deserialize(new byte[FtsSegmentMetaSerializer.PayloadSize + 1]).IsFailure);
    }

    // ------------------------------------------------------------- Search root (17)

    [Fact]
    public void SearchRoot_round_trips_its_segment_list()
    {
        var root = FtsSearchRoot.Create(42, new[] { Ulid(1), Ulid(50), Ulid(200) });

        var bytes = FtsSearchRootSerializer.Serialize(root);
        Assert.Equal(FtsSearchRootSerializer.PayloadSize(3), bytes.Length);

        var round = FtsSearchRootSerializer.Deserialize(bytes);
        Assert.True(round.IsSuccess, round.Error);
        Assert.Equal(42UL, round.Value.SearchRootSequence);
        Assert.Equal(3, round.Value.SegmentCount);
        Assert.Equal(Ulid(1), round.Value.SegmentMetaBlockIds[0]);
        Assert.Equal(Ulid(200), round.Value.SegmentMetaBlockIds[2]);
    }

    [Fact]
    public void SearchRoot_empty_round_trips()
    {
        var root = FtsSearchRoot.Create(0, Array.Empty<byte[]>());
        var round = FtsSearchRootSerializer.Deserialize(FtsSearchRootSerializer.Serialize(root));
        Assert.True(round.IsSuccess, round.Error);
        Assert.Empty(round.Value.SegmentMetaBlockIds);
    }

    [Fact]
    public void SearchRoot_deserialize_rejects_a_count_length_mismatch()
    {
        var root = FtsSearchRoot.Create(1, new[] { Ulid(1), Ulid(2) });
        var bytes = FtsSearchRootSerializer.Serialize(root);
        // Bump the declared segment count without adding bytes.
        var corrupt = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(8, 4), 3);
        Assert.True(FtsSearchRootSerializer.Deserialize(corrupt).IsFailure);
    }

    // ------------------------------------------------------------- Checkpoint registration

    [Fact]
    public void SearchRoot_registers_under_IndexKind_Fts_in_the_checkpoint_secondary_table()
    {
        var rootBlockId = Ulid(77);
        var entry = FtsSearchRoot.ToCheckpointSecondaryIndex(rootBlockId, searchRootOffset: 0x9000);

        Assert.Equal(BTreeIndexKind.Fts, entry.IndexKind);
        Assert.Equal((ushort)3, (ushort)entry.IndexKind); // IndexKind 3 per spec Section 6.1
        Assert.Equal(rootBlockId, entry.Pointer.BlockId);
        Assert.Equal(0x9000, entry.Pointer.Offset);
    }

    [Fact]
    public void SearchRoot_checkpoint_registration_rejects_a_bad_block_id_or_offset()
    {
        Assert.Throws<ArgumentException>(() => FtsSearchRoot.ToCheckpointSecondaryIndex(new byte[15], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FtsSearchRoot.ToCheckpointSecondaryIndex(Ulid(1), -1));
    }

    // ------------------------------------------------------------- Always-encrypted policy

    [Theory]
    [InlineData(BlockType.FTSSegmentMeta)]
    [InlineData(BlockType.FTSTermDictionary)]
    [InlineData(BlockType.FTSPostingList)]
    [InlineData(BlockType.FTSSearchRoot)]
    public void Fts_block_types_are_encrypted_under_every_policy(BlockType type)
    {
        // Trigrams reverse to the indexed addresses, so FTS blocks must never be plaintext
        // (spec Section 9.5) — required under both Default and Full.
        Assert.True(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Default, type));
        Assert.True(EncryptionPolicySet.RequiresEncryption(EncryptionPolicy.Full, type));
    }
}
