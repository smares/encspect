using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// Reads the file through one window that moves forward with the frames, so a frame is read once from the file however long it is, and the
// memory held is the window: a megabyte, or as much as the frame in hand needs.
internal sealed class Source(ByteReader reader)
{
    private const int MinimumWindow = 1 << 20;

    private byte[] _buffer = new byte[MinimumWindow];
    private long _start = -1;
    private int _length;

    public long Start => reader.Start;

    public long End => reader.End;

    public CancellationToken CancellationToken => reader.CancellationToken;

    public ByteReader Reader => reader;

    // The bytes from offset on, at least want of them where the file has so many, and no more than the window holds. Valid until the next call.
    public ReadOnlySpan<byte> Window(long offset, int want) => Slice(offset, want).Span;

    public ReadOnlyMemory<byte> Slice(long offset, int want)
    {
        if (offset < reader.Start || offset >= reader.End)
        {
            return default;
        }

        int needed = (int)Math.Min(want, reader.End - offset);
        bool isLoaded = offset >= _start && offset < _start + _length;
        if (!isLoaded || (offset - _start) + needed > _length && _start + _length < reader.End)
        {
            Load(offset, needed);
        }

        int index = (int)(offset - _start);
        return _buffer.AsMemory(index, _length - index);
    }

    // The byte at offset, or -1 outside the reader.
    public int ByteAt(long offset)
    {
        ReadOnlySpan<byte> window = Window(offset, 1);
        return window.IsEmpty ? -1 : window[0];
    }

    // The first offset in [from, limit) that holds value, or -1.
    public long IndexOf(byte value, long from, long limit)
    {
        from = Math.Max(from, reader.Start);
        limit = Math.Min(limit, reader.End);
        while (from < limit)
        {
            ReadOnlySpan<byte> window = Window(from, 1);
            int count = (int)Math.Min(window.Length, limit - from);
            int found = window[..count].IndexOf(value);
            if (found >= 0)
            {
                return from + found;
            }

            from += count;
        }

        return -1;
    }

    private void Load(long offset, int needed)
    {
        int size = Math.Max(MinimumWindow, needed);
        if (_buffer.Length < size)
        {
            _buffer = new byte[size];
        }
        else if (_buffer.Length > MinimumWindow && size <= MinimumWindow)
        {
            _buffer = new byte[MinimumWindow];
        }

        _start = offset;
        _length = (int)Math.Min(_buffer.Length, reader.End - offset);
        reader.Position = offset;
        reader.Read(_buffer.AsSpan(0, _length));
    }
}
