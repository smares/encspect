using Encspect.Core;

namespace Encspect.Formats.Ogg;

[Flags]
internal enum PageStatus : byte
{
    None = 0,

    // Bytes between pages, not a page.
    Gap = 1,

    CrcMismatch = 2,

    // Cut short by the end of the data or by the next page, so its CRC cannot be checked.
    Truncated = 4,

    // Fewer than the 27 bytes of a page header.
    HeaderCut = 8,

    // A gap in the page sequence before this page: a packet left unfinished is lost.
    DropsPending = 16,

    // A packet was unfinished when the page began.
    PendingBefore = 32,

    // A page of a stream beyond the region's limit: recorded, not followed.
    Untracked = 64,
}

// One page or gap, 56 bytes (the fields run from the widest to the narrowest, so no padding): a long file has one per few kilobytes, and nothing else per page is
// kept but its segment table.
internal struct PageRecord
{
    public long Offset;
    public long Granule;

    // The bytes the page covers in the file: header, segment table and body, or as many as there
    // are; a gap may be longer than any page.
    public long Length;

    // Where the segment table is kept in Region.Lacing.
    public uint Lacing;

    // The logical stream, or -1.
    public int Stream;

    // The packets the stream completed before this page.
    public int CompletedBefore;

    public uint Serial;
    public uint Sequence;
    public uint Crc;
    public uint ComputedCrc;
    public byte Flags;
    public byte Segments;
    public PageStatus Status;

    public readonly bool IsGap => (Status & PageStatus.Gap) != 0;

    public readonly bool IsLost => (Status & (PageStatus.CrcMismatch | PageStatus.Truncated)) != 0;

    public readonly long BodyOffset => Offset + PageHeader.FixedLength + Segments;

    // The segment table's bytes that are in the data.
    public readonly int LacingPresent => (int)Math.Max(0, Math.Min(Segments, Length - PageHeader.FixedLength));
}

// One stretch of Ogg data between tags.
internal sealed class Region(long start, long end)
{
    public long Start => start;

    public long End => end;

    public ChunkedList<PageRecord> Pages { get; } = new();

    public ByteStore Lacing { get; } = new();

    public List<LogicalStream> Streams { get; } = [];

    // The latest stream of each serial number.
    public Dictionary<uint, LogicalStream> BySerial { get; } = [];

    public FindingCollector Findings { get; } = new();

    // The playback groups of earlier regions: a stream's group is this and its link.
    public int GroupBase { get; init; }

    // The most logical streams followed: every stream has a node of its own and findings, about 10 KB, so a file of tiny streams costs
    // more than its size by far. Real files hold a few, or a chain of links of some kilobytes each at least; the limit grows with the data.
    public int MaxStreams { get; } = (int)Math.Min(int.MaxValue, 4096 + ((end - start) / 8192));

    public ResyncBudget Resync { get; } = ResyncBudget.For(end - start);

    public int PageCount { get; set; }

    public long GapBytes { get; set; }

    // The link of the chain the next beginning-of-stream page belongs to, from 1, and whether a page
    // other than a beginning-of-stream page came since the link began.
    public int Link { get; set; } = 1;

    public bool LinkHasData { get; set; }

    // The streams of the current link that have not ended.
    public int OpenInLink { get; set; }
}

internal sealed class LogicalStream(int index, uint serial, int link, bool hasBos)
{
    public int Index => index;

    public uint Serial => serial;

    public int Link => link;

    public bool HasBos => hasBos;

    public bool HasEos { get; set; }

    public bool ContinuesAcrossLinks { get; set; }

    // The records of its pages, in file order.
    public ChunkedList<int> Pages { get; } = new();

    public int FirstPage { get; init; }

    public int LastPage { get; set; }

    public long Start { get; init; }

    public long End { get; set; }

    public uint? LastSequence { get; set; }

    public long? LastGranule { get; set; }

    public int LastGranulePage { get; set; } = -1;

    public PacketAssembler Assembler { get; } = new();

