using System.Text;
using Tenray.ZoneTree.Comparers;
using Tenray.ZoneTree.Serializers;

namespace EmailDB.Format.ZoneTree;

public class StringSerializer : ISerializer<string>, IRefComparer<string>
{
    public int Compare(in string x, in string y)
    {
        return string.Compare(x, y, StringComparison.Ordinal);
    }

    public string Deserialize(Memory<byte> bytes)
    {
        return Encoding.UTF8.GetString(bytes.Span);
    }

    public Memory<byte> Serialize(in string entry)
    {
        return Encoding.UTF8.GetBytes(entry ?? string.Empty);
    }
}