using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// Reads the file through one window that moves forward with the frames, so a frame is read once from the file however long it is, and the
// memory held is the window: a megabyte, a whole file of up to 128 KiB, or as much as the frame in hand needs (SourceWindow).
internal sealed class Source(ByteReader reader, int minimumWindow = Source.MinimumWindow)
{
    public const int MinimumWindow = 1 << 20;

    private readonly int _window = SourceWindow.For(reader.Length, minimumWindow);
    private byte[] _buffer = [];
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
        long loadedEnd = _start + _length;
        bool isLoaded = offset >= _start && offset < loadedEnd;
        if (!isLoaded || ((offset - _start) + needed > _length && loadedEnd < reader.End))
        {
            Load(offset, needed, isLoaded);
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

    // Moves the window to start at offset. What the window already holds from offset on is kept and only the bytes after it are read, so the
    // file is read about once however the requests fall.
    private void Load(long offset, int needed, bool keepsTail)
    {
        int size = Math.Max(_window, needed);
        int kept = keepsTail ? _length - (int)(offset - _start) : 0;
        if (_buffer.Length < size || (_buffer.Length > _window && size <= _window))
        {
            byte[] next = new byte[size];
            kept = Math.Min(kept, size);
            if (kept > 0)
            {
                _buffer.AsSpan((int)(offset - _start), kept).CopyTo(next);
            }

            _buffer = next;
        }
        else if (kept > 0)
        {
            _buffer.AsSpan((int)(offset - _start), kept).CopyTo(_buffer);
        }

        _start = offset;
        int count = (int)Math.Min(_buffer.Length - kept, reader.End - offset - kept);
        reader.Position = offset + kept;
        reader.Read(_buffer.AsSpan(kept, count));
        _length = kept + count;
    }
}
