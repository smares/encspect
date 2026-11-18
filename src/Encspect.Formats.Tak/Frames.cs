using Encspect.Core;

namespace Encspect.Formats.Tak;

[Flags]
internal enum EntryFlags : ushort
{
    None = 0,
    IsLast = 1,
    IsInfo = 2,
    HeaderCrcFails = 4,
    DataCrcFails = 8,

    // The data ends inside the frame: its data CRC-24 is not compared.
    Cut = 16,

    // The frame follows skipped data or a break in the frame numbers.
    FollowsGap = 32,

    // Bytes that hold no frame whose header CRC-24 holds; the other fields of the record are not set.
    Skipped = 64,
}

// One frame of the audio, or one stretch of bytes the walk skipped: where it lies, what its header states and the CRC-24s it stores and the ones computed.
internal struct Entry
{
    public long Offset;

    // The frame's bytes from its sync word to the end of its data CRC-24, or the bytes skipped.
    public long Length;

    public int Number;

    // The samples a last frame states; 0 for any other.
    public int Samples;

    // The header's bytes in Records.InfoHeaders plus 1, for an info frame; 0 where there are none or the report keeps no more of them.
    public int InfoSlot;

    public uint HeaderStoredCrc;
    public uint HeaderComputedCrc;
    public uint DataStoredCrc;
    public uint DataComputedCrc;
    public byte HeaderLength;
    public EntryFlags Flags;

    public readonly bool IsSkipped => (Flags & EntryFlags.Skipped) != 0;

    public readonly bool IsCut => (Flags & EntryFlags.Cut) != 0;

    public readonly bool IsHeaderCrcValid => (Flags & EntryFlags.HeaderCrcFails) == 0;

    public readonly bool IsDataCrcValid => (Flags & EntryFlags.DataCrcFails) == 0;
}

// What the walk over the frames found: the records the nodes are built from and the counts the statistics and the checks use.
internal sealed class FrameFacts
{
    // The most entries (frames and skipped stretches) the records keep: the frame number has 21 bits, so a stream holds at most 2,097,152 frames, and half of that is more than 27 hours
    // at the shortest frame. The entries past it are counted and their CRC-24s compared, but they have no record and no node.
    public const int MaxEntries = 1 << 20;

    // The headers of info frames the records keep, for their stream info (an info frame comes every 2 seconds at least, takStream.pas, tak_str_InfoFrameDistanceInDurationQuants).
    public const int MaxInfoHeaders = 1 << 16;

    public ChunkedList<Entry> Entries { get; } = new();

    public List<byte[]> InfoHeaders { get; } = [];

    // The entries added, recorded or not.
    public long Listed { get; set; }

    // Frames the walk read whole or whose data CRC-24 differs, and those the data cuts short.
    public long Frames { get; set; }

    public long CutFrames { get; set; }

    public long HeaderFailures { get; set; }

    public long DataFailures { get; set; }

    public long SkippedBytes { get; set; }

    public long SkippedRuns { get; set; }

    // The samples the frames hold, a cut frame's too where its length is known; those of the cut frames alone are in LostSamples.
    public long Samples { get; set; }

    public long LostSamples { get; set; }

    // A frame follows skipped data or a break in the frame numbers.
    public bool HasGap { get; set; }

    // The last frame the walk read is flagged as the last of the stream.
    public bool IsLastFlagged { get; set; }

    public StreamFigures? Figures { get; set; }

    public bool HasFrames => Frames + CutFrames > 0;
}

// Reads the file in windows for the walk: the frame headers, the search for the next sync word and the CRC-24 over a frame's coded data.
internal sealed class FrameScanner(ByteReader reader, long end)
{
    private const int ChunkSize = 1 << 16;

    private static readonly byte[] Sync = [0xFF, 0xA0];

    private readonly byte[] _buffer = new byte[2 * ChunkSize];
    private long _start;
    private int _length;

    // The bytes at a position, as many as ChunkSize at most and as the data up to the end holds.
    public ReadOnlySpan<byte> At(long position, int count)
    {
        count = (int)Math.Min(Math.Min(count, ChunkSize), end - position);
        if (count <= 0)
        {
            return default;
        }

        if (position < _start || position + count > _start + _length)
        {
            int fill = (int)Math.Min(_buffer.Length, end - position);
            reader.Position = position;
            reader.Read(_buffer.AsSpan(0, fill));
            _start = position;
            _length = fill;
        }

        return _buffer.AsSpan((int)(position - _start), count);
    }

