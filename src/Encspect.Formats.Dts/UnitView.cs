namespace Encspect.Formats.Dts;

// The bytes of one unit as the syntax reads them, in the bit-packed stream, at unit coordinates: the
// core frame from 0, each extension substream from where it starts. The bytes come in segments,
// because only a part of a unit is ever read: the core frame's header, the headers of the extensions,
// the start of each component of a substream.
//
// The same syntax code reads a unit twice, as the AC-3 parser's does: while the file is analyzed,
// from segments made from the file, and every byte it reads is recorded (Touched), so the record of
// the unit keeps exactly those; and when the unit's nodes are built, long after the file is closed,
// from segments made from that record. A read outside the segments throws EndOfStreamException,
// which the syntax catches: the data ends there.
internal sealed class UnitView
{
    private readonly List<Segment> _segments = [];
    private readonly List<(int Start, int End)> _touched = [];
    private int _last;
    private int _runStart = -1;
    private int _runEnd = -1;

    public bool IsBuildingNodes { get; init; }

    // Adds the bytes at unit coordinate start.
    public void Add(int start, byte[] data, int length)
    {
        if (length > 0)
        {
            _segments.Add(new Segment(start, data, length));
        }
    }

    public bool Has(int start, int length)
    {
        foreach (Segment segment in _segments)
        {
            if (start >= segment.Start && start + length <= segment.Start + segment.Length)
            {
                return true;
            }
        }

        return false;
    }

    // The bytes from start that the segment holding it has, not recorded as read: for checks the analysis makes,
    // such as a CRC, whose result the record keeps instead of its input.
    public ReadOnlySpan<byte> Peek(int start, int length)
    {
        if (start < 0 || length < 0)
        {
            throw new EndOfStreamException();
        }

        foreach (Segment segment in _segments)
        {
            if (start >= segment.Start && start + length <= segment.Start + segment.Length)
            {
                return segment.Data.AsSpan(start - segment.Start, length);
            }
        }

        throw new EndOfStreamException();
    }

    // Bytes a structure decision rests on, recorded as read: the record then keeps them, and the nodes built from it make the same decision.
    public ReadOnlySpan<byte> Take(int start, int length)
    {
        ReadOnlySpan<byte> bytes = Peek(start, length);
        if (length > 0)
        {
            Touch(start, start + length - 1);
        }

        return bytes;
    }

    // Bytes that exist for the search of the core frame's end: unrecorded, zero outside the segments.
    public int PeekByte(int index)
    {
        foreach (Segment segment in _segments)
        {
            if (index >= segment.Start && index < segment.Start + segment.Length)
            {
                return segment.Data[index - segment.Start];
            }
        }

        return -1;
    }

    // Up to 32 bits at bit position bit.
    public uint Read(int bit, int width)
    {
        if (width is < 0 or > 32 || bit < 0)
        {
            throw new EndOfStreamException();
        }

        if (width == 0)
        {
            return 0;
        }

        int first = bit >> 3;
        int last = (bit + width - 1) >> 3;
        Segment segment = SegmentOf(first, last);
        Touch(first, last);
        ulong value = 0;
        for (int i = first; i <= last; i++)
        {
            value = (value << 8) | segment.Data[i - segment.Start];
        }

        int spare = ((last - first + 1) * 8) - ((bit & 7) + width);
        return (uint)((value >> spare) & ((1UL << width) - 1));
    }

    // The bytes read so far as (start, end) pairs of unit coordinates, merged and in order.
    public List<(int Start, int End)> Touched()
    {
        Flush();
        _touched.Sort();
        var merged = new List<(int Start, int End)>();
        foreach ((int start, int end) in _touched)
        {
            if (merged.Count > 0 && start <= merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged;
    }

    private Segment SegmentOf(int first, int last)
    {
        // The hint is read once: node builders on several threads share one view, and a hint that another thread
        // moves between the check and the return would name a segment that does not hold the bytes.
        int hint = Volatile.Read(ref _last);
        if (hint < _segments.Count && Fits(_segments[hint], first, last))
        {
            return _segments[hint];
        }

        for (int i = 0; i < _segments.Count; i++)
        {
            if (Fits(_segments[i], first, last))
            {
                Volatile.Write(ref _last, i);
                return _segments[i];
            }
        }

        throw new EndOfStreamException();
    }

    private static bool Fits(Segment segment, int first, int last) => first >= segment.Start && last < segment.Start + segment.Length;

    private void Touch(int first, int last)
    {
        if (IsBuildingNodes)
        {
            return;
        }

        if (_runStart >= 0 && first >= _runStart && last <= _runEnd)
        {
            return;
        }

        if (_runStart >= 0 && first >= _runStart && first <= _runEnd + 1)
        {
            _runEnd = Math.Max(_runEnd, last);
            return;
        }

        Flush();
        _runStart = first;
        _runEnd = last;
    }

    private void Flush()
    {
        if (_runStart >= 0)
        {
            _touched.Add((_runStart, _runEnd));
            _runStart = -1;
        }
    }

    private sealed record Segment(int Start, byte[] Data, int Length);
}
