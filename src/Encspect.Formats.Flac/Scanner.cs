using Encspect.Core;

namespace Encspect.Formats.Flac;

// What reading the frame at a position found.
internal struct Attempt
{
    public HeaderFault Fault;
    public FrameHeader Header;
    public FrameWalk Walk;

    // The header reads: its fields are valid, and its CRC-8 holds or is the only thing that does not.
    public readonly bool HasHeader => Fault is HeaderFault.None or HeaderFault.Crc8;
}

// Finds frames in the file, following the rule that a header only counts as sync when a chain of consistent frames follows it: a candidate
// found by searching must read, agree with STREAMINFO, have a CRC-16 that holds and be followed by the end of the data or another header.
internal sealed class FrameScanner(Source source, StreamInfoBlock? info, long probeBudget)
{
    private readonly SubframeFacts[] _facts = new SubframeFacts[8];
    private readonly PartitionLog[] _logs = [new(), new(), new(), new(), new(), new(), new(), new()];

    // Bytes the searches have read in candidates that did not hold; past the budget they stop.
    public long ProbedBytes { get; private set; }

    public bool IsProbeLimitReached => ProbedBytes > probeBudget;

    public SubframeFacts[] Facts => _facts;

    public PartitionLog[] Logs => _logs;

    public StreamInfoBlock? Info { get; set; } = info;

    public Source Source => source;

    public static FrameHeader Resolve(in FrameHeader header, StreamInfoBlock? info) => header.Resolve(info?.SampleRate ?? 0, info?.BitsPerSample ?? 0);

    // Whether a header agrees with STREAMINFO where it states the same things; without STREAMINFO any header does.
    public bool IsConsistent(in FrameHeader header)
    {
        if (Info is not { } stream)
        {
            return true;
        }

        return header.Channels == stream.Channels
            && (header.SampleRateCode == 0 || header.SampleRate == stream.SampleRate)
            && (header.SampleSizeCode == 0 || header.BitsPerSample == stream.BitsPerSample);
    }

    // Reads the frame at position. With a session the walk stores the samples in the buffers it lends.
    public Attempt Read(long position, IDecodeSession? session, PartitionLog[]? logs = null)
    {
        var attempt = new Attempt();
        ReadOnlySpan<byte> head = source.Window(position, FrameHeader.MaxLength);
        attempt.Fault = FrameHeader.TryRead(head, out FrameHeader header);
        if (!attempt.HasHeader)
        {
            return attempt;
        }

        header = Resolve(header, Info);
        attempt.Header = header;
        if (header.BitsPerSample == 0)
        {
            attempt.Fault = HeaderFault.SampleSize;
            return attempt;
        }

        long remaining = source.End - position;
        int want = (int)Math.Min(remaining, 1 << 20);
        while (true)
        {
            ReadOnlySpan<byte> data = source.Window(position, want);
            if (data.Length > FrameWalker.MaxFrameBytes)
            {
                data = data[..FrameWalker.MaxFrameBytes];
            }

            attempt.Walk = FrameWalker.Walk(data, header, _facts, session, logs);
            if (attempt.Walk.IsCut && data.Length < remaining)
            {
                if (data.Length >= FrameWalker.MaxFrameBytes)
                {
                    attempt.Walk.IsCut = false;
                    attempt.Walk.Fault = SubframeFault.TooLong;
                }
                else
                {
                    want = (int)Math.Min(remaining, Math.Min(FrameWalker.MaxFrameBytes, (long)data.Length * 2));
                    continue;
                }
            }

            return attempt;
        }
    }

    public bool IsHeaderAt(long position)
    {
        ReadOnlySpan<byte> head = source.Window(position, FrameHeader.MaxLength);
        return FrameHeader.TryRead(head, out FrameHeader header) == HeaderFault.None && IsConsistent(Resolve(header, Info));
    }

    // Whether a frame that is chained to its successor starts at position: see the class comment.
    public bool IsChained(long position)
    {
        Attempt attempt = Read(position, null);
        if (attempt.Fault != HeaderFault.None || !IsConsistent(attempt.Header))
        {
            return false;
        }

        ProbedBytes += Math.Max(attempt.Walk.BytesRead, FrameHeader.MinLength);
        if (!attempt.Walk.Crc16Holds)
        {
            return false;
        }

        long end = position + attempt.Walk.Length;
        return end == source.End || IsHeaderAt(end);
    }

    // The first chained frame in [from, limit), or -1.
    public long FindNext(long from, long limit)
    {
        limit = Math.Min(limit, source.End - 1);
        long position = Math.Max(from, source.Start);
        int steps = 0;
        while (position < limit && !IsProbeLimitReached)
        {
            if (++steps % 1024 == 0)
            {
                source.CancellationToken.ThrowIfCancellationRequested();
            }

            long found = source.IndexOf(0xFF, position, limit);
            if (found < 0)
            {
                return -1;
            }

            int next = source.ByteAt(found + 1);
            if (next >= 0 && (next & 0xFC) == 0xF8 && IsChained(found))
            {
                return found;
            }

            position = found + 1;
        }

        return -1;
    }
}