    public FrameHeader HeaderAt(long position) => FrameHeader.Read(At(position, FrameHeader.MaxLength));

    // The first position in [from, limit) where the sync word FF A0 is, or -1.
    public long FindSync(long from, long limit)
    {
        limit = Math.Min(limit, end - 1);
        long position = Math.Max(from, 0);
        int steps = 0;
        while (position < limit)
        {
            if (++steps % 64 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> window = At(position, (int)Math.Min(ChunkSize, limit - position + 1));
            if (window.Length < 2)
            {
                return -1;
            }

            int index = window.IndexOf(Sync);
            if (index >= 0)
            {
                return position + index;
            }

            position += window.Length - 1;
        }

        return -1;
    }

    // The first position in [from, end) with a frame header whose CRC-24 holds, or -1.
    public long FindStrict(long from)
    {
        long position = from;
        while ((position = FindSync(position, end)) >= 0)
        {
            if (HeaderAt(position).Fault == HeaderFault.None)
            {
                return position;
            }

            position++;
        }

        return -1;
    }

    // The CRC-24 state after the bytes in [from, to).
    public uint Advance(uint crc, long from, long to)
    {
        int steps = 0;
        for (long position = from; position < to;)
        {
            if (++steps % 64 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> window = At(position, (int)Math.Min(ChunkSize, to - position));
            crc = Layout.Crc24Update(crc, window);
            position += window.Length;
        }

        return crc;
    }

    public uint StoredAt(long position)
    {
        ReadOnlySpan<byte> bytes = At(position, Layout.CrcLength);
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));
    }
}

// The walk over the frames (takStream.pas, "Audio frames"): a frame is its header, its coded data and the CRC-24 over the data. Nothing in the header states the frame's length, so a frame
// ends where the next one begins, and the next one is found by its sync word. A candidate counts as the next frame when its header's CRC-24 holds, or, where it fails, when the frame
// number is the one expected or the CRC-24 that ends the bytes before it holds over the frame's data; any of these is evidence of 24 bits or more. Where no frame is found, the bytes are
// skipped up to the next header whose CRC-24 holds.
internal static class FrameWalker
{
    // The longest frame: 16,384 samples of 16 channels of 24 bits is 786,432 bytes before coding, so a candidate further than this from the frame's header is not its end.
    private const int MaxFrameBytes = 4 << 20;

    private const int NumberMask = (1 << 21) - 1;

