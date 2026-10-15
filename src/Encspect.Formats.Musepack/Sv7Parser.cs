using System.Runtime.InteropServices;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Musepack;

// Stream version 7 (libmpcdec r495 streaminfo_read_header_sv7, mpc_demux.c and
// mpc_decoder_read_bitstream_sv7; mppenc 1.16 WriteHeader_SV7 and its main loop). The header and the
// frames are one bitstream of 32-bit little-endian words; the frames start 200 bits in (libmpcdec
// 1.2.6 get_initial_fpos). Each frame is a 20-bit length and that many bits of data; frames have no
// sync word, so they are followed by their lengths, and a damaged length loses the rest. After the
// last frame the header counts, 11 bits repeat the last frame's sample count; with true gapless and
// more than 671 samples in that frame, libmpcdec decodes one more frame, which mppenc writes.
internal static class Sv7Parser
{
    public const int HeaderBytes = 28;
    public const long DataBit = 200;
    public const int FrameSamples = 1152;
    public const int SynthesisDelay = 481;

    // A frame needs bits for its subband resolutions, so no encoder writes a length of 0; after this
    // many in a row the frames are not followed further, since each would cost a record per 20 bits.
    public const int MaxEmptyRun = 4096;

    private static readonly string[] ProfileNames =
    [
        "n.a.", "Unstable/Experimental", "n.a.", "n.a.", "n.a.", "quality 0", "quality 1", "Telephone", "Thumb", "Radio", "Standard", "Extreme",
        "Insane", "BrainDead", "quality 9", "quality 10",
    ];

    // libmpcdec r495 huffman.c, mpc_table_HuffHdr: the change of a subband's resolution from the
    // subband below, by the 16 bits that start at the code; 4 escapes to 4 bits of resolution.
    private static readonly (int Code, int Length, int Value)[] HuffHdr =
    [
        (0x8000, 1, 0), (0x6000, 3, 1), (0x5E00, 7, -4), (0x5D80, 9, 3), (0x5D00, 9, 4), (0x5C00, 8, -5), (0x5800, 6, 2), (0x5000, 5, -3),
        (0x4000, 4, -2), (0x0, 2, -1),
    ];

    public static ReadOnlySpan<int> SampleRates => [44100, 48000, 37800, 32000];

    public static string ProfileName(int profile) => profile >= 0 && profile < ProfileNames.Length ? ProfileNames[profile] : "n.a.";

    // Where the stream ends by its frames: the end of the word that holds the last bit of the frames
    // the header counts (and the frame after them that gapless needs, where one is there), or null
    // where they run past the end of the data, since a stream cut short says nothing about where its
    // data ends in this file.
    public static long? DeclaredEnd(ByteReader reader)
    {
        if (reader.Length < HeaderBytes)
        {
            return null;
        }

        var words = new WordStream(reader, reader.Start, reader.End);
        var header = Sv7Header.Read(words);
        long bit = DataBit;
        int? final = null;
        for (long i = 0; i < header.Frames; i++)
        {
            if (i % 4096 == 0)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
            }

            if (bit + 20 > words.BitLength)
            {
                return null;
            }

            long dataEnd = bit + 20 + words.Read(bit, 20);
            bit = dataEnd + (i == header.Frames - 1 ? 11 : 0);
            if (bit > words.BitLength)
            {
                return null;
            }

            final = i == header.Frames - 1 ? (int)words.Read(dataEnd, 11) : null;
        }

        if (NeedsExtraFrame(header, final) && ExtraFrameAt(reader, words, header, bit))
        {
            bit += 20 + words.Read(bit, 20);
            if (bit > words.BitLength)
            {
                return null;
            }
        }

