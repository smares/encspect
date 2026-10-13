using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// The streams of the file: frames of one version, layer, sample rate and free format or not, in
// file order across gaps and tags, so a stream that an embedded tag splits into two regions is one
// stream, and a change of those parameters starts the next (MpegHeader.IsSameStream).
public sealed partial class MpegAudioParser
{
    private static void AddStreams(StreamState state, ParseContext context)
    {
        ChunkedList<Entry> frames = state.Build.Frames;
        int start = -1;
        int end = -1;
        MpegHeader? previous = null;
        for (int i = 0; i < frames.Count; i++)
        {
            state.CheckCancellation(i);
            if (frames[i].IsGap)
            {
                continue;
            }

            var header = new MpegHeader(frames[i].Header);
            if (previous is { } last && !header.IsSameStream(last))
            {
                context.AddStream(Stream(state, start, end));
                start = -1;
            }

            start = start < 0 ? i : start;
            end = i + 1;
            previous = header;
        }

        if (start >= 0)
        {
            context.AddStream(Stream(state, start, end));
        }
    }

    // The stream of the frames among the records start to end.
    private static StreamInfo Stream(StreamState state, int start, int end)
    {
        ChunkedList<Entry> frames = state.Build.Frames;
        // The channel mode is the audio's: a VBR header frame need not state it (ffmpeg writes its
        // Info frame as stereo before joint stereo audio).
        int described = start;
        while (described < end - 1 && !IsAudio(frames[described]))
        {
            described++;
        }

        var header = new MpegHeader(frames[IsAudio(frames[described]) ? described : start].Header);
        long ticks = 0;
        long bits = 0;
        long samples = 0;
        int? firstBitrate = null;
        bool isVbr = false;
        int audio = 0;
        var regions = new List<RegionState>();
        int region = RegionOf(state, start);
        for (int i = start; i < end; i++)
        {
            state.CheckCancellation(i - start);
            ref readonly Entry frame = ref frames[i];
            if (frame.IsGap)
            {
                continue;
            }

            while (i >= (state.Regions[region].End ?? frames.Count))
            {
                region++;
            }

            if (regions.Count == 0 || regions[^1] != state.Regions[region])
            {
                regions.Add(state.Regions[region]);
            }

            if (IsAudio(frame))
            {
                ticks += Ticks(frame);
                bits += frame.Length * 8;
                samples += new MpegHeader(frame.Header).Samples;
                int? bitrate = BitrateOf(frame);
                isVbr |= audio > 0 && bitrate != firstBitrate;
                firstBitrate = audio == 0 ? bitrate : firstBitrate;
                audio++;
            }
        }

        Node? encoder = regions.Select(r => r.Encoder).FirstOrDefault(e => e is not null);
        string? confidence = encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString();
        (IReadOnlyList<Speaker> layout, int multilingual) = Layout(state, start, end, header);

        return new StreamInfo
        {
            Nodes = regions.Select(r => r.Node!).ToList(),
            Codec = $"{header.VersionName} Layer {header.LayerName}",
            SampleRate = header.SampleRate,
            Channels = layout.Count,
            ChannelMode = header.ChannelMode,
            ChannelLayout = layout,
            MultilingualChannels = multilingual,
            Samples = samples,
            Duration = ticks / (double)Clock,
            AverageBitrate = ticks > 0 ? bits * (double)Clock / (ticks * 1000.0) : null,
            IsVbr = isVbr,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = Series(state, start, end, audio),
        };
    }

