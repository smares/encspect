using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// MPEG-1, 2 and 2.5 audio, Layers I to III, at frame-header level. A header starts a stream only
// when two more frames of the same stream follow at the positions its length predicts, or when
// its frames end exactly at the end of the data (plan, "MPEG sync"). Once in sync, frames are
// followed by their lengths and never searched inside; anything that is not a frame is a gap.
public sealed partial class MpegAudioParser : IFormatParser
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
        var stream = new StreamState(blocks, findings, new BuildContext(runs), new BitReservoir(runs), [], [], []);
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

        DecideMultichannel(stream);
        CheckMusicCrcs(stream);
        foreach (RegionState region in stream.Regions)
        {
            region.Encoder = DetectEncoder(stream, region.Entries);
        }

        AddStatistics(stream);

        findings.Flush(context);
    }

    private static void ParseSegment(StreamState state, Node root, long start, long end)
    {
        (BlockReader blocks, FindingCollector findings) = (state.Blocks, state.Findings);
        var entries = new List<Entry>();
        BuildContext build = state.Build;
        var regionState = new RegionState(entries);
        state.Regions.Add(regionState);
        Node region = root.Add(new Node("MPEG Audio", start, end - start, null, () => BuildRegion(regionState, build)));
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
                    findings.Add(Severity.Warning, "mpeg.stream-changed", FindingNode(frame), count =>
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
            var gap = new Entry(gapStart, gapEnd - gapStart, 0, -1, -1, null, 0, 0, 0, null, null, false);
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

        Layer2Record? layer2 = header.Layer == 2 ? ReadLayer2(blocks, position, available, length, header, ref computed, findings) : null;
        byte[]? vbrFrame = HasVbrMagic(blocks, position, available, header) ? blocks.Read(position, (int)available) : null;
        VbrHeader? vbr = vbrFrame is null ? null : VbrHeader.TryRead(vbrFrame, header);
        vbrFrame = vbr is null ? null : vbrFrame;

        byte[]? sideInfo = null;
        int mainDataLength = 0;
        int earlier = 0;
        int fill = 0;
        MainDataPlacement? placement = null;
        long sideInfoOffset = position + 4 + (header.IsProtected ? 2 : 0);
        if (vbr is null && header.Layer == 3 && sideInfoOffset + Layer3SideInfo.Size(header) <= position + available)
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
        else if (vbr is not null && sideInfoOffset + Layer3SideInfo.Size(header) <= position + available)
        {
            // A VBR header frame is a valid frame without main data, so decoders that do not skip it
            // keep its bytes in the bit reservoir (ISO/IEC 11172-3 2.4.2.7).
            long dataOffset = sideInfoOffset + Layer3SideInfo.Size(header);
            state.Reservoir.Add(header, position, position + available, dataOffset, (int)(position + available - dataOffset), 0, 0, available < length);
        }
        else
        {
            state.Reservoir.Reset();
        }

        var frame = new Entry(position, available, header.Raw, stored, computed, sideInfo, mainDataLength, earlier, fill, layer2, vbrFrame,
            available < length);
        entries.Add(frame);
        if (vbr is not null)
        {
            ReportVbrHeader(state, frame, vbr, entries.Count > 1 && !entries[^2].IsGap);
        }

        if (layer2?.Multichannel is not null)
        {
            state.Candidates.Add(frame);
        }
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
            if (computed >= 0 && computed != stored)
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

    private static List<Node> BuildRegion(RegionState region, BuildContext build)
    {
        var nodes = new List<Node>();
        if (region.Encoder is { } encoder)
        {
            nodes.Add(encoder);
        }

        if (region.Statistics is { } statistics)
        {
            nodes.Add(statistics);
        }

        nodes.AddRange(region.Entries.Select(e => e.IsGap ? GapNode(e) : FrameNode(e, build)));
        return nodes;
    }

    private static Node GapNode(Entry gap) => new("Gap", gap.Offset, gap.Length, $"{gap.Length} bytes");

    private static Node FrameNode(Entry frame, BuildContext build) =>
        new("Frame", frame.Offset, frame.Length, new MpegHeader(frame.Header).Summary, () => BuildFrame(frame, build));

    // Findings only need to say where the frame is.
    private static Node FindingNode(Entry frame) => new("Frame", frame.Offset, frame.Length, new MpegHeader(frame.Header).Summary);

    private static List<Node> BuildFrame(Entry frame, BuildContext build)
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

        if (frame.VbrFrame is not null)
        {
            VbrHeader vbr = VbrHeader.TryRead(frame.VbrFrame, header)!;
            children.AddRange(vbr.ToNodes(position, (int)(dataStart - position), build.MusicCrcs.TryGetValue(position, out int crc) ? crc : null));
        }
        else if (frame.SideInfo is not null)
        {
            children.Add(Layer3SideInfo.Read(frame.SideInfo, header).ToNode(dataStart, frame.ReservoirFill));
            AddMainData(children, frame, dataStart + frame.SideInfo.Length, build.Runs);
        }
        else if (frame.Layer2 is { } layer2)
        {
            AddLayer2(children, frame, header, layer2, build.HasMultichannel);
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

    // Reads a Layer II frame's audio data, verifies its CRC over the header bits, the bit allocation
    // and scfsi (ISO/IEC 11172-3 2.4.3.1), and reads a multichannel candidate after the samples.
    private static Layer2Record? ReadLayer2(BlockReader blocks, long position, long available, int length, MpegHeader header,
        ref int computed, FindingCollector findings)
    {
        byte[] bytes = blocks.Read(position, (int)available);
        var audio = Layer2AudioData.Read(bytes, header, length);
        if (audio.SampleStart > bytes.Length * 8)
        {
            return null;
        }

        if (header.IsProtected)
        {
            computed = Crc16.ComputeBits(bytes, audio.AllocationStart, audio.ScalefactorStart - audio.AllocationStart, Crc16.ComputeBits(bytes, 16, 16));
        }

        foreach ((int ch, int sb, int _) in audio.UndefinedScalefactors().Take(1))
        {
            int bit = audio.ScalefactorBit[ch, sb] + (6 * Array.IndexOf(audio.Scalefactors[ch, sb], 63));
            // TwoLAME writes it, and ffmpeg, mpg123 and libmad decode it.
            findings.Add(Severity.Warning, "mpeg.scalefactor-undefined", Layer2AudioData.Span("Scalefactors", position, bit, bit + 6), count =>
                $"{Frames(count)} use scalefactor index 63, which Table B.1 of ISO/IEC 11172-3 does not define but decoders accept, the first at 0x{position:X}.");
        }

        if (audio.End > bytes.Length * 8 && available == length)
        {
            findings.Add(Severity.Warning, "mpeg.audio-data-overflow", new Node("Frame", position, available), count =>
                $"{Frames(count)} need more bits for their Layer II audio data than they hold, the first at 0x{position:X}.");
        }

        byte[] audioBytes = bytes[..Math.Min(bytes.Length, (audio.SampleStart + 7) / 8)];
        if (!header.IsMpeg1 || header.IsMono || available < length || audio.End + 32 > bytes.Length * 8)
        {
            return new Layer2Record(audioBytes, length, null, 0, 0, 0);
        }

        int[,] baseAllocation = BaseAllocation(audio);
        var mc = Layer2Multichannel.Read(bytes, audio.End, bytes.Length * 8, baseAllocation, header.SampleRate);
        int first = audio.End / 8;
        int last = Math.Min(bytes.Length, (Math.Max(mc.SideInfoEnd, mc.CrcBit + 16) + 7) / 8);
        return new Layer2Record(audioBytes, length, bytes[first..last], audio.End - (first * 8), (bytes.Length - first) * 8, position + first);
    }

    private static int[,] BaseAllocation(Layer2AudioData audio)
    {
        var allocation = new int[2, audio.Subbands];
        for (int sb = 0; sb < audio.Subbands; sb++)
        {
            allocation[0, sb] = audio.Allocation[0, sb];
            allocation[1, sb] = audio.Allocation[1, sb];
        }

        return allocation;
    }

    private static Layer2Multichannel ReadMultichannel(Layer2Record record, MpegHeader header) =>
        Layer2Multichannel.Read(record.Multichannel!, record.McStart, record.McEnd,
            BaseAllocation(Layer2AudioData.Read(record.Audio, header, record.FrameLength)), header.SampleRate);

    // The ancillary data of MPEG-1 Layer II frames carries the multichannel extension only by
    // convention, so a file counts as multichannel once two frames' multichannel CRCs hold;
    // random ancillary data passes a 16-bit CRC once in 65,536 frames. Where every frame's side
    // information continues in an extension bitstream, no CRC can be checked: the same header in
    // nearly every frame is then reported as a possible extension, not as one.
    private static void DecideMultichannel(StreamState state)
    {
        var read = state.Candidates.Select(e => (Frame: e, Mc: ReadMultichannel(e.Layer2!, new MpegHeader(e.Header)))).ToList();
        if (read.Count(r => r.Mc.IsCrcValid) < 2)
        {
            var continued = read.Where(r => !r.Mc.Fits && r.Mc.HasExtension).ToList();
            if (continued.Count >= 2 && continued.Count * 10 >= read.Count * 9 && continued.Select(r => r.Mc.HeaderKey).Distinct().Count() == 1)
            {
                Layer2Multichannel first = continued[0].Mc;
                state.Findings.Add(Severity.Info, "mpeg.multichannel-unverified", FindingNode(continued[0].Frame), count =>
                    $"The ancillary data of {continued.Count} frames looks like an MPEG-2 multichannel extension ({first.Configuration}) whose side information continues in an extension bitstream, a separate file, so it cannot be verified.");
            }

            return;
        }

        state.Build.HasMultichannel = true;
        foreach ((Entry frame, Layer2Multichannel mc) in read)
        {
            Node node = FindingNode(frame);
            if (mc.Fits && !mc.IsCrcValid)
            {
                state.Findings.Add(Severity.Warning, "mpeg.multichannel-crc-mismatch", node, count =>
                    $"{Frames(count)} fail the multichannel CRC check, the first at 0x{frame.Offset:X}.");
            }
            else if (!mc.Fits && mc.HasExtension)
            {
                state.Findings.Add(Severity.Info, "mpeg.multichannel-in-extension", node, count =>
                    $"{Frames(count)} continue their multichannel side information in the extension bitstream, a separate file, so their multichannel CRC is not verified; the first at 0x{frame.Offset:X}.");
            }
            else if (!mc.Fits)
            {
                state.Findings.Add(Severity.Warning, "mpeg.multichannel-too-long", node, count =>
                    $"{Frames(count)} have multichannel side information longer than the frame, the first at 0x{frame.Offset:X}.");
            }
        }
    }

    private static void AddLayer2(List<Node> children, Entry frame, MpegHeader header, Layer2Record record, bool hasMultichannel)
    {
        var audio = Layer2AudioData.Read(record.Audio, header, record.FrameLength);
        children.AddRange(audio.ToNodes(frame.Offset, frame.Length));
        long end = frame.End;
        if (hasMultichannel && record.Multichannel is not null)
        {
            Layer2Multichannel mc = ReadMultichannel(record, header);
            children.Add(mc.ToNode(record.McOffset, header.SampleRate));
            end -= mc.AncillaryBytes;
            if (mc.AncillaryBytes > 0)
            {
                children.Add(new Node("Ancillary Data", end, mc.AncillaryBytes, $"{mc.AncillaryBytes} bytes"));
            }

            return;
        }

        long ancillary = frame.Offset + ((audio.End + 7) / 8);
        if (end > ancillary)
        {
            children.Add(new Node("Ancillary Data", ancillary, end - ancillary, $"{end - ancillary} bytes"));
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
    // in the bit reservoir when the frame starts. VbrFrame holds the bytes of a frame that carries
    // a Xing, Info or VBRI header, which is not audio. IsTruncated marks a frame the data ends inside.
    private readonly record struct Entry(long Offset, long Length, uint Header, int Crc, int ComputedCrc, byte[]? SideInfo,
        int MainDataLength, int Earlier, int ReservoirFill, Layer2Record? Layer2, byte[]? VbrFrame, bool IsTruncated)
    {
        public bool IsGap => Header == 0;

        public long End => Offset + Length;
    }

    private static string Frames(int count) => count == 1 ? "1 frame" : $"{count.ToString(CultureInfo.InvariantCulture)} frames";

    private static string Times(int count) => count == 1 ? "once" : $"{count.ToString(CultureInfo.InvariantCulture)} times";

    // What a file's frames share: the bytes, the findings, and the bit reservoir with the main-data
    // runs it has recorded, which frames on both sides of a tag can own.
    private sealed record StreamState(BlockReader Blocks, FindingCollector Findings, BuildContext Build, BitReservoir Reservoir,
        List<Entry> Candidates, List<RegionState> Regions, List<(Entry Frame, VbrHeader Tag)> LameTags);

    // A region's frames and gaps, and the encoder they point to and their statistics, known once
    // every frame is read.
    private sealed class RegionState(List<Entry> entries)
    {
        public List<Entry> Entries => entries;

        public Node? Encoder { get; set; }

        public Node? Statistics { get; set; }
    }

    // What building frame nodes needs beyond each frame's record: the Layer III main-data runs, and
    // whether the file carries the MPEG-2 multichannel extension, known once every frame is read.
    private sealed class BuildContext(List<MainDataRun> runs)
    {
        public List<MainDataRun> Runs => runs;

        public bool HasMultichannel { get; set; }

        // The music CRC computed for each LAME tag, by the offset of its frame.
        public Dictionary<long, int> MusicCrcs { get; } = [];
    }

    // A Layer II frame's bytes up to its scalefactors, and the bytes of a multichannel candidate:
    // McStart and McEnd in bits within Multichannel, which starts at McOffset in the file.
    private sealed record Layer2Record(byte[] Audio, int FrameLength, byte[]? Multichannel, int McStart, int McEnd, long McOffset);

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
