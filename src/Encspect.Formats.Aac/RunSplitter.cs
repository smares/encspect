namespace Encspect.Formats.Aac;

// Cuts a run of consecutive frames into records of at most limit bytes (a record's length is an int), at frame boundaries.
internal sealed class RunSplitter(long limit)
{
    private long _start;
    private long _bytes;
    private long _frames;

    // Adds the next frame of the run; returns the record that it closes, if the frame does not fit into it.
    public (long Offset, long Length, long Frames)? Add(long offset, int length)
    {
        (long, long, long)? closed = null;
        if (_frames > 0 && _bytes + length > limit)
        {
            closed = Flush();
        }

        if (_frames == 0)
        {
            _start = offset;
        }

        _bytes += length;
        _frames++;
        return closed;
    }

    // The record that is open, if any; the run starts afresh.
    public (long Offset, long Length, long Frames)? Flush()
    {
        if (_frames == 0)
        {
            return null;
        }

        var record = (_start, _bytes, _frames);
        _bytes = 0;
        _frames = 0;
        return record;
    }

    // [start, start + length) cut into pieces of at most limit bytes.
    public static IEnumerable<(long Offset, int Length)> Pieces(long start, long length, int limit = int.MaxValue)
    {
        for (long at = 0; at < length; at += limit)
        {
            yield return (start + at, (int)Math.Min(limit, length - at));
        }
    }
}
