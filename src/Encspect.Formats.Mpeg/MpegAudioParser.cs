using System.Globalization;
using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// MPEG-1, 2 and 2.5 audio, Layers I to III, at frame-header level. A header starts a stream only
// when two more frames of the same stream follow at the positions its length predicts, or when
// its one or two frames end exactly at the end of the data and each holds what its header
// promises (plan, "File layout and tags"). Once in sync, frames are followed by their lengths and
// never searched inside; anything that is not a frame is a gap.
public sealed partial class MpegAudioParser : IFormatParser, ICodecParser
{
    private const int SniffLimit = 1 << 20;

    // Free-format frames are found from the next matching header; 640 kbps at 32000 Hz is the
    // largest frame LAME writes, and this leaves room for Layer I.
    private const int MaxFreeFormatLength = 8192;

    // The streams of a file; the frames from the one that would start the next stream on are in no stream.
    public const int MaxStreams = 64;

    public string Name => "MPEG Audio";

    public string Id => "mpeg-audio";

    // A chain of frames anywhere in the first MiB is class 90, with where it starts and how many of its
    // frames are whole and pass the CRC their header gives (ISO/IEC 11172-3 2.4.1.4; unprotected frames
    // have none). Another format's frames can hold a protected header at a fixed offset (an AC-3 frame of
    // 768 bytes holds the bytes of a protected MPEG-2.5 Layer I header of 768 bytes), which chains from
    // frame to frame: a protected chain further in than the start of the data none of whose frames passes
    // its CRC is class 30, like a free-format chain that is not credible, which is also what data with a
    // header-like word at a fixed period makes. Both rank below every other format's chain, not out: the
    // stream may be genuine, but the format that owns the data should win. Junk passes a CRC once in
    // 65,536 frames.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        var blocks = new BlockReader(reader);
        // Zero bytes before the audio are common padding and cheap to skip.
        long start = ZeroRunEnd(blocks, reader.Start, reader.End);
        long limit = Math.Min(reader.End, start + SniffLimit);
        SniffResult? incredible = null;
        for (long from = reader.Start; from < limit;)
        {
            long position = FindStreamStart(blocks, from, limit, reader.End, out int freeLength);
            if (position < 0)
            {
                break;
            }

            blocks.TryReadUInt32(position, out uint raw);
            var header = new MpegHeader(raw);
            (int frames, int verified, long chainEnd) = CountChain(blocks, position, header, freeLength, reader.End);
            bool credible = header.IsFreeFormat
                ? IsCredibleFreeFormat(blocks, position, header, freeLength, reader.End)
                : position == start || !header.IsProtected || verified > 0;
            if (credible)
            {
                return new SniffResult(90, position, frames, verified, End: chainEnd);
            }

            incredible ??= new SniffResult(SniffResult.WeakClass, position, frames, verified, header.IsFreeFormat ? "free format" : "protected, no CRC holds", chainEnd);
            from = position + 1;
        }

