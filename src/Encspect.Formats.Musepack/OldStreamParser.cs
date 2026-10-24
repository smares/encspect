// Tables and reading derived from libmpcdec 1.2.6 (huffsv46.c and mpc_decoder.c), Copyright (c) 2005-2009 The Musepack Development Team, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.InteropServices;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// Stream versions 4 to 6, before SV7 (libmpcdec 1.2.6 streaminfo_read_header_sv6, mpc_decoder.c
// mpc_decoder_read_bitstream_sv6; the Portable Musepack decoder, the C++ code libmpcdec 1.2.6 was
// ported from, has the same functions as StreamInfo::ReadHeaderSV6 and Lese_Bitstrom_SV6, so it is
// the same reading, not a second one). Their header has no magic number: the first
// 32-bit little-endian word holds a bitrate, the intensity and mid/side stereo flags, the stream
// version, the max band and the block size; the frame count follows, in 16 bits in version 4 and 32
// in 5 and 6, and the frames start 48 or 64 bits in. Each unit of block-size frames is a 20-bit length
// and its data, as the SV4 and SV5 encoders (Rarewares' mppsv4 and mppsv5 builds) write them; those
// encoders write whole words only, so the last counted frame can run up to 31 bits past the end of
// the data, and decoders (libmpcdec 1.2.6, mppdec) decode the frames before it ("last frame was
// invalid for up to SV5"). In version 6 libmpcdec 1.2.6 decodes every counted frame, the last one too,
// and then reads 11 bits after it: how many of its samples belong to the audio. mppdec, a separate
// decoder, also decodes the last counted SV6 frame; nothing at hand confirms the 11 bits.
//
// A header alone is too weak to tell such a stream from other data, so the stream is recognized only
// where its units, followed by their lengths, end where the encoders end them: in versions 4 and 5
// at the end of the data or less than 32 bits after it; in version 6 in its last word, or followed by
// one zero word where it ends on a word boundary. The SV6 rule assumes an SV6 encoder wrote the last
// frame and the 11 bits whole, since libmpcdec 1.2.6 drops the last frame only "up to SV5"; the zero
// word is what mppenc 1.16, the SV7 descendant of those encoders, writes in that case
// (encode_sv7.c FinishBitstream always writes the word it is filling, even an empty one). A stream of
// version 4 or 5 whose units end inside its last word is not one these encoders wrote, so it is not
// taken. Frames are read, as libmpcdec 1.2.6 reads them, where it decodes them: a variable bitrate, no
// intensity stereo and a block size of 1. Encodes confirm that syntax for versions 4 and 5; no SV6
// encoder was found, so version 6 rests on libmpcdec 1.2.6's source, on SV5 encodes relabeled as
// version 6 and on mppdec decoding all their frames.
internal static class OldStreamParser
{
    private const int MinimumBytes = 8;
    private const int MaxBlockSize = 16;

    // libmpcdec 1.2.6 huffsv46.c, mpc_table_Region_A, B and C: (code, length, index into Q_res) by
    // the 32 bits that start at the code, for subbands 0 to 10, 11 to 22 and 23 to 31.
    private static readonly (uint Code, int Length, int Value)[] RegionA =
    [
        (2147483648u, 1, 1), (2013265920u, 5, 3), (1946157056u, 6, 4), (1912602624u, 7, 7), (1895825408u, 8, 8), (1887436800u, 9, 9),
        (1883242496u, 10, 10), (1881145344u, 11, 11), (1880096768u, 12, 12), (1879572480u, 13, 13), (1879310336u, 14, 14), (1879048192u, 14, 15),
        (1744830464u, 5, 5), (1610612736u, 5, 6), (1073741824u, 3, 0), (0u, 2, 2),
    ];

    private static readonly (uint Code, int Length, int Value)[] RegionB =
    [
        (2147483648u, 1, 1), (1073741824u, 2, 0), (536870912u, 3, 2), (268435456u, 4, 3), (134217728u, 5, 4), (67108864u, 6, 5), (33554432u, 7, 6),
        (0u, 7, 7),
    ];

    private static readonly (uint Code, int Length, int Value)[] RegionC = [(2147483648u, 1, 0), (1073741824u, 2, 1), (536870912u, 3, 2), (0u, 3, 3)];