        return words.Layout.WordEnd(bit);
    }

    // The last frame's samples as libmpcdec r495 decodes them (mpc_decoder_decode_frame): with true
    // gapless from the 11 bits after the last frame, else from the header; 0 means all 1,152.
    public static int LastFrameSamples(Sv7Header h, int? final)
    {
        int last = h.Gapless && final is { } bits ? bits : h.LastSamples;
        return last == 0 ? FrameSamples : last;
    }

    // Whether libmpcdec decodes a frame after the counted ones: with true gapless, where the last
    // frame's samples and the synthesis filter's delay pass the frame.
    public static bool NeedsExtraFrame(Sv7Header h, int? final) => h.Gapless && h.Frames > 0 && LastFrameSamples(h, final) + SynthesisDelay > FrameSamples;

    // Whether that frame can be at bit: mppenc writes it right after the counted frames, so a tag's
    // signature at the next word, or bits whose subband resolutions run past their length or out of
    // range, mean the encoder did not write it.
    private static bool ExtraFrameAt(ByteReader reader, WordStream words, Sv7Header h, long bit)
    {
        long next = words.Layout.WordEnd(bit);
        if (bit + 20 > words.BitLength || reader.MatchesAt(next, "APETAGEX"u8) || reader.MatchesAt(next, "ID3"u8) || reader.MatchesAt(next, "TAG"u8))
        {
            return false;
        }

        long end = bit + 20 + words.Read(bit, 20);
        Bands bands = ReadBands(words, bit + 20, Math.Min(end, words.BitLength), Math.Min(h.MaxBand, 31), h.MidSide);
        return !bands.Overrun && !bands.Invalid;
    }

    // Whether the header the data starts with is one an encoder writes: a whole header of stream version 7, a frame count,
    // a max band from 1 to 31 and a last frame of at most a frame's samples (the checks HeaderFindings warns about; libmpcdec
    // refuses the stream otherwise). "MP+" alone is three bytes any data can start with.
    public static bool IsConsistent(ByteReader reader)
    {
        if (reader.Length < HeaderBytes)
        {
            return false;
        }

        Sv7Header h = Sv7Header.Read(new WordStream(reader, reader.Start, reader.End));
        return h.Version == 7 && h.Frames > 0 && h.MaxBand is > 0 and < 32 && h.LastSamples <= FrameSamples;
    }

    public static void Parse(ByteReader reader, Node parent, AnalysisContext context, IReadOnlyList<EncoderClaim> claims)
    {
        long start = reader.Start;
        long end = reader.End;
        if (end - start < HeaderBytes)
        {
            var cut = new Node("Musepack SV7", start, end - start) { Kind = NodeKind.Stream, Description = Explain.Sv7Stream, Topic = Topics.Sv7Stream };
            Node header = cut.Add(new Node("Header", start, end - start) { Description = Explain.Header, Topic = Topics.Sv7Stream });
            parent.Add(cut);
            context.AddFinding(Severity.Warning, "musepack.header-truncated", $"The SV7 header needs {HeaderBytes} bytes; the data holds {end - start}.", header);
            return;
        }

        var words = new WordStream(reader, start, end);
        WordLayout layout = words.Layout;
        var h = Sv7Header.Read(words);
        var state = new State(layout, h, context.CancellationToken);
        Node headerNode = HeaderNode(layout, h);
        state.Leading.Add(headerNode);
        HeaderFindings(state, h, headerNode);

        Walk(reader, words, state);

        long streamEnd = layout.WordEnd(state.EndBit);
        if (state.Truncated || state.EmptyFrom is not null)
        {
            streamEnd = end;
        }

        if (streamEnd < end)
        {
            state.TrailingStart = streamEnd;
            var gap = GapNode(streamEnd, end);
            state.Findings.AddNow(Severity.Warning, "musepack.trailing-data",
                $"{end - streamEnd} bytes after the stream's last frame, from 0x{streamEnd:X}, are not part of it.", gap);
        }

        int count = state.Frames.Count + (state.TrailingStart is null ? 0 : 1);
        Node node = new("Musepack SV7", start, end - start, null, () => new BlockedChildren(state.Leading, count, i => FrameNode(state, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Sv7Stream,
            Topic = Topics.Sv7Stream,
        };
        parent.Add(node);
        AddStream(state, node, context, claims);
        state.Findings.Flush(context);
    }

    private static void Walk(ByteReader reader, WordStream words, State state)
    {
        Sv7Header h = state.Header;
        long bit = DataBit;
        long length = words.BitLength;
        int maxBand = Math.Min(h.MaxBand, 31);
        int empty = 0;
        for (long i = 0; i <= h.Frames; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            bool isFinal = i == h.Frames - 1;
            bool isExtra = i == h.Frames;
            if (isExtra && !(NeedsExtraFrame(h, state.FinalSamples) && ExtraFrameAt(reader, words, h, bit)))
            {
                state.ExtraMissing = NeedsExtraFrame(h, state.FinalSamples);
                break;
            }

            if (bit + 20 > length)
            {
                if (bit < length)
                {
                    state.Frames.Add(new Frame(bit, -1, Flags.LengthCut | Flags.Truncated));
                    state.Truncated = true;
                }

                break;
            }

            int frameLength = (int)words.Read(bit, 20);
            long dataEnd = bit + 20 + frameLength;
            long frameEnd = dataEnd + (isFinal ? 11 : 0);
            Flags flags = (isFinal ? Flags.Final : 0) | (isExtra ? Flags.Extra : 0);
            if (frameEnd > length)
            {
                flags |= Flags.Truncated;
                state.Truncated = true;
            }

            Bands bands = ReadBands(words, bit + 20, Math.Min(dataEnd, length), maxBand, h.MidSide);
            if (bands.Overrun)
            {
                flags |= Flags.Overrun;
            }

            if (bands.Invalid)
            {
                flags |= Flags.BadResolution;
            }

            var frame = new Frame(bit, frameLength, flags) { MidSide = bands.MidSide, UsedBands = (byte)bands.Used, NoiseChannels = (byte)bands.Noise, FlaggedBands = (byte)bands.Flagged };
            state.Frames.Add(frame);
            if (state.Truncated)
            {
                break;
            }

            empty = frameLength == 0 ? empty + 1 : 0;
            if (empty == MaxEmptyRun)
            {
                state.EmptyFrom = bit - ((MaxEmptyRun - 1) * 20L);
                state.Count(frame, bands);
                state.EndBit = frameEnd;
                break;
            }

            state.Count(frame, bands);
            if (isFinal)
            {
                state.FinalSamples = (int)words.Read(dataEnd, 11);
            }

            bit = frameEnd;
            state.EndBit = bit;
        }

        if (!state.Truncated && state.Frames.Count == 0)
        {
            state.EndBit = DataBit;
        }
    }

    private readonly record struct Bands(int Used, uint MidSide, int Flagged, int Noise, bool Overrun, bool Invalid);

    // The resolutions and mid/side flags at the start of a frame (libmpcdec r495
    // mpc_decoder_read_bitstream_sv7, "Header"): subband 0 in 4 bits per channel, the others as a
    // change from the subband below or, after the escape 4, in 4 bits; a mid/side flag after each
    // subband that either channel codes, where the header allows mid/side.
    private static Bands ReadBands(WordStream words, long bit, long end, int maxBand, bool midSide)
    {
        int left = (int)words.Read(bit, 4);
        int right = (int)words.Read(bit + 4, 4);
        long position = bit + 8;
        int used = 0;
        uint mask = 0;
        int flagged = 0;
        int noise = 0;
        bool invalid = false;
        for (int band = 0; band <= maxBand; band++)
        {
            if (band > 0)
            {
                left = Next(words, ref position, left);
                right = Next(words, ref position, right);
            }

            invalid |= left is < -1 or > 17 || right is < -1 or > 17;
            noise += (left == -1 ? 1 : 0) + (right == -1 ? 1 : 0);
            if (left != 0 || right != 0)
            {
                flagged++;
                if (midSide && words.Read(position++, 1) == 1)
                {
                    mask |= 1u << band;
                }

                used = band + 1;
            }

            if (position > end)
            {
                return new Bands(used, mask, flagged, noise, true, invalid);
            }
        }

        return new Bands(used, mask, flagged, noise, position > end, invalid);
    }

    private static int Next(WordStream words, ref long position, int previous)
    {
        int code = (int)words.Read(position, 16);
        foreach ((int entry, int length, int value) in HuffHdr)
        {
            if (code >= entry)
            {
                position += length;
                if (value != 4)
                {
                    return previous + value;
                }

                int escaped = (int)words.Read(position, 4);
                position += 4;
                return escaped;
            }
        }

        return previous;
    }

    private static void HeaderFindings(State state, Sv7Header h, Node header)
    {
        Node Field(string name) => header.Children.First(c => c.Name == name);
        if (h.Version != 7)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.version-unknown",
                $"The header's stream version is {h.Version}; libmpcdec and ffmpeg read \"MP+\" streams only of version 7, so the stream is read as SV7.", Field("Stream Version"));
        }

        if (h.Frames == 0)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.no-frames", "The header counts no frames, so decoders output nothing.", Field("Frame Count"));
        }

        if (h.MaxBand is 0 or >= 32)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.max-band-invalid",
                $"The header's max band is {h.MaxBand}; libmpcdec refuses streams with 0 or a value from 32 on{(h.MaxBand >= 32 ? ", and the analyzer reads the subbands up to 31" : string.Empty)}.", Field("Max Band"));
        }

        if (h.IntensityStereo)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.intensity-stereo-flag",
                "The header's former intensity stereo flag is set; mppenc 1.16 writes 0, as SV7 no longer supports intensity stereo, and libmpcdec ignores it.", Field("Intensity Stereo"));
        }

        if (h.LastSamples > FrameSamples)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.last-frame-samples-invalid",
                $"The header gives {h.LastSamples} samples for the last frame, more than a frame's {FrameSamples}; libmpcdec refuses the stream.", Field("Last Frame Samples"));
        }

        if (h.Unused != 0)
        {
            state.Findings.AddNow(Severity.Info, "musepack.unused-bits-set", "Bits the header leaves unused are not 0.", Field("Unused"));
        }
    }

    private static Node HeaderNode(WordLayout w, Sv7Header h)
    {
        ByteRange[] ranges = [new(w.Start, 24), new(w.Start + 27, 1)];
        var header = new Node("Header", w.Start, HeaderBytes) { Description = Explain.Header, Topic = Topics.Sv7Stream, Ranges = ranges };
        header.Add(new Node("Magic", w.Start, 3, "MP+") { Description = Explain.Magic });
        header.Add(w.Field("Stream Version", 4, 8, NodeValue.Integer(h.Version), Explain.StreamVersion));
        header.Add(w.Field("PNS", 0, 4, NodeValue.Integer(h.PnsNibble), Explain.PnsFlag, Topics.NoiseSubstitution));
        header.Add(w.Field("Frame Count", 32, 64, NodeValue.Integer(h.Frames, "frames", "frame"), Explain.FrameCount));
        header.Add(w.Field("Intensity Stereo", 64, 65, NodeValue.Boolean(h.IntensityStereo), Explain.IntensityStereoFlag, Topics.IntensityStereo));
        header.Add(w.Field("Mid/Side Stereo", 65, 66, NodeValue.Boolean(h.MidSide), Explain.MidSideFlag, Topics.MidSideStereo));
        header.Add(w.Field("Max Band", 66, 72, NodeValue.Integer(h.MaxBand), Explain.MaxBand, Topics.SubbandCoding));
        Node profile = header.Add(w.Field("Profile", 72, 76, NodeValue.Integer(h.Profile), Explain.Profile, Topics.SubbandCoding));
        profile.Add(w.Field("Name", 72, 76, ProfileName(h.Profile), Explain.ProfileName));
        profile.Add(w.Field("Quality", 72, 76, NodeValue.Integer(h.Profile - 5), Explain.Quality));
        header.Add(w.Field("Link", 76, 78, NodeValue.Integer(h.Link), Explain.Link));
        header.Add(w.Field("Sample Rate", 78, 80, NodeValue.Integer(SampleRates[h.RateIndex], "Hz"), Explain.SampleRateIndex));
        header.Add(w.Field("Max Level", 80, 96, NodeValue.Integer(h.MaxLevel), Explain.MaxLevel));
        header.Add(w.Field("Title Gain", 96, 112, NodeValue.Decimal(h.TitleGain / 100.0, 2, "dB", showsSign: true), Explain.Sv7TitleGain, Topics.ReplayGain));
        header.Add(w.Field("Title Peak", 112, 128, NodeValue.Integer(h.TitlePeak), Explain.Sv7TitlePeak, Topics.ReplayGain));
        header.Add(w.Field("Album Gain", 128, 144, NodeValue.Decimal(h.AlbumGain / 100.0, 2, "dB", showsSign: true), Explain.Sv7AlbumGain, Topics.ReplayGain));
        header.Add(w.Field("Album Peak", 144, 160, NodeValue.Integer(h.AlbumPeak), Explain.Sv7AlbumPeak, Topics.ReplayGain));
        header.Add(w.Field("True Gapless", 160, 161, NodeValue.Boolean(h.Gapless), Explain.TrueGapless, Topics.GaplessPlayback));
        header.Add(w.Field("Last Frame Samples", 161, 172, NodeValue.Integer(h.LastSamples, "samples", "sample"), Explain.LastFrameSamples, Topics.GaplessPlayback));
        header.Add(w.Field("Fast Seeking", 172, 173, NodeValue.Boolean(h.FastSeek), Explain.FastSeek));
        header.Add(w.Field("Unused", 173, 192, NodeValue.Hex(h.Unused, 5), Explain.Unused));
        Node encoder = header.Add(w.Field("Encoder Version", 192, 200, NodeValue.Integer(h.Encoder), Explain.EncoderVersion, Topics.EncoderDetection));
        encoder.Add(w.Field("Name", 192, 200, MusepackEncoder.LibmpcdecName(h.Encoder), Explain.VersionName));
        return header;
    }

    private static Node GapNode(long start, long end) =>
        new("Gap", start, end - start, NodeValue.Integer(end - start, "bytes")) { Description = Explain.Gap };

    private static Node FrameNode(State state, int index)
    {
        if (index >= state.Frames.Count)
        {
            return GapNode(state.TrailingStart!.Value, state.Layout.End);
        }

        Frame frame = state.Frames[index];
        WordLayout w = state.Layout;
        long end = frame.Length < 0 ? w.End * 8 : frame.End;
        string description = (frame.Flags & Flags.Extra) != 0 ? Explain.ExtraFrame : Explain.Sv7Frame;
        if (frame.Length < 0)
        {
            Node cut = w.Field("Frame", frame.Bit, Math.Min((w.End - w.Start) * 8, frame.Bit + 20), "cut short", description, null, NodeKind.Frame);
            return cut;
        }

        Node node = w.Field("Frame", frame.Bit, Math.Min((w.End - w.Start) * 8, end), Summary(frame), description, null, NodeKind.Frame);
        return new Node(node.Name, node.Offset, node.Length, node.Value, () => FrameChildren(state, frame))
        {
            Kind = NodeKind.Frame,
            Description = description,
            Ranges = node.Ranges,
        };
    }

    private static string Summary(Frame frame)
    {
        var text = new StringBuilder();
        text.Append(frame.Length).Append(" bits, ").Append(frame.UsedBands).Append(frame.UsedBands == 1 ? " subband" : " subbands");
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
        var nodes = new List<Node>
        {
            w.Field("Frame Length", frame.Bit, frame.Bit + 20, NodeValue.Integer(frame.Length, "bits", "bit"), Explain.FrameLength),
        };
        long dataEnd = Math.Min(limit, frame.Bit + 20 + frame.Length);
        Node data = w.Field("Frame Data", frame.Bit + 20, Math.Max(dataEnd, frame.Bit + 20), null, Explain.FrameData, Topics.SubbandCoding);

        // In a last word the data cuts short, the bits of a field may lie only in bytes past the end.
        if (data.Length == 0)
        {
            return [.. nodes.Where(n => n.Length > 0)];
        }

        Node Cover(string name, NodeValue value, string description, string? topic = null) =>
            new(name, data.Offset, data.Length, value) { Description = description, Topic = topic, Ranges = data.Ranges };
        data.Add(Cover("Used Subbands", NodeValue.Integer(frame.UsedBands), Explain.UsedBands, Topics.SubbandCoding));
        data.Add(Cover("Mid/Side Subbands", BandList(frame.MidSide), Explain.MidSideBands, Topics.MidSideStereo));
        data.Add(Cover("Noise-Substituted Channels", NodeValue.Integer(frame.NoiseChannels), Explain.NoiseBands, Topics.NoiseSubstitution));
        nodes.Add(data);
        if ((frame.Flags & Flags.Final) != 0 && frame.Bit + 20 + frame.Length + 11 <= limit)
        {
            nodes.Add(w.Field("Last Frame Samples", frame.Bit + 20 + frame.Length, frame.Bit + 20 + frame.Length + 11,
                NodeValue.Integer(state.FinalSamples ?? 0, "samples", "sample"), Explain.FinalSamples, Topics.GaplessPlayback));
        }

        return [.. nodes.Where(n => n.Length > 0)];
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

    private static void AddStream(State state, Node node, AnalysisContext context, IReadOnlyList<EncoderClaim> claims)
    {
        Sv7Header h = state.Header;
        int rate = SampleRates[h.RateIndex];
        var figures = new StreamFigures(rate);
        long complete = 0;
        Frame? firstBad = null;
        Frame? firstOverrun = null;
        int bad = 0;
        int overrun = 0;
        for (int i = 0; i < state.Frames.Count; i++)
        {
            if (i % 4096 == 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
            }

            Frame frame = state.Frames[i];
            if ((frame.Flags & Flags.Truncated) != 0)
            {
                continue;
            }

            complete++;
            figures.Add(state.Layout.WordStart(frame.Bit), frame.End - frame.Bit, FrameSamples, inSeries: frame.Length > 0);
            if ((frame.Flags & Flags.BadResolution) != 0)
            {
                bad++;
                firstBad ??= frame;
            }

            if ((frame.Flags & Flags.Overrun) != 0)
            {
                overrun++;
                firstOverrun ??= frame;
            }
        }

        Node FrameAt(Frame frame) => WithoutChildren(state.Layout.Field("Frame", frame.Bit, Math.Min(frame.End, (state.Layout.End - state.Layout.Start) * 8),
            frame.Length < 0 ? "cut short" : Summary(frame), Explain.Sv7Frame, null, NodeKind.Frame));

        if (firstOverrun is { } o)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.band-header-overrun",
                $"{(overrun == 1 ? "1 frame's" : $"{overrun} frames'")} subband resolutions run past the frame's length, first at 0x{FrameAt(o).Offset:X}: the frame is damaged.", FrameAt(o));
        }

        if (firstBad is { } b)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.resolution-invalid",
                $"{(bad == 1 ? "1 frame codes" : $"{bad} frames code")} a subband resolution outside -1 to 17, first at 0x{FrameAt(b).Offset:X}, which libmpcdec does not decode as coded samples: the frame is damaged.", FrameAt(b));
        }

        Frame? last = state.Frames.Count > 0 ? state.Frames[state.Frames.Count - 1] : null;
        if (state.Truncated && last is { } cut)
        {
            long have = (state.Layout.End - state.Layout.Start) * 8 - cut.Bit;
            string what = cut.Length < 0 ? "its 20-bit length" : $"{cut.End - cut.Bit} bits";
            state.Findings.AddNow(Severity.Warning, "musepack.frame-truncated",
                $"The frame at 0x{FrameAt(cut).Offset:X} has {Math.Max(0, have)} bits of {what}: the stream is cut short.", FrameAt(cut));
        }

        long counted = 0;
        for (int i = 0; i < state.Frames.Count; i++)
        {
            counted += (state.Frames[i].Flags & (Flags.Truncated | Flags.Extra)) == 0 ? 1 : 0;
        }

        Node header = state.Leading[0];
        if (state.EmptyFrom is { } emptyFrom)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.frames-empty",
                $"{MaxEmptyRun} frames in a row from 0x{state.Layout.WordStart(emptyFrom):X} have a length of 0 bits, which no encoder writes: the data is damaged, and the frames after them are not read.",
                header.Children.First(c => c.Name == "Frame Count"));
        }
        else if (counted < h.Frames)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.frames-missing",
                $"The header counts {h.Frames} frames, but the data holds {counted} whole ones.", header.Children.First(c => c.Name == "Frame Count"));
        }

        if (state.ExtraMissing)
        {
            state.Findings.AddNow(Severity.Info, "musepack.gapless-frame-missing",
                $"The stream ends after the frames the header counts, without the frame libmpcdec decodes after them where the last frame holds more than 671 samples, so its output ends {LastFrameSamples(h, state.FinalSamples) - 671} samples early.",
                header.Children.First(c => c.Name == "Last Frame Samples"));
        }

        if (h.Gapless && state.FinalSamples is { } final && final != h.LastSamples)
        {
            state.Findings.AddNow(Severity.Warning, "musepack.last-frame-samples-differ",
                $"The 11 bits after the last frame give {final} samples, the header {h.LastSamples}; with true gapless, libmpcdec takes the bits after the frame, and so does the analyzer.",
                header.Children.First(c => c.Name == "Last Frame Samples"));
        }

        // libmpcdec r495: true gapless keeps the last frame's samples, otherwise the synthesis
        // filter's delay is cut off the end; the output never runs past the frames decoded.
        long frames = complete;
        long announced = h.Frames == 0 ? 0 : h.Gapless ? (h.Frames * FrameSamples) - FrameSamples + LastFrameSamples(h, state.FinalSamples) : (h.Frames * FrameSamples) - SynthesisDelay;
        long samples = Math.Max(0, Math.Min(announced, (frames * FrameSamples) - SynthesisDelay));
        double duration = rate > 0 ? samples / (double)rate : 0;

        Node Cover(string name, NodeValue? value, string description) =>
            new(name, node.Offset, node.Length, value) { Description = description };

        Node? encoder = MusepackEncoder.Detect(h, null, header.Children.First(c => c.Name == "Encoder Version"), claims,
            (name, value, description) => name == "Encoder"
                ? new Node(name, node.Offset, node.Length, value) { Description = description, Topic = ClaimRoles.EncoderTopic }
                : Cover(name, value, description),
            (claim, text, name) => state.Findings.Add(Severity.Info, "musepack.encoder-claim-differs", claim.Source, _ =>
                $"The {claim.Description} names \"{text}\", but the stream points to {name}; the tag may have been written by other software, or kept when the audio was re-encoded."));
        if (encoder is not null)
        {
            state.Leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(claims, (name, value, description) => new Node(name, node.Offset, node.Length, value) { Description = description, Topic = ClaimRoles.TopicOf(name) },
            (claim, text, name) => state.Findings.Add(Severity.Info, "musepack.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)));
        if (writer is not null)
        {
            state.Leading.Add(writer);
        }

        Node statistics = new("Statistics", node.Offset, node.Length) { Kind = NodeKind.Statistics, Description = Explain.Statistics };
        statistics.Add(Cover("Frames", NodeValue.Integer(frames, "frames", "frame"), Explain.StatFrames));
        statistics.Add(Cover("Coded Samples", NodeValue.Integer(frames * FrameSamples, "samples", "sample"), Explain.CodedSamples));
        statistics.Add(Cover("Decoder Delay", NodeValue.Integer(SynthesisDelay, "samples", "sample"), Explain.DecoderDelay));
        statistics.Add(Cover("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples));
        statistics.Add(Cover("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration));
        figures.AddTo(statistics, Cover, "Frame", "frames", inBytes: false, out Node bitrate);
        statistics.Add(new Node("Frames With Mid/Side", node.Offset, node.Length, $"{StreamFigures.Counted(state.FramesWithMidSide, "Frame", "frames")} ({StreamFigures.Share(state.FramesWithMidSide, frames)})")
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
        statistics.Add(new Node("Noise-Substituted Channels", node.Offset, node.Length, NodeValue.Integer(state.NoiseChannels, "subband channels", "subband channel"))
        {
            Description = Explain.NoiseCount,
            Topic = Topics.NoiseSubstitution,
        });
        state.Leading.Add(statistics);

        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = "Musepack SV7",
            SampleRate = rate,
            Channels = 2,
            ChannelMode = h.MidSide ? "Stereo, mid/side per subband" : "Stereo",
            ChannelLayout = ChannelLayouts.Stereo,
            Samples = samples,
            Duration = duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = true,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = MusepackEncoder.ConfidenceOf(encoder),
            Writer = ClaimRoles.Resolved(writer).Name,
            WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
            Series = figures.Series(bitrate, state.Cancellation),
        });
    }

    private static Node WithoutChildren(Node node) =>
        new(node.Name, node.Offset, node.Length, node.Value) { Kind = node.Kind, Description = node.Description, Ranges = node.Ranges };

    [Flags]
    private enum Flags : byte
    {
        Truncated = 1,
        Overrun = 2,
        BadResolution = 4,
        Extra = 8,
        Final = 16,
        LengthCut = 32,
    }

    // A frame in 24 bytes: where it starts in the bitstream, its length and what its subband
    // resolutions say.
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Frame(long bit, int length, Flags flags)
    {
        public long Bit => bit;

        public int Length => length;

        public Flags Flags => flags;

        public long End => bit + 20 + Math.Max(0, length) + ((flags & Flags.Final) != 0 ? 11 : 0);

        public uint MidSide { get; init; }

        public byte UsedBands { get; init; }

        public byte FlaggedBands { get; init; }

        public byte NoiseChannels { get; init; }
    }

    private sealed class State(WordLayout layout, Sv7Header header, CancellationToken cancellation)
    {
        public WordLayout Layout => layout;

        public Sv7Header Header => header;

        public CancellationToken Cancellation => cancellation;

        public ChunkedList<Frame> Frames { get; } = new();

        public List<Node> Leading { get; } = [];

        public FindingCollector Findings { get; } = new();

        public long EndBit { get; set; } = DataBit;

        public bool Truncated { get; set; }

        public bool ExtraMissing { get; set; }

        public long? TrailingStart { get; set; }

        public int? FinalSamples { get; set; }

        public long? EmptyFrom { get; set; }

        public long FramesWithMidSide { get; private set; }

        public long MidSideBands { get; private set; }

        public long FlaggedBands { get; private set; }

        public long NoiseChannels { get; private set; }

        public void Count(Frame frame, Bands bands)
        {
            int midSide = System.Numerics.BitOperations.PopCount(frame.MidSide);
            FramesWithMidSide += midSide > 0 ? 1 : 0;
            MidSideBands += midSide;
            FlaggedBands += bands.Flagged;
            NoiseChannels += bands.Noise;
        }
    }
}

// The SV7 header's fields (libmpcdec r495 streaminfo_read_header_sv7).
internal readonly record struct Sv7Header(
    int Version, int PnsNibble, long Frames, bool IntensityStereo, bool MidSide, int MaxBand, int Profile, int Link, int RateIndex, int MaxLevel,
    short TitleGain, int TitlePeak, short AlbumGain, int AlbumPeak, bool Gapless, int LastSamples, bool FastSeek, int Unused, int Encoder)
{
    public static Sv7Header Read(WordStream w) => new(
        Version: (int)w.Read(4, 4),
        PnsNibble: (int)w.Read(0, 4),
        Frames: w.Read(32, 32),
        IntensityStereo: w.Read(64, 1) == 1,
        MidSide: w.Read(65, 1) == 1,
        MaxBand: (int)w.Read(66, 6),
        Profile: (int)w.Read(72, 4),
        Link: (int)w.Read(76, 2),
        RateIndex: (int)w.Read(78, 2),
        MaxLevel: (int)w.Read(80, 16),
        TitleGain: (short)w.Read(96, 16),
        TitlePeak: (int)w.Read(112, 16),
        AlbumGain: (short)w.Read(128, 16),
        AlbumPeak: (int)w.Read(144, 16),
        Gapless: w.Read(160, 1) == 1,
        LastSamples: (int)w.Read(161, 11),
        FastSeek: w.Read(172, 1) == 1,
        Unused: (int)w.Read(173, 19),
        Encoder: (int)w.Read(192, 8));
}
