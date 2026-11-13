namespace Encspect.Formats.Ac3;

// The integers the entropy pass hands to the decoder through the buffers its session lends (docs/design/lossy-decoders.md, "Lending for lossy frames"): the side
// information of every audio block of a syncframe that its reconstruction needs, then the exponent, the bit allocation pointer and the mantissa code of every bin, in the
// order FrameSyntax.WriteBlock documents. Where nothing is decoded the writer is disabled and every Add is a no-op.
internal sealed class IntWriter
{
    private int[] _buffer = new int[4096];

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
}

// A cursor over the integers of one frame, which the decoder reads in the order the walk wrote them. Reading past the end is a decoder bug and throws.
internal ref struct IntReader(ReadOnlySpan<int> data)
{
    private readonly ReadOnlySpan<int> _data = data;
    private int _position;

    public readonly bool IsAtEnd => _position >= _data.Length;

    public int Next()
    {
        if (_position >= _data.Length)
        {
            throw new InvalidOperationException("The decoder read more symbols than the entropy pass stored");
        }

        return _data[_position++];
    }

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