    // libmpcdec 1.2.6 mpc_decoder.c Q_res: the resolution each index stands for, by subband range.
    private static readonly int[] ResolutionsA = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 17];
    private static readonly int[] ResolutionsB = [0, 1, 2, 3, 4, 5, 6, 17];
    private static readonly int[] ResolutionsC = [0, 1, 2, 17];

    private readonly record struct OldHeader(int Bitrate, bool IntensityStereo, bool MidSide, int Version, int MaxBand, int BlockSize, long Frames)
    {
        public long DataBit => Version == 4 ? 48 : 64;

        public long Units => BlockSize > 0 ? (Frames + BlockSize - 1) / BlockSize : 0;

        // Whether libmpcdec 1.2.6 decodes the frames.
        public bool IsReadable => Version is >= 4 and <= 6 && Bitrate == 0 && !IntensityStereo && BlockSize == 1;

        // Version 6 keeps the last counted frame and follows it with 11 bits; 4 and 5 drop it.
        public bool KeepsLastFrame => Version == 6;

        public static OldHeader Read(WordStream w)
        {
            int version = (int)w.Read(11, 10);
            return new OldHeader((int)w.Read(0, 9), w.Read(9, 1) == 1, w.Read(10, 1) == 1, version, (int)w.Read(21, 5), (int)w.Read(26, 6),
                version == 4 ? w.Read(32, 16) : w.Read(32, 32));
        }
    }

    // How sure it is that the data is such a stream: a header of version 4 to 6 whose units end
    // where the encoders end them; surer with three units or more (MusepackParser.Sniff).
    public static SniffResult Sniff(ByteReader reader)
    {
        if (reader.Length < MinimumBytes || reader.Length % 4 != 0)
        {
            return SniffResult.None;
        }

        var words = new WordStream(reader, reader.Start, reader.End);
        OldHeader header = OldHeader.Read(words);
        if (header.Version is < 4 or > 6 || header.Frames == 0 || header.BlockSize is < 1 or > MaxBlockSize)
        {
            return SniffResult.None;
        }

        long bit = header.DataBit;
        for (long i = 0; i < header.Units; i++)
        {
            if (i % 4096 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            if (bit + 20 > words.BitLength)
            {
                return SniffResult.None;
            }

            bit += 20 + words.Read(bit, 20) + (header.KeepsLastFrame && i == header.Units - 1 ? 11 : 0);
        }

        bool ends = header.KeepsLastFrame
            ? bit <= words.BitLength && (words.Layout.WordEnd(bit) == reader.End || EndsWithZeroWord(words, bit))
            : bit >= words.BitLength && bit < words.BitLength + 32;
        return !ends ? SniffResult.None : new SniffResult(header.Units >= 3 ? 95 : 60, reader.Start, (int)Math.Min(header.Units, SniffResult.MaxFrames));
    }

    // Whether the stream ends on a word boundary at bit and one word of zeros follows it, the last of
    // the data: mppenc 1.16 (encode_sv7.c FinishBitstream) writes the word it is filling at the end even
    // where it is empty.
    private static bool EndsWithZeroWord(WordStream words, long bit) =>
        bit % 32 == 0 && bit + 32 == words.BitLength && words.Read(bit, 32) == 0;

    public static void Parse(ByteReader reader, Node parent, AnalysisContext context)
    {
        var words = new WordStream(reader, reader.Start, reader.End);
        WordLayout w = words.Layout;
        OldHeader h = OldHeader.Read(words);
        string name = $"Musepack SV{h.Version}";
        var state = new State(w, context.CancellationToken);
        Node header = HeaderNode(w, h, reader);
        state.Leading.Add(header);
        Node version = header.Children.First(c => c.Name == "Stream Version");

        if (h.IsReadable)
        {
            Walk(words, h, state);
        }
        else
        {
            var reasons = new List<string>();
            if (h.Version is < 4 or > 6)
            {
                reasons.Add("are not of stream version 4 to 6");
            }

            if (h.Bitrate != 0)
            {
                reasons.Add($"have a constant bitrate ({h.Bitrate} kbps)");
            }

            if (h.IntensityStereo)
            {
                reasons.Add("use intensity stereo");
            }

            if (h.BlockSize != 1)
            {
                reasons.Add($"lead blocks of frames ({h.BlockSize}) with one length");
            }

            string why = $"libmpcdec 1.2.6 does not decode streams that {(reasons.Count > 1 ? string.Join(", ", reasons[..^1]) + " or " : string.Empty)}{reasons[^1]}";
            context.AddFinding(Severity.Info, "musepack.frames-not-read", $"The frames of this Musepack SV{h.Version} stream are not read: {why}.", version);
        }

        long dataStart = reader.Start + (h.DataBit / 8);
        if (state.FinalSamples is not null && EndsWithZeroWord(words, state.Frames[state.Frames.Count - 1].End))
        {
            state.PaddingStart = w.WordEnd(state.Frames[state.Frames.Count - 1].End);
        }

        int count = state.Frames.Count + (state.PaddingStart is null ? 0 : 1);
        if (count == 0)
        {
            IReadOnlyList<ByteRange> data = h.Version == 4 ? [new ByteRange(reader.Start + 4, 2), new ByteRange(reader.Start + 8, reader.Length - 8)] : [new ByteRange(dataStart, reader.End - dataStart)];
            data = [.. data.Where(r => r.Length > 0)];
            if (data.Count > 0)
            {
                state.Leading.Add(new Node("Audio Data", data[0].Offset, data[^1].End - data[0].Offset, NodeValue.Integer(data.Sum(r => r.Length), "bytes"))
                {
                    Description = Explain.AudioData,
                    Ranges = data.Count > 1 ? data : null,
                });
            }
        }

        var node = new Node(name, reader.Start, reader.Length, null, () => new BlockedChildren(state.Leading, count, i => FrameNode(state, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.OldStream,
            Topic = Topics.OldStreams,
        };
        parent.Add(node);
        AddStream(state, h, node, context);
        state.Findings.Flush(context);
    }

    private static void Walk(WordStream words, OldHeader h, State state)
    {
        long bit = h.DataBit;
        long length = words.BitLength;
        int maxBand = Math.Min(h.MaxBand, 31);
        int damaged = 0;
        long damagedFrom = 0;
        for (long i = 0; i < h.Frames; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            bool isLast = i == h.Frames - 1;
            bool isFinal = isLast && h.KeepsLastFrame;
            if (bit + 20 > length)
            {
                if (bit < length)
                {
                    state.Frames.Add(new Frame(bit, -1, FrameFlags.Truncated));
                }

                state.Truncated = true;
                break;
            }

            int frameLength = (int)words.Read(bit, 20);
            long end = bit + 20 + frameLength;
            FrameFlags flags = isFinal ? FrameFlags.Final : isLast ? FrameFlags.Last : 0;
            if (end + (isFinal ? 11 : 0) > length)
            {
                bool unwritten = isLast && !isFinal;
                flags |= unwritten ? 0 : FrameFlags.Truncated;
                state.Truncated |= !unwritten;
                state.LastRunsPast = unwritten ? end - length : 0;
            }

            // In versions 4 and 5, decoders decode the frames before the last, which these encoders
            // may leave unfinished.
            if (isLast && !isFinal)
            {
                state.Frames.Add(new Frame(bit, frameLength, flags | FrameFlags.Last));
                break;
            }

            Bands bands = ReadBands(words, bit + 20, Math.Min(end, length), maxBand, h.MidSide);
            if (bands.Overrun)
            {
                flags |= FrameFlags.Overrun;
            }

            var frame = new Frame(bit, frameLength, flags) { MidSide = bands.MidSide, UsedBands = (byte)bands.Used, FlaggedBands = (byte)bands.Flagged };
            state.Frames.Add(frame);
            if ((flags & FrameFlags.Truncated) != 0)
            {
                break;
            }

            if (isFinal)
            {
                state.FinalSamples = (int)words.Read(end, 11);
            }

            state.Count(frame);
            bit = end;
            if ((flags & FrameFlags.Overrun) != 0)
            {
                damagedFrom = damaged++ == 0 ? frame.Bit : damagedFrom;
            }
            else
            {
                damaged = 0;
            }

            if (damaged == Sv7Parser.MaxDamagedRun && i + 1 < (h.KeepsLastFrame ? h.Frames : h.Frames - 1))
            {
                state.DamagedFrom = damagedFrom;
                break;
            }
        }
    }

    private readonly record struct Bands(int Used, uint MidSide, int Flagged, bool Overrun);

    // The resolutions and mid/side flags at the start of a frame (libmpcdec 1.2.6
    // mpc_decoder_read_bitstream_sv6, "HEADER"): per subband, the left resolution's code, the
    // mid/side flag where the header allows mid/side, then the right resolution's code.
    private static Bands ReadBands(WordStream words, long bit, long end, int maxBand, bool midSide)
    {
        long position = bit;
        int used = 0;
        uint mask = 0;
        int flagged = 0;
        for (int band = 0; band <= maxBand; band++)
        {
            int left = Resolution(words, ref position, band);
            bool flag = midSide && words.Read(position++, 1) == 1;
            int right = Resolution(words, ref position, band);
            if (left != 0 || right != 0)
            {
                flagged++;
                used = band + 1;
                mask |= flag ? 1u << band : 0;
            }

            if (position > end)
            {
                return new Bands(used, mask, flagged, true);
            }
        }

        return new Bands(used, mask, flagged, position > end);
    }

    private static int Resolution(WordStream words, ref long position, int band)
    {
        (uint Code, int Length, int Value)[] table = band < 11 ? RegionA : band <= 22 ? RegionB : RegionC;
        int[] resolutions = band < 11 ? ResolutionsA : band <= 22 ? ResolutionsB : ResolutionsC;
        uint code = words.Read(position, 32);
        foreach ((uint entry, int length, int value) in table)
        {
            if (code >= entry)
            {
                position += length;
                return value < resolutions.Length ? resolutions[value] : 0;
            }
        }

        return 0;
    }

    private static Node HeaderNode(WordLayout w, OldHeader h, ByteReader reader)
    {
        // Version 4's 16-bit frame count lies in the second word's high bytes, its frames start in
        // the low ones.
        long start = reader.Start;
        var header = new Node("Header", start, 8)
        {
            Description = Explain.Sv4Header,
            Topic = Topics.OldStreams,
            Ranges = h.Version == 4 ? [new ByteRange(start, 4), new ByteRange(start + 6, 2)] : null,
        };
        header.Add(w.Field("Bitrate", 0, 9, NodeValue.Integer(h.Bitrate, "kbps"), Explain.Sv4Bitrate, Topics.BitrateModes));
        header.Add(w.Field("UsesIntensityStereo", 9, 10, NodeValue.Boolean(h.IntensityStereo), Explain.Sv4IntensityStereo, Topics.IntensityStereo));
        header.Add(w.Field("UsesMidSideStereo", 10, 11, NodeValue.Boolean(h.MidSide), Explain.Sv4MidSide, Topics.MidSideStereo));
        header.Add(w.Field("Stream Version", 11, 21, NodeValue.Integer(h.Version), Explain.Sv4StreamVersion));
        header.Add(w.Field("Max Band", 21, 26, NodeValue.Integer(h.MaxBand), Explain.Sv4MaxBand, Topics.SubbandCoding));
        header.Add(w.Field("Block Size", 26, 32, NodeValue.Integer(h.BlockSize, "frames", "frame"), Explain.Sv4BlockSize));
        header.Add(w.Field("Frame Count", 32, h.Version == 4 ? 48 : 64, NodeValue.Integer(h.Frames, "frames", "frame"), Explain.Sv4FrameCount));
        return header;
    }

    private static Node FrameNode(State state, int index)
    {
        if (index >= state.Frames.Count)
        {
            long start = state.PaddingStart!.Value;
            return new Node("Padding Word", start, 4, NodeValue.Integer(4, "bytes", "byte")) { Description = Explain.Sv6PaddingWord, Topic = Topics.OldStreams };
        }

        Frame frame = state.Frames[index];
        WordLayout w = state.Layout;
        long limit = (w.End - w.Start) * 8;
        bool isLast = (frame.Flags & FrameFlags.Last) != 0;
        string description = isLast ? Explain.OldLastFrame
            : (frame.Flags & FrameFlags.Final) == 0 ? Explain.OldFrame
            : (frame.Flags & FrameFlags.Truncated) != 0 ? Explain.Sv6LastFrameCut : Explain.Sv6LastFrame;
        if (frame.Length < 0 || isLast)
        {
            return WithoutChildren(w.Field("Frame", frame.Bit, Math.Min(limit, frame.Length < 0 ? frame.Bit + 20 : frame.End), isLast ? "not decoded" : "cut short",
                description, null, NodeKind.Frame));
        }

        Node node = w.Field("Frame", frame.Bit, Math.Min(limit, frame.End), Summary(frame), description, null, NodeKind.Frame);
        return new Node(node.Name, node.Offset, node.Length, node.Value, () => FrameChildren(state, frame))
        {
            Kind = NodeKind.Frame,
            Description = description,
            Topic = Topics.OldStreams,
            Ranges = node.Ranges,
        };
    }

    private static Node WithoutChildren(Node node) =>
        new(node.Name, node.Offset, node.Length, node.Value) { Kind = node.Kind, Description = node.Description, Topic = node.Topic, Ranges = node.Ranges };

    private static string Summary(Frame frame)
    {
        var text = new StringBuilder();
        text.Append(Counts.Bits(frame.Length)).Append(", ").Append(frame.UsedBands).Append(frame.UsedBands == 1 ? " subband" : " subbands");
        int midSide = System.Numerics.BitOperations.PopCount(frame.MidSide);
        if (midSide > 0)
        {
            text.Append(", ").Append(midSide).Append(" mid/side");
        }

        return text.ToString();
    }

    private static List<Node> FrameChildren(State state, Frame frame)
    {
        WordLayout w = state.Layout;
        long limit = (w.End - w.Start) * 8;
        var nodes = new List<Node> { w.Field("Frame Length", frame.Bit, frame.Bit + 20, NodeValue.Integer(frame.Length, "bits", "bit"), Explain.FrameLength) };
        long dataEnd = Math.Min(limit, frame.DataEnd);
        Node data = w.Field("Frame Data", frame.Bit + 20, Math.Max(dataEnd, frame.Bit + 20), null, Explain.FrameData, Topics.SubbandCoding);
        if (data.Length > 0)
        {
            Node Cover(string name, NodeValue value, string description, string topic) =>
                new(name, data.Offset, data.Length, value) { Description = description, Topic = topic, Ranges = data.Ranges };
            data.Add(Cover("Used Subbands", NodeValue.Integer(frame.UsedBands), Explain.UsedBands, Topics.SubbandCoding));
            data.Add(Cover("Mid/Side Subbands", BandList(frame.MidSide), Explain.MidSideBands, Topics.MidSideStereo));
            nodes.Add(data);
        }

        if ((frame.Flags & FrameFlags.Final) != 0 && frame.End <= limit)
        {
            nodes.Add(FinalSamplesNode(state, frame));
        }

        return [.. nodes.Where(n => n.Length > 0)];
    }

    private static Node FinalSamplesNode(State state, Frame frame) =>
        state.Layout.Field("Last Frame Samples", frame.DataEnd, frame.End, NodeValue.Integer(state.FinalSamples ?? 0, "samples", "sample"), Explain.Sv6FinalSamples,
            Topics.GaplessPlayback);

    // The samples libmpcdec 1.2.6 outputs for the last frame of version 6
    // (mpc_decoder_decode_internal): 0 stands for
    // 1,152, and the synthesis filter's delay is decoded after the given samples, wrapping at a frame.
    private static int LastFrameOutput(int given)
    {
        int samples = given == 0 ? Sv7Parser.FrameSamples : given;
        int decay = (samples + Sv7Parser.SynthesisDelay) % Sv7Parser.FrameSamples;
        return samples + Sv7Parser.SynthesisDelay >= Sv7Parser.FrameSamples ? Sv7Parser.FrameSamples + decay : decay;
    }

    private static string BandList(uint mask)
    {
        if (mask == 0)
        {
            return "none";
        }

        var text = new StringBuilder();
        int band = 0;
        while (band < 32)
        {
            if ((mask & (1u << band)) == 0)
            {
                band++;
                continue;
            }

            int first = band;
            while (band + 1 < 32 && (mask & (1u << (band + 1))) != 0)
            {
                band++;
            }

            text.Append(text.Length > 0 ? ", " : string.Empty).Append(first);
            if (band > first)
            {
                text.Append(band == first + 1 ? ", " : " to ").Append(band);
            }

            band++;
        }

        return text.ToString();
    }

    private static void AddStream(State state, OldHeader h, Node node, AnalysisContext context)
    {
        const int sampleRate = 44100;
        var figures = new StreamFigures(sampleRate);
        long decoded = 0;
        Frame? firstOverrun = null;
        int overruns = 0;
        for (int i = 0; i < state.Frames.Count; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            Frame frame = state.Frames[i];
            if ((frame.Flags & (FrameFlags.Truncated | FrameFlags.Last)) != 0)
            {
                continue;
            }

            decoded++;
            figures.Add(state.Layout.WordStart(frame.Bit), frame.End - frame.Bit, Sv7Parser.FrameSamples, inSeries: frame.Length > 0);
            if ((frame.Flags & FrameFlags.Overrun) != 0)
            {
                overruns++;
                firstOverrun ??= frame;
            }
        }

        WordLayout w = state.Layout;
        long limit = (w.End - w.Start) * 8;
        Node FrameAt(Frame f) => w.Field("Frame", f.Bit, Math.Min(limit, f.Length < 0 ? f.Bit + 20 : f.End), null, Explain.OldFrame);
        if (firstOverrun is { } o)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.band-header-overrun",
                $"{(overruns == 1 ? "1 frame's" : $"{overruns} frames'")} subband resolutions run past the frame's length, first at 0x{FrameAt(o).Offset:X}: the frame is damaged.", FrameAt(o));
        }

        Node header = state.Leading[0];
        if (state.DamagedFrom is { } damagedFrom)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.frames-damaged",
                $"{Counts.Of(Sv7Parser.MaxDamagedRun, "frame", "frames")} in a row from 0x{w.WordStart(damagedFrom):X} {Counts.Agree(Sv7Parser.MaxDamagedRun, "has", "have")} subband resolutions that run past their length, which no encoder writes: the data is damaged, and the frames after them are not read.",
                header.Children.First(c => c.Name == "Frame Count"));
        }
        else if (state.Truncated)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.frames-missing",
                $"The header counts {Counts.Of(h.Frames, "frame", "frames")}, but the data holds {decoded} whole ones{(h.KeepsLastFrame ? string.Empty : " before the last")}: the stream is cut short.", header.Children.First(c => c.Name == "Frame Count"));
        }
        else if (state.LastRunsPast > 0)
        {
            Frame last = state.Frames[state.Frames.Count - 1];
            state.Findings.AddNow(Severity.Info, "musepack.last-frame-unwritten",
                $"The last of the {Counts.Of(h.Frames, "frame", "frames")} the header counts runs {Counts.Of(state.LastRunsPast, "bit", "bits")} past the end of the data: the SV4 and SV5 encoders write whole words only, and decoders (libmpcdec 1.2.6, mppdec) decode the frames before it.",
                WithoutChildren(w.Field("Frame", last.Bit, Math.Min(limit, last.End), "not decoded", Explain.OldLastFrame, null, NodeKind.Frame)));
        }

        Frame? final = state.FinalSamples is not null ? state.Frames[state.Frames.Count - 1] : null;
        if (final is { } f && state.FinalSamples > Sv7Parser.FrameSamples)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.last-frame-samples-invalid",
                $"The 11 bits after the last frame give {state.FinalSamples} samples, more than a frame's 1,152; libmpcdec 1.2.6 still decodes the frame, which adds {LastFrameOutput(state.FinalSamples.Value) - Sv7Parser.SynthesisDelay} samples to its output.",
                FinalSamplesNode(state, f));
        }

        // In versions 4 and 5, decoders decode the frames before the last (libmpcdec 1.2.6, mppdec),
        // and libmpcdec drops the synthesis filter's delay at the start. In version 6 libmpcdec decodes
        // the last frame too, and the 11 bits after it say how many samples it adds. Version 6 streams
        // libmpcdec refuses give none. A damaged frame stops libmpcdec earlier; the count assumes the
        // frames whole.
        long frames = h.IsReadable ? decoded : h.Version is 4 or 5 ? Math.Max(0, h.Frames - 1) : 0;
        long samples = state.FinalSamples is { } given && decoded > 0
            ? ((decoded - 1) * Sv7Parser.FrameSamples) + LastFrameOutput(given) - Sv7Parser.SynthesisDelay
            : Math.Max(0, (frames * Sv7Parser.FrameSamples) - Sv7Parser.SynthesisDelay);
        double duration = samples / (double)sampleRate;
        Node Cover(string name, NodeValue? value, string description) => new(name, node.Offset, node.Length, value) { Description = description };
        Node? bitrate = null;
        if (h.IsReadable)
        {
            Node statistics = new("Statistics", node.Offset, node.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
            statistics.Add(Cover("Frames", NodeValue.Integer(frames, "frames", "frame"), h.KeepsLastFrame ? Explain.Sv6StatFrames : Explain.OldStatFrames));
            statistics.Add(Cover("Coded Samples", NodeValue.Integer(frames * Sv7Parser.FrameSamples, "samples", "sample"), Explain.CodedSamples));
            statistics.Add(Cover("Decoder Delay", NodeValue.Integer(Sv7Parser.SynthesisDelay, "samples", "sample"), Explain.DecoderDelay));
            statistics.Add(Cover("Samples", NodeValue.Integer(samples, "samples", "sample"), h.KeepsLastFrame ? Explain.Sv6StatSamples : Explain.OldStatSamples));
            statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
            figures.AddTo(statistics, Cover, "Frame", "frames", inBytes: false, out Node average);
            bitrate = average;
            statistics.Add(new Node("Frames With Mid/Side", node.Offset, node.Length,
                $"{StreamFigures.Counted(state.FramesWithMidSide, "Frame", "frames")} ({StreamFigures.Share(state.FramesWithMidSide, frames)})")
            {
                Description = Explain.FramesWithMidSide,
                Topic = Topics.MidSideStereo,
            });
            Node use = statistics.Add(new Node("Subband Stereo Coding", node.Offset, node.Length, NodeValue.Integer(state.FlaggedBands, "subbands", "subband"))
            {
                Description = Explain.MidSideUse,
                Topic = Topics.MidSideStereo,
            });
            use.Add(Cover("Mid/Side", $"{StreamFigures.Counted(state.MidSideBands, "Subband", "subbands")} ({StreamFigures.Share(state.MidSideBands, state.FlaggedBands)})", Explain.MidSideCount));
            use.Add(Cover("Left/Right", $"{StreamFigures.Counted(state.FlaggedBands - state.MidSideBands, "Subband", "subbands")} ({StreamFigures.Share(state.FlaggedBands - state.MidSideBands, state.FlaggedBands)})", Explain.LeftRightCount));
            state.Leading.Add(statistics);
        }

        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = $"Musepack SV{h.Version}",

            // libmpcdec 1.2.6: "used by all files up to SV7".
            SampleRate = sampleRate,
            Channels = 2,
            ChannelMode = h.IntensityStereo ? "Stereo, intensity stereo" : h.MidSide ? "Stereo, mid/side per subband" : "Stereo",
            ChannelLayout = ChannelLayouts.Stereo,
            Samples = samples,
            Duration = duration,
            AverageBitrate = h.IsReadable ? figures.AverageBitrate : null,
            IsVbr = h.Bitrate == 0,
            Series = h.IsReadable ? figures.Series(bitrate, state.Cancellation) : [],
        });
    }

    [Flags]
    private enum FrameFlags : byte
    {
        Truncated = 1,
        Overrun = 2,
        Last = 4,
        Final = 8,
    }

    // A frame in 24 bytes, as an SV7 frame is kept.
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Frame(long bit, int length, FrameFlags flags)
    {
        public long Bit => bit;

        public int Length => length;

        public FrameFlags Flags => flags;

        public long DataEnd => bit + 20 + Math.Max(0, length);

        // The last frame of version 6 is followed by 11 bits of its own.
        public long End => DataEnd + ((flags & FrameFlags.Final) != 0 ? 11 : 0);

        public uint MidSide { get; init; }

        public byte UsedBands { get; init; }

        public byte FlaggedBands { get; init; }
    }

    private sealed class State(WordLayout layout, CancellationToken cancellation)
    {
        public WordLayout Layout => layout;

        public CancellationToken Cancellation => cancellation;

        public ChunkedList<Frame> Frames { get; } = new();

        public List<Node> Leading { get; } = [];

        public FindingCollector Findings { get; } = new();

        public bool Truncated { get; set; }

        public long LastRunsPast { get; set; }

        public int? FinalSamples { get; set; }

        public long? DamagedFrom { get; set; }

        public long? PaddingStart { get; set; }

        public long FramesWithMidSide { get; private set; }

        public long MidSideBands { get; private set; }

        public long FlaggedBands { get; private set; }

        public void Count(Frame frame)
        {
            int midSide = System.Numerics.BitOperations.PopCount(frame.MidSide);
            FramesWithMidSide += midSide > 0 ? 1 : 0;
            MidSideBands += midSide;
            FlaggedBands += frame.FlaggedBands;
        }
    }
}