    public static void Run(State s, ByteReader reader, long end)
    {
        Records r = s.Records;
        FrameFacts f = r.Frames;
        StreamInfoFields? info = r.Stream;
        int frameLength = info is not null && Layout.IsFrameSizeValid(info.FrameSizeType, info.SampleRate) ? Layout.FrameSamples(info.FrameSizeType, info.SampleRate) : -1;
        f.Figures = info is { SampleRate: > 0 } ? new StreamFigures(info.SampleRate) : null;
        var scanner = new FrameScanner(reader, end);
        long position = r.AudioStart;
        long expected = 0;
        bool isFirst = true;
        bool followsGap = false;
        long skipFrom = -1;
        FrameHeader header = default;
        bool hasHeader = false;
        int steps = 0;
        while (position < end)
        {
            if (++steps % 64 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            if (!hasHeader)
            {
                header = scanner.HeaderAt(position);
                if (header.Fault != HeaderFault.None)
                {
                    skipFrom = skipFrom < 0 ? position : skipFrom;
                    long found = scanner.FindStrict(position + 1);
                    position = found < 0 ? end : found;
                    continue;
                }
            }

            hasHeader = false;
            if (skipFrom >= 0)
            {
                AddSkipped(s, skipFrom, position - skipFrom);
                skipFrom = -1;
                followsGap = true;
            }

            // A header whose CRC-24 fails states a number that cannot be trusted: the sequence goes on from the one expected.
            bool isTrusted = header.Fault == HeaderFault.None;
            long number = isTrusted ? header.Number : expected;
            long dataStart = position + header.Length;
            long limit = Math.Min(end, position + MaxFrameBytes);
            long next = -1;
            FrameHeader nextHeader = default;
            uint crc = Layout.Crc24Initial;
            long crcAt = dataStart;
            for (long from = dataStart + Layout.CrcLength; from < limit;)
            {
                long candidate = scanner.FindSync(from, limit);
                if (candidate < 0)
                {
                    break;
                }

                FrameHeader other = scanner.HeaderAt(candidate);
                bool isAccepted = other.Fault == HeaderFault.None
                    || (other.Fault == HeaderFault.Crc && (other.Number == ((number + 1) & NumberMask) || DataCrcHolds(scanner, candidate, dataStart, ref crc, ref crcAt)));
                if (isAccepted)
                {
                    next = candidate;
                    nextHeader = other;
                    break;
                }

                from = candidate + 1;
            }

            bool isFinal = next < 0;
            if (isFinal && end - position > MaxFrameBytes)
            {
                // No frame within reach: the header stands alone, and the bytes after it are skipped up to the next header that holds.
                skipFrom = position;
                long found = scanner.FindStrict(position + 1);
                position = found < 0 ? end : found;
                continue;
            }

            long frameEnd = isFinal ? end : next;
            var entry = new Entry
            {
                Offset = position,
                Length = frameEnd - position,
                Number = header.Number,
                Samples = header.IsLast ? header.Samples : 0,
                HeaderLength = (byte)header.Length,
                HeaderStoredCrc = header.StoredCrc,
                HeaderComputedCrc = header.ComputedCrc,
                Flags = (header.IsLast ? EntryFlags.IsLast : 0) | (header.IsInfo ? EntryFlags.IsInfo : 0) | (header.Fault == HeaderFault.Crc ? EntryFlags.HeaderCrcFails : 0)
                    | (followsGap ? EntryFlags.FollowsGap : 0),
            };
            bool isCut = frameEnd - dataStart < Layout.CrcLength;
            if (!isCut)
            {
                crc = scanner.Advance(crc, crcAt, frameEnd - Layout.CrcLength);
                entry.DataComputedCrc = crc;
                entry.DataStoredCrc = scanner.StoredAt(frameEnd - Layout.CrcLength);
                if (entry.DataStoredCrc != entry.DataComputedCrc)
                {
                    // The last frame of a file is cut where the data ends before the stream does: a frame not flagged as the last, or the end the last frame info states.
                    isCut = isFinal && (!header.IsLast || r.DeclaredEnd > r.DataEnd);
                    entry.Flags |= isCut ? EntryFlags.Cut : EntryFlags.DataCrcFails;
                }
            }
            else
            {
                entry.Flags |= EntryFlags.Cut;
            }

            long samples = header.IsLast ? header.Samples : Math.Max(0, frameLength);
            AddFrame(s, reader, entry, header, samples, isTrusted && number != expected, isFirst, expected);
            isFirst = false;
            followsGap = false;
            expected = (number + 1) & NumberMask;
            position = frameEnd;
            if (!isFinal)
            {
                header = nextHeader;
                hasHeader = true;
            }
        }

        if (skipFrom >= 0)
        {
            AddSkipped(s, skipFrom, end - skipFrom);
        }
    }

    // Whether the CRC-24 stored in the 3 bytes before a candidate equals the one over the coded data from dataStart to them.
    private static bool DataCrcHolds(FrameScanner scanner, long candidate, long dataStart, ref uint crc, ref long crcAt)
    {
        long stop = candidate - Layout.CrcLength;
        if (stop < dataStart)
        {
            return false;
        }

        if (stop > crcAt)
        {
            crc = scanner.Advance(crc, crcAt, stop);
            crcAt = stop;
        }
        else if (stop < crcAt)
        {
            return false;
        }

        return scanner.StoredAt(stop) == crc;
    }

    private static Func<Node> NodeAt(State s, long index, Func<int, Node> node) => index < FrameFacts.MaxEntries ? () => node((int)index) : () => s.Stream;

    private static void AddSkipped(State s, long offset, long length)
    {
        FrameFacts f = s.Records.Frames;
        long index = f.Listed++;
        if (index < FrameFacts.MaxEntries)
        {
            f.Entries.Add(new Entry { Offset = offset, Length = length, Flags = EntryFlags.Skipped });
        }

        f.SkippedBytes += length;
        f.SkippedRuns++;
        f.HasGap = true;
        Records records = s.Records;
        s.Findings.Add(Severity.Warning, "tak.data-skipped", NodeAt(s, index, i => FrameNodes.Entry(records, i)), count =>
            $"{Counts.Of(length, "byte", "bytes")} at 0x{offset:X} {Counts.Agree(length, "holds", "hold")} no frame whose header CRC-24 holds{(count > 1 ? $", and {Counts.Of(count - 1, "more such stretch", "more such stretches")} {Counts.Agree(count - 1, "does", "do")}" : string.Empty)}; frames are found again after {(length == 1 ? "it" : "them")}.");
    }

    private static void AddFrame(State s, ByteReader reader, Entry entry, FrameHeader header, long samples, bool isNumberWrong, bool isFirst, long expected)
    {
        Records records = s.Records;
        FrameFacts f = records.Frames;
        long index = f.Listed++;
        bool isRecorded = index < FrameFacts.MaxEntries;
        long offset = entry.Offset;
        if (isRecorded)
        {
            if (header.IsInfo && f.InfoHeaders.Count < FrameFacts.MaxInfoHeaders)
            {
                f.InfoHeaders.Add(reader.ReadBytesAt(offset, header.Length));
                entry.InfoSlot = f.InfoHeaders.Count;
            }

            f.Entries.Add(entry);
        }

        Func<Node> At(Func<int, Node> node) => NodeAt(s, index, node);
        Func<int, Node> frame = i => FrameNodes.Entry(records, i);
        long number = entry.Number;
        if (isNumberWrong)
        {
            f.HasGap = true;
            s.Findings.Add(Severity.Warning, "tak.frame-number-discontinuity", At(frame), count =>
                $"The frame at 0x{offset:X} has the number {number}, but {(isFirst ? "the first frame has the number 0" : $"the number after the frame before it is {expected}")}{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "breaks", "break")} the sequence" : string.Empty)}.");
        }

        if (header.Fault == HeaderFault.Crc)
        {
            f.HeaderFailures++;
            uint stored = header.StoredCrc;
            uint computed = header.ComputedCrc;
            s.Findings.Add(Severity.Warning, "tak.frame-header-crc-mismatch", At(i => FrameNodes.HeaderCrcField(records, i)), count =>
                $"The header of the frame at 0x{offset:X} stores the CRC-24 {stored:x6}, but its bytes give {computed:x6}{(count > 1 ? $"; {Counts.Of(count - 1, "more header", "more headers")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.");
        }

        if ((entry.Flags & EntryFlags.Cut) != 0)
        {
            f.CutFrames++;
            long held = entry.Length;
            f.Samples += samples;
            f.LostSamples += samples;
            s.Findings.Add(Severity.Warning, "tak.frame-cut", At(frame), _ =>
                $"The data ends inside the frame at 0x{offset:X}, after {Counts.Of(held, "byte", "bytes")} of it, so its data CRC-24 is not compared.");
        }
        else
        {
            f.Frames++;
            f.Samples += samples;
            if ((entry.Flags & EntryFlags.DataCrcFails) != 0)
            {
                f.DataFailures++;
                uint stored = entry.DataStoredCrc;
                uint computed = entry.DataComputedCrc;
                s.Findings.Add(Severity.Warning, "tak.frame-crc-mismatch", At(i => FrameNodes.DataCrcField(records, i)), count =>
                    $"The frame at 0x{offset:X} stores the CRC-24 {stored:x6} over its coded data, but the data gives {computed:x6}{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "differs", "differ")}" : string.Empty)}.");
            }

            if (isRecorded && f.Figures is { } figures && samples > 0)
            {
                figures.Add(offset, entry.Length * 8, samples);
            }
        }

        f.IsLastFlagged = header.IsLast;
        if (!isRecorded && index == FrameFacts.MaxEntries)
        {
            s.Findings.Add(Severity.Warning, "tak.frames-limit", () => s.Stream, _ =>
                $"The stream holds more than {FrameFacts.MaxEntries} frames; the report keeps the first {FrameFacts.MaxEntries}. The rest are counted and their CRC-24s compared, but they have no nodes, and the graphs and the bitrate figures cover the frames before them.");
        }
    }
}
