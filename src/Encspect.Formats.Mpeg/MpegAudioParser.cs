using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// MPEG-1, 2 and 2.5 audio, Layers I to III, at frame-header level. A header starts a stream only
// when two more frames of the same stream follow at the positions its length predicts, or when
// its frames end exactly at the end of the data (plan, "MPEG sync"). Once in sync, frames are
// followed by their lengths and never searched inside; anything that is not a frame is a gap.
public sealed class MpegAudioParser : IFormatParser
{
    private const int SniffLimit = 1 << 20;

    // Free-format frames are found from the next matching header; 640 kbps at 32000 Hz is the
    // largest frame LAME writes, and this leaves room for Layer I.
    private const int MaxFreeFormatLength = 8192;

    public string Name => "MPEG Audio";

    public int Sniff(ByteReader reader)
    {
        var blocks = new BlockReader(reader);
        long limit = Math.Min(reader.End, reader.Start + SniffLimit);
        return FindStreamStart(blocks, reader.Start, limit, reader.End, out _) >= 0 ? 90 : 0;
    }

    public void Parse(ParseContext context)
    {
        var blocks = new BlockReader(context.Reader);
        var findings = new FindingCollector();
        var runs = new List<MainDataRun>();
        var stream = new StreamState(blocks, findings, runs, new BitReservoir(runs));
        long position = context.Reader.Start;

        foreach (Node? tag in context.EmbeddedTags.OrderBy(t => t.Offset).Append<Node?>(null))
        {
            long end = tag?.Offset ?? context.Reader.End;
            if (end > position)
            {
                ParseSegment(stream, context.Root, position, end);
            }

            position = tag?.End ?? position;
        }

        findings.Flush(context);
    }

    private static void ParseSegment(StreamState state, Node root, long start, long end)
    {
        (BlockReader blocks, FindingCollector findings) = (state.Blocks, state.Findings);
        var entries = new List<Entry>();
        List<MainDataRun> runs = state.Runs;
        Node region = root.Add(new Node("MPEG Audio", start, end - start, null, () => BuildRegion(entries, runs)));
        int frames = 0;
        long position = start;
        MpegHeader? stream = null;
        int freeLength = 0;

        while (position < end)
        {
            long at = position;
            if (stream is { } current && blocks.TryReadUInt32(at, out uint raw) && new MpegHeader(raw) is { IsValid: true } header)
            {
                if (header.IsSameStream(current))
                {
                    Entry frame = AddFrame(state, entries, at, end, header, freeLength);
                    if (header.ChannelModeBits != current.ChannelModeBits)
                    {
                        findings.Add(Severity.Info, "mpeg.channel-mode-changed", FindingNode(frame), count =>
                            $"The channel mode changes {Times(count)}, first from {current.ChannelMode} to {header.ChannelMode} at 0x{at:X}.");
                    }

                    position = frame.End;
                    stream = header;
                    frames++;
                    continue;
                }

                if (FindStreamStart(blocks, at, at + 1, end, out int newFreeLength) == at)
                {
                    freeLength = newFreeLength;
                    stream = header;
                    Entry frame = AddFrame(state, entries, at, end, header, freeLength);
                    findings.Add(Severity.Info, "mpeg.stream-changed", FindingNode(frame), count =>
                        $"The stream changes {Times(count)}, first from {current.StreamName} to {header.StreamName} at 0x{at:X}.");
                    position = frame.End;
                    frames++;
                    continue;
                }
            }

            long next = FindStreamStart(blocks, stream is null ? position : position + 1, end, end, out int foundFreeLength);
            long gapEnd = next < 0 ? end : next;
            if (gapEnd > position)
            {
                AddGap(position, gapEnd, frames == 0, next < 0 && frames > 0);
            }

            if (next < 0)
            {
                break;
            }

            position = next;
            blocks.TryReadUInt32(position, out uint first);
            stream = new MpegHeader(first);
            freeLength = foundFreeLength;
            if (stream.Value.IsFreeFormat)
            {
                findings.Add(Severity.Info, "mpeg.free-format", null, _ =>
                    $"The stream is free format: frames of {foundFreeLength} bytes plus padding.");
            }
        }

        region.Value = $"{frames} frames";

        void AddGap(long gapStart, long gapEnd, bool leading, bool trailing)
        {
            var gap = new Entry(gapStart, gapEnd - gapStart, 0, -1, -1, null, 0, 0, 0);
            entries.Add(gap);
            long length = gap.Length;
            if (leading && !trailing && gapEnd < end)
            {
                bool zero = Enumerable.Range(0, (int)Math.Min(length, 1 << 20)).All(i => blocks.ByteAt(gapStart + i) == 0);
                string what = zero ? " (all zero bytes)" : string.Empty;
                findings.AddNow(Severity.Warning, "mpeg.leading-junk", $"{length} bytes before the first frame are not audio{what}.", GapNode(gap));
            }
            else if (leading)
            {
                return;
            }
            else if (trailing)
            {
                findings.AddNow(Severity.Warning, "mpeg.trailing-data", $"{length} bytes after the last frame at 0x{gapStart:X} are not a frame.", GapNode(gap));
            }
            else
            {
                findings.AddNow(Severity.Warning, "mpeg.sync-lost", $"Sync is lost for {length} bytes at 0x{gapStart:X}.", GapNode(gap));
            }
        }
    }

