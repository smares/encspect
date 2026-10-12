namespace Encspect.Core;

// Bytes as a parser sees them once a container or a tag has taken them apart, together with the
// file offset each byte came from: a packet that spans Ogg pages, MP4 sample data, ID3v2 data with
// unsynchronization undone. Nodes built from them (Node.FromMapped, Node.FromBits) still point at
// the file. The map is a list of segments, each a run of bytes that lie next to each other in the
// file, in file order.
public sealed class MappedBytes
{
    // Segment i maps bytes [_starts[i], _starts[i + 1]) to the file from _offsets[i] on.
    private readonly int[] _starts;
    private readonly long[] _offsets;

    // Bytes [0, Length) that come from [fileOffset, fileOffset + Length).
    public MappedBytes(ReadOnlyMemory<byte> data, long fileOffset)
        : this(data, [new ByteRange(fileOffset, data.Length)], fileOffset + data.Length)
    {
    }

    // Bytes that come from the ranges in turn, which are in file order and do not overlap.
    // fileEnd is where index Length maps to, the end of the last range by default; it may lie
    // further on when the decoding dropped bytes after the last one (ID3v2 unsynchronization).
    public MappedBytes(ReadOnlyMemory<byte> data, IReadOnlyList<ByteRange> ranges, long? fileEnd = null)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        // Checked and counted first, then filled, so a packet handed over by the million allocates
        // only its two arrays.
        int segments = 0;
        int used = 0;
        long total = 0;
        long previousEnd = -1;
        for (; used < ranges.Count && total <= data.Length; used++)
        {
            ByteRange range = ranges[used];
            if (range.Offset < 0 || range.Length < 0)
            {
                throw new ArgumentException("A range cannot have a negative offset or length.", nameof(ranges));
            }

            if (range.Length == 0)
            {
                continue;
            }

            if (range.Offset < previousEnd)
            {
                throw new ArgumentException("The ranges must be in file order and must not overlap.", nameof(ranges));
            }

            // Ranges that touch form one segment, so a contiguous packet maps in one step.
            if (range.Offset != previousEnd)
            {
                segments++;
            }

            total += range.Length;
            previousEnd = range.End;
        }

        if (total != data.Length)
        {
            throw new ArgumentException($"The ranges hold {total} bytes, the data {data.Length}.", nameof(ranges));
        }

        long end = previousEnd < 0 ? fileEnd ?? 0 : previousEnd;
        if (fileEnd is { } explicitEnd && explicitEnd < end)
        {
            throw new ArgumentException("The end lies before the last range ends.", nameof(fileEnd));
        }

        _starts = new int[segments];
        _offsets = new long[segments];
        int segment = -1;
        long position = 0;
        previousEnd = -1;
        for (int i = 0; i < used; i++)
        {
            ByteRange range = ranges[i];
            if (range.Length == 0)
            {
                continue;
            }

            if (range.Offset != previousEnd)
            {
                segment++;
                _starts[segment] = (int)position;
                _offsets[segment] = range.Offset;
            }

            position += range.Length;
            previousEnd = range.End;
        }

