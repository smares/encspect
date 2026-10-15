using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Raw AC-3 and E-AC-3 files (.ac3, .eac3, .ec3): syncframes one after another (A/52 5.1). As with
// MPEG sync, a sync word only starts a stream when a chain of frames follows it: two more at the
// positions the frame sizes give, with the same sample rate, or frames that end exactly at the end
// of the data. Once in sync, frames are followed by their lengths; a frame in which a new chain
// starts is cut short there as damaged, and anything else is a gap, after which sync is searched
// again.
public sealed class Ac3Parser : IFormatParser
{
    private const int SniffLimit = 1 << 20;
    private const int ChainLength = 3;

    public string Name => "AC-3";

    // Set by a test to check that frame nodes read the bits the analysis kept (see FrameRecords).
    internal Action<long, int, int>? RecordMismatch { get; init; }

    // A chain at the start of the data, after any zero bytes, outranks MPEG audio (90) and ADTS
    // (91), since a chain of AC-3 frames is no MPEG or ADTS chain: their sync words, 0xFFE and
    // 0xFFF, cannot start with 0x0B. One found further in ranks below MPEG audio and Ogg, so a stray
    // run inside another format's junk never takes the file. A chain further in in which three of
    // the first 16 frames pass their CRCs (crc1 over the first 5/8 and crc2 over the whole frame in
    // AC-3, the one CRC over the whole frame in E-AC-3; A/52 7.10.1 and Annex E 3.2) is not a stray
    // run: it takes 55, above an ADTS chain's 50 and below MPEG audio, Ogg, Musepack and WavPack.
    // Junk passes three given frames about once in 2^48, any three of 16 about C(16,3) times that.
    // Other formats' frames can repeat header-like bytes at the fixed distance of AC-3's frame
    // size, and several bit errors may spoil the first frames, so the count is of passing frames,
    // not of the first three.
    public int Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        var cache = new BlockCache(reader);
        long start = ZeroRunEnd(cache, reader.Start, reader.End);
        long found = FindChain(cache, start, Math.Min(reader.End, start + SniffLimit), reader.End);
        return found < 0 ? 0 : found == start ? 92 : CrcsHold(cache, found, reader.End) ? 55 : 50;
    }

    private const int CrcFrames = 16;
    private const int CrcsNeeded = 3;

    // Whether CrcsNeeded of the first CrcFrames frames of the chain at position are whole and pass their CRCs.
    private static bool CrcsHold(BlockCache cache, long position, long end)
    {
        var buffer = new byte[Ac3Analysis.MaxFrame];
        int passing = 0;
        SyncFrame? previous = null;
        for (int count = 0; count < CrcFrames; count++)
        {
            if (HeaderAt(cache, position, end) is not { } header || (previous is { } before && !header.Continues(before)) || position + header.FrameLength > end)
            {
                break;
            }

            int length = header.FrameLength;
            cache.Read(position, buffer.AsSpan(0, length));
            bool holds = Crc16.Compute(buffer.AsSpan(2, length - 2)) == 0;
            if (holds && !header.IsEac3)
            {
                int words = header.FrameSizeWords;
                holds = Crc16.Compute(buffer.AsSpan(2, (((words >> 1) + (words >> 3)) * 2) - 2)) == 0;
            }

            if (holds && ++passing == CrcsNeeded)
            {
                return true;
            }

            previous = header;
            position += length;
        }

        return false;
    }

    public void Parse(ParseContext context)
    {
        var analysis = new Ac3Analysis(context, RecordMismatch);
        var cache = new BlockCache(context.Reader);
        long position = context.Reader.Start;
        foreach (Node? tag in context.EmbeddedTags.OrderBy(t => t.Offset).Append<Node?>(null))
        {
            long end = tag?.Offset ?? context.Reader.End;
            if (end > position)
            {
                ParseSegment(analysis, cache, context.Root, position, end);
            }

            position = tag?.End ?? position;
        }

        analysis.Finish(context.EncoderClaims);
    }

    private static void ParseSegment(Ac3Analysis analysis, BlockCache cache, Node parent, long start, long end)
    {
        var region = new Ac3Analysis.Region(analysis.Frames.Count);
        analysis.Regions.Add(region);
        FrameRecords records = analysis.Records;
        region.Node = parent.Add(new Node("AC-3", start, end - start, null, () => RegionChildren(records, region))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.FramesAndBsi,
        });
        long position = start;
        SyncFrame? stream = null;
        var buffer = new byte[Ac3Analysis.MaxFrame];
        int frames = 0;
        bool hasEac3 = false;
        while (position < end)
        {
            if (frames % 4096 == 0)
            {
                analysis.Cancellation.ThrowIfCancellationRequested();
            }

            if (stream is { } current && HeaderAt(cache, position, end) is { } header && header.Continues(current))
            {
                long frameEnd = position + header.FrameLength;
                long cutAt = frameEnd;
                if (frameEnd < end && (HeaderAt(cache, frameEnd, end) is not { } next || !next.Continues(header)))
                {
                    long restart = FindChain(cache, position + 1, Math.Min(frameEnd, end), end);
                    if (restart > position)
                    {
                        cutAt = restart;
                    }
                }

                long stop = Math.Min(cutAt, end);
                int length = (int)(stop - position);
                cache.Read(position, buffer.AsSpan(0, length));
                analysis.AddFrame(region, position, buffer.AsSpan(0, length), header, cut: cutAt < frameEnd, truncated: cutAt == frameEnd && frameEnd > end);
                hasEac3 |= header.IsEac3;
                position = stop;
                frames++;
                continue;
            }

            long found = FindChain(cache, position, end, end);
            long gapEnd = found < 0 ? end : found;
            if (gapEnd > position)
            {
                analysis.AddGap(region, position, gapEnd, leading: frames == 0, trailing: found < 0 && frames > 0, cache);
            }

            if (found < 0)
            {
                break;
            }

            stream = HeaderAt(cache, found, end);
            position = found;
        }

        region.End = analysis.Frames.Count;
        if (hasEac3)
        {
            region.Node.Value = "E-AC-3";
        }
    }

    private static IReadOnlyList<Node> RegionChildren(FrameRecords records, Ac3Analysis.Region region)
    {
        int first = region.First;
        int end = region.End ?? records.Count;
        return new BlockedChildren(region.Leading, end - first, i => records.FrameNode(first + i));
    }

    // The offset of the first sync word in [from, limit) that a chain of frames follows, or -1.
    internal static long FindChain(BlockCache cache, long from, long limit, long end)
    {
        for (long position = cache.IndexOf(0x0B, from, limit); position >= 0; position = cache.IndexOf(0x0B, position + 1, limit))
        {
            if (cache.ByteAt(position + 1) != 0x77)
            {
                continue;
            }

            if (HeaderAt(cache, position, end) is { } header && Chains(cache, position, header, end))
            {
                return position;
            }
        }

        return -1;
    }

    private static bool Chains(BlockCache cache, long position, SyncFrame header, long end)
    {
        long next = position + header.FrameLength;
        SyncFrame previous = header;
        for (int count = 1; count < ChainLength; count++)
        {
            if (next == end)
            {
                return true;
            }

            if (HeaderAt(cache, next, end) is not { } following || !following.Continues(previous))
            {
                return false;
            }

            next += following.FrameLength;
            previous = following;
            if (next > end)
            {
                return count >= 2;
            }
        }

        return true;
    }

    internal static SyncFrame? HeaderAt(BlockCache cache, long position, long end)
    {
        if (position + SyncFrame.Length > end)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[SyncFrame.Length];
        if (cache.Read(position, bytes) < SyncFrame.Length)
        {
            return null;
        }

        SyncFrame header = SyncFrame.Read(bytes);
        return header.IsValid ? header : null;
    }

    private static long ZeroRunEnd(BlockCache cache, long start, long end)
    {
        long position = start;
        while (position < end && cache.ByteAt(position) == 0)
        {
            position++;
        }

        return position;
    }
}