    // The first bytes of its first packet, which name the codec, and where they are.
    public byte[]? Magic { get; set; }

    public long MagicOffset { get; set; }
}

// Findings of one kind are reported once per logical stream with a count, pointing at the first
// case, as MPEG reports them once per stream.
internal sealed class FindingCollector
{
    private readonly Dictionary<(int Stream, string Code), (Severity Severity, Node Node, int Count, Func<int, string> Message)> _findings = [];
    private readonly List<(int Stream, string Code)> _order = [];

    public void Add(int stream, Severity severity, string code, Node node, Func<int, string> message)
    {
        if (_findings.TryGetValue((stream, code), out var existing))
        {
            _findings[(stream, code)] = existing with { Count = existing.Count + 1 };
            return;
        }

        _findings[(stream, code)] = (severity, node, 1, message);
        _order.Add((stream, code));
    }

    public void Flush(AnalysisContext context)
    {
        foreach ((int, string) key in _order)
        {
            var entry = _findings[key];
            context.AddFinding(entry.Severity, key.Item2, entry.Message(entry.Count), entry.Node, entry.Count);
        }
    }
}

public sealed partial class OggParser
{
    // The stream's node with what the pages say about it, then the codec parser's nodes, which it
    // adds from the packets ParsePackets hands it.
    private static void AddStream(ParseContext context, Region region, PageWindow window, Node parent, LogicalStream stream)
    {
        OggCodec? codec = stream.Magic is { } magic ? OggCodecs.Identify(magic) : null;
        ref readonly PageRecord first = ref region.Pages[stream.FirstPage];
        Node node = parent.Add(new Node("Logical Stream", stream.Start, stream.End - stream.Start, codec?.Name ?? "Unknown")
        {
            Description = Explain.LogicalStream,
            Topic = Topics.OggPages,
        });
        node.Add(new Node("Serial Number", first.Offset + 14, 4, NodeValue.Hex(stream.Serial, 8)) { Description = Explain.StreamSerialNumber });
        string codecText = codec is null ? Explain.CodecUnknown : Explain.Codec;
        node.Add(stream.Magic is { } start
            ? new Node("Codec", stream.MagicOffset, start.Length, codec?.Name ?? "Unknown") { Description = codecText }
            : new Node("Codec", stream.Start, 0, "Unknown") { Description = codecText });
        node.Add(new Node("Chain Link", first.Offset + 5, 1, NodeValue.Integer(stream.Link)) { Description = Explain.ChainLink });
        node.Add(new Node("Page Count", stream.Start, stream.End - stream.Start, NodeValue.Integer(stream.Pages.Count, "pages", "page")) { Description = Explain.PageCount });
        node.Add(new Node("Packet Count", stream.Start, stream.End - stream.Start, NodeValue.Integer(stream.Assembler.Completed, "packets", "packet"))
        {
            Description = Explain.PacketCount,
        });
        if (stream.LastGranulePage >= 0)
        {
            ref readonly PageRecord page = ref region.Pages[stream.LastGranulePage];
            node.Add(new Node("Last Granule Position", page.Offset + 6, 8, NodeValue.Integer(page.Granule))
            {
                Description = Explain.LastGranulePosition,
                Topic = Topics.GranulePosition,
            });
        }

        node.Add(new Node("HasEndOfStream", region.Pages[stream.LastPage].Offset + 5, 1, NodeValue.Boolean(stream.HasEos)) { Description = Explain.HasEndOfStream });

        if (codec is null)
        {
            string what = stream.Magic is { Length: > 0 } bytes
                ? $"starts with bytes no Ogg mapping is known for ({Convert.ToHexString(bytes)})"
                : "has no complete first packet to name its codec";
            region.Findings.Add(stream.Index, Severity.Info, "ogg.codec-unknown", node, _ =>
                $"The logical stream 0x{stream.Serial:X8} {what}; its packets are not analyzed.");
            return;
        }

        int streamsBefore = context.Streams.Count;
        if (context.ParsePackets(codec.Id, node, Packets(region, stream, window, context.CancellationToken)))
        {
            // A codec that reports several streams is not known to play them together, as Matroska does not mark them.
            List<StreamInfo> made = [.. context.Streams.Skip(streamsBefore)];
            if (made.Count == 1)
            {
                context.UpdateStream(made[0], made[0] with { PlaybackGroup = region.GroupBase + stream.Link });
            }
        }
        else
        {
            region.Findings.Add(stream.Index, Severity.Info, "ogg.codec-not-analyzed", node, _ =>
                $"The logical stream 0x{stream.Serial:X8} holds {codec.Name}, which is recognized but not analyzed yet; only its pages are.");
        }
    }