    // The channels of the stream's first audio frame: one for mono, two independent programs without
    // speaker positions for dual channel, left and right for stereo and joint stereo. Where the file
    // carries the MPEG-2 multichannel extension (ISO/IEC 13818-3) and every frame of the stream
    // that carries it states the same configuration, the stream has the configuration's channels in
    // the order of the decoder's output: left and right, which the base frames carry, the center,
    // the surround channels or the second stereo program, then the LFE. Multilingual channels are
    // other programs, not speakers, so they are counted apart.
    private static (IReadOnlyList<Speaker> Layout, int Multilingual) Layout(StreamState state, int start, int end, MpegHeader header)
    {
        IReadOnlyList<Speaker> layout = header.IsMono ? ChannelLayouts.Mono : header.ChannelModeBits == 2 ? ChannelLayouts.Discrete(2) : ChannelLayouts.Stereo;
        if (!state.Build.HasMultichannel)
        {
            return (layout, 0);
        }

        ChunkedList<Entry> frames = state.Build.Frames;
        Layer2Multichannel? first = null;
        int firstIndex = -1;
        for (int i = start; i < end; i++)
        {
            state.CheckCancellation(i - start);
            ref readonly Entry frame = ref frames[i];
            if (frame.IsGap || !frame.IsMultichannelCandidate)
            {
                continue;
            }

            Layer2Multichannel mc = ReadMultichannel(state.Build, frame, new MpegHeader(frame.Header)).Mc;
            if (!mc.IsCrcValid && (mc.Fits || !mc.HasExtension || mc.HasAncillaryOverflow))
            {
                continue;
            }

            if (first is null)
            {
                (first, firstIndex) = (mc, i);
            }
            else if (mc.ChannelKey != first.ChannelKey)
            {
                Layer2Multichannel from = first;
                long offset = frame.Offset;
                state.Findings.Add(Severity.Info, "mpeg.multichannel-configuration-changes", FindingNode(frames[firstIndex]), _ =>
                    $"The multichannel configuration changes within the stream, from {from.Configuration} to {mc.Configuration} at 0x{offset:X}, so the stream reports the channels of its stereo base.");
                return (layout, 0);
            }
        }

        return first is null ? (layout, 0) : (first.Layout, first.MultilingualChannels);
    }

    // The region that holds a record: the last that starts at or before it.
    private static int RegionOf(StreamState state, int index)
    {
        int low = 0;
        int high = state.Regions.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            (low, high) = state.Regions[middle].First <= index ? (middle + 1, high) : (low, middle);
        }

        return low - 1;
    }

    // Per frame, the bitrate its header gives (a free-format frame's from its length) and the bit
    // reservoir fill when it starts; per second of audio, the bits of the frames that start in it
    // over their duration and their mean reservoir fill, rounded half to even. The reservoir fill
    // is there when every frame is Layer III. Only these arrays grow with the number of frames.
    private static List<Series> Series(StreamState state, int start, int end, int count)
    {
        if (count == 0)
        {
            return [];
        }

        ChunkedList<Entry> frames = state.Build.Frames;
        var offsets = new double[count];
        var bitrates = new double[count];
        var fills = new double[count];
        var seconds = new List<(long Bits, long Ticks, long Fill, int Count)>();
        long time = 0;
        bool hasSideInfo = true;
        int firstAudio = -1;
        int n = 0;
        for (int i = start; i < end; i++)
        {
            state.CheckCancellation(i - start);
            ref readonly Entry frame = ref frames[i];
            if (!IsAudio(frame))
            {
                continue;
            }

            firstAudio = firstAudio < 0 ? i : firstAudio;
            var header = new MpegHeader(frame.Header);
            offsets[n] = frame.Offset;
            bitrates[n] = header.IsFreeFormat ? frame.Length * 8.0 * header.SampleRate / header.Samples / 1000 : header.Bitrate;
            fills[n] = frame.ReservoirFill;
            hasSideInfo &= frame.HasSideInfo;
            n++;

            // Frames last less than a second, so every second has a frame that starts in it.
            int second = (int)(time / Clock);
            while (seconds.Count <= second)
            {
                seconds.Add(default);
            }

            (long b, long t, long f, int c) = seconds[second];
            seconds[second] = (b + (frame.Length * 8), t + Ticks(frame), f + frame.ReservoirFill, c + 1);
            time += Ticks(frame);
        }

        double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
        RegionState first = state.Regions[RegionOf(state, firstAudio)];
        var series = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = SeriesText.Bitrate },
            new("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts,
                seconds.Select(s => s.Ticks > 0 ? (double)RoundHalfEven((Int128)s.Bits * Clock, (Int128)s.Ticks * 1000) : 0).ToArray(), first.Distribution)
            {
                Description = SeriesText.BitrateOverTime,
            },
        };

        if (hasSideInfo)
        {
            series.Add(new("Reservoir Fill", "bytes", SeriesAxis.FrameOffset, offsets, fills) { Description = SeriesText.ReservoirFill });
            series.Add(new("Reservoir Fill Over Time", "bytes", SeriesAxis.Seconds, starts,
                seconds.Select(s => s.Count > 0 ? (double)RoundHalfEven(s.Fill, s.Count) : 0).ToArray(), first.MaximumFill)
            {
                Description = SeriesText.ReservoirFillOverTime,
            });
        }

        return series;
    }
}
