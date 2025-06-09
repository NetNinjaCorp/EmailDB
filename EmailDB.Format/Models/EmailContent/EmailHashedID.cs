using System;
using System.Text;
using DZen.Security.Cryptography;
using MimeKit;
using SimpleBase;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Serializers;

public struct EmailHashedID : IComparable<EmailHashedID>, IEquatable<EmailHashedID>, ISerializer<EmailHashedID>, IRefComparer<EmailHashedID>
{
    // Store the full 256 bits (32 bytes) of the SHA3-256 hash
    private readonly ulong _part1; // First 8 bytes
    private readonly ulong _part2; // Second 8 bytes
    private readonly ulong _part3; // Third 8 bytes
    private readonly ulong _part4; // Fourth 8 bytes

    public ulong Part1 => _part1;
    public ulong Part2 => _part2;
    public ulong Part3 => _part3;
    public ulong Part4 => _part4;

    // Define a static default instance
    public static readonly EmailHashedID Empty = new EmailHashedID(new byte[32]);

    // Property to check if this is a default/empty instance
    public bool IsEmpty => _part1 == 0 && _part2 == 0 && _part3 == 0 && _part4 == 0;

    // Constructor for database reconstruction
    public EmailHashedID(ulong part1, ulong part2, ulong part3, ulong part4)
    {
        _part1 = part1;
        _part2 = part2;
        _part3 = part3;
        _part4 = part4;
    }

    public EmailHashedID()
    {
        _part1 = 0;
        _part2 = 0;
        _part3 = 0;
        _part4 = 0;
    }
    public EmailHashedID(byte[] hash)
    {
        if (hash == null || hash.Length != 32)
            throw new ArgumentException("Hash must be exactly 32 bytes (SHA3-256).", nameof(hash));

        _part1 = BitConverter.ToUInt64(hash, 0);
        _part2 = BitConverter.ToUInt64(hash, 8);
        _part3 = BitConverter.ToUInt64(hash, 16);
        _part4 = BitConverter.ToUInt64(hash, 24);
    }

    public EmailHashedID(string MessageID, long EmailTime, string EmailSubject, string EmailFrom, string EmailTo)
    {
        using var sha3 = SHA3.Create();
        var hash = sha3.ComputeHash(Encoding.UTF8.GetBytes(
            MessageID + EmailTime + EmailSubject + EmailFrom + EmailTo));

        _part1 = BitConverter.ToUInt64(hash, 0);
        _part2 = BitConverter.ToUInt64(hash, 8);
        _part3 = BitConverter.ToUInt64(hash, 16);
        _part4 = BitConverter.ToUInt64(hash, 24);
    }

    public EmailHashedID(MimeMessage message)
    {
        using var sha3 = SHA3.Create();
        var hash = sha3.ComputeHash(Encoding.UTF8.GetBytes(
            message.MessageId +
            message.Date.ToUnixTimeMilliseconds() +
            message.Subject +
            message.From.ToString() +
            message.To.ToString()));

        _part1 = BitConverter.ToUInt64(hash, 0);
        _part2 = BitConverter.ToUInt64(hash, 8);
        _part3 = BitConverter.ToUInt64(hash, 16);
        _part4 = BitConverter.ToUInt64(hash, 24);
    }

    public byte[] GetBytes()
    {
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), _part1);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), _part2);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 8), _part3);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 8), _part4);
        return bytes;
    }

    public string ToBase32String()
    {
        return Base32.ZBase32.Encode(GetBytes());
    }

    public static EmailHashedID FromBase32String(string base32)
    {
        var bytes = Base32.ZBase32.Decode(base32);
        if (bytes.Length != 32)
            throw new ArgumentException("Invalid base32 string length", nameof(base32));
        return new EmailHashedID(bytes);
    }

    public override bool Equals(object obj)
    {
        return obj is EmailHashedID other && Equals(other);
    }

    public bool Equals(EmailHashedID other)
    {
        return _part1 == other._part1 &&
               _part2 == other._part2 &&
               _part3 == other._part3 &&
               _part4 == other._part4;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + _part1.GetHashCode();
            hash = (hash * 31) + _part2.GetHashCode();
            hash = (hash * 31) + _part3.GetHashCode();
            hash = (hash * 31) + _part4.GetHashCode();
            return hash;
        }
    }

    public int CompareTo(EmailHashedID other)
    {
        int comparison = _part1.CompareTo(other._part1);
        if (comparison != 0) return comparison;

        comparison = _part2.CompareTo(other._part2);
        if (comparison != 0) return comparison;

        comparison = _part3.CompareTo(other._part3);
        if (comparison != 0) return comparison;

        return _part4.CompareTo(other._part4);
    }

    public override string ToString()
    {
        return ToBase32String();
    }

    public static bool TryParse(string input, out EmailHashedID result)
    {
        try
        {
            result = FromBase32String(input);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
    {
        var str = ToBase32String();
        if (destination.Length < str.Length)
        {
            charsWritten = 0;
            return false;
        }
        str.CopyTo(destination);
        charsWritten = str.Length;
        return true;
    }

    public EmailHashedID Deserialize(Memory<byte> bytes)
    {
        return new EmailHashedID(bytes.ToArray());
    }

    public Memory<byte> Serialize(in EmailHashedID entry)
    {
        return GetBytes();
    }

    public int Compare(in EmailHashedID x, in EmailHashedID y)
    {
        return x.CompareTo(y);
    }
}