        Data = data;
        FileEnd = fileEnd ?? end;
        FileStart = segments > 0 ? _offsets[0] : FileEnd;
    }

    private MappedBytes(ReadOnlyMemory<byte> data, long fileStart, long fileEnd)
    {
        Data = data;
        _starts = [];
        _offsets = [];
        FileStart = fileStart;
        FileEnd = fileEnd;
        IsOpaque = true;
    }

    public ReadOnlyMemory<byte> Data { get; }

    // For a packet a container hands to a codec parser (AnalysisContext.ParsePackets): the
    // container's position at the end of the packet in the codec's own units, where the container
    // records one, such as the granule position of the Ogg page the packet ends on, which only the
    // last packet that ends there carries; null elsewhere. EndPositionField is the node of the
    // field that holds it, for findings. IsEndOfStream marks the last packet of a stream the
    // container says has ended (an Ogg page with the end-of-stream flag), and FollowsGap the first
    // packet after packets the container lost (a damaged page, a gap in the page sequence), so a
    // codec does not count positions across the gap.
    public long? EndPosition { get; init; }

    public Node? EndPositionField { get; init; }

    public bool IsEndOfStream { get; init; }

    public bool FollowsGap { get; init; }

    public ReadOnlySpan<byte> Span => Data.Span;

    public int Length => Data.Length;

    // The file range the bytes come from, from the first to where index Length maps.
    public long FileStart { get; }

    public long FileEnd { get; }

    // True for bytes with no byte-for-byte source in the file, such as decompressed data: every
    // part of them maps to the whole source range.
    public bool IsOpaque { get; }

    public static MappedBytes Opaque(ReadOnlyMemory<byte> data, long fileStart, long fileEnd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileStart);
        ArgumentOutOfRangeException.ThrowIfLessThan(fileEnd, fileStart);
        return new MappedBytes(data, fileStart, fileEnd);
    }

    // Reads the ranges, in file order, into one buffer: a packet put together from its pages.
    public static MappedBytes Read(ByteReader reader, IReadOnlyList<ByteRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ranges);

        // Corrupt sizes must read as truncation, not as a huge allocation.
        long total = 0;
        foreach (ByteRange range in ranges)
        {
            if (range.Offset < reader.Start || range.Length < 0 || range.Length > reader.End - range.Offset)
            {
                throw new EndOfStreamException();
            }

            total += range.Length;
            if (total > reader.Length)
            {
                throw new EndOfStreamException();
            }
        }

        if (total > Array.MaxLength)
        {
            throw new InvalidDataException($"A packet of {total} bytes is too large to read.");
        }

        var data = new byte[total];
        int at = 0;
        foreach (ByteRange range in ranges)
        {
            reader.Position = range.Offset;
            reader.Read(data.AsSpan(at, (int)range.Length));
            at += (int)range.Length;
        }

        return new MappedBytes(data, ranges, ranges.Count > 0 ? ranges[^1].End : reader.Start);
    }

    // The file offset of the byte at index; Length maps to FileEnd.
    public long OffsetOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Length);

        if (IsOpaque)
        {
            return FileStart;
        }

        if (index == Length)
        {
            return FileEnd;
        }

        int segment = SegmentOf(index);
        return _offsets[segment] + index - _starts[segment];
    }

    // How many file bytes lie from the byte at start to the one at end, including bytes the
    // decoding dropped between them; the whole source range for opaque bytes.
    public long FileLength(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        return IsOpaque ? FileEnd - FileStart : OffsetOf(end) - OffsetOf(start);
    }

    // The file ranges that hold bytes [start, end), in file order; the whole source range for
    // opaque bytes.
    public IReadOnlyList<ByteRange> FileRanges(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);

        if (IsOpaque)
        {
            return [new ByteRange(FileStart, FileEnd - FileStart)];
        }

        var ranges = new List<ByteRange>();
        if (start == end)
        {
            return ranges;
        }

        for (int segment = SegmentOf(start); segment < _starts.Length && _starts[segment] < end; segment++)
        {
            int segmentEnd = segment + 1 < _starts.Length ? _starts[segment + 1] : Length;
            int from = Math.Max(start, _starts[segment]);
            int to = Math.Min(end, segmentEnd);
            ranges.Add(new ByteRange(_offsets[segment] + from - _starts[segment], to - from));
        }

        return ranges;
    }

    public IReadOnlyList<ByteRange> FileRanges() => FileRanges(0, Length);

    public MappedBytes Slice(int start) => Slice(start, Length - start);

    // The bytes [start, start + length), which map to the file as they did here.
    public MappedBytes Slice(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length - start);

        ReadOnlyMemory<byte> data = Data.Slice(start, length);
        if (IsOpaque)
        {
            return new MappedBytes(data, FileStart, FileEnd);
        }

        return new MappedBytes(data, FileRanges(start, start + length), OffsetOf(start + length));
    }

    private int SegmentOf(int index)
    {
        int low = 0;
        int high = _starts.Length;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            (low, high) = _starts[middle] <= index ? (middle, high) : (low, middle);
        }

        return low;
    }
}
