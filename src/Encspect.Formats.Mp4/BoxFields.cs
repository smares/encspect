using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Reads a box's fields in order, big-endian as ISO/IEC 14496-12 stores them, and adds a node for
// each at its bytes. A field that runs past the box's end throws EndOfStreamException, which the box
// reports as cut short.
internal sealed class BoxFields(ByteReader reader, long position, long end)
{
    public ByteReader Reader => reader;

    public long Position { get; private set; } = position;

    public long End => end;

    public long Remaining => end - Position;

    public bool Has(long count) => Remaining >= count;

    // An unsigned big-endian integer of 1 to 8 bytes, without a node.
    public ulong Take(int bytes)
    {
        Span<byte> buffer = stackalloc byte[8];
        Fill(buffer[..bytes]);
        ulong value = 0;
        foreach (byte b in buffer[..bytes])
        {
            value = (value << 8) | b;
        }

        return value;
    }

    public void Fill(Span<byte> buffer)
    {
        if (buffer.Length > Remaining)
        {
            throw new EndOfStreamException();
        }

        reader.Position = Position;
        reader.Read(buffer);
        Position += buffer.Length;
    }

    public byte[] TakeBytes(long count)
    {
        if (count < 0 || count > Remaining || count > Array.MaxLength)
        {
            throw new EndOfStreamException();
        }

        var buffer = new byte[count];
        Fill(buffer);
        return buffer;
    }

    public void Skip(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new EndOfStreamException();
        }

        Position += count;
    }

    // An unsigned field of bytes bytes with a node whose value describe gives (the number, with
    // unit, when it is null).
    public ulong Field(Node parent, string name, int bytes, string description, Func<ulong, NodeValue?>? describe = null, string? unit = null, string? topic = null)
    {
        long start = Position;
        ulong value = Take(bytes);
        parent.Add(new Node(name, start, bytes, describe?.Invoke(value) ?? NodeValue.Integer((long)value, unit!)) { Description = description, Topic = topic });
        return value;
    }

    public uint U32(Node parent, string name, string description, string? unit = null) => (uint)Field(parent, name, 4, description, unit: unit);

    public ushort U16(Node parent, string name, string description, string? unit = null) => (ushort)Field(parent, name, 2, description, unit: unit);

    public byte U8(Node parent, string name, string description, string? unit = null) => (byte)Field(parent, name, 1, description, unit: unit);

    public bool Flag8(Node parent, string name, string description) => Field(parent, name, 1, description, v => NodeValue.Boolean(v != 0)) != 0;

    // A four-character code.
    public string FourCc(Node parent, string name, string description)
    {
        long start = Position;
        string code = FourCcText((uint)Take(4));
        parent.Add(new Node(name, start, 4, code) { Description = description });
        return code;
    }

    // A 16.16 or 8.8 fixed-point number, signed but where isUnsigned says otherwise (a sample rate).
    public double Fixed(Node parent, string name, int bytes, int fraction, string description, string? unit = null, bool isUnsigned = false)
    {
        long start = Position;
        ulong raw = Take(bytes);
        long signed = isUnsigned ? (long)raw : bytes == 4 ? (int)(uint)raw : (short)(ushort)raw;
        double value = signed / (double)(1 << fraction);
        parent.Add(new Node(name, start, bytes, NodeValue.Decimal(value, fraction == 16 ? 4 : 2, unit)) { Description = description });
        return value;
    }

    // Bytes shown as a node of their length, or of their text where text is set.
    public byte[] Bytes(Node parent, string name, long count, string description, Func<byte[], NodeValue?>? describe = null)
    {
        long start = Position;
        byte[] bytes = TakeBytes(count);
        parent.Add(new Node(name, start, count, describe?.Invoke(bytes) ?? NodeValue.Integer(count, "bytes")) { Description = description });
        return bytes;
    }

    // A null-terminated UTF-8 string that may also end with the box.
    public string CString(Node parent, string name, string description)
    {
        long start = Position;
        var bytes = new List<byte>();
        while (Remaining > 0)
        {
            byte b = (byte)Take(1);
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        string text = Encoding.UTF8.GetString([.. bytes]);
        parent.Add(new Node(name, start, Position - start, text) { Description = description });
        return text;
    }

    // Seconds since midnight, January 1, 1904, in UTC (ISO/IEC 14496-12 8.2.2.3).
    public void Time(Node parent, string name, int bytes, string description)
    {
        long start = Position;
        ulong seconds = Take(bytes);
        string text = seconds == 0 ? "0 (not set)"
            : seconds < (ulong)(DateTime.MaxValue - Epoch).TotalSeconds
                ? $"{seconds} ({Epoch.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC)"
                : $"{seconds} (beyond the year 9999)";
        parent.Add(new Node(name, start, bytes, text) { Description = description });
    }

    // A duration in a timescale's units, with the seconds it comes to.
    public ulong Duration(Node parent, string name, int bytes, uint timescale, string description)
    {
        long start = Position;
        ulong value = Take(bytes);
        parent.Add(new Node(name, start, bytes, DurationText(value, timescale, bytes)) { Description = description });
        return value;
    }

    public static string DurationText(ulong value, uint timescale, int bytes = 8)
    {
        if ((bytes == 4 && value == uint.MaxValue) || (bytes == 8 && value == ulong.MaxValue))
        {
            return $"{value} (unknown)";
        }

        return timescale == 0 ? $"{value} (no timescale)" : $"{value} ({(value / (double)timescale).ToString("F3", CultureInfo.InvariantCulture)} s)";
    }

    public static string FourCcText(uint code)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(code >> (24 - (8 * i)));
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : b == 0xA9 ? '©' : '.';
        }

        return new string(chars);
    }

    public static uint FourCcValue(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static readonly DateTime Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
