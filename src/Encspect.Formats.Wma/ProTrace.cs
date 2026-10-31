namespace Encspect.Formats.Wma;

// What the reader found in a frame, as a short sequence of tagged numbers (LEB128): the lengths in bits of the fields in the order of
// the bits, and the values the nodes show. The node builders replay it, so a frame's nodes are built when read and the report keeps a few
// dozen bytes of a frame, not its tree.
internal static class Tag
{
    public const int End = 0;

    // Frame level.
    public const int FrameLength = 1;
    public const int TileHeader = 2;
    public const int Postprocessing = 3;
    public const int DynamicRange = 4;
    public const int Trim = 5;
    public const int Subframe = 6;
    public const int Padding = 7;
    public const int Trailer = 8;

    // Subframe level.
    public const int ExtendedHeader = 1;
    public const int ReservedBit = 2;
    public const int ChannelTransform = 3;
    public const int CoefficientsSent = 4;
    public const int VectorCounts = 5;
    public const int QuantizationStep = 6;
    public const int ScaleFactors = 7;
    public const int Coefficients = 8;
}

internal sealed class TraceWriter
{
    private byte[] _data = new byte[512];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => _data.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void Put(long value)
    {
        ulong v = (ulong)value;
        while (v >= 0x80)
        {
            Append((byte)(v | 0x80));
            v >>= 7;
        }

        Append((byte)v);
    }

    public void PutSigned(long value) => Put((value << 1) ^ (value >> 63));

    private void Append(byte b)
    {
        if (Length == _data.Length)
        {
            Array.Resize(ref _data, Length * 2);
        }

        _data[Length++] = b;
    }
}

internal ref struct TraceReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly bool IsEnd => _position >= _data.Length;

    public long Get()
    {
        ulong value = 0;
        int shift = 0;
        while (_position < _data.Length)
        {
            byte b = _data[_position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return (long)value;
    }

    public int GetInt() => (int)Get();

    public long GetSigned()
    {
        long raw = Get();
        return (raw >>> 1) ^ -(raw & 1);
    }
}
