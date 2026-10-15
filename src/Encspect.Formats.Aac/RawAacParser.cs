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

    // A chain further in than the start of the data, where other formats' frames or junk can repeat header-like bytes at a fixed distance,
    // must be longer to count for the sniff: the ones that FFmpeg's E-AC-3 samples make hold three frames at most.
    private const int FurtherChainLength = 8;

    public string Name => "AAC";

    // An ADTS chain at the start of the data, after any zero bytes, is class 91, one above MPEG audio's
    // 90, so a file that is ADTS from the start is never taken for MPEG; one found further in, as in
    // junk inside an MP3, is class 50, below it, and needs eight consistent frames or frames to the end
    // of the data, since other formats' frames repeat ADTS-looking header bytes in chains of three
    // (E-AC-3's among them). ADTS's layer bits are 00, which no MPEG audio header has, so neither takes
    // the other's headers. ADTS frames have a CRC only when the header says so and it takes the frame's
    // elements to check, which the sniff does not read: no frame is counted as verified.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.MatchesAt(reader.Start, "ADIF"u8))
        {
            return Adif.Plausible(reader) ? new SniffResult(91, reader.Start, 1, Note: "ADIF header") : SniffResult.None;
        }

        var cache = new BlockCache(reader);
        long start = ZeroRunEnd(cache, reader.Start, reader.End);
        long limit = Math.Min(reader.End, start + SniffLimit);
        if (FindChain(cache, start, Math.Min(limit, start + 1), reader.End) == start)
        {
            return Chain(91, cache, start, reader.End);
        }

        long found = FindChain(cache, start, limit, reader.End, FurtherChainLength);
        return found < 0 ? SniffResult.None : Chain(50, cache, found, reader.End);
    }

    // The whole frames of the chain at position that follow one another with the same fixed header, at most SniffResult.MaxFrames.
    private static SniffResult Chain(int sniffClass, BlockCache cache, long position, long end)
    {
        long start = position;
        AdtsHeader? first = null;
        int frames = 0;
        while (frames < SniffResult.MaxFrames && HeaderAt(cache, position, end) is { } header && (first is not { } stream || header.IsSameStream(stream))
            && position + header.FrameLength <= end)
        {
            first ??= header;
            frames++;
            position += header.FrameLength;
        }

        return new SniffResult(sniffClass, start, frames, End: position);
    }

    public void Parse(ParseContext context) => ParseInto(context.Reader, context.Root, context, context.EncoderClaims, context.EmbeddedTags);

    // Raw AAC as a file or as a payload a container hands to the codec: nodes go under parent.
    internal static void ParseInto(ByteReader reader, Node parent, AnalysisContext context, IReadOnlyList<EncoderClaim> claims, IReadOnlyList<Node>? embedded = null)
    {
        if (reader.MatchesAt(reader.Start, "ADIF"u8))
        {
            Adif.Parse(reader, parent, context, claims);
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
        Records records = state.Records;
        region.Node = parent.Add(new Node("ADTS", start, end - start, null, () => RegionChildren(records, region))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Adts,
            Topic = Topics.Adts,
        });
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
        var node = new Node("Gap", start, end - start, NodeValue.Integer(end - start, "bytes")) { Description = Explain.Gap };
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
        var node = new Node("Frame", position, length, header.Summary(length)) { Kind = NodeKind.Frame, Description = Explain.AdtsFrame };
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

        BlockLog log = state.LogFor(index, header);
        FrameDetails details = Analyze(bytes, position, header, length < header.FrameLength || cut, log, () => node, state.Findings);
        if (details.CrcState == CrcState.Mismatch)
        {
            state.Findings.Add(Severity.Warning, "aac.crc-mismatch", node, count =>
                $"{(count == 1 ? "1 ADTS frame fails" : $"{count} ADTS frames fail")} the CRC check, first at 0x{position:X}.");
        }
        else if (details.CrcState == CrcState.NotVerified)
        {
            state.Findings.Add(Severity.Info, "aac.crc-not-verified", node, count =>
                $"The CRC of {(count == 1 ? "1 ADTS frame" : $"{count} ADTS frames")}, first at 0x{position:X}, covers elements of a raw data block that could not be read to its end, so it is not verified.");
        }

        if (details.HasBadPositions)
        {
            state.Findings.Add(Severity.Warning, "aac.block-positions-invalid", node, count =>
                $"The raw data block positions of {(count == 1 ? "1 ADTS frame" : $"{count} ADTS frames")}, first at 0x{position:X}, do not lie in increasing order inside the frame.");
        }

        if (state.Signature is null && log.Tally.FirstText is { } text)
        {
            state.Signature = (text, index, position);
        }

        if (details.Blocks.Length > 1 || details.Positions.Length > 0)
        {
            state.Details[index] = details;
        }

        if (details.Channels is { } channels)
        {
            region.PceChannels ??= channels;
            region.PceLayout ??= details.Layout;
        }

        Block first = details.Blocks[0];
        state.Frames.Add(new Frame(position, length, header.Raw)
        {
            Crc = details.Crc,
            CrcState = details.CrcState,
            Elements = first.Elements,
            Skeleton = first.Skeleton,
            IsTruncated = truncated,
        });
    }

    // The raw data blocks of a frame, each read element by element (BlockReader), and the CRC verdict
    // (1.A.2.2.2 and 1.A.3.2.2): with one raw data block, the CRC after the header covers the header
    // and the regions of the block's elements that Crc16.AddElements gives; with more blocks it
    // covers the header and the block positions, and each block ends in its own CRC over its
    // elements. A block that cannot be read to its end leaves its CRC unverified. Blocks of an
    // unprotected frame lie one after another; a damaged or cut frame's blocks are read without
    // reporting what breaks in them, which the frame's own finding covers.
    private static FrameDetails Analyze(ReadOnlySpan<byte> frame, long offset, AdtsHeader header, bool damaged, BlockLog log, Func<Node> frameNode, FindingCollector findings)
    {
        int headerLength = header.HeaderLength;
        int blocks = header.RawDataBlocks;
        if (frame.Length < headerLength)
        {
            return new FrameDetails([new Block(headerLength, 0, -1, -1, -1, CrcState.None)], [], -1, CrcState.None, false, null);
        }

        int[] positions = [];
        bool separate = header.IsProtected && blocks > 1;
        if (separate)
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
        foreach (int position in positions)
        {
            int start = headerLength + position;
            if (damaged && start > frame.Length)
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

        var result = new List<Block>(blocks);
        CrcState overall = CrcState.None;
        int next = headerLength;
        bool readable = true;
        for (int i = 0; i < blocks && readable; i++)
        {
            int start;
            int end;
            if (separate)
            {
                if (badPositions && i > 0 || i >= starts.Count)
                {
                    break;
                }

                start = starts[i];
                end = i + 1 < starts.Count ? starts[i + 1] : frame.Length;
            }
            else
            {
                start = next;
                end = frame.Length;
                if (start >= end && i > 0)
                {
                    break;
                }
            }

            int blockCrc = -1;
            if (separate && !damaged && end - start >= 2)
            {
                blockCrc = (frame[end - 2] << 8) | frame[end - 1];
                end -= 2;
            }

            ReadOnlySpan<byte> data = frame[start..end];
            var where = new UnitBytes(offset + start, end - start, null);
            (long skeleton, BlockResult read) = log.Read(data, 0, data.Length * 8, where, frameNode, report: !damaged);
            readable = read.Ok;
            if (read.Ok && (separate || blocks == 1) && read.End < data.Length * 8 && !damaged)
            {
                Node trailing = TrailingNode(where, read.End, data.Length);
                long at = trailing.Offset;
                findings.Add(Severity.Warning, "aac.block-trailing-bytes", trailing, count => count == 1
                    ? $"The raw data block at 0x{offset + start:X} holds bytes after its END element, from 0x{at:X}."
                    : $"{count} raw data blocks hold bytes after their END element, first at 0x{at:X}.");
            }

            CrcState state = CrcState.None;
            if (blockCrc >= 0)
            {
                var block = new Crc16();
                state = Verdict(read, data, ref block, blockCrc, log);
            }

            result.Add(new Block(start, separate || blocks > 1 ? (read.Ok && !separate ? read.End / 8 : end - start) : -1, read.Elements, skeleton, blockCrc, state));
            next = start + (read.End / 8);
        }

        if (result.Count == 0)
        {
            result.Add(new Block(headerLength, 0, -1, -1, -1, CrcState.None));
        }

        if (header.IsProtected && !damaged)
        {
            var crc16 = new Crc16();
            crc16.Add(frame, 0, (headerLength - 2) * 8);
            if (separate)
            {
                overall = crc16.Value == crc ? CrcState.Verified : CrcState.Mismatch;
                foreach (Block block in result)
                {
                    overall = Worst(overall, block.CrcState);
                }

                if (result.Count < blocks)
                {
                    overall = Worst(overall, CrcState.NotVerified);
                }
            }
            else if (!readable || result[0].Skeleton < 0)
            {
                overall = CrcState.NotVerified;
            }
            else
            {
                crc16.AddElements(frame[headerLength..], log.LastElements);
                overall = crc16.Value == crc ? CrcState.Verified : CrcState.Mismatch;
            }
        }

        IReadOnlyList<Speaker>? layout = null;
        if (BlockLog.FirstElement(result[0].Elements) == 5 && header.ChannelConfiguration == 0)
        {
            try
            {
                var fields = new BitFields(new MappedBytes(frame[starts[0]..].ToArray(), 0));
                fields.Skip(3);
                ProgramConfigElement.Read(fields, new Node("PCE", 0, 1), 0, (_, _, _, _) => { }, out IReadOnlyList<Speaker> read);
                layout = read;
            }
            catch (EndOfStreamException)
            {
                layout = null;
            }
        }

        return new FrameDetails([.. result], positions, crc, overall, badPositions, layout);
    }

    private static CrcState Verdict(BlockResult read, ReadOnlySpan<byte> block, ref Crc16 crc, int expected, BlockLog log)
    {
        if (!read.Ok)
        {
            return CrcState.NotVerified;
        }

        crc.AddElements(block, log.LastElements);
        return crc.Value == expected ? CrcState.Verified : CrcState.Mismatch;
    }

    private static CrcState Worst(CrcState a, CrcState b) => (CrcState)Math.Max((int)a, (int)b);

    private static Node TrailingNode(UnitBytes where, int blockEnd, int length) =>
        where.Bits("Trailing Bytes", blockEnd, length * 8, NodeValue.Integer(length - (blockEnd / 8), "bytes"), Explain.TrailingUnitBytes);

    // The offset of the first header in [from, limit) that a chain of consistent frames follows, or
    // -1.
    internal static long FindChain(BlockCache cache, long from, long limit, long end, int chainLength = ChainLength)
    {
        for (long position = cache.IndexOf(0xFF, from, limit); position >= 0; position = cache.IndexOf(0xFF, position + 1, limit))
        {
            if ((cache.ByteAt(position + 1) & 0xF6) != 0xF0)
            {
                continue;
            }

            if (HeaderAt(cache, position, end) is { } header && Chains(cache, position, header, end, chainLength))
            {
                return position;
            }
        }

        return -1;
    }

    private static bool Chains(BlockCache cache, long position, AdtsHeader header, long end, int chainLength)
    {
        long next = position + header.FrameLength;
        for (int count = 1; count < chainLength; count++)
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
                return count >= chainLength - 1;
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

    private static IReadOnlyList<Node> RegionChildren(Records records, Region region)
    {
        int first = region.First;
        int end = region.End ?? records.Frames.Count;
        return new BlockedChildren(region.Leading, end - first, i => FrameNode(records, first + i));
    }

    private static Node FrameNode(Records records, int index)
    {
        Frame frame = records.Frames[index];
        if (frame.IsGap)
        {
            return new Node("Gap", frame.Offset, frame.Length, NodeValue.Integer(frame.Length, "bytes")) { Description = Explain.Gap };
        }

        var header = new AdtsHeader(frame.Header);
        return new Node("Frame", frame.Offset, frame.Length, header.Summary(frame.Length), () => FrameChildren(records, index, frame, header))
        {
            Kind = NodeKind.Frame,
            Description = Explain.AdtsFrame,
        };
    }

    private static List<Node> FrameChildren(Records records, int index, Frame frame, AdtsHeader header)
    {
        var nodes = new List<Node>();
        long offset = frame.Offset;
        nodes.AddRange(header.Nodes(offset).Where(n => n.End <= frame.Offset + frame.Length));
        records.Details.TryGetValue(index, out FrameDetails? details);
        if (details is not null)
        {
            for (int i = 0; i < details.Positions.Length; i++)
            {
                long at = offset + AdtsHeader.Length + (2 * i);
                if (at + 2 <= frame.Offset + frame.Length)
                {
                    nodes.Add(new Node("Raw Data Block Position", at, 2, NodeValue.Integer(details.Positions[i], "bytes")) { Description = Explain.RawDataBlockPosition });
                }
            }
        }

        int headerLength = header.HeaderLength;
        if (frame.Crc >= 0)
        {
            Node crc = new("CRC", offset + headerLength - 2, 2, NodeValue.Hex(frame.Crc, 4)) { Description = Explain.Crc, Topic = Topics.Crc };
            crc.Add(new Node("Check", crc.Offset, 2, Verdict(frame.CrcState)) { Description = Explain.CrcCheck });
            nodes.Add(crc);
        }

        Block[] blocks = details?.Blocks ?? [new Block(headerLength, -1, frame.Elements, frame.Skeleton, -1, CrcState.None)];
        (StreamSetup setup, UnitStore store) = records.UnitsAt(index);
        foreach (Block block in blocks)
        {
            // Each block's own entry: the store may start a block's entry in a new chunk.
            long position = block.Skeleton;
            long start = offset + block.Start;
            if (start >= frame.Offset + frame.Length)
            {
                continue;
            }

            long length = block.Length >= 0 ? block.Length : frame.Offset + frame.Length - start;
            var unit = new UnitBytes(start, (int)length, null);
            Node node = new("Raw Data Block", start, length, BlockLog.ElementList(block.Elements)) { Description = Explain.RawDataBlock, Topic = Topics.RawDataBlocks };
            if (position >= 0)
            {
                UnitStore.Entry entry = store.Read(position);
                List<Node> elements = BlockLog.Nodes(store, setup, position, unit);
                foreach (Node child in elements)
                {
                    node.Add(child);
                }

                long last = elements.Count > 0 ? elements.Max(n => n.End) : start;
                if (!entry.IsWhole && last < start + length)
                {
                    node.Add(TrailingNode(unit, (int)(last - start) * 8, (int)length));
                }
            }

            nodes.Add(node);
            if (block.Crc >= 0)
            {
                Node crc = new("Raw Data Block CRC", start + length, 2, NodeValue.Hex(block.Crc, 4)) { Description = Explain.BlockCrc, Topic = Topics.Crc };
                crc.Add(new Node("Check", crc.Offset, 2, Verdict(block.CrcState)) { Description = Explain.CrcCheck });
                nodes.Add(crc);
            }
        }

        return nodes;
    }

    private static string Verdict(CrcState state) => state switch
    {
        CrcState.Verified => "verified",
        CrcState.Mismatch => "mismatch",
        CrcState.NotVerified => "not verified: the raw data block could not be read to its end",
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
                    Node node = FrameNode(state.Records, i);
                    state.Findings.AddNow(Severity.Info, "aac.stream-changed",
                        $"The ADTS stream changes at 0x{node.Offset:X} from {Describe(before)} to {Describe(current)}.", WithoutChildren(node));
                }

                start = start < 0 ? i : start;
                previous = current;
            }
        }
    }

    private const int AppleMinimumFrames = 20;

    // The headers afconvert writes (macOS 15.7, 16 ADTS files of AAC-LC, HE-AAC and HE-AAC v2 of at least 20 frames, 8 on each of arm64 and
    // x86-64): the MPEG-2 ID, no CRC, the original/copy bit set, the other flag bits clear and a buffer fullness of 0 in
    // every frame. In the 56 other ADTS fixtures (FAAC, fdkaac, ffmpeg, hand-built) the original/copy bit is clear and
    // the buffer fullness is 0x7FF, 256, or varies.
    private static bool IsAppleHeader(AdtsHeader header) =>
        header.Id == 1 && !header.IsProtected && header.IsOriginal && !header.IsHome && !header.IsPrivate && !header.CopyrightIdBit
        && !header.IsCopyrightIdStart && header.BufferFullness == 0;

    private static string Describe(AdtsHeader header) =>
        $"{header.ProfileName}, {header.SampleRate} Hz, channel configuration {header.ChannelConfiguration}";

    private static Node WithoutChildren(Node node) => new(node.Name, node.Offset, node.Length, node.Value) { Kind = node.Kind };

    private static void AddStream(State state, AnalysisContext context, IReadOnlyList<EncoderClaim> claims, int start, int end)
    {
        var header = new AdtsHeader(state.Frames[start].Header);
        List<Region> regions = state.Regions.Where(r => r.First < end && (r.End ?? state.Frames.Count) > start).ToList();
        Region first = regions[0];
        BlockLog found = state.LogAt(start);
        AacConfig config = header.Config with
        {
            Channels = header.ChannelConfiguration == 0 ? first.PceChannels : AacTables.Channels(header.ChannelConfiguration),
            Layout = header.ChannelConfiguration == 0 ? first.PceLayout : null,
            SbrFound = found.Tally.SbrBlocks > 0,
            PsFound = found.Tally.PsBlocks > 0,
        };
        var figures = new StreamFigures(config.OutputSampleRate);
        int vbr = 0;
        int frames = 0;
        int appleHeaders = 0;
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
            appleHeaders += IsAppleHeader(frameHeader) ? 1 : 0;
            frames++;
        }

        Node FirstFrame() => WithoutChildren(FrameNode(state.Records, start));
        if (vbr > 0 && vbr < frames)
        {
            state.Findings.AddNow(Severity.Info, "aac.buffer-fullness-mixed",
                $"{vbr} of {frames} ADTS frames signal a variable bitrate (buffer fullness 0x7FF), the others a buffer fullness.", FirstFrame());
        }

        found.ReportSignaling(config, FirstFrame(), "ADTS");

        // The encoder and statistics go into the stream's first region, which holds its first frame,
        // and cover it.
        Node Cover(string name, NodeValue? value, NodeKind kind, string description, string? topic = null) =>
            new(name, first.Node!.Offset, first.Node.Length, value) { Kind = kind, Description = description, Topic = topic };

        // Apple's ADTS writer: see IsAppleHeader. A habit of one writer, so a claim of low confidence; it names the
        // writer, which writes these headers around any stream it is given.
        if (frames >= AppleMinimumFrames && appleHeaders == frames)
        {
            claims = [.. claims, new EncoderClaim("Apple AudioToolbox", FirstFrame(),
                $"ADTS header layout of Apple's AudioToolbox writer: all {frames} frames have the MPEG-2 ID, the original/copy bit set and an adts_buffer_fullness of 0", ClaimRole.Writer)
            {
                IsLayout = true,
            }];
        }

        Node? encoder = AacEncoder.Detect(
            state.Signature is { } signature && signature.Index >= start && signature.Index < end
                ? new AacEncoder.Signature(signature.Text, WithoutChildren(FrameNode(state.Records, signature.Index)), signature.Index - start)
                : null,
            claims,
            (name, value, description) => Cover(name, value, NodeKind.Detail, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => state.Findings.Add(Severity.Info, "aac.encoder-claim-differs", claim.Source, _ =>
                AacEncoder.Disagreement(claim, text, name, "the tag may have been written by other software, or kept when the audio was re-encoded.")),
            tally: found.Tally);
        if (encoder is not null)
        {
            first.Leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(claims, (name, value, description) => Cover(name, value, NodeKind.Detail, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => state.Findings.Add(Severity.Info, "aac.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            AacEncoder.WriterName,
            format: new WriterDetection.StreamFormat(KnownWriters.Aac, "AAC", (claim, message, node) => state.Findings.Add(Severity.Info, "aac.writer-other-format", node, _ => message)));
        if (writer is not null)
        {
            first.Leading.Add(writer);
        }

        ClaimRoles.CompleteVersion(encoder, claims, AacEncoder.WriterName);

        Node statistics = figures.Statistics((name, value, kind, description) => Cover(name, value, kind, description), "Frame", "Frames", out Node bitrate);
        CodingStatistics.Add(statistics, found.Tally, found.Setup, (name, value, description, topic) => Cover(name, value, NodeKind.Detail, description, topic));
        first.Leading.Add(statistics);

        (string? encoderName, Confidence? encoderConfidence) = ClaimRoles.Resolved(encoder);
        (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
        context.AddStream(new StreamInfo
        {
            Nodes = regions.Select(r => r.Node!).ToList(),
            Codec = config.Codec,
            SampleRate = config.OutputSampleRate,
            Channels = config.OutputChannels ?? 0,
            ChannelMode = config.ChannelMode,
            ChannelLayout = config.OutputLayout,
            Samples = figures.Samples,
            Duration = figures.Duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = vbr > 0 && vbr == frames,
            Encoder = encoderName,
            EncoderConfidence = encoderConfidence,
            Writer = writerName,
            WriterConfidence = writerConfidence,
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

    // A raw data block of a frame: where it starts in the frame and its length (-1: to the frame's
    // end), its packed element IDs, where the store keeps it, and its own CRC and verdict.
    private sealed record Block(int Start, int Length, int Elements, long Skeleton, int Crc, CrcState CrcState);

    // Layout: the speakers of a program config element that starts the frame's first block.
    private sealed record FrameDetails(Block[] Blocks, int[] Positions, int Crc, CrcState CrcState, bool HasBadPositions, IReadOnlyList<Speaker>? Layout)
    {
        public int? Channels => Layout?.Count;
    }

    // A frame or, with a zero header (never a valid one), a gap, in about 40 bytes.
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Frame(long offset, int length, ulong header)
    {
        public long Offset => offset;

        public int Length => length;

        public ulong Header => header;

        public bool IsGap => header == 0;

        public int Crc { get; init; } = -1;

        public CrcState CrcState { get; init; }

        // The packed element IDs of the first raw data block (BlockResult.Elements).
        public int Elements { get; init; }

        // Where the store keeps the first raw data block, the others following it, or -1.
        public long Skeleton { get; init; } = -1;

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

        public IReadOnlyList<Speaker>? PceLayout { get; set; }
    }

    // What frame nodes are built from once the parse is over, and nothing else: the frames, their
    // details and, per run of frames with one fixed header, the setup and store their blocks were
    // kept in. The parse's block cache (and the reader it reads), findings and block readers stay
    // with State, which node factories must not capture (hoist `state.Records` into a local first):
    // a report that holds State keeps the input stream alive and a build could consume its readers.
    private sealed class Records
    {
        private readonly List<(int First, StreamSetup Setup, UnitStore Store)> _units = [];

        public ChunkedList<Frame> Frames { get; } = new();

        public Dictionary<int, FrameDetails> Details { get; } = [];

        public void AddUnits(int first, StreamSetup setup, UnitStore store) => _units.Add((first, setup, store));

        public (StreamSetup Setup, UnitStore Store) UnitsAt(int index)
        {
            int at = _units.FindLastIndex(l => l.First <= index);
            (int _, StreamSetup setup, UnitStore store) = _units[Math.Max(at, 0)];
            return (setup, store);
        }
    }

    private sealed class State(BlockCache cache, CancellationToken cancellation)
    {
        public BlockCache Cache => cache;

        public Records Records { get; } = new();

        public CancellationToken Cancellation => cancellation;

        public ChunkedList<Frame> Frames => Records.Frames;

        public List<Region> Regions { get; } = [];

        public Dictionary<int, FrameDetails> Details => Records.Details;

        public FindingCollector Findings { get; } = new();

        public (string Text, int Index, long Offset)? Signature { get; set; }

        // The raw data blocks of each run of frames with the same fixed header, by the run's first
        // frame, in order.
        public List<(int First, AdtsHeader Header, BlockLog Log)> Logs { get; } = [];

        public BlockLog LogFor(int index, AdtsHeader header)
        {
            if (Logs.Count == 0 || !Logs[^1].Header.IsSameStream(header))
            {
                var log = new BlockLog(new StreamSetup(header.Config), Findings);
                Logs.Add((index, header, log));
                Records.AddUnits(index, log.Setup, log.Store);
            }

            return Logs[^1].Log;
        }

        public BlockLog LogAt(int index)
        {
            int at = Logs.FindLastIndex(l => l.First <= index);
            return Logs[Math.Max(at, 0)].Log;
        }
    }
}