    // Records the frame at position, with what checking it needs the bytes for, and reports its
    // problems. Its nodes are built from the record when the report is read.
    private static Entry AddFrame(StreamState state, List<Entry> entries, long position, long end, MpegHeader header, int freeLength)
    {
        (BlockReader blocks, FindingCollector findings) = (state.Blocks, state.Findings);
        int length = header.FrameLength(freeLength);
        long available = Math.Min(length, end - position);
        int stored = -1;
        int computed = -1;

        if (header.IsProtected && available >= 6)
        {
            byte[] crcBytes = blocks.Read(position + 4, 2);
            stored = (crcBytes[0] << 8) | crcBytes[1];
            int coverage = header.CrcCoverage;
            if (coverage >= 0 && 6 + coverage <= available)
            {
                byte[] headerTail = blocks.Read(position + 2, 2);
                byte[] covered = blocks.Read(position + 6, coverage);
                computed = Crc16.Compute(covered, Crc16.Compute(headerTail));
            }
        }

        byte[]? sideInfo = null;
        int mainDataLength = 0;
        int earlier = 0;
        int fill = 0;
        MainDataPlacement? placement = null;
        long sideInfoOffset = position + 4 + (header.IsProtected ? 2 : 0);
        if (header.Layer == 3 && sideInfoOffset + Layer3SideInfo.Size(header) <= position + available)
        {
            sideInfo = blocks.Read(sideInfoOffset, Layer3SideInfo.Size(header));
            var info = Layer3SideInfo.Read(sideInfo, header);
            CheckSideInfo(info, position, sideInfoOffset, findings);
            mainDataLength = info.MainDataLength;
            long dataOffset = sideInfoOffset + sideInfo.Length;
            placement = state.Reservoir.Add(header, position, position + available, dataOffset,
                (int)(position + available - dataOffset), info.MainDataBegin.Value, mainDataLength, available < length);
            earlier = placement.Value.Earlier;
            fill = (int)Math.Min(placement.Value.Reservoir, (1 << info.MainDataBegin.Length) - 1);
        }
        else
        {
            state.Reservoir.Reset();
        }

        var frame = new Entry(position, available, header.Raw, stored, computed, sideInfo, mainDataLength, earlier, fill);
        entries.Add(frame);
        if (placement is { } placed)
        {
            ReportPlacement(placed, FindingNode(frame), findings);
        }

        if (header.EmphasisBits == 2)
        {
            findings.Add(Severity.Warning, "mpeg.emphasis-reserved", new Node("Emphasis", position + 3, 1, header.Emphasis), count =>
                $"{Frames(count)} use the reserved emphasis value, the first at 0x{position:X}.");
        }

        if (!header.IsLayer2BitrateModeAllowed)
        {
            findings.Add(Severity.Warning, "mpeg.bitrate-mode-not-allowed", FindingNode(frame), count =>
                $"{Frames(count)} combine Layer II at {header.Bitrate} kbps with {header.ChannelMode}, which ISO/IEC 11172-3 does not allow, the first at 0x{position:X}.");
        }

        if (stored >= 0)
        {
            var crc = new Node("CRC", position + 4, 2, $"0x{stored:X4}");
            if (header.CrcCoverage < 0)
            {
                findings.Add(Severity.Info, "mpeg.crc-not-verified", crc, count =>
                    $"{Frames(count)} carry a CRC that is not verified yet; Layer II CRC coverage depends on the allocation tables.");
            }
            else if (computed >= 0 && computed != stored)
            {
                findings.Add(Severity.Warning, "mpeg.crc-mismatch", crc, count =>
                    $"{Frames(count)} fail the CRC check, the first at 0x{position:X}.");
            }
        }

        if (available < length)
        {
            findings.AddNow(Severity.Warning, "mpeg.frame-truncated", $"The last frame at 0x{position:X} has {available} of its {length} bytes.", FindingNode(frame));
        }

        return frame;
    }

