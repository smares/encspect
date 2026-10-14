using System.Buffers;
using Encspect.Core;

namespace Encspect.Formats.Dts;

// Raw DTS files (.dts, .dtshd, .cpt): core frames, each possibly followed by extension substreams, in any of the four forms of
// the core stream (ETSI TS 102 114 5.4.2, 7.2; FFmpeg's dca_syncwords.h; libdca's parse.c), or extension substreams alone. As
// with MPEG sync, a sync word only starts a stream when a chain of frames follows it: two more at the positions the frame
// sizes give, in the same form and at the same sampling frequency, or frames that end exactly at the end of the data. Once in sync, frames
// are followed by their lengths; a frame in which a new chain starts is cut short there as damaged, and anything else is a gap, after
// which sync is searched again.
public sealed class DtsParser : IFormatParser
{
    private const int SniffLimit = 1 << 20;

    // The first bytes of every sync word: 0x7F, 0xFE, 0x1F and 0xFF start the core's four forms, 0x64 and 0x58 the extension substream's two.
    private static readonly SearchValues<byte> SyncStarts = SearchValues.Create([0x7F, 0xFE, 0x1F, 0xFF, 0x64, 0x58]);

    public string Name => "DTS";

    // A chain at the start of the data, after any zero bytes, takes 93, above AC-3's 92, ADTS's 91 and MPEG audio's 90: the sync words
    // of these formats differ in their first bytes (0x7F, 0xFE, 0x1F, 0xFF with a second byte that is not 0xFx, 0x64 and 0x58, against 0x0B,
    // and 0xFF with 0xFx), so no chain is two formats' at once. One found further in takes 91, just above MPEG audio's 90 for a stream
    // anywhere in the first MiB, whose free format reads the bytes DTS frames hold (the 0xFFFF that ends a subframe, followed by zero
    // bytes) as a chain of its own frames. A DTS chain is a four-byte sync word, a header that must be valid and two more frames of the
    // same form and sampling frequency at the offsets the sizes give, which junk or another format's data does not make. It stays below
    // AC-3's 92 and every container's 95 and 100, which a payload of DTS frames in an MP4 file, or in the junk of another format, cannot take.
    public int Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        var cache = new BlockCache(reader);
        long start = ZeroRunEnd(cache, reader.Start, reader.End);
        long found = FindChain(cache, start, Math.Min(reader.End, start + SniffLimit), reader.End);
        return found < 0 ? 0 : found == start ? 93 : 91;
    }

    public void Parse(ParseContext context)
    {
        var analysis = new DtsAnalysis(context);
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

    private static void ParseSegment(DtsAnalysis analysis, BlockCache cache, Node parent, long start, long end)
    {
        var region = new DtsAnalysis.Region(analysis.Frames.Count);
        analysis.Regions.Add(region);
        region.Node = parent.Add(new Node("DTS", start, end - start, null, () => RegionChildren(analysis, region))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.CoreFrames,
        });
        long position = start;
        UnitInfo? stream = null;
        int frames = 0;
        while (position < end)
        {
            if (frames % 4096 == 0)
            {
                analysis.Cancellation.ThrowIfCancellationRequested();
            }

            if (stream is { } current && Framing.UnitAt(cache, position, end, false) is { } unit && unit.First.Continues(current.First))
            {
                long unitEnd = position + unit.Length;
                long cutAt = unitEnd;
                if (unitEnd < end && (Framing.UnitAt(cache, unitEnd, end, false) is not { } next || !next.First.Continues(unit.First)))
                {
                    long restart = FindChain(cache, position + 1, Math.Min(unitEnd, end), end);
                    if (restart > position)
                    {
                        cutAt = restart;
                    }
                }

                long stop = Math.Min(cutAt, end);
                analysis.AddUnit(region, LayoutOf(position, stop, unit, cutAt < unitEnd, cutAt == unitEnd && unitEnd > end), cache);
                position = stop;
                stream = unit;
                frames++;
                continue;
            }

            long found = FindChain(cache, position, end, end);
            long gapEnd = found < 0 ? end : found;
            if (gapEnd > position)
            {
                ReportInvalidHeader(analysis, cache, position, end, gapEnd);
                analysis.AddGap(position, gapEnd, leading: frames == 0, trailing: found < 0 && frames > 0, cache);
            }

            if (found < 0)
            {
                break;
            }

            stream = Framing.UnitAt(cache, found, end, true);
            position = found;
        }

        region.End = analysis.Frames.Count;
    }

    // What a gap starts with, where it is a sync word of a core frame whose header cannot be a frame's.
    private static void ReportInvalidHeader(DtsAnalysis analysis, BlockCache cache, long position, long end, long gapEnd)
    {
        Span<byte> bytes = stackalloc byte[16];
        int read = cache.Read(position, bytes[..(int)Math.Min(16, end - position)]);
        if (read >= 14 && CoreHeader.ReadUnchecked(bytes[..read]) is { } header && header.Header.InvalidReason is { } reason)
        {
            analysis.AddInvalidHeader(position, gapEnd - position, reason);
        }
    }

    // The unit of info from position as far as stop, where a cut or the end of the data stops it.
    internal static UnitLayout LayoutOf(long position, long stop, UnitInfo unit, bool isCut, bool isTruncated)
    {
        int length = (int)(stop - position);
        var layout = new UnitLayout
        {
            Form = unit.First.Form,
            Offset = position,
            Length = length,
            IsCut = isCut,
            IsTruncated = isTruncated,
            CoreLength = unit.First.IsCore ? Math.Min(unit.First.Length, length) : 0,
            CoreDeclaredLength = unit.First.IsCore ? unit.First.Length : 0,
        };
        if (!unit.First.IsCore)
        {
            layout.Substreams.Add(new ExssRef(0, unit.First.Length, unit.First.Exss.Index, Math.Min(unit.First.Length, length)));
        }

        for (int i = 0; i < unit.Count; i++)
        {
            ExssRef substream = unit[i];
            if (substream.Start < length)
            {
                layout.Substreams.Add(substream with { Available = Math.Min(substream.Length, length - substream.Start) });
            }
        }

        return layout;
    }

    private static IReadOnlyList<Node> RegionChildren(DtsAnalysis analysis, DtsAnalysis.Region region)
    {
        int end = region.End ?? analysis.Frames.Count;
        return new BlockedChildren(region.Leading, end - region.First, i => analysis.FrameNode(region.First + i));
    }

    // The offset of the first sync word in [from, limit) that a chain of frames follows, or -1.
    internal static long FindChain(BlockCache cache, long from, long limit, long end)
    {
        for (long position = cache.IndexOfAny(SyncStarts, from, limit); position >= 0; position = cache.IndexOfAny(SyncStarts, position + 1, limit))
        {
            if (Framing.UnitAt(cache, position, end, true) is { } unit && Framing.Chains(cache, position, unit, end))
            {
                return position;
            }
        }

        return -1;
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
