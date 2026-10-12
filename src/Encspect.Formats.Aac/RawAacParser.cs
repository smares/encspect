using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// Raw AAC files: ADTS (ISO/IEC 14496-3 1.A.2.2 and ISO/IEC 13818-7 6.2), a header before every
// frame, and ADIF (1.A.2.1), one header before raw data blocks that nothing delimits. As with MPEG
// sync, an ADTS header only starts a stream when a chain of consistent frames follows it: two more
// with the same fixed header at the positions its length gives, or frames that end exactly at the
// end of the data. Once in sync, frames are followed by their lengths; anything else is a gap, and
// sync is searched again after it.
public sealed class RawAacParser : IFormatParser
{
    private const int SniffLimit = 1 << 20;
    private const int ChainLength = 3;

    public string Name => "AAC";

    // MPEG audio returns 90 wherever its chain starts. An ADTS chain at the start of the data, after
    // any zero bytes, outranks it by one, so a file that is ADTS from the start is never taken for
    // MPEG; one found further in, as in junk inside an MP3, ranks below it.
    public int Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.MatchesAt(reader.Start, "ADIF"u8))
        {
            return Adif.Plausible(reader) ? 91 : 0;
        }

        var cache = new BlockCache(reader);
        long start = ZeroRunEnd(cache, reader.Start, reader.End);
        long found = FindChain(cache, start, Math.Min(reader.End, start + SniffLimit), reader.End);
        return found < 0 ? 0 : found == start ? 91 : 50;
    }

    public void Parse(ParseContext context) => ParseInto(context.Reader, context.Root, context, context.EncoderClaims, context.EmbeddedTags);

    // Raw AAC as a file or as a payload a container hands to the codec: nodes go under parent.
    internal static void ParseInto(ByteReader reader, Node parent, AnalysisContext context, IReadOnlyList<EncoderClaim> claims, IReadOnlyList<Node>? embedded = null)
    {
        if (reader.MatchesAt(reader.Start, "ADIF"u8))
        {
            Adif.Parse(reader, parent, context);
            return;
        }

        var state = new State(new BlockCache(reader), context.CancellationToken);
        long position = reader.Start;
        foreach (Node? tag in (embedded ?? []).OrderBy(t => t.Offset).Append<Node?>(null))
        {
            long end = tag?.Offset ?? reader.End;
            if (end > position)
            {
                ParseSegment(state, parent, position, end);
            }

            position = tag?.End ?? position;
        }

        AddStreams(state, context, claims);
        state.Findings.Flush(context);
    }

    private static void ParseSegment(State state, Node parent, long start, long end)
    {
        var region = new Region(state.Frames.Count);
        state.Regions.Add(region);
        region.Node = parent.Add(new Node("ADTS", start, end - start, null, () => RegionChildren(state, region)) { Kind = NodeKind.Stream });
        BlockCache cache = state.Cache;
        long position = start;
        AdtsHeader? stream = null;
        var buffer = new byte[1 << 13];
        int frames = 0;
        while (position < end)
        {
            if (frames % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            if (stream is { } current && HeaderAt(cache, position, end) is { } header && header.IsSameStream(current))
            {
                long frameEnd = position + header.FrameLength;
                if (frameEnd > end)
                {
                    AddFrame(state, region, position, end, header, buffer, truncated: true);
                    position = end;
                    frames++;
                    break;
                }

                // A frame is only taken on its header while a frame of the stream follows it;
                // otherwise a stream that starts inside it cuts it short where it was damaged.
                AdtsHeader? next = frameEnd < end ? HeaderAt(cache, frameEnd, end) : null;
                if (frameEnd < end && (next is null || !next.Value.IsSameStream(header)))
                {
                    long restart = FindChain(cache, position + 1, frameEnd, end);
                    if (restart > position)
                    {
                        AddFrame(state, region, position, restart, header, buffer, cut: true);
                        position = restart;
                        frames++;
                        continue;
                    }
                }

                AddFrame(state, region, position, frameEnd, header, buffer);
                position = frameEnd;
                frames++;
                continue;
            }

            long found = FindChain(cache, position, end, end);
            long gapEnd = found < 0 ? end : found;
            if (gapEnd > position)
            {
                AddGap(state, position, gapEnd, leading: frames == 0, trailing: found < 0 && frames > 0);
            }

            if (found < 0)
            {
                break;
            }

            stream = HeaderAt(cache, found, end);
            position = found;
        }

        region.End = state.Frames.Count;
    }

    private static void AddGap(State state, long start, long end, bool leading, bool trailing)
    {
        state.Frames.Add(Frame.Gap(start, end - start));
        var node = new Node("Gap", start, end - start, NodeValue.Integer(end - start, "bytes"));
        long length = end - start;
        if (leading)
        {
            string what = ZeroRunEnd(state.Cache, start, end) == end ? " (all zero bytes)" : string.Empty;
            state.Findings.AddNow(Severity.Warning, "aac.leading-junk", $"{length} bytes before the first ADTS frame are not audio{what}.", node);
        }
        else if (trailing)
        {
            state.Findings.AddNow(Severity.Warning, "aac.trailing-data", $"{length} bytes after the last ADTS frame at 0x{start:X} are not a frame.", node);
        }
        else
        {
            state.Findings.AddNow(Severity.Warning, "aac.sync-lost", $"ADTS sync is lost for {length} bytes at 0x{start:X}.", node);
        }
    }

    private static void AddFrame(State state, Region region, long position, long end, AdtsHeader header, byte[] buffer, bool truncated = false, bool cut = false)
    {
        int length = (int)(end - position);
        Span<byte> bytes = buffer.AsSpan(0, length);
        state.Cache.Read(position, bytes);
        int index = state.Frames.Count;
        var node = new Node("Frame", position, length, header.Summary(length)) { Kind = NodeKind.Frame };
        if (truncated)
        {
            state.Findings.AddNow(Severity.Warning, "aac.frame-truncated", $"The last ADTS frame at 0x{position:X} has {length} of its {header.FrameLength} bytes.", node);
        }

        if (cut)
        {
            state.Findings.Add(Severity.Warning, "aac.frame-damaged", node, count =>
                $"{(count == 1 ? "An ADTS frame is" : $"{count} ADTS frames are")} cut short by the next frame's header, first at 0x{position:X}: their header or data is damaged, or bytes are missing.");
        }

        if (header.Id == 1 && header.Profile == 3)
        {
            state.Findings.Add(Severity.Warning, "aac.reserved-profile", node, count => $"The MPEG-2 ADTS profile 3 is reserved, first at 0x{position:X} ({FindingCollector.Times(count)}).");
        }

        FrameDetails details = Analyze(bytes, header, length < header.FrameLength);
        if (details.CrcState == CrcState.Mismatch)
        {
            state.Findings.Add(Severity.Warning, "aac.crc-mismatch", node, count =>
                $"{(count == 1 ? "1 ADTS frame fails" : $"{count} ADTS frames fail")} the CRC check, first at 0x{position:X}.");
        }
        else if (details.CrcState == CrcState.NotVerified)
        {
            state.Findings.Add(Severity.Info, "aac.crc-not-verified", node, count =>
                $"The CRC of {(count == 1 ? "1 ADTS frame" : $"{count} ADTS frames")}, first at 0x{position:X}, covers a channel element whose end is not known without its spectral data, which is not parsed yet, so it is not verified.");
        }

        if (details.HasBadPositions)
        {
            state.Findings.Add(Severity.Warning, "aac.block-positions-invalid", node, count =>
                $"The raw data block positions of {(count == 1 ? "1 ADTS frame" : $"{count} ADTS frames")}, first at 0x{position:X}, do not lie in increasing order inside the frame.");
        }

        if (details.Blocks[0].Text is { } text)
        {
            state.Signature ??= (text, index, position);
        }

        if (details.Blocks.Length > 1 || details.Blocks[0].Element.Id is 4 or 6 || details.Positions.Length > 0)
        {
            state.Details[index] = details;
        }

        if (details.Channels is { } channels)
        {
            region.PceChannels ??= channels;
        }

        state.Frames.Add(new Frame(position, length, header.Raw)
        {
            Crc = details.Crc,
            CrcState = details.CrcState,
            Element = (sbyte)details.Blocks[0].Element.Id,
            Tag = (byte)Math.Max(0, details.Blocks[0].Element.Tag),
            IsTruncated = truncated,
        });
    }

    // The raw data blocks of a frame, their first elements and the CRC verdict (1.A.2.2.2 and
    // 1.A.3.2.2): with one raw data block, the CRC after the header covers the header and, in the
    // block, the first 192 bits of each single channel, LFE or coupling element and of a channel
    // pair's first channel, the first 128 of its second channel, and the whole of each program
    // config and data stream element, after their IDs, filled up with zero bits where an element is
    // shorter. With more blocks, it covers the header and the block positions, and each block ends
    // in its own CRC over its elements. Where an element's end cannot be known without its spectral
    // data, a CRC that matches with the element taken as at least 192 bits long is verified, and one
    // that does not stays unverified rather than failing.
    private static FrameDetails Analyze(ReadOnlySpan<byte> frame, AdtsHeader header, bool truncated)
    {
        int headerLength = header.HeaderLength;
        int blocks = header.RawDataBlocks;
        if (frame.Length < headerLength)
        {
            return new FrameDetails([new Block(headerLength, -1, FirstElement.Empty, null, -1, CrcState.None)], [], -1, CrcState.None, false, null);
        }

        int[] positions = [];
        if (header.IsProtected && blocks > 1)
        {
            positions = new int[blocks - 1];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = (frame[AdtsHeader.Length + (2 * i)] << 8) | frame[AdtsHeader.Length + (2 * i) + 1];
            }
        }

        int crc = header.IsProtected ? (frame[headerLength - 2] << 8) | frame[headerLength - 1] : -1;
        var starts = new List<int> { headerLength };
        bool badPositions = false;
        foreach (int offset in positions)
        {
            int start = headerLength + offset;
            if (truncated && start > frame.Length)
            {
                break;
            }

            if (start <= starts[^1] || start > frame.Length)
            {
                badPositions = true;
                break;
            }

            starts.Add(start);
        }

        var result = new Block[header.IsProtected && blocks > 1 && !badPositions ? starts.Count : 1];
        CrcState overall = CrcState.None;
        for (int i = 0; i < result.Length; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : frame.Length;
            int blockCrc = -1;
            if (header.IsProtected && blocks > 1 && !truncated && end - start >= 2)
            {
                blockCrc = (frame[end - 2] << 8) | frame[end - 1];
                end -= 2;
            }

            ReadOnlySpan<byte> data = frame[start..end];
            FirstElement element = FirstElement.Read(data, 0);
            CrcState state = CrcState.None;
            if (blockCrc >= 0)
            {
                var block = new Crc16();
                state = Check(data, ref block, blockCrc);
            }

            result[i] = new Block(start, blocks > 1 && (header.IsProtected || i > 0) ? end - start : -1, element, element.Text(data), blockCrc, state);
        }

        if (header.IsProtected && !truncated)
        {
            var crc16 = new Crc16();
            crc16.Add(frame, 0, (headerLength - 2) * 8);
            if (blocks > 1)
            {
                overall = crc16.Value == crc ? CrcState.Verified : CrcState.Mismatch;
            }
            else
            {
                overall = Check(frame[headerLength..], ref crc16, crc);
            }

            foreach (Block block in result.Skip(1).Concat(blocks > 1 ? [result[0]] : []))
            {
                overall = Worst(overall, block.CrcState);
            }
        }

        int? channels = null;
        if (result[0].Element.Id == 5 && header.ChannelConfiguration == 0)
        {
            try
            {
                var fields = new BitFields(new MappedBytes(frame[starts[0]..].ToArray(), 0));
                fields.Skip(3);
                channels = ProgramConfigElement.Read(fields, new Node("PCE", 0, 1), 0, (_, _, _, _) => { });
            }
            catch (EndOfStreamException)
            {
                channels = null;
            }
        }

        return new FrameDetails(result, positions, crc, overall, badPositions, channels);
    }

    private static CrcState Worst(CrcState a, CrcState b) => (CrcState)Math.Max((int)a, (int)b);

    // Adds the protected regions of a raw data block to crc and compares it with expected.
    private static CrcState Check(ReadOnlySpan<byte> block, ref Crc16 crc, int expected)
    {
        int bit = 0;
        int length = block.Length * 8;
        while (bit + 3 <= length)
        {
            int id = (int)FirstElement.Bits(block, bit, 3);
            switch (id)
            {
                case 7:
                    return crc.Value == expected ? CrcState.Verified : CrcState.Mismatch;
                case 4 or 6:
                    FirstElement element = FirstElement.Read(block, bit);
                    if (element.EndBit < 0)
                    {
                        return CrcState.NotVerified;
                    }

                    if (id == 4)
                    {
                        crc.Add(block, bit + 3, element.EndBit - bit - 3);
                    }

                    bit = element.EndBit;
                    break;
                case 0 or 2 or 3:
                    // Taken as at least 192 bits long, and as the last protected element.
                    if (bit + 3 + 192 > length)
                    {
                        return CrcState.NotVerified;
                    }

                    crc.Add(block, bit + 3, 192);
                    return crc.Value == expected ? CrcState.Verified : CrcState.NotVerified;
                default:
                    // A channel pair's second channel and a PCE's end need more than the element ID.
                    return CrcState.NotVerified;
            }
        }

        return CrcState.NotVerified;
    }

    // The offset of the first header in [from, limit) that a chain of consistent frames follows, or
    // -1.
    internal static long FindChain(BlockCache cache, long from, long limit, long end)
    {
        for (long position = from; position < limit; position++)
        {
            if (cache.ByteAt(position) != 0xFF || (cache.ByteAt(position + 1) & 0xF6) != 0xF0)
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

    private static bool Chains(BlockCache cache, long position, AdtsHeader header, long end)
    {
        long next = position + header.FrameLength;
        for (int count = 1; count < ChainLength; count++)
        {
            if (next == end)
            {
                return true;
            }

            if (HeaderAt(cache, next, end) is not { } following || !following.IsSameStream(header))
            {
                return false;
            }

            next += following.FrameLength;
            if (next > end)
            {
                return count >= 2;
            }
        }

        return true;
    }

    private static AdtsHeader? HeaderAt(BlockCache cache, long position, long end)
    {
        if (position + AdtsHeader.Length > end)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[AdtsHeader.Length];
        if (cache.Read(position, bytes) < AdtsHeader.Length)
        {
            return null;
        }

        var header = AdtsHeader.Read(bytes);
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

    private static IReadOnlyList<Node> RegionChildren(State state, Region region)
    {
        int end = region.End ?? state.Frames.Count;
        return new BlockedChildren(region.Leading, end - region.First, i => FrameNode(state, region.First + i));
    }

    private static Node FrameNode(State state, int index)
    {
        Frame frame = state.Frames[index];
        if (frame.IsGap)
        {
            return new Node("Gap", frame.Offset, frame.Length, NodeValue.Integer(frame.Length, "bytes"));
        }

        var header = new AdtsHeader(frame.Header);
        return new Node("Frame", frame.Offset, frame.Length, header.Summary(frame.Length), () => FrameChildren(state, index, frame, header)) { Kind = NodeKind.Frame };
    }

    private static List<Node> FrameChildren(State state, int index, Frame frame, AdtsHeader header)
    {
        var nodes = new List<Node>();
        long offset = frame.Offset;
        nodes.AddRange(header.Nodes(offset).Where(n => n.End <= frame.Offset + frame.Length));
        state.Details.TryGetValue(index, out FrameDetails? details);
        if (details is not null)
        {
            for (int i = 0; i < details.Positions.Length; i++)
            {
                long at = offset + AdtsHeader.Length + (2 * i);
                if (at + 2 <= frame.Offset + frame.Length)
                {
                    nodes.Add(new Node("Raw Data Block Position", at, 2, NodeValue.Integer(details.Positions[i], "bytes")));
                }
            }
        }

        int headerLength = header.HeaderLength;
        if (frame.Crc >= 0)
        {
            Node crc = new("CRC", offset + headerLength - 2, 2, NodeValue.Hex(frame.Crc, 4));
            crc.Add(new Node("Check", crc.Offset, 2, Verdict(frame.CrcState)));
            nodes.Add(crc);
        }

        Block[] blocks = details?.Blocks ?? [new Block(headerLength, -1, FirstElement.Empty with { Id = frame.Element, Tag = frame.Tag }, null, -1, CrcState.None)];
        foreach (Block block in blocks)
        {
            long start = offset + block.Start;
            if (start >= frame.Offset + frame.Length)
            {
                continue;
            }

            long length = block.Length >= 0 ? block.Length : frame.Offset + frame.Length - start;
            var unit = new UnitBytes(start, (int)length, null);
            Node node = new("Raw Data Block", start, length, block.Element.Summary);
            foreach (Node child in block.Element.Nodes(unit, 0, block.Text))
            {
                if (child.End <= node.End)
                {
                    node.Add(child);
                }
            }

            nodes.Add(node);
            if (block.Crc >= 0)
            {
                Node crc = new("Raw Data Block CRC", start + length, 2, NodeValue.Hex(block.Crc, 4));
                crc.Add(new Node("Check", crc.Offset, 2, Verdict(block.CrcState)));
                nodes.Add(crc);
            }
        }

        return nodes;
    }

    private static string Verdict(CrcState state) => state switch
    {
        CrcState.Verified => "verified",
        CrcState.Mismatch => "mismatch",
        CrcState.NotVerified => "not verified: a channel element's end needs its spectral data",
        _ => "not checked: the frame is cut short",
    };

    // Streams end where the fixed header changes; a stream continues across gaps and tags.
    private static void AddStreams(State state, AnalysisContext context, IReadOnlyList<EncoderClaim> claims)
    {
        int start = -1;
        AdtsHeader? previous = null;
        for (int i = 0; i <= state.Frames.Count; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            if (i < state.Frames.Count && state.Frames[i].IsGap)
            {
                continue;
            }

            AdtsHeader? header = i < state.Frames.Count ? new AdtsHeader(state.Frames[i].Header) : null;
            if (start >= 0 && (header is null || !header.Value.IsSameStream(previous!.Value)))
            {
                AddStream(state, context, claims, start, i);
                start = -1;
            }

            if (header is { } current)
            {
                if (start < 0 && previous is { } before && !current.IsSameStream(before))
                {
                    Node node = FrameNode(state, i);
                    state.Findings.AddNow(Severity.Info, "aac.stream-changed",
                        $"The ADTS stream changes at 0x{node.Offset:X} from {Describe(before)} to {Describe(current)}.", WithoutChildren(node));
                }

                start = start < 0 ? i : start;
                previous = current;
            }
        }
    }

    private static string Describe(AdtsHeader header) =>
        $"{header.ProfileName}, {header.SampleRate} Hz, channel configuration {header.ChannelConfiguration}";

    private static Node WithoutChildren(Node node) => new(node.Name, node.Offset, node.Length, node.Value) { Kind = node.Kind };

    private static void AddStream(State state, AnalysisContext context, IReadOnlyList<EncoderClaim> claims, int start, int end)
    {
        var header = new AdtsHeader(state.Frames[start].Header);
        List<Region> regions = state.Regions.Where(r => r.First < end && (r.End ?? state.Frames.Count) > start).ToList();
        Region first = regions[0];
        AacConfig config = header.Config with { Channels = header.ChannelConfiguration == 0 ? first.PceChannels : AacTables.Channels(header.ChannelConfiguration) };
        var figures = new StreamFigures(config.OutputSampleRate);
        int vbr = 0;
        int frames = 0;
        for (int i = start; i < end; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            ref readonly Frame frame = ref state.Frames[i];
            if (frame.IsGap)
            {
                continue;
            }

            var frameHeader = new AdtsHeader(frame.Header);
            figures.Add(frame.Offset, frame.Length, config.OutputFrameLength * frameHeader.RawDataBlocks);
            vbr += frameHeader.IsVbr ? 1 : 0;
            frames++;
        }

        Node FirstFrame() => WithoutChildren(FrameNode(state, start));
        if (vbr > 0 && vbr < frames)
        {
            state.Findings.AddNow(Severity.Info, "aac.buffer-fullness-mixed",
                $"{vbr} of {frames} ADTS frames signal a variable bitrate (buffer fullness 0x7FF), the others a buffer fullness.", FirstFrame());
        }

        if (config.ImplicitPossible)
        {
            state.Findings.AddNow(Severity.Info, "aac.implicit-signaling-not-checked",
                "ADTS cannot signal SBR or PS explicitly. They can be signaled implicitly, in the fill elements of the access units, which are not checked yet; the stream may be HE-AAC or HE-AAC v2.",
                FirstFrame());
        }

        // The encoder and statistics go into the stream's first region, which holds its first frame,
        // and cover it.
        Node Cover(string name, NodeValue? value, NodeKind kind = NodeKind.Detail) => new(name, first.Node!.Offset, first.Node.Length, value) { Kind = kind };

        Node? encoder = AacEncoder.Detect(
            state.Signature is { } signature && signature.Index >= start && signature.Index < end
                ? new AacEncoder.Signature(signature.Text, WithoutChildren(FrameNode(state, signature.Index)), signature.Index - start)
                : null,
            claims,
            (name, value) => Cover(name, value),
            (claim, text, name) => state.Findings.Add(Severity.Info, "aac.encoder-claim-differs", claim.Source, _ =>
                $"The {claim.Description} names \"{text}\", but the stream points to {name}; the tag may have been written by other software, or kept when the audio was re-encoded."));
        if (encoder is not null)
        {
            first.Leading.Add(encoder);
        }

        Node statistics = figures.Statistics(Cover, "Frame", "Frames", out Node bitrate);
        first.Leading.Add(statistics);

        string? confidence = encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString();
        context.AddStream(new StreamInfo
        {
            Nodes = regions.Select(r => r.Node!).ToList(),
            Codec = config.Codec,
            SampleRate = config.OutputSampleRate,
            Channels = config.OutputChannels ?? 0,
            ChannelMode = config.ChannelMode,
            Samples = figures.Samples,
            Duration = figures.Duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = vbr > 0 && vbr == frames,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = figures.Series(bitrate, state.Cancellation),
        });
    }

    internal enum CrcState
    {
        None,
        Verified,
        NotVerified,
        Mismatch,
    }

    private sealed record Block(int Start, int Length, FirstElement Element, string? Text, int Crc, CrcState CrcState);

    private sealed record FrameDetails(Block[] Blocks, int[] Positions, int Crc, CrcState CrcState, bool HasBadPositions, int? Channels);

    // A frame or, with a zero header (never a valid one), a gap, in about 32 bytes.
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Frame(long offset, int length, ulong header)
    {
        public long Offset => offset;

        public int Length => length;

        public ulong Header => header;

        public bool IsGap => header == 0;

        public int Crc { get; init; } = -1;

        public CrcState CrcState { get; init; }

        public sbyte Element { get; init; }

        public byte Tag { get; init; }

        public bool IsTruncated { get; init; }

        public static Frame Gap(long offset, long length) => new(offset, (int)Math.Min(length, int.MaxValue), 0);
    }

    private sealed class Region(int first)
    {
        public int First => first;

        public int? End { get; set; }

        public Node? Node { get; set; }

        public List<Node> Leading { get; } = [];

        public int? PceChannels { get; set; }
    }

    private sealed class State(BlockCache cache, CancellationToken cancellation)
    {
        public BlockCache Cache => cache;

        public CancellationToken Cancellation => cancellation;

        public ChunkedList<Frame> Frames { get; } = new();

        public List<Region> Regions { get; } = [];

        public Dictionary<int, FrameDetails> Details { get; } = [];

        public FindingCollector Findings { get; } = new();

        public (string Text, int Index, long Offset)? Signature { get; set; }
    }
}