    private static List<Node> BuildRegion(List<Entry> entries, List<MainDataRun> runs) =>
        entries.Select(e => e.IsGap ? GapNode(e) : FrameNode(e, runs)).ToList();

    private static Node GapNode(Entry gap) => new("Gap", gap.Offset, gap.Length, $"{gap.Length} bytes");

    private static Node FrameNode(Entry frame, List<MainDataRun> runs) =>
        new("Frame", frame.Offset, frame.Length, new MpegHeader(frame.Header).Summary, () => BuildFrame(frame, runs));

    // Findings only need to say where the frame is.
    private static Node FindingNode(Entry frame) => new("Frame", frame.Offset, frame.Length, new MpegHeader(frame.Header).Summary);

    private static List<Node> BuildFrame(Entry frame, List<MainDataRun> runs)
    {
        var header = new MpegHeader(frame.Header);
        long position = frame.Offset;
        var children = new List<Node>();

        Node fields = new("Header", position, 4);
        children.Add(fields);
        fields.Add("Version", position + 1, 1, header.VersionName);
        fields.Add("Layer", position + 1, 1, header.LayerName);
        fields.Add("IsProtected", position + 1, 1, Bool(header.IsProtected));
        fields.Add("Bitrate", position + 2, 1, header.BitrateName);
        fields.Add("Sample Rate", position + 2, 1, $"{header.SampleRate} Hz");
        fields.Add("HasPadding", position + 2, 1, Bool(header.Padding == 1));
        fields.Add("IsPrivate", position + 2, 1, Bool(header.IsPrivate));
        fields.Add("Channel Mode", position + 3, 1, header.ChannelMode);
        fields.Add("Mode Extension", position + 3, 1, header.ModeExtensionName);
        fields.Add("IsCopyrighted", position + 3, 1, Bool(header.IsCopyrighted));
        fields.Add("IsOriginal", position + 3, 1, Bool(header.IsOriginal));
        fields.Add("Emphasis", position + 3, 1, header.Emphasis);

        long dataStart = position + 4;
        if (frame.Crc >= 0)
        {
            Node crc = new("CRC", position + 4, 2, $"0x{frame.Crc:X4}");
            children.Add(crc);
            if (frame.ComputedCrc >= 0)
            {
                crc.Add("Computed", position + 4, 2, $"0x{frame.ComputedCrc:X4}");
            }

            dataStart += 2;
        }

        if (frame.SideInfo is not null)
        {
            children.Add(Layer3SideInfo.Read(frame.SideInfo, header).ToNode(dataStart, frame.ReservoirFill));
            AddMainData(children, frame, dataStart + frame.SideInfo.Length, runs);
        }
        else if (frame.End > dataStart)
        {
            children.Add(new Node("Data", dataStart, frame.End - dataStart, $"{frame.End - dataStart} bytes"));
        }

        return children;
    }

    // Splits a Layer III frame's bytes after the side information by owner: its own main data,
    // main data of later frames in the bit reservoir, and ancillary data no frame uses.
    private static void AddMainData(List<Node> children, Entry frame, long dataStart, List<MainDataRun> runs)
    {
        string own = $"{frame.MainDataLength} bytes" + (frame.Earlier > 0 ? $", {frame.Earlier} of them in earlier frames" : string.Empty);
        int first = children.Count;
        bool hasOwn = false;
        long cursor = dataStart;
        int low = 0;
        int high = runs.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            (low, high) = runs[middle].End <= dataStart ? (middle + 1, high) : (low, middle);
        }

        for (int i = low; i < runs.Count && runs[i].Offset < frame.End; i++)
        {
            MainDataRun run = runs[i];
            AddAncillary(cursor, run.Offset);
            if (run.Owner == frame.Offset)
            {
                children.Add(new Node("Main Data", run.Offset, run.Length, own));
                hasOwn = true;
            }
            else
            {
                children.Add(new Node("Bit Reservoir", run.Offset, run.Length, $"{run.Length} bytes of the main data of the frame at 0x{run.Owner:X}"));
            }

            cursor = run.End;
        }

