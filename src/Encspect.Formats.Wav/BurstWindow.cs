using Encspect.Core;

namespace Encspect.Formats.Wav;

// The data read in blocks, so that a walk over bursts that lie a few bytes apart (the pause bursts of 12 bytes at their recommended period) reads each
// byte once instead of a block per burst. The span At returns is valid until the next call. One window serves one walk.
internal sealed class BurstWindow(ByteReader data)
{
    private const int Size = 1 << 16;

    private byte[] _buffer = [];
    private long _start;
    private int _length;

    public long Start => data.Start;

    public long End => data.End;

    // The bytes from position, fewer than count where the data ends first.
    public ReadOnlySpan<byte> At(long position, int count)
    {
        count = (int)Math.Min(count, End - position);
        if (count <= 0)
        {
            return [];
        }

        if (position < _start || position + count > _start + _length)
        {
            int size = (int)Math.Min(Math.Max(Size, count), End - position);
            if (_buffer.Length < size)
            {
                _buffer = new byte[size];
            }

            data.Position = position;
            data.Read(_buffer.AsSpan(0, size));
            _start = position;
            _length = size;
        }

        return _buffer.AsSpan((int)(position - _start), count);
    }
}
