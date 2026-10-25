using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aiff;

// Reads the fields of one chunk in order and adds a node for each to the chunk's node, at the bytes it was read from. The integers are
// big-endian (Motorola byte order). Reading past the reader's end throws EndOfStreamException, which the caller turns into a finding about
// the chunk.
internal sealed class Fields(ByteReader reader, Node node)
{
    // The most of a text or byte value that is read into memory to be shown; the node still covers all of it.
    public const int MaxText = 4096;

    public ByteReader Reader => reader;

    public Node Node => node;

    public long Position => reader.Position;

    public long Remaining => reader.Remaining;

    public bool AtEnd => reader.Remaining == 0;

    public byte Byte(string name, string description, NodeValue? shown = null, string? topic = null)
    {
        long at = reader.Position;
        byte value = reader.ReadByte();
        node.Add(name, at, 1, shown ?? NodeValue.Integer(value), description, topic);
        return value;
    }

    public sbyte SignedByte(string name, string description, string? unit = null)
    {
        long at = reader.Position;
        sbyte value = unchecked((sbyte)reader.ReadByte());
        node.Add(name, at, 1, NodeValue.Integer(value, unit), description);
        return value;
    }

    public ushort Word(string name, string description, string? unit = null, string? topic = null) =>
        Word(name, description, v => NodeValue.Integer(v, unit), topic);

    public ushort Word(string name, string description, Func<ushort, NodeValue> show, string? topic = null)
    {
        long at = reader.Position;
        ushort value = ReadWord();
        node.Add(name, at, 2, show(value), description, topic);
        return value;
    }

    public uint Dword(string name, string description, string? unit = null, string? topic = null) =>
        Dword(name, description, v => NodeValue.Integer(v, unit), topic);

    public uint Dword(string name, string description, Func<uint, NodeValue> show, string? topic = null)
    {
        long at = reader.Position;
        uint value = ReadDword();
        node.Add(name, at, 4, show(value), description, topic);
        return value;
    }

    public int SignedWord(string name, string description, string? unit = null)
    {
        long at = reader.Position;
        short value = unchecked((short)ReadWord());
        node.Add(name, at, 2, NodeValue.Integer(value, unit), description);
        return value;
    }

    public ulong Qword(string name, string description, string? unit = null, string? topic = null)
    {
        long at = reader.Position;
        ulong value = reader.ReadUInt64BigEndian();
        node.Add(name, at, 8, value <= long.MaxValue ? NodeValue.Integer((long)value, unit) : NodeValue.Hex(value, 16), description, topic);
        return value;
    }

    public ushort ReadWord() => reader.ReadUInt16BigEndian();

    public uint ReadDword() => reader.ReadUInt32BigEndian();

    // Four characters, a chunk or list type, as the text they spell.
    public string FourCc(string name, string description, string? topic = null)
    {
        long at = reader.Position;
        string text = Encoding.Latin1.GetString(reader.ReadBytes(4));
        node.Add(name, at, 4, text, description, topic);
        return text;
    }

    // Text of byteCount bytes in Latin-1, without its terminating NULs. Only the first
    // MaxText bytes are read into memory; the node covers all of them.
    public string Text(string name, long byteCount, string description, string? topic = null)
    {
        long at = reader.Position;
        string text = ReadText(byteCount);
        node.Add(name, at, byteCount, text, description, topic);
        return text;
    }

    public string ReadText(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount > reader.Remaining)
        {
            throw new EndOfStreamException();
        }

        int read = (int)Math.Min(byteCount, MaxText);
        byte[] bytes = reader.ReadBytes(read);
        reader.Skip(byteCount - read);
        string text = Clean(Encoding.Latin1.GetString(bytes));
        return byteCount > MaxText ? text + "…" : text;
    }

    // Bytes shown by their count only.
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

    // Text with the NULs at its end cut off and any other control character replaced, so a report holds no raw control bytes.
    public static string Clean(string text)
    {
        text = text.TrimEnd('\0');
        if (!text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
        {
            return text;
        }

        var cleaned = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            cleaned.Append(char.IsControl(c) && c is not ('\r' or '\n' or '\t') ? '�' : c);
        }

        return cleaned.ToString();
    }

    public static ushort Word(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16BigEndian(bytes);

    public static uint Dword(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
