using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Asf;

// Reads the fields of one object in order and adds a node for each to the object's node, at the
// bytes it was read from. Reading past the reader's end throws EndOfStreamException, which the
// caller turns into a finding about the object.
internal sealed class Fields(ByteReader reader, Node node)
{
    // The most of a text or byte value that is read into memory to be shown; the node still covers
    // all of it.
    public const int MaxText = 4096;

    public ByteReader Reader => reader;

    public Node Node => node;

    public long Position => reader.Position;

    public long Remaining => reader.Remaining;

    public bool AtEnd => reader.Remaining == 0;

    public byte Byte(string name, string description, string? unit = null, string? topic = null)
    {
        long at = reader.Position;
        byte value = reader.ReadByte();
        node.Add(name, at, 1, NodeValue.Integer(value, unit), description, topic);
        return value;
    }

    public ushort Word(string name, string description, string? unit = null, string? topic = null) =>
        Word(name, description, v => NodeValue.Integer(v, unit), topic);

    public ushort Word(string name, string description, Func<ushort, NodeValue> show, string? topic = null)
    {
        long at = reader.Position;
        ushort value = reader.ReadUInt16LittleEndian();
        node.Add(name, at, 2, show(value), description, topic);
        return value;
    }

    public uint Dword(string name, string description, string? unit = null, string? topic = null) =>
        Dword(name, description, v => NodeValue.Integer(v, unit), topic);

    public uint Dword(string name, string description, Func<uint, NodeValue> show, string? topic = null)
    {
        long at = reader.Position;
        uint value = reader.ReadUInt32LittleEndian();
        node.Add(name, at, 4, show(value), description, topic);
        return value;
    }

    public ulong Qword(string name, string description, string? unit = null, string? topic = null) =>
        Qword(name, description, v => v <= long.MaxValue ? NodeValue.Integer((long)v, unit) : NodeValue.Hex(v, 16), topic);

    public ulong Qword(string name, string description, Func<ulong, NodeValue> show, string? topic = null)
    {
        long at = reader.Position;
        ulong value = reader.ReadUInt64LittleEndian();
        node.Add(name, at, 8, show(value), description, topic);
        return value;
    }

    // A field of 1, 2 or 4 bytes whose width a length type gives; width 0 reads nothing.
    public uint Variable(string name, string description, int width, string? unit = null, string? topic = null)
    {
        long at = reader.Position;
        uint value = width switch
        {
            1 => reader.ReadByte(),
            2 => reader.ReadUInt16LittleEndian(),
            4 => reader.ReadUInt32LittleEndian(),
            _ => 0,
        };
        if (width > 0)
        {
            node.Add(name, at, width, NodeValue.Integer(value, unit), description, topic);
        }

        return value;
    }

    public Guid Guid(string name, string description, Func<Guid, string?>? known = null)
    {
        long at = reader.Position;
        Span<byte> bytes = stackalloc byte[16];
        reader.Read(bytes);
        var value = new Guid(bytes);
        node.Add(name, at, 16, AsfGuids.Describe(value, known?.Invoke(value)), description);
        return value;
    }

    // UTF-16LE text of byteCount bytes, without its terminating NULs. Only the first MaxText bytes
    // are read into memory; the node covers all of them.
    public string Unicode(string name, long byteCount, string description, string? topic = null)
    {
        long at = reader.Position;
        string text = ReadUnicode(byteCount);
        node.Add(name, at, byteCount, text, description, topic);
        return text;
    }

    public string ReadUnicode(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount > reader.Remaining)
        {
            throw new EndOfStreamException();
        }

        int read = (int)Math.Min(byteCount, MaxText) & ~1;
        byte[] bytes = reader.ReadBytes(read);
        reader.Skip(byteCount - read);
        string text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        return byteCount > MaxText ? text + "…" : text;
    }

    // An ASCII string of byteCount bytes, as the encryption objects hold theirs.
    public string Ascii(string name, long byteCount, string description)
    {
        long at = reader.Position;
        if (byteCount > reader.Remaining)
        {
            throw new EndOfStreamException();
        }

        int read = (int)Math.Min(byteCount, MaxText);
        byte[] bytes = reader.ReadBytes(read);
        reader.Skip(byteCount - read);
        string text = Encoding.Latin1.GetString(bytes).TrimEnd('\0');
        node.Add(name, at, byteCount, byteCount > MaxText ? text + "…" : text, description);
        return text;
    }

    // Bytes that are shown by their count only.
    public Node Bytes(string name, long count, string description, string? topic = null)
    {
        long at = reader.Position;
        if (count > reader.Remaining)
        {
            throw new EndOfStreamException();
        }

        reader.Skip(count);
        return node.Add(new Node(name, at, count, NodeValue.Integer(count, "bytes", "byte")) { Description = description, Topic = topic });
    }

    // A flag or a field of bits in the integer at offset, numbered from its least significant bit
    // as the specification numbers them, which is how a little-endian integer lies in its bytes.
    public static Node Bits(Node parent, string name, long offset, int firstBit, int width, NodeValue value, string description, string? topic = null)
    {
        Node field = Node.FromBits(name, offset, firstBit, firstBit + width, value, description: description, topic: topic, order: BitOrder.LeastSignificantFirst);
        return parent.Add(field);
    }

    public static string FileTime(ulong ticks)
    {
        if (ticks == 0)
        {
            return "0 (not set)";
        }

        // DateTime.FromFileTimeUtc accepts up to the year 30828.
        if (ticks > 2650467743999999999UL)
        {
            return $"0x{ticks:X16} (out of range)";
        }

        return DateTime.FromFileTimeUtc((long)ticks).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + " UTC";
    }

    // 100-nanosecond units as seconds.
    public static NodeValue Seconds(ulong ticks) => NodeValue.Decimal(ticks / 10_000_000.0, 3, "s");
}