        AddAncillary(cursor, frame.End);
        if (!hasOwn)
        {
            children.Insert(first, new Node("Main Data", dataStart, 0, own));
        }

        void AddAncillary(long from, long to)
        {
            if (to > from)
            {
                children.Add(new Node("Ancillary Data", from, to - from, $"{to - from} bytes"));
            }
        }
    }

    private static void CheckSideInfo(Layer3SideInfo info, long position, long offset, FindingCollector findings)
    {
        MpegHeader header = info.Header;
        foreach ((int gr, int ch, Layer3Granule g) in info.All)
        {
            if (g.BigValues.Value > 288)
            {
                findings.Add(Severity.Warning, "mpeg.big-values-too-large", Field("Big Values", g.BigValues), count =>
                    $"{Frames(count)} have more than 288 big_values pairs, the first {g.BigValues.Value} at 0x{position:X}.");
            }

            if (g.IsSwitched && g.BlockType.Value == 0)
            {
                findings.Add(Severity.Warning, "mpeg.block-type-reserved", Field("Block Type", g.BlockType), count =>
                    $"{Frames(count)} switch windows with block type 0, which is reserved, the first at 0x{position:X}.");
            }

            foreach (BitField table in g.TableSelect.Where(t => t.Value is 4 or 14))
            {
                findings.Add(Severity.Warning, "mpeg.huffman-table-undefined", Field("Table Select", table), count =>
                    $"{Frames(count)} select a Huffman table that does not exist, the first table {table.Value} at 0x{position:X}.");
            }

            int part2 = info.Part2Bits(gr, ch);
            if (part2 > g.Part23Length.Value)
            {
                findings.Add(Severity.Warning, "mpeg.scalefactors-exceed-granule", Field("Part 2 and 3 Length", g.Part23Length), count =>
                    $"{Frames(count)} have scalefactors longer than part2_3_length, the first {part2} of {g.Part23Length.Value} bits at 0x{position:X}.");
            }

            // libmad rejects these frames; ffmpeg and mpg123 decode them.
            if (header.IsMpeg1 && g.IsShort && info.Scfsi[ch].Value != 0)
            {
                findings.Add(Severity.Warning, "mpeg.scfsi-with-short-blocks", Field("SCFSI", info.Scfsi[ch]), count =>
                    $"{Frames(count)} set scfsi for a channel with short blocks, which libmad rejects, the first at 0x{position:X}.");
            }
        }

        bool jointStereo = header.ChannelModeBits == 1 && header.ModeExtension != 0;
        foreach (Layer3Granule[] row in info.Granules.Where(row => jointStereo && row.Length == 2))
        {
            if (row[0].BlockTypeValue != row[1].BlockTypeValue || row[0].IsMixed != row[1].IsMixed)
            {
                findings.Add(Severity.Warning, "mpeg.stereo-block-types-differ", Field("Block Type", row[1].WindowSwitching), count =>
                    $"{Frames(count)} use MS or intensity stereo with a different block type in each channel, which libmad rejects, the first at 0x{position:X}.");
            }
        }

        Node Field(string name, BitField field) => Layer3SideInfo.Span(name, offset, field.Bit, field.Bit + field.Length);
    }

    private static void ReportPlacement(MainDataPlacement placement, Node frame, FindingCollector findings)
    {
        long position = frame.Offset;
        switch (placement.Problem)
        {
            case MainDataProblem.BeforeStream:
                findings.Add(Severity.Warning, "mpeg.main-data-before-stream", frame, count =>
                    $"{Frames(count)} have main data that begins before the stream, the first at 0x{position:X}; the stream was probably cut.");
                break;
            case MainDataProblem.Overlap:
                findings.Add(Severity.Warning, "mpeg.main-data-overlap", frame, count =>
                    $"{Frames(count)} have main data that overlaps the previous frame's, the first at 0x{position:X}; frames are probably missing.");
                break;
            case MainDataProblem.AcrossGap:
                findings.Add(Severity.Warning, "mpeg.reservoir-across-gap", frame, count =>
                    $"{Frames(count)} take main data from before a gap, the first at 0x{position:X}; decoders differ on whether it is still there.");
                break;
        }

        if (placement.Overflows)
        {
            findings.Add(Severity.Warning, "mpeg.main-data-overflow", frame, count =>
                $"{Frames(count)} have more main data than the bit reservoir and the frame hold, the first at 0x{position:X}.");
        }
    }

    // The first offset in [from, limit) where a stream starts, or -1. A stream starts at a valid
    // header followed by two more frames of the same stream, or whose frames end exactly at end.
    private static long FindStreamStart(BlockReader blocks, long from, long limit, long end, out int freeLength)
    {
        freeLength = 0;
        for (long position = from; position < limit; position++)
        {
            if (blocks.ByteAt(position) != 0xFF || !blocks.TryReadUInt32(position, out uint raw))
            {
                continue;
            }

            var header = new MpegHeader(raw);
            if (!header.IsValid)
            {
                continue;
            }

            foreach (int candidate in FreeLengths(blocks, position, header, end))
            {
                if (IsChain(blocks, position, header, candidate, end))
                {
                    freeLength = candidate;
                    return position;
                }
            }
        }

        return -1;
    }

    // Possible unpadded lengths of a free-format frame: distances to later headers of the same
    // stream. For other frames the length comes from the header, so there is one dummy value.
    private static IEnumerable<int> FreeLengths(BlockReader blocks, long position, MpegHeader header, long end)
    {
        if (!header.IsFreeFormat)
        {
            yield return 0;
            yield break;
        }

        long limit = Math.Min(end - 4, position + MaxFreeFormatLength);
        for (long next = position + 4; next <= limit; next++)
        {
            if (blocks.ByteAt(next) == 0xFF && blocks.TryReadUInt32(next, out uint raw)
                && new MpegHeader(raw) is { IsValid: true } candidate && candidate.IsSameStream(header))
            {
                yield return (int)(next - position) - (header.Padding * header.SlotSize);
            }
        }
    }

    private static bool IsChain(BlockReader blocks, long position, MpegHeader first, int freeLength, long end)
    {
        long next = position;
        MpegHeader header = first;
        for (int frame = 0; frame < 3; frame++)
        {
            next += header.FrameLength(freeLength);
            if (next == end)
            {
                return true;
            }

            if (frame == 2)
            {
                return true;
            }

            if (next > end || !blocks.TryReadUInt32(next, out uint raw) || new MpegHeader(raw) is not { IsValid: true } following
                || !following.IsSameStream(first))
            {
                return false;
            }

            header = following;
        }

        return true;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    // A frame or, with a zero header (never a valid one), a gap: all a region keeps per frame
    // until its nodes are asked for. Crc and ComputedCrc are -1 when absent.
    // SideInfo holds a Layer III frame's raw side information; MainDataLength and Earlier, how
    // long its main data is and how much of it lies in earlier frames; ReservoirFill, the bytes
    // in the bit reservoir when the frame starts.
    private readonly record struct Entry(long Offset, long Length, uint Header, int Crc, int ComputedCrc, byte[]? SideInfo,
        int MainDataLength, int Earlier, int ReservoirFill)
    {
        public bool IsGap => Header == 0;

        public long End => Offset + Length;
    }

    private static string Frames(int count) => count == 1 ? "1 frame" : $"{count.ToString(CultureInfo.InvariantCulture)} frames";

    private static string Times(int count) => count == 1 ? "once" : $"{count.ToString(CultureInfo.InvariantCulture)} times";

    // What a file's frames share: the bytes, the findings, and the bit reservoir with the main-data
    // runs it has recorded, which frames on both sides of a tag can own.
    private sealed record StreamState(BlockReader Blocks, FindingCollector Findings, List<MainDataRun> Runs, BitReservoir Reservoir);

    // Findings of one kind are reported once per stream with a count, pointing at the first case.
    private sealed class FindingCollector
    {
        private readonly Dictionary<string, (Severity Severity, Node? Node, int Count, Func<int, string> Message)> _aggregated = [];
        private readonly List<Finding> _immediate = [];

        public void Add(Severity severity, string code, Node? node, Func<int, string> message)
        {
            _aggregated[code] = _aggregated.TryGetValue(code, out var existing)
                ? existing with { Count = existing.Count + 1 }
                : (severity, node, 1, message);
        }

        public void AddNow(Severity severity, string code, string message, Node node) =>
            _immediate.Add(new Finding(severity, code, message, node));

        public void Flush(ParseContext context)
        {
            foreach (Finding finding in _immediate)
            {
                context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node);
            }

            foreach ((string code, var entry) in _aggregated)
            {
                context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node ?? context.Root);
            }
        }
    }
}