    // The stream's packets, read again from its pages as the scan followed them. Pages the scan
    // found damaged are skipped, so a packet they held a part of is lost, and the next packet
    // follows a gap.
    private static IEnumerable<MappedBytes> Packets(Region region, LogicalStream stream, PageWindow window, CancellationToken cancellationToken)
    {
        var builder = new PacketBuilder(region, window);
        var completed = new List<MappedBytes>();
        for (int i = 0; i < stream.Pages.Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            builder.Feed(stream.Pages[i], completed);
            foreach (MappedBytes packet in completed)
            {
                yield return packet;
            }
        }
    }

    // Puts packets together from the pages of one stream: the bytes of a packet that continues on a
    // later page are kept until it ends.
    private sealed class PacketBuilder(Region region, PageWindow window)
    {
        private readonly PacketAssembler _assembler = new();
        private readonly List<Run> _runs = [];
        private readonly List<ByteRange> _ranges = [];
        private byte[] _buffer = new byte[4096];
        private int _length;
        private bool _gap;

        public void Feed(int index, List<MappedBytes> completed)
        {
            completed.Clear();
            ref readonly PageRecord page = ref region.Pages[index];
            if (page.IsLost || (page.Status & PageStatus.DropsPending) != 0)
            {
                _assembler.Lose();
                Reset();
                _gap = true;
                if (page.IsLost)
                {
                    return;
                }
            }

            PageOutcome outcome = _assembler.Feed((page.Flags & Pages.Continued) != 0, region.Lacing.Span(page.Lacing, page.Segments), _runs);
            if (outcome.ContinuationMissing)
            {
                Reset();
                _gap = true;
            }

            int lastEnding = _runs.FindLastIndex(r => r is { Ends: true, Kind: RunKind.Packet });
            for (int r = 0; r < _runs.Count; r++)
            {
                Run run = _runs[r];
                if (run.Kind != RunKind.Packet)
                {
                    Reset();
                    _gap |= run.Ends || run.Kind == RunKind.Orphan;
                    continue;
                }

                Append(window.Get(page.BodyOffset + run.Start, run.Length), page.BodyOffset + run.Start);
                if (!run.Ends)
                {
                    continue;
                }

                bool positioned = r == lastEnding && page.Granule != -1;
                // MappedBytes copies the ranges, so the list is reused for the next packet.
                completed.Add(new MappedBytes(_buffer.AsSpan(0, _length).ToArray(), _ranges, page.BodyOffset + run.Start + run.Length)
                {
                    EndPosition = positioned ? page.Granule : null,
                    EndPositionField = positioned ? GranuleField(page.Offset) : null,
                    IsEndOfStream = r == lastEnding && (page.Flags & Pages.End) != 0 && !outcome.EndsInsidePacket,
                    FollowsGap = _gap,
                });
                _gap = false;
                Reset();
            }
        }

        private void Append(ReadOnlySpan<byte> data, long offset)
        {
            if (_length + data.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
            _ranges.Add(new ByteRange(offset, data.Length));
        }

        private void Reset()
        {
            _length = 0;
            _ranges.Clear();
            if (_buffer.Length > 1 << 20)
            {
                _buffer = new byte[4096];
            }
        }
    }
}
