namespace Encspect.Formats.Opus;

// The integers the entropy pass hands to the decoder through the buffers its session lends (docs/design/lossy-decoders.md, "Lending for lossy frames"): the symbols of
// every frame of a packet in the order the walk reads them. The layout is the format's own, documented on OpusWalker (the frame header), SilkSyntax (SILK) and
// CeltSyntax (CELT). Where nothing is decoded the writer is disabled and every Add is a no-op.
internal sealed class IntWriter
{
    private int[] _buffer = new int[1024];

    public int Count { get; private set; }

    public bool IsEnabled { get; set; }

    public ReadOnlySpan<int> Written => _buffer.AsSpan(0, Count);

    public void Clear() => Count = 0;

    public void Add(int value)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (Count == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        _buffer[Count++] = value;
    }

    public void Add(uint value) => Add(unchecked((int)value));

    // Reserves n integers to be set later and returns the index of the first; the disabled writer returns 0 and ignores the sets.
    public int Reserve(int n)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        while (Count + n > _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        int at = Count;
        Array.Clear(_buffer, at, n);
        Count += n;
        return at;
    }

    public void Set(int index, int value)
    {
        if (IsEnabled)
        {
            _buffer[index] = value;
        }
    }
}

// A cursor over the integers of one packet, which the decoder reads in the order the walk wrote them. Reading past the end is a decoder bug and throws.
internal ref struct IntReader(ReadOnlySpan<int> data)
{
    private readonly ReadOnlySpan<int> _data = data;
    private int _position;

    public readonly int Position => _position;

    public readonly bool IsAtEnd => _position >= _data.Length;

    public int Next()
    {
        if (_position >= _data.Length)
        {
            throw new InvalidOperationException("The decoder read more symbols than the entropy pass stored");
        }

        return _data[_position++];
    }

    public uint NextUInt() => unchecked((uint)Next());

    public ReadOnlySpan<int> Take(int count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw new InvalidOperationException("The decoder read more symbols than the entropy pass stored");
        }

        ReadOnlySpan<int> slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }
}