        return incredible ?? SniffResult.None;
    }

    // The whole frames of the chain at position, at most SniffResult.MaxFrames, how many of them hold
    // their CRC, and where the last one ends.
    private static (int Frames, int Verified, long End) CountChain(BlockReader blocks, long position, MpegHeader first, int freeLength, long end)
    {
        int frames = 0;
        int verified = 0;
        long next = position;
        MpegHeader header = first;
        while (frames < SniffResult.MaxFrames)
        {
            int length = header.FrameLength(freeLength);
            if (next + length > end)
            {
                break;
            }

            frames++;
            if (header.IsProtected && FrameCrcHolds(blocks, next, header, length))
            {
                verified++;
            }

            next += length;
            if (!blocks.TryReadUInt32(next, out uint raw) || new MpegHeader(raw) is not { IsValid: true } following || !following.IsSameStream(first))
            {
                break;
            }

            header = following;
        }

        return (frames, verified, next);
    }

    public void Parse(ParseContext context)
    {
        var blocks = new BlockReader(context.Reader);
        var findings = new FindingCollector();
        var build = new BuildContext();
        var stream = new StreamState(blocks, findings, build, new BitReservoir(build.Runs), [], [], context.CancellationToken);
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
            ScanAncillary(stream, region);
            region.Encoder = WeighClaims(stream, region, DetectEncoder(stream, region), ClaimRoles.Encoders(context.EncoderClaims));
            region.Writer = DetectWriter(stream, region, context.EncoderClaims);
            ClaimRoles.CompleteVersion(region.Encoder, context.EncoderClaims);
        }

        AddStatistics(stream);
        AddStreams(stream, context);

        // The region nodes' factories hold their RegionState; the scan holds the block reader, and
        // with it the input, which the report must not keep alive.
        foreach (RegionState region in stream.Regions)
        {
            region.Ancillary = null;
        }

        findings.Flush(context);
    }

    // MPEG audio that a container holds as one payload (WAV's WAVE_FORMAT_MPEGLAYER3, MP4's object
    // type 0x6B) is parsed as the same bytes in a file would be, under the container's node.
    public void Parse(CodecContext context)
    {
        if (context.Payload is not { } payload)
        {
            context.AddFinding(Severity.Info, "mpeg.packets-not-analyzed", "MPEG audio handed over as packets is not analyzed; only one contiguous payload is.", context.Parent);
            return;
        }

        var inner = new ParseContext(payload, context.Parent, encoderClaims: context.EncoderClaims);
        try
        {
            Parse(inner);
        }
        finally
        {
            foreach (Finding finding in inner.Findings)
            {
                context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node, finding.Count);
            }

            foreach (StreamInfo info in inner.Streams)
            {
                context.AddStream(info);
            }
        }
    }

    // Built in a method of its own so that the node's factory captures these arguments and not the
    // locals of ParseSegment, whose block reader would stay alive with the report, and with it the input.
    private static Node RegionNode(long start, long length, RegionState regionState, BuildContext build) =>
        new("MPEG Audio", start, length, null, () => BuildRegion(regionState, build))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Region,
            Topic = Topics.FrameSync,
        };

    private static void ParseSegment(StreamState state, Node root, long start, long end)
    {
        (BlockReader blocks, FindingCollector findings) = (state.Blocks, state.Findings);
        BuildContext build = state.Build;
        var regionState = new RegionState(build.Frames.Count);
        state.Regions.Add(regionState);
        Node region = root.Add(RegionNode(start, end - start, regionState, build));
        regionState.Node = region;
        int frames = 0;
        long position = start;
        MpegHeader? stream = null;
        // The last audio frame's header since sync was found: a VBR header frame's channel mode
        // describes no audio (ffmpeg writes its Info frame as stereo before joint stereo audio).
        MpegHeader? audio = null;
        int freeLength = 0;

        // A stream change found where sync returns after a gap is reported on the frame there,
        // once its length is known.
        (MpegHeader From, MpegHeader To, int FreeLength)? resynced = null;

        while (position < end)
        {
            state.CheckCancellation(frames);
            long at = position;
            if (stream is { } current && at + 4 <= end && blocks.TryReadUInt32(at, out uint raw) && new MpegHeader(raw) is { IsValid: true } header)
            {
                if (header.IsSameStream(current))
                {
                    // A header is only taken as a frame while a frame follows it. Otherwise a
                    // free-format stream may have changed its frame length, or a stream that starts
                    // inside the frame cuts it short, where it was cut or its header is damaged.
                    long frameEnd = at + header.FrameLength(freeLength);
                    long frameLimit = end;
                    int? previous = null;
                    if (frameEnd < end && !StartsFrame(blocks, frameEnd, header, end))
                    {
                        if (header.IsFreeFormat && FindStreamStart(blocks, at, at + 1, end, out int changed) == at && changed != freeLength)
                        {
                            previous = freeLength;
                            freeLength = changed;
                        }
                        // A frame cannot end inside its own header or CRC.
                        else if (FindStreamStart(blocks, at + 4 + (header.IsProtected ? 2 : 0), frameEnd, end, out _) is var cut and >= 0)
                        {
                            frameLimit = cut;
                        }
                    }

                    // The node AddFrame's entry will have, so findings keep the order they are found in.
                    var node = new Node("Frame", at, Math.Min(header.FrameLength(freeLength), frameLimit - at), header.Summary) { Kind = NodeKind.Frame };
                    if (resynced is { } change)
                    {
                        StreamChanged(change.From, change.To, node);
                        FreeFormat(change.To, change.FreeLength);
                        resynced = null;
                    }

                    if (previous is { } from)
                    {
                        int to = freeLength;
                        findings.Add(Severity.Warning, "mpeg.free-format-length-changed", node, count =>
                            $"The free-format frame length changes {Times(count)}, first from {from} to {to} bytes plus padding at 0x{at:X}; ISO/IEC 11172-3 requires a fixed bitrate.");
                    }

                    Entry frame = AddFrame(state, regionState, at, frameLimit, header, freeLength, frameLimit < end);
                    if (!frame.IsVbrHeader)
                    {
                        if (audio is { } last && header.ChannelModeBits != last.ChannelModeBits)
                        {
                            findings.Add(Severity.Info, "mpeg.channel-mode-changed", FindingNode(frame), count =>
                                $"The channel mode changes {Times(count)}, first from {last.ChannelMode} to {header.ChannelMode} at 0x{at:X}.");
                        }

                        audio = header;
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
                    Entry frame = AddFrame(state, regionState, at, end, header, freeLength);
                    audio = frame.IsVbrHeader ? null : header;
                    StreamChanged(current, header, FindingNode(frame));
                    FreeFormat(header, freeLength);
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
            var found = new MpegHeader(first);
            resynced = stream is { } before && !found.IsSameStream(before) ? (before, found, foundFreeLength) : null;
            stream = found;
            audio = null;
            freeLength = foundFreeLength;
            if (resynced is null)
            {
                FreeFormat(found, foundFreeLength);
            }
        }

        region.Value = NodeValue.Integer(frames, "frames", "frame");
        regionState.End = build.Frames.Count;

        void StreamChanged(MpegHeader from, MpegHeader to, Node node) =>
            findings.Add(Severity.Info, "mpeg.stream-changed", node, count =>
                count == 1
                    ? $"The stream changes from {from.StreamName} to {to.StreamName} at 0x{node.Offset:X}."
                    : $"The stream changes {count} times, first from {from.StreamName} to {to.StreamName} at 0x{node.Offset:X}.");

        void FreeFormat(MpegHeader header, int length)
        {
            if (header.IsFreeFormat)
            {
                findings.Add(Severity.Info, "mpeg.free-format", null, _ => $"The stream is free format: frames of {Counts.Of(length, "byte", "bytes")} plus padding.");
            }
        }

        void AddGap(long gapStart, long gapEnd, bool leading, bool trailing)
        {
            var gap = Entry.Gap(gapStart, gapEnd - gapStart);
            build.Frames.Add(gap);
            long length = gap.Length;
            if (leading && !trailing && gapEnd < end)
            {
                string what = ZeroRunEnd(blocks, gapStart, gapEnd) == gapEnd ? " (all zero bytes)" : string.Empty;
                findings.AddNow(Severity.Warning, "mpeg.leading-junk", $"{Counts.Of(length, "byte", "bytes")} before the first frame {Counts.Agree(length, "is", "are")} not audio{what}.", GapNode(gap));
            }
            else if (leading)
            {
                return;
            }
            else if (trailing)
            {
                findings.AddNow(Severity.Warning, "mpeg.trailing-data", $"{Counts.Of(length, "byte", "bytes")} after the last frame at 0x{gapStart:X} {Counts.Agree(length, "is", "are")} not a frame.", GapNode(gap));
            }
            else
            {
                findings.AddNow(Severity.Warning, "mpeg.sync-lost", $"Sync is lost for {Counts.Of(length, "byte", "bytes")} at 0x{gapStart:X}.", GapNode(gap));
            }
        }
    }

    // Records the frame at position, with the bytes its nodes are built from, and reports its
    // problems. Its nodes are built from the record when the report is read.
    // A cut frame ends where the next frame starts, before the length its header gives.
    private static Entry AddFrame(StreamState state, RegionState region, long position, long end, MpegHeader header, int freeLength, bool isCut = false)
    {
        (BlockReader blocks, FindingCollector findings, BuildContext build) = (state.Blocks, state.Findings, state.Build);
        int index = build.Frames.Count;
        int length = header.FrameLength(freeLength);
        long available = Math.Min(length, end - position);
        bool isComplete = available == length;
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

        // Where Layer I and II audio data ends, in bits from the frame's start, for the statistics.
        int audioEnd = -1;
        Layer2Record? layer2 = header.Layer == 2 ? ReadLayer2(blocks, position, available, length, header, ref computed, findings, out audioEnd) : null;
        byte[]? layer1 = header.Layer == 1 ? ReadLayer1(blocks, position, available, length, header, findings, out audioEnd) : null;
        byte[]? vbrFrame = HasVbrMagic(blocks, position, available, header) ? blocks.Read(position, (int)available) : null;
        VbrHeader? vbr = vbrFrame is null ? null : VbrHeader.TryRead(vbrFrame, header);

        var frame = new Entry(position, available, header.Raw) { Crc = stored, ComputedCrc = computed, IsTruncated = available < length };
        MainDataPlacement? placement = null;
        Layer3SideInfo? audioSideInfo = null;
        long sideInfoOffset = position + 4 + (header.IsProtected ? 2 : 0);
        if (vbr is null && header.Layer == 3 && sideInfoOffset + Layer3SideInfo.Size(header) <= position + available)
        {
            byte[] sideInfo = blocks.Read(sideInfoOffset, Layer3SideInfo.Size(header));
            var info = Layer3SideInfo.Read(sideInfo, header);
            CheckSideInfo(info, position, sideInfoOffset, length, findings);
            long dataOffset = sideInfoOffset + sideInfo.Length;
            placement = state.Reservoir.Add(header, index, position, position + available, dataOffset,
                (int)(position + available - dataOffset), info.MainDataBegin.Value, info.MainDataLength, available < length);
            frame = frame with
            {
                Data = build.Store.Add(sideInfo),
                DataKind = FrameData.SideInfo,
                DataLength = sideInfo.Length,
                MainDataBegin = info.MainDataBegin.Value,
                MainDataLength = info.MainDataLength,
                Earlier = placement.Value.Earlier,
                ReservoirFill = (int)Math.Min(placement.Value.Reservoir, (1 << info.MainDataBegin.Length) - 1),
            };
            if (isComplete)
            {
                region.Traits.Add(info, position, position + available);
                audioSideInfo = info;
            }
        }
        else if (vbr is not null && sideInfoOffset + Layer3SideInfo.Size(header) <= position + available)
        {
            long dataOffset = sideInfoOffset + Layer3SideInfo.Size(header);
            state.Reservoir.Add(header, index, position, position + available, dataOffset, (int)(position + available - dataOffset), 0, 0, available < length,
                isHeaderFrame: true);
        }
        else
        {
            state.Reservoir.Reset();
        }

        if (vbr is not null)
        {
            frame = frame with { Data = build.Store.Add(vbrFrame), DataKind = FrameData.VbrHeader, DataLength = vbrFrame!.Length };
        }
        else if (layer2 is not null)
        {
            frame = frame with
            {
                Data = build.Store.Add(layer2.Audio, layer2.Multichannel),
                DataKind = FrameData.Layer2,
                DataLength = layer2.Audio.Length,
                MultichannelLength = layer2.Multichannel?.Length ?? 0,
                IsMultichannelCandidate = layer2.Multichannel is not null,
            };
            if (isComplete)
            {
                region.Layer2Frames++;
                region.UndefinedScalefactor ??= layer2.UndefinedScalefactor;
            }
        }
        else if (layer1 is not null)
        {
            frame = frame with { Data = build.Store.Add(layer1), DataKind = FrameData.Layer1, DataLength = layer1.Length };
        }

        build.Frames.Add(frame);
        bool midStream = state.FrameCount > 0;
        state.FrameCount++;
        if (vbr is null && isComplete)
        {
            region.AddAudio(frame);
            region.Tally.Add(header, length, audioSideInfo, audioEnd);
        }

        if (vbr is not null)
        {
            ReportVbrHeader(state, index, frame, vbr, midStream);
        }

        if (placement is { } placed)
        {
            ReportPlacement(placed, FindingNode(frame), findings);
        }

        if (header.EmphasisBits == 2)
        {
            findings.Add(Severity.Warning, "mpeg.emphasis-reserved", HeaderField(position, "Emphasis"), count =>
                $"{Frames(count, "uses", "use")} the reserved emphasis value, the first at 0x{position:X}.");
        }

        // MPEG 2.5 is Fraunhofer's extension of Layer III; mpg123, libmad and ffmpeg decode the
        // other layers at its rates all the same.
        if (header.VersionBits == 0 && header.Layer != 3)
        {
            findings.Add(Severity.Warning, "mpeg.mpeg25-layer", FindingNode(frame), count =>
                $"{Frames(count, "is", "are")} MPEG-2.5 Layer {header.LayerName}, which no standard defines: MPEG 2.5 extends only Layer III. The first at 0x{position:X}.");
        }

        if (!header.IsLayer2BitrateModeAllowed)
        {
            findings.Add(Severity.Warning, "mpeg.bitrate-mode-not-allowed", FindingNode(frame), count =>
                $"{Frames(count, "combines", "combine")} Layer II at {header.Bitrate} kbps with {header.ChannelMode}, which ISO/IEC 11172-3 does not allow, the first at 0x{position:X}.");
        }

        if (stored >= 0)
        {
            if (computed >= 0 && computed != stored)
            {
                findings.Add(Severity.Warning, "mpeg.crc-mismatch", CrcNode(position, stored), count =>
                    $"{Frames(count, "fails", "fail")} the CRC check, the first at 0x{position:X}.");
            }
        }

        if (available < length && isCut)
        {
            findings.Add(Severity.Warning, "mpeg.frame-cut", FindingNode(frame), count =>
                $"{Frames(count, "ends", "end")} where the next frame starts, before the length {(count == 1 ? "its" : "their")} header gives: the frame was cut or its header is damaged. The first at 0x{position:X} has {available} of its {length} bytes.");
        }
        else if (available < length)
        {
            findings.AddNow(Severity.Warning, "mpeg.frame-truncated", $"The last frame at 0x{position:X} has {available} of its {Counts.Of(length, "byte", "bytes")}.", FindingNode(frame));
        }

        return frame;
    }

    private static IReadOnlyList<Node> BuildRegion(RegionState region, BuildContext build)
    {
        var nodes = new List<Node>();
        if (region.Encoder is { } encoder)
        {
            nodes.Add(encoder);
        }

        if (region.Writer is { } writer)
        {
            nodes.Add(writer);
        }

        if (region.Enhancement is { } enhancement)
        {
            nodes.Add(enhancement);
        }

        if (region.Ofl is { } ofl)
        {
            nodes.Add(ofl);
        }

        if (region.Statistics is { } statistics)
        {
            nodes.Add(statistics);
        }

        int first = region.First;
        return new BlockedChildren(nodes, (region.End ?? build.Frames.Count) - first, (start, length) => RegionBlock(build, first, start, length));
    }

    private static Node GapNode(in Entry gap) =>
        new("Gap", gap.Offset, gap.Length, Bytes(gap.Length)) { Description = Explain.Gap, Topic = Topics.FrameSync };

    private static Node FrameNode(int index, BuildContext build, NodeValue summary)
    {
        ref readonly Entry frame = ref build.Frames[index];
        return new("Frame", frame.Offset, frame.Length, summary, () => BuildFrame(index, build))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.GranulesAndFrames,
        };
    }

    // The frame's node for a finding: equal to the one in the tree, without its children.
    private static Node FindingNode(in Entry frame) =>
        new("Frame", frame.Offset, frame.Length, new MpegHeader(frame.Header).Summary) { Kind = NodeKind.Frame };

    // ISO/IEC 11172-3 2.4.1.3, with the syncword cut to 11 bits for MPEG 2.5's two version bits.
    private static Node HeaderField(long position, string name, NodeValue? value = null, string? description = null, string? topic = null)
    {
        (int start, int end) = name switch
        {
            "Version" => (11, 13),
            "Layer" => (13, 15),
            "IsProtected" => (15, 16),
            "Bitrate" => (16, 20),
            "Sample Rate" => (20, 22),
            "HasPadding" => (22, 23),
            "IsPrivate" => (23, 24),
            "Channel Mode" => (24, 26),
            "Mode Extension" => (26, 28),
            "IsCopyrighted" => (28, 29),
            "IsOriginal" => (29, 30),
            "Emphasis" => (30, 32),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a header field."),
        };
        return Node.FromBits(name, position, start, end, value, description: description, topic: topic);
    }

    private static Node CrcNode(long position, int crc) => new("CRC", position + 4, 2, NodeValue.Hex(crc, 4)) { Description = Explain.Crc, Topic = Topics.Crc };

    private static NodeValue Bytes(long count) => NodeValue.Integer(count, "bytes");

    private static List<Node> BuildFrame(int index, BuildContext build)
    {
        Entry frame = build.Frames[index];
        var header = new MpegHeader(frame.Header);
        long position = frame.Offset;
        var children = new List<Node>();

        Node fields = new("Header", position, 4) { Description = Explain.Header, Topic = Topics.FrameHeader };
        children.Add(fields);
        fields.Add(HeaderField(position, "Version", header.VersionName, Explain.Version, Topics.MpegVersions));
        fields.Add(HeaderField(position, "Layer", header.LayerName, Explain.Layer, Topics.MpegLayers));
        fields.Add(HeaderField(position, "IsProtected", NodeValue.Boolean(header.IsProtected), Explain.IsProtected, Topics.Crc));
        fields.Add(header.IsFreeFormat
            ? HeaderField(position, "Bitrate", "free", Explain.FreeBitrate, Topics.FreeFormat)
            : HeaderField(position, "Bitrate", NodeValue.Integer(header.Bitrate, "kbps"), Explain.Bitrate, Topics.BitrateModes));
        fields.Add(HeaderField(position, "Sample Rate", NodeValue.Integer(header.SampleRate, "Hz"), Explain.SampleRate, Topics.MpegVersions));
        fields.Add(HeaderField(position, "HasPadding", NodeValue.Boolean(header.Padding == 1), Explain.HasPadding, Topics.Padding));
        fields.Add(HeaderField(position, "IsPrivate", NodeValue.Boolean(header.IsPrivate), Explain.IsPrivate));
        fields.Add(HeaderField(position, "Channel Mode", header.ChannelMode, ChannelModeText(header), header.ChannelModeBits == 1 ? Topics.JointStereo : Topics.ChannelModes));
        fields.Add(HeaderField(position, "Mode Extension", header.ModeExtensionName, ModeExtensionText(header), ModeExtensionTopic(header)));
        fields.Add(HeaderField(position, "IsCopyrighted", NodeValue.Boolean(header.IsCopyrighted), Explain.IsCopyrighted));
        fields.Add(HeaderField(position, "IsOriginal", NodeValue.Boolean(header.IsOriginal), Explain.IsOriginal));
        fields.Add(HeaderField(position, "Emphasis", header.Emphasis, Explain.Emphasis, Topics.Emphasis));

        long dataStart = position + 4;
        if (frame.Crc >= 0)
        {
            Node crc = CrcNode(position, frame.Crc);
            children.Add(crc);
            if (frame.ComputedCrc >= 0)
            {
                crc.Add("Computed", position + 4, 2, NodeValue.Hex(frame.ComputedCrc, 4), Explain.ComputedCrc);
            }

            dataStart += 2;
        }

        if (frame.DataKind == FrameData.VbrHeader)
        {
            VbrHeader vbr = ReadVbrHeader(build, frame);
            List<Node> nodes = vbr.ToNodes(position, (int)(dataStart - position), build.MusicCrcs.TryGetValue(position, out int crc) ? crc : null);
            if (nodes[^1] is { Name: "Unused" } unused)
            {
                nodes.RemoveAt(nodes.Count - 1);
                nodes.AddRange(ReservoirPieces(unused.Offset, unused.End, build, "Unused", Explain.HeaderFrameUnused));
            }

            children.AddRange(nodes);
        }
        else if (frame.DataKind == FrameData.SideInfo)
        {
            children.Add(Layer3SideInfo.Read(build.Store.Span(frame.Data, frame.DataLength).ToArray(), header).ToNode(dataStart, frame.ReservoirFill));
            AddMainData(children, index, frame, dataStart + frame.DataLength, build);
        }
        else if (frame.DataKind == FrameData.Layer2)
        {
            AddLayer2(children, frame, header, build);
        }
        else if (frame.DataKind == FrameData.Layer1)
        {
            var audio = Layer1AudioData.Read(build.Store.Span(frame.Data, frame.DataLength).ToArray(), header);
            children.AddRange(audio.ToNodes(frame.Offset, frame.Length));
            long ancillary = frame.Offset + ((audio.End + 7) / 8);
            if (frame.End > ancillary)
            {
                children.Add(Layer12Ancillary(ancillary, frame.End));
            }
        }
        else if (frame.End > dataStart)
        {
            children.Add(new Node("Data", dataStart, frame.End - dataStart, Bytes(frame.End - dataStart)) { Description = Explain.FrameData });
        }

        return children;
    }

    // The bytes from start to end in the main data of later frames, as "Bit Reservoir" nodes, and
    // the bytes between them as nodes named rest.
    private static IEnumerable<Node> ReservoirPieces(long start, long end, BuildContext build, string rest, string restDescription)
    {
        ChunkedList<MainDataRun> runs = build.Runs;
        long cursor = start;
        int low = 0;
        int high = runs.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            (low, high) = runs[middle].End <= start ? (middle + 1, high) : (low, middle);
        }

        for (int i = low; i < runs.Count && runs[i].Offset < end; i++)
        {
            long from = Math.Max(runs[i].Offset, start);
            long to = Math.Min(runs[i].End, end);
            if (from > cursor)
            {
                yield return new Node(rest, cursor, from - cursor, Bytes(from - cursor)) { Description = restDescription };
            }

            yield return ReservoirNode(from, to - from, build.Frames[runs[i].Owner].Offset);
            cursor = to;
        }

        if (end > cursor)
        {
            yield return new Node(rest, cursor, end - cursor, Bytes(end - cursor)) { Description = restDescription };
        }
    }

    // Splits a Layer III frame's bytes after the side information by owner: its own main data,
    // main data of later frames in the bit reservoir, and ancillary data no frame uses.
    private static void AddMainData(List<Node> children, int index, in Entry frame, long dataStart, BuildContext build)
    {
        ChunkedList<MainDataRun> runs = build.Runs;
        NodeValue own = frame.Earlier > 0 ? $"{Counts.Bytes(frame.MainDataLength)}, {frame.Earlier} of them in earlier frames" : Bytes(frame.MainDataLength);
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
            if (run.Owner == index)
            {
                children.Add(new Node("Main Data", run.Offset, run.Length, own) { Description = Explain.MainData, Topic = Topics.BitReservoir });
                hasOwn = true;
            }
            else
            {
                children.Add(ReservoirNode(run.Offset, run.Length, build.Frames[run.Owner].Offset));
            }

            cursor = run.End;
        }

        AddAncillary(cursor, frame.End);
        if (!hasOwn)
        {
            children.Insert(first, new Node("Main Data", dataStart, 0, own) { Description = Explain.MainData, Topic = Topics.BitReservoir });
        }

        void AddAncillary(long from, long to)
        {
            if (to > from)
            {
                children.Add(new Node("Ancillary Data", from, to - from, Bytes(to - from)) { Description = Explain.Layer3Ancillary, Topic = Topics.AncillaryData });
            }
        }
    }

    private static Node ReservoirNode(long offset, long length, long owner) =>
        new("Bit Reservoir", offset, length, $"{Counts.Bytes(length)} of the main data of the frame at 0x{owner:X}") { Description = Explain.ReservoirBytes, Topic = Topics.BitReservoir };

    private static Node Layer12Ancillary(long start, long end) =>
        new("Ancillary Data", start, end - start, Bytes(end - start)) { Description = Explain.Layer12Ancillary, Topic = Topics.AncillaryData };

    private static string ChannelModeText(MpegHeader header) => header.ChannelModeBits switch
    {
        0 => Explain.ChannelStereo,
        1 => Explain.ChannelJoint,
        2 => Explain.ChannelDual,
        _ => Explain.ChannelMono,
    };

    private static string ModeExtensionText(MpegHeader header) =>
        header.ChannelModeBits != 1 ? Explain.ModeExtensionUnused : header.Layer == 3 ? Explain.ModeExtensionLayer3 : Explain.ModeExtensionBound;

    // Layer III: bit 1 mid/side stereo, bit 0 intensity stereo.
    private static string ModeExtensionTopic(MpegHeader header) =>
        header.ChannelModeBits != 1 ? Topics.JointStereo
        : header.Layer != 3 || header.ModeExtension == 1 ? Topics.IntensityStereo
        : header.ModeExtension >= 2 ? Topics.MidSideStereo
        : Topics.JointStereo;

    // Reads a Layer I frame's audio data and reports what breaks ISO/IEC 11172-3 in it.
    // audioEnd: where its samples end, in bits from the frame's start, or int.MaxValue where its
    // allocation and scalefactors do not fit.
    private static byte[]? ReadLayer1(BlockReader blocks, long position, long available, int length, MpegHeader header, FindingCollector findings,
        out int audioEnd)
    {
        byte[] bytes = blocks.Read(position, (int)available);
        var audio = Layer1AudioData.Read(bytes, header);
        bool hasScalefactors = audio.SampleStart <= bytes.Length * 8;
        audioEnd = hasScalefactors ? audio.End : int.MaxValue;
        foreach ((int ch, int sb) in audio.Coded().Where(c => hasScalefactors && audio.Allocation[c.Channel, c.Subband] == Layer1AudioData.ForbiddenAllocation).Take(1))
        {
            findings.Add(Severity.Warning, "mpeg.allocation-forbidden", audio.AllocationNode(position, ch, sb), count =>
                $"{Frames(count, "uses", "use")} the bit allocation 15, which ISO/IEC 11172-3 forbids for Layer I and libmad rejects, the first at 0x{position:X}.");
        }

        foreach ((int ch, int sb) in Enumerable.Range(0, audio.Channels).SelectMany(ch => Enumerable.Range(0, Layer1AudioData.Subbands).Select(sb => (ch, sb)))
            .Where(c => hasScalefactors && audio.Scalefactor[c.ch, c.sb] == 63).Take(1))
        {
            findings.Add(Severity.Warning, "mpeg.scalefactor-undefined", audio.ScalefactorNode(position, ch, sb), count =>
                $"{Frames(count, "uses", "use")} scalefactor index 63, which Table B.1 of ISO/IEC 11172-3 does not define but decoders accept, the first at 0x{position:X}.");
        }

        if (audio.End > bytes.Length * 8 && available == length)
        {
            findings.Add(Severity.Warning, "mpeg.audio-data-overflow", new Node("Frame", position, available), count =>
                $"{Frames(count, "needs", "need")} more bits for {(count == 1 ? "its" : "their")} Layer I audio data than {(count == 1 ? "it holds" : "they hold")}, the first at 0x{position:X}.");
        }

        // Without room for its allocation and scalefactors, the frame shows only its bytes.
        return hasScalefactors ? bytes[..((audio.SampleStart + 7) / 8)] : null;
    }

    // Reads a Layer II frame's audio data, verifies its CRC over the header bits, the bit allocation
    // and scfsi (ISO/IEC 11172-3 2.4.3.1), and reads a multichannel candidate after the samples.
    private static Layer2Record? ReadLayer2(BlockReader blocks, long position, long available, int length, MpegHeader header,
        ref int computed, FindingCollector findings, out int audioEnd)
    {
        byte[] bytes = blocks.Read(position, (int)available);
        var audio = Layer2AudioData.Read(bytes, header);
        audioEnd = audio.SampleStart <= bytes.Length * 8 ? audio.End : int.MaxValue;
        if (header.IsProtected && audio.ScalefactorStart <= bytes.Length * 8)
        {
            computed = Crc16.ComputeBits(bytes, audio.AllocationStart, audio.ScalefactorStart - audio.AllocationStart, Crc16.ComputeBits(bytes, 16, 16));
        }

        if (audio.SampleStart > bytes.Length * 8)
        {
            if (available == length)
            {
                ReportOverflow();
            }

            return null;
        }

        Node? undefined = null;
        foreach ((int ch, int sb, int _) in audio.UndefinedScalefactors().Take(1))
        {
            int bit = audio.ScalefactorBit[ch, sb] + (6 * Array.IndexOf(audio.Scalefactors[ch, sb], 63));
            undefined = Layer2AudioData.Span("Scalefactors", position, bit, bit + 6);
            // TwoLAME writes it, and ffmpeg, mpg123 and libmad decode it.
            findings.Add(Severity.Warning, "mpeg.scalefactor-undefined", audio.ScalefactorNode(position, ch, sb), count =>
                $"{Frames(count, "uses", "use")} scalefactor index 63, which Table B.1 of ISO/IEC 11172-3 does not define but decoders accept, the first at 0x{position:X}.");
        }

        if (audio.End > bytes.Length * 8 && available == length)
        {
            ReportOverflow();
        }

        byte[] audioBytes = bytes[..Math.Min(bytes.Length, (audio.SampleStart + 7) / 8)];
        if (!header.IsMpeg1 || header.IsMono || available < length || audio.End + 32 > bytes.Length * 8)
        {
            return new Layer2Record(audioBytes, null, undefined);
        }

        int[,] baseAllocation = BaseAllocation(audio);
        var mc = Layer2Multichannel.Read(bytes, audio.End, bytes.Length * 8, baseAllocation, header.SampleRate);
        if (!mc.HeaderFits)
        {
            return new Layer2Record(audioBytes, null, undefined);
        }

        int first = audio.End / 8;
        int last = Math.Min(bytes.Length, (Math.Max(mc.SideInfoEnd, mc.CrcBit + 16) + 7) / 8);
        return new Layer2Record(audioBytes, bytes[first..last], undefined);

        void ReportOverflow() =>
            findings.Add(Severity.Warning, "mpeg.audio-data-overflow", new Node("Frame", position, available), count =>
                $"{Frames(count, "needs", "need")} more bits for {(count == 1 ? "its" : "their")} Layer {header.LayerName} audio data than {(count == 1 ? "it holds" : "they hold")}, the first at 0x{position:X}.");
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

    // A Layer II frame's multichannel candidate, read again from the bytes its record keeps: those
    // of the audio data up to the samples, which give the base allocation and where the candidate
    // starts, then the candidate's.
    private static (Layer2Multichannel Mc, long Offset) ReadMultichannel(BuildContext build, in Entry frame, MpegHeader header)
    {
        var audio = Layer2AudioData.Read(build.Store.Span(frame.Data, frame.DataLength).ToArray(), header);
        int first = audio.End / 8;
        byte[] bytes = build.Store.Span(frame.Data + (uint)frame.DataLength, frame.MultichannelLength).ToArray();
        var mc = Layer2Multichannel.Read(bytes, audio.End - (first * 8), (int)(frame.Length - first) * 8, BaseAllocation(audio), header.SampleRate);
        return (mc, frame.Offset + first);
    }

    // The ancillary data of MPEG-1 Layer II frames carries the multichannel extension only by
    // convention, so a file counts as multichannel once at least two frames' multichannel CRCs
    // hold, and at least half of those whose side information fits the frame: random ancillary
    // data passes a 16-bit CRC about once in 65,536 frames, so a long file passes it a few times
    // by chance. Where every frame's side information continues in an extension bitstream, no CRC
    // can be checked: the same header in nearly every frame is then reported as a possible
    // extension, not as one. The candidates are read once to decide and once more to report, so
    // no more than one is in memory at a time.
    private static void DecideMultichannel(StreamState state)
    {
        ChunkedList<Entry> frames = state.Build.Frames;
        int candidates = 0;
        int verified = 0;
        int fitting = 0;
        int continued = 0;
        bool isOneHeader = true;
        (int Index, Layer2Multichannel Mc)? firstContinued = null;
        for (int i = 0; i < frames.Count; i++)
        {
            state.CheckCancellation(i);
            if (!frames[i].IsMultichannelCandidate)
            {
                continue;
            }

            Layer2Multichannel mc = ReadMultichannel(state.Build, frames[i], new MpegHeader(frames[i].Header)).Mc;
            candidates++;
            verified += mc.IsCrcValid ? 1 : 0;
            fitting += mc.Fits ? 1 : 0;
            if (!mc.Fits && mc.HasExtension && !mc.HasAncillaryOverflow)
            {
                continued++;
                firstContinued ??= (i, mc);
                isOneHeader &= mc.HeaderKey == firstContinued.Value.Mc.HeaderKey;
            }
        }

        if (verified < 2 || verified * 2 < fitting)
        {
            if (continued >= 2 && continued * 10 >= candidates * 9 && isOneHeader && firstContinued is { } found)
            {
                Layer2Multichannel first = found.Mc;
                state.Findings.Add(Severity.Info, "mpeg.multichannel-unverified", FindingNode(frames[found.Index]), count =>
                    $"The ancillary data of {Counts.Of(continued, "frame", "frames")} looks like an MPEG-2 multichannel extension ({first.Configuration}) whose side information continues in an extension bitstream, a separate file, so it cannot be verified.");
            }

            return;
        }

        state.Build.HasMultichannel = true;
        for (int i = 0; i < frames.Count; i++)
        {
            state.CheckCancellation(i);
            if (!frames[i].IsMultichannelCandidate)
            {
                continue;
            }

            Entry frame = frames[i];
            Layer2Multichannel mc = ReadMultichannel(state.Build, frame, new MpegHeader(frame.Header)).Mc;
            Node node = FindingNode(frame);
            if (mc.Center == 2)
            {
                state.Findings.Add(Severity.Warning, "mpeg.multichannel-center-reserved", node, count =>
                    $"{Frames(count, "uses", "use")} the reserved center value in {(count == 1 ? "its" : "their")} multichannel header, read as no center, the first at 0x{frame.Offset:X}.");
            }

            if (mc.HasAncillaryOverflow)
            {
                state.Findings.Add(Severity.Warning, "mpeg.multichannel-ancillary-overflow", node, count =>
                    $"{Frames(count, "declares", "declare")} more MPEG-1 ancillary bytes in {(count == 1 ? "its" : "their")} multichannel header than the frame holds after the header and its CRC, the first {Counts.Of(mc.AncillaryBytes, "byte", "bytes")} at 0x{frame.Offset:X}.");
            }
            else if (mc.Fits && !mc.IsCrcValid)
            {
                state.Findings.Add(Severity.Warning, "mpeg.multichannel-crc-mismatch", node, count =>
                    $"{Frames(count, "fails", "fail")} the multichannel CRC check, the first at 0x{frame.Offset:X}.");
            }
            else if (!mc.Fits && mc.HasExtension)
            {
                state.Findings.Add(Severity.Info, "mpeg.multichannel-in-extension", node, count =>
                    $"{Frames(count, "continues", "continue")} {(count == 1 ? "its" : "their")} multichannel side information in the extension bitstream, a separate file, so {(count == 1 ? "its" : "their")} multichannel CRC is not verified; the first at 0x{frame.Offset:X}.");
            }
            else if (!mc.Fits)
            {
                state.Findings.Add(Severity.Warning, "mpeg.multichannel-too-long", node, count =>
                    $"{Frames(count, "has", "have")} multichannel side information longer than the frame, the first at 0x{frame.Offset:X}.");
            }
        }
    }

    private static void AddLayer2(List<Node> children, in Entry frame, MpegHeader header, BuildContext build)
    {
        var audio = Layer2AudioData.Read(build.Store.Span(frame.Data, frame.DataLength).ToArray(), header);
        children.AddRange(audio.ToNodes(frame.Offset, frame.Length));
        long end = frame.End;
        if (build.HasMultichannel && frame.IsMultichannelCandidate)
        {
            (Layer2Multichannel mc, long offset) = ReadMultichannel(build, frame, header);
            children.Add(mc.ToNode(offset, header.SampleRate));
            long start = offset + ((mc.End + 7) / 8);
            if (end > start)
            {
                children.Add(Layer12Ancillary(start, end));
            }

            return;
        }

        long ancillary = frame.Offset + ((audio.End + 7) / 8);
        if (end > ancillary)
        {
            children.Add(Layer12Ancillary(ancillary, end));
        }
    }

    private static void CheckSideInfo(Layer3SideInfo info, long position, long offset, int frameLength, FindingCollector findings)
    {
        foreach ((string code, Node node, Func<int, string> message) in SideInfoProblems(info, position, offset))
        {
            findings.Add(Severity.Warning, code, node, message);
        }

        // The decoder's buffer holds one frame at the highest bitrate (bitrate_index 14) at the
        // stream's sample rate, so main_data_begin plus the frame's length may not exceed that
        // frame's length, and main_data_begin is 0 at bitrate_index 14: ISO/IEC 11172-3's bit
        // reservoir text, quoted in LAME's libmp3lame/reservoir.c. Only MPEG-1 is checked: LAME's
        // --strictly-enforce-ISO applies the rule to MPEG-2 at 160 kbps (bitstream.c,
        // MDB_STRICT_ISO), but that reading of ISO/IEC 13818-3 could not be confirmed.
        int mainDataBegin = info.MainDataBegin.Value;
        int buffer = new MpegHeader((info.Header.Raw & ~0xF200u) | (14u << 12)).FrameLength();
        if (info.Header.IsMpeg1 && mainDataBegin > 0 && mainDataBegin + frameLength > buffer)
        {
            findings.Add(Severity.Warning, "mpeg.main-data-begin-exceeds-buffer",
                Layer3SideInfo.Span("Main Data Begin", offset, info.MainDataBegin.Bit, info.MainDataBegin.Bit + info.MainDataBegin.Length), count =>
                    $"{Frames(count, "reaches", "reach")} back further for main data than the decoder buffer of ISO/IEC 11172-3 allows, which holds one frame at the highest bitrate: the first at 0x{position:X} has a main_data_begin of {Counts.Of(mainDataBegin, "byte", "bytes")} where at most {Math.Max(buffer - frameLength, 0)} {Counts.Agree(Math.Max(buffer - frameLength, 0), "is", "are")} allowed. LAME has not enforced this limit by default since 3.81, and decoders accept it.");
        }
    }

    private static IEnumerable<(string Code, Node Node, Func<int, string> Message)> SideInfoProblems(Layer3SideInfo info, long position, long offset)
    {
        MpegHeader header = info.Header;
        foreach ((int gr, int ch, Layer3Granule g) in info.All)
        {
            if (g.BigValues.Value > 288)
            {
                yield return ("mpeg.big-values-too-large", Field("Big Values", g.BigValues), count =>
                    $"{Frames(count, "has", "have")} more than 288 big_values pairs, the first {g.BigValues.Value} at 0x{position:X}.");
            }

            if (g.IsSwitched && g.BlockType.Value == 0)
            {
                yield return ("mpeg.block-type-reserved", Layer3SideInfo.BlockTypeSpan(offset, g), count =>
                    $"{Frames(count, "switches", "switch")} windows with block type 0, which is reserved, the first at 0x{position:X}.");
            }

            foreach (BitField table in g.TableSelect.Where(t => t.Value is 4 or 14))
            {
                yield return ("mpeg.huffman-table-undefined", Layer3SideInfo.TableSelectSpan(offset, g), count =>
                    $"{Frames(count, "selects", "select")} a Huffman table that does not exist, the first table {table.Value} at 0x{position:X}.");
            }

            int part2 = info.Part2Bits(gr, ch);
            if (part2 > g.Part23Length.Value)
            {
                yield return ("mpeg.scalefactors-exceed-granule", Field("Part 2 and 3 Length", g.Part23Length), count =>
                    $"{Frames(count, "has", "have")} scalefactors longer than part2_3_length, the first {part2} of {Counts.Of(g.Part23Length.Value, "bit", "bits")} at 0x{position:X}.");
            }

            // libmad rejects these frames; ffmpeg and mpg123 decode them.
            if (header.IsMpeg1 && g.IsShort && info.Scfsi[ch].Value != 0)
            {
                yield return ("mpeg.scfsi-with-short-blocks", Field($"Channel {ch}", info.Scfsi[ch]), count =>
                    $"{Frames(count, "sets", "set")} scfsi for a channel with short blocks, which libmad rejects, the first at 0x{position:X}.");
            }
        }

        bool jointStereo = header.ChannelModeBits == 1 && header.ModeExtension != 0;
        foreach (Layer3Granule[] row in info.Granules.Where(row => jointStereo && row.Length == 2))
        {
            if (row[0].BlockTypeValue != row[1].BlockTypeValue || row[0].IsMixed != row[1].IsMixed)
            {
                yield return ("mpeg.stereo-block-types-differ", Layer3SideInfo.BlockTypeSpan(offset, row[1]), count =>
                    $"{Frames(count, "uses", "use")} mid/side or intensity stereo with a different block type in each channel, which libmad rejects, the first at 0x{position:X}.");
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
                    $"{Frames(count, "has", "have")} main data that begins before the stream, the first at 0x{position:X}; the stream was probably cut.");
                break;
            case MainDataProblem.Overlap:
                findings.Add(Severity.Warning, "mpeg.main-data-overlap", frame, count =>
                    $"{Frames(count, "has", "have")} main data that overlaps the previous frame's, the first at 0x{position:X}; frames are probably missing.");
                break;
            case MainDataProblem.InHeaderFrame:
                findings.Add(Severity.Warning, "mpeg.main-data-in-vbr-header-frame", frame, count =>
                    $"{Frames(count, "has", "have")} main data that reaches back into the Xing, Info or VBRI header frame before {(count == 1 ? "it" : "them")}, the first at 0x{position:X}. minimp3 decodes {(count == 1 ? "it" : "them")} from its bytes, mpg123 and ffmpeg skip that frame and decode {(count == 1 ? "it" : "them")} with data missing; the stream was probably cut and the header frame written afterwards.");
                break;
            case MainDataProblem.AcrossGap:
                findings.Add(Severity.Warning, "mpeg.reservoir-across-gap", frame, count =>
                    $"{Frames(count, "takes", "take")} main data from before a gap, the first at 0x{position:X}; decoders differ on whether it is still there.");
                break;
        }

        if (placement.Overflows)
        {
            findings.Add(Severity.Warning, "mpeg.main-data-overflow", frame, count =>
                $"{Frames(count, "has", "have")} more main data than the bit reservoir and the frame hold, the first at 0x{position:X}.");
        }
    }

    // The first offset in [from, limit) where a stream starts, or -1. A stream starts at a valid
    // header followed by two more frames of the same stream, or whose one or two well-formed frames
    // end exactly at end.
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

    // Whether a frame starts at position: a header of the same stream, or a new stream.
    private static bool StartsFrame(BlockReader blocks, long position, MpegHeader stream, long end) =>
        position + 4 <= end && blocks.TryReadUInt32(position, out uint raw) && new MpegHeader(raw) is { IsValid: true } next
        && (next.IsSameStream(stream) || FindStreamStart(blocks, position, position + 1, end, out _) == position);

    // Where the run of zero bytes from start ends, at most at end.
    private static long ZeroRunEnd(BlockReader blocks, long start, long end)
    {
        long position = start;
        while (position < end && blocks.ByteAt(position) == 0)
        {
            position++;
        }

        return position;
    }

    // The possible unpadded length of a free-format frame: the distance to the next header of the
    // same stream, as mpg123 finds it, if that leaves room for what every frame holds, in whole
    // slots. Data with a header every few bytes would otherwise chain at any multiple of its
    // period. For other frames the length comes from the header, so there is one dummy value.
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
                int length = (int)(next - position) - (header.Padding * header.SlotSize);
                if (length >= header.MinimumLength && length % header.SlotSize == 0)
                {
                    yield return length;
                }

                yield break;
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
            if (frame == 2)
            {
                return true;
            }

            // A header alone is a 4-byte match, so a short chain only counts where its frames
            // hold what their headers promise.
            if (next == end)
            {
                return IsWellFormedRun(blocks, position, frame + 1, freeLength);
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

    // A free-format chain is only four bytes of header repeating at one distance, which data with a header-like word at a fixed period
    // (the 0xFFFF that ends a DTS subframe, in frames of one size) makes as well. Real free-format frames hold what their headers
    // promise, and those with a CRC pass it: the chain's frames that fit in the data must be well-formed, and in Layers I and II, where
    // free format is a curiosity, one of them must be protected and have the CRC its header says (ISO/IEC 11172-3 2.4.1.4).
    private static bool IsCredibleFreeFormat(BlockReader blocks, long position, MpegHeader first, int freeLength, long end)
    {
        var protectedFrames = new List<bool>();
        int count = 0;
        long next = position;
        MpegHeader header = first;
        while (count < 3 && next + header.FrameLength(freeLength) <= end)
        {
            if (header.IsProtected)
            {
                protectedFrames.Add(FrameCrcHolds(blocks, next, header, header.FrameLength(freeLength)));
            }

            next += header.FrameLength(freeLength);
            count++;
            if (count < 3 && blocks.TryReadUInt32(next, out uint raw) && new MpegHeader(raw) is { IsValid: true } following)
            {
                header = following;
            }
            else
            {
                break;
            }
        }

        // Layers I and II are silent when their allocation is all zero, which any run of zero bytes under a repeating header is; only a CRC
        // that holds says such a frame was written as one.
        bool crcHolds = protectedFrames.Contains(true);
        return count > 0 && IsWellFormedRun(blocks, position, count, freeLength) && (crcHolds || (protectedFrames.Count == 0 && first.Layer == 3));
    }

    // Whether the CRC of the protected frame at position is the one its header and bit allocation give (Layer II needs its allocation tables).
    private static bool FrameCrcHolds(BlockReader blocks, long position, MpegHeader header, int length)
    {
        byte[] bytes = blocks.Read(position, length);
        if (bytes.Length < 6)
        {
            return false;
        }

        int stored = (bytes[4] << 8) | bytes[5];
        if (header.Layer == 2)
        {
            var audio = Layer2AudioData.Read(bytes, header);
            return audio.ScalefactorStart <= bytes.Length * 8
                && Crc16.ComputeBits(bytes, audio.AllocationStart, audio.ScalefactorStart - audio.AllocationStart, Crc16.ComputeBits(bytes, 16, 16)) == stored;
        }

        int coverage = header.CrcCoverage;
        return coverage >= 0 && 6 + coverage <= bytes.Length && Crc16.Compute(bytes.AsSpan(6, coverage), Crc16.Compute(bytes.AsSpan(2, 2))) == stored;
    }

    // Whether count consecutive frames from position each hold what their headers promise: in
    // Layers I and II audio data that fits the frame (ISO/IEC 11172-3 2.4.1.5, 2.4.1.6) without
    // the forbidden Layer I allocation 15; in Layer III side information that fits the frame, shows
    // none of the problems CheckSideInfo reports and declares no more main data than the frame and
    // the reservoir before it can hold.
    private static bool IsWellFormedRun(BlockReader blocks, long position, int count, int freeLength)
    {
        for (int i = 0; i < count; i++)
        {
            blocks.TryReadUInt32(position, out uint raw);
            var header = new MpegHeader(raw);
            int length = header.FrameLength(freeLength);
            byte[] bytes = blocks.Read(position, length);
            bool isWellFormed;
            if (header.Layer == 1)
            {
                var audio = Layer1AudioData.Read(bytes, header);
                isWellFormed = audio.End <= length * 8
                    && !audio.Coded().Any(c => audio.Allocation[c.Channel, c.Subband] == Layer1AudioData.ForbiddenAllocation);
            }
            else if (header.Layer == 2)
            {
                isWellFormed = Layer2AudioData.Read(bytes, header).End <= length * 8;
            }
            else
            {
                int offset = 4 + (header.IsProtected ? 2 : 0);
                int size = Layer3SideInfo.Size(header);
                var info = offset + size <= length ? Layer3SideInfo.Read(bytes[offset..(offset + size)], header) : null;
                isWellFormed = info is not null && !SideInfoProblems(info, position, position + offset).Any()
                    && info.MainDataLength <= length - offset - size + info.MainDataBegin.Value;
            }

            if (!isWellFormed)
            {
                return false;
            }

            position += length;
        }

        return true;
    }

    // What a frame record keeps of the frame's bytes, in the ByteStore, to build its nodes from.
    private enum FrameData : byte
    {
        None,

        // Layer III side information.
        SideInfo,

        // The whole frame, which carries a Xing, Info or VBRI header and is not audio.
        VbrHeader,

        // Layer I audio data up to the samples.
        Layer1,

        // Layer II audio data up to the samples, then the bytes of a multichannel candidate.
        Layer2,
    }

    // A frame or, with a zero header (never a valid one), a gap, in 48 bytes: all a region keeps per
    // frame until its nodes are asked for, so a long file costs little more than the records. Crc and
    // ComputedCrc are -1 when absent. MainDataBegin, MainDataLength and Earlier give where a Layer III
    // frame's main data starts, how long it is and how much of it lies in earlier frames;
    // ReservoirFill, the bytes in the bit reservoir when the frame starts. Data and DataLength place
    // the bytes kept for its nodes in the ByteStore, and MultichannelLength those of a Layer II
    // multichannel candidate after them. IsTruncated marks a frame the data ends inside.
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Entry
    {
        private const byte HasCrcFlag = 1;
        private const byte HasComputedCrcFlag = 2;
        private const byte TruncatedFlag = 4;
        private const byte CandidateFlag = 8;

        private readonly ushort _crc;
        private readonly ushort _computedCrc;
        private readonly ushort _dataLength;
        private readonly ushort _multichannelLength;
        private readonly ushort _mainDataBegin;
        private readonly ushort _mainDataLength;
        private readonly ushort _earlier;
        private readonly ushort _reservoirFill;
        private readonly byte _flags;

        public Entry(long offset, long length, uint header)
        {
            Offset = offset;
            Length = length;
            Header = header;
        }

        public long Offset { get; }

        public long Length { get; }

        public uint Header { get; }

        public uint Data { get; init; }

        public FrameData DataKind { get; init; }

        public bool IsGap => Header == 0;

        public long End => Offset + Length;

        // Frames are at most a few kilobytes and main data at most 2,048 bytes, so 16 bits hold
        // every length and count below.
        public int Crc
        {
            get => (_flags & HasCrcFlag) != 0 ? _crc : -1;
            init => (_crc, _flags) = value >= 0 ? ((ushort)value, (byte)(_flags | HasCrcFlag)) : (_crc, _flags);
        }

        public int ComputedCrc
        {
            get => (_flags & HasComputedCrcFlag) != 0 ? _computedCrc : -1;
            init => (_computedCrc, _flags) = value >= 0 ? ((ushort)value, (byte)(_flags | HasComputedCrcFlag)) : (_computedCrc, _flags);
        }

        public bool IsTruncated
        {
            get => (_flags & TruncatedFlag) != 0;
            init => _flags = value ? (byte)(_flags | TruncatedFlag) : _flags;
        }

        public bool IsMultichannelCandidate
        {
            get => (_flags & CandidateFlag) != 0;
            init => _flags = value ? (byte)(_flags | CandidateFlag) : _flags;
        }

        public int DataLength { get => _dataLength; init => _dataLength = (ushort)value; }

        public int MultichannelLength { get => _multichannelLength; init => _multichannelLength = (ushort)value; }

        public int MainDataBegin { get => _mainDataBegin; init => _mainDataBegin = (ushort)value; }

        public int MainDataLength { get => _mainDataLength; init => _mainDataLength = (ushort)value; }

        public int Earlier { get => _earlier; init => _earlier = (ushort)value; }

        public int ReservoirFill { get => _reservoirFill; init => _reservoirFill = (ushort)value; }

        public bool HasSideInfo => DataKind == FrameData.SideInfo;

        public bool IsVbrHeader => DataKind == FrameData.VbrHeader;

        public static Entry Gap(long offset, long length) => new(offset, length, 0);
    }

    private static string Frames(int count) => count == 1 ? "1 frame" : $"{count.ToString(CultureInfo.InvariantCulture)} frames";

    // The count of frames with the verb that agrees with it.
    private static string Frames(int count, string singular, string plural) => $"{Frames(count)} {(count == 1 ? singular : plural)}";

    private static string Times(int count) => count == 1 ? "once" : $"{count.ToString(CultureInfo.InvariantCulture)} times";

    // What a file's frames share: the bytes, the findings, and the bit reservoir with the main-data
    // runs it has recorded, which frames on both sides of a tag can own.
    private sealed record StreamState(BlockReader Blocks, FindingCollector Findings, BuildContext Build, BitReservoir Reservoir,
        List<RegionState> Regions, List<(int Index, VbrHeader Tag)> LameTags, CancellationToken Cancellation)
    {
        // The frames recorded so far, gaps not counted.
        public int FrameCount { get; set; }

        // The frames each VBR header describes, by the header frame's index.
        public Dictionary<int, Coverage> Coverages { get; } = [];

        // Reads check cancellation already; passes over the recorded frames only compute, so they
        // check it every few thousand frames.
        public void CheckCancellation(int frame)
        {
            if (frame % 4096 == 0)
            {
                Cancellation.ThrowIfCancellationRequested();
            }
        }
    }

    // A region's frames and gaps, First to End in the file's records, what its frames have in
    // common, and the encoder they point to and their statistics, known once every frame is read.
    private sealed class RegionState(int first)
    {
        public int First => first;

        // Null while the region is being read.
        public int? End { get; set; }

        // Its complete audio frames: not a frame that holds a VBR header, not a truncated one.
        public int AudioFrames { get; private set; }

        public long AudioStart { get; private set; }

        public long AudioEnd { get; private set; }

        public Layer3Traits Traits { get; } = new();

        public FrameTally Tally { get; } = new();

        // Its complete Layer II frames with their audio data, and the first scalefactor with index 63
        // in them.
        public int Layer2Frames { get; set; }

        public Node? UndefinedScalefactor { get; set; }

        public Node? Encoder { get; set; }

        public Node? Writer { get; set; }

        // mp3PRO or MP3 Surround, and the Original File Length block, from the ancillary data.
        public Node? Enhancement { get; set; }

        public Node? Ofl { get; set; }

        public AncillaryScan? Ancillary { get; set; }

        public Node? Statistics { get; set; }

        // The statistics figures the stream's series are shown after.
        public Node? Distribution { get; set; }

        // The tally nodes the stream's category series and coding series are anchored to.
        public TallyNodes Nodes { get; } = new();

        public Node? MaximumFill { get; set; }

        public Node? Node { get; set; }

        public void AddAudio(in Entry frame)
        {
            AudioStart = AudioFrames == 0 ? frame.Offset : AudioStart;
            AudioEnd = frame.End;
            AudioFrames++;
        }
    }

    // What building nodes needs: every frame's and gap's record, the bytes kept for them, the
    // Layer III main-data runs, and whether the file carries the MPEG-2 multichannel extension,
    // known once every frame is read.
    private sealed class BuildContext
    {
        public ChunkedList<Entry> Frames { get; } = new();

        public ByteStore Store { get; } = new();

        public ChunkedList<MainDataRun> Runs { get; } = new();

        public bool HasMultichannel { get; set; }

        // The music CRC computed for each LAME tag, by the offset of its frame.
        public Dictionary<long, int> MusicCrcs { get; } = [];
    }

    // Neighboring frames mostly share a header, and so the text that sums it up. The nodes of a
    // region's frames and gaps are built in blocks (BlockedChildren) after its leading nodes.
    private static Node[] RegionBlock(BuildContext build, int first, int start, int length)
    {
        var nodes = new Node[length];
        var summaries = new Dictionary<uint, NodeValue>();
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = first + start + i;
            ref readonly Entry entry = ref build.Frames[index];
            if (entry.IsGap)
            {
                nodes[i] = GapNode(entry);
                continue;
            }

            if (!summaries.TryGetValue(entry.Header, out NodeValue? summary))
            {
                summaries[entry.Header] = summary = new MpegHeader(entry.Header).Summary;
            }

            nodes[i] = FrameNode(index, build, summary);
        }

        return nodes;
    }

    // A Layer II frame's bytes up to its samples, the bytes of a multichannel candidate, from the
    // byte its first bit is in, and the first scalefactor with index 63, which Table B.1 leaves
    // undefined, if any.
    private sealed record Layer2Record(byte[] Audio, byte[]? Multichannel, Node? UndefinedScalefactor);

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
                context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node ?? context.Root, entry.Count);
            }
        }
    }
}